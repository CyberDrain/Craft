using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Craft.Storage;

namespace Craft.Hosting;

/// <summary>
/// Masks UPNs and customer domains in log lines while keeping them recognisable at a glance:
/// <c>jane.doe@contoso.onmicrosoft.com</c> becomes <c>ja~…~@co~…~.onmicrosoft.com</c>. The hidden middle
/// is AES-encrypted with a fixed IV, so a value always yields the same token (raw files stay greppable
/// and correlatable) and <see cref="Reveal"/> restores it with the instance key. Tenant ids and other
/// GUIDs are left alone.
/// </summary>
public static partial class LogRedactor
{
    private const string KeyTable = "CraftInstanceKeys";
    private const string KeyPartition = "LogRedaction";
    private const string KeyRow = "v1";
    private const int MaxCacheEntries = 50_000;

    private static readonly HashSet<string> s_tlds =
        new(TldList.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    // Kept readable; the label in front of them is masked.
    private static readonly string[] s_suffixes =
    [
        "mail.protection.outlook.com", "onmicrosoft.com", "sharepoint.com",
        "co.uk", "org.uk", "ac.uk", "gov.uk", "ltd.uk", "plc.uk", "uk.net",
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "co.nz", "org.nz", "co.za", "com.br",
    ];

    // Platform hosts left in clear, along with their subdomains.
    private static readonly string[] s_builtInAllow =
    [
        "microsoft.com", "microsoftonline.com", "windows.net", "office365.com", "office.com", "office.net",
        "outlook.com", "live.com", "azure.com", "azure.net", "azurewebsites.net", "azure-api.net",
        "windowsazure.com", "cloud.microsoft", "microsoft", "github.com", "githubusercontent.com", "ghcr.io",
        "powershellgallery.com", "nuget.org",
    ];

    // Bytes of HMAC carried in each token: it seeds the keystream and rejects tokens from another key.
    private const int TagBytes = 3;
    private static readonly object s_aesLock = new();
    private static byte[] s_key = RandomNumberGenerator.GetBytes(16);
    private static Aes s_aes = CreateAes(s_key);
    private static string[] s_allow = s_builtInAllow;
    private static volatile bool s_enabled = true;
    private static readonly ConcurrentDictionary<string, string> s_encrypted = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string?> s_decrypted = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string?> s_domainMasks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string?> s_emailHostMasks = new(StringComparer.Ordinal);

    [GeneratedRegex(@"~(?<blob>[A-Za-z0-9_-]{6,})~", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex TokenPattern();

    public static void Configure(bool enabled, IEnumerable<string>? allowDomains)
    {
        s_enabled = enabled;
        s_allow = [.. s_builtInAllow, .. (allowDomains ?? []).Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim().TrimStart('.').ToLowerInvariant())];
        s_domainMasks.Clear();
        s_emailHostMasks.Clear();
    }

    public static void SetKey(byte[] key)
    {
        lock (s_aesLock)
        {
            s_key = key;
            s_aes = CreateAes(key);
            s_encrypted.Clear();
            s_decrypted.Clear();
            s_domainMasks.Clear();
            s_emailHostMasks.Clear();
        }
    }

    /// <summary>
    /// Loads the instance key from the Craft key table, creating it on first start. Every node of an
    /// instance shares it, so any node can reveal any node's lines.
    /// </summary>
    public static async Task LoadKeyAsync(ICraftTableStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        await store.EnsureTableAsync(KeyTable, ct);
        var row = await store.GetAsync(KeyTable, KeyPartition, KeyRow, ct);
        if (row?.GetString("Key") is not { Length: > 0 })
        {
            var created = new StoreRow(KeyPartition, KeyRow);
            created["Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            await store.UpsertAsync(KeyTable, created, ct);
            // Re-read so two nodes racing on first start settle on the same key.
            row = await store.GetAsync(KeyTable, KeyPartition, KeyRow, ct);
        }
        SetKey(Convert.FromBase64String(row!.GetString("Key")!));
    }

    /// <summary>
    /// One pass over the line, jumping between dots: each dotted run is a candidate host, and a run
    /// preceded by <c>@</c> or <c>%40</c> takes its local part with it.
    /// </summary>
    public static string Redact(string line)
    {
        if (!s_enabled || string.IsNullOrEmpty(line)) return line;
        StringBuilder? sb = null;
        var copied = 0;
        var dot = line.IndexOf('.');
        while (dot >= 0)
        {
            var start = dot;
            while (start > 0 && IsHostChar(line[start - 1])) start--;
            var end = dot + 1;
            while (end < line.Length && IsHostChar(line[end])) end++;
            var resume = end;

            start = SkipEscape(line, start, end);
            while (start < end && line[start] is '.' or '-') start++;
            while (end > start && line[end - 1] is '.' or '-') end--;

            if (end - start >= 3 && !IsInLocalPart(line, end))
            {
                var atLength = start >= 1 && line[start - 1] == '@' ? 1
                    : start >= 3 && line.AsSpan(start - 3, 3).SequenceEqual("%40") ? 3 : 0;
                var host = line[start..end];
                var masked = CachedMaskHost(host, isEmail: atLength > 0);

                var from = start;
                var replacement = masked;
                if (atLength > 0)
                {
                    var localEnd = start - atLength;
                    var localStart = localEnd;
                    while (localStart > copied && IsLocalChar(line[localStart - 1])) localStart--;
                    localStart = SkipEscape(line, localStart, localEnd);
                    if (localEnd > localStart)
                    {
                        from = localStart;
                        replacement = string.Concat(MaskLabel(line[localStart..localEnd]),
                            line.AsSpan(localEnd, atLength), masked ?? host);
                    }
                }

                if (replacement != null)
                {
                    sb ??= new StringBuilder(line.Length + 64);
                    sb.Append(line, copied, from - copied).Append(replacement);
                    copied = end;
                }
            }

            dot = resume < line.Length ? line.IndexOf('.', resume) : -1;
        }
        return sb == null ? line : sb.Append(line, copied, line.Length - copied).ToString();
    }

    private static string? CachedMaskHost(string host, bool isEmail)
    {
        var cache = isEmail ? s_emailHostMasks : s_domainMasks;
        if (cache.TryGetValue(host, out var masked)) return masked;
        masked = MaskHost(host, isEmail);
        if (cache.Count >= MaxCacheEntries) cache.Clear();
        cache.TryAdd(host, masked);
        return masked;
    }

    private static bool IsHostChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-';

    private static bool IsLocalChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or '\'' or '#';

    // %27contoso.com%27: the two hex digits after % belong to the escape, not the name.
    private static int SkipEscape(string line, int start, int end) =>
        start > 0 && line[start - 1] == '%' && end - start > 2
            && char.IsAsciiHexDigit(line[start]) && char.IsAsciiHexDigit(line[start + 1]) ? start + 2 : start;

    // A dotted run that carries on into an @ is the local part of an email, handled with its host.
    private static bool IsInLocalPart(string line, int end)
    {
        var i = end;
        while (i < line.Length && IsLocalChar(line[i])) i++;
        return i < line.Length && (line[i] == '@' || line.AsSpan(i).StartsWith("%40"));
    }

    /// <summary>Restores masked values in a line written by this instance. Unknown tokens are left as is.</summary>
    public static string Reveal(string line)
    {
        if (string.IsNullOrEmpty(line) || line.IndexOf('~') < 0) return line;
        return TokenPattern().Replace(line, static m => Decrypt(m.Groups["blob"].Value) ?? m.Value);
    }

    // Null leaves the host as written.
    private static string? MaskHost(string host, bool isEmail)
    {
        if (!host.Contains('.', StringComparison.Ordinal) || host.Contains("..", StringComparison.Ordinal)
            || !char.IsAsciiLetter(host[host.LastIndexOf('.') + 1]))
            return null;
        var lower = host.ToLowerInvariant();
        var suffixLength = 0;
        foreach (var suffix in s_suffixes)
        {
            if (lower == suffix) return null;
            if (lower.Length > suffix.Length && lower.EndsWith(suffix, StringComparison.Ordinal)
                && lower[^(suffix.Length + 1)] == '.')
            {
                suffixLength = suffix.Length;
                break;
            }
        }

        if (suffixLength == 0)
        {
            foreach (var allowed in s_allow)
            {
                if (lower == allowed || (lower.EndsWith(allowed, StringComparison.Ordinal)
                    && lower[^(allowed.Length + 1)] == '.'))
                    return null;
            }
            suffixLength = host.Length - host.LastIndexOf('.') - 1;
            if (!isEmail && !s_tlds.Contains(host[^suffixLength..])) return null;
        }

        var name = host[..^(suffixLength + 1)];
        var labelStart = name.LastIndexOf('.') + 1;
        return name[..labelStart] + MaskLabel(name[labelStart..]) + host[^(suffixLength + 1)..];
    }

    // Roughly half of a label stays readable, split across both ends; short ones keep only the first character.
    private static string MaskLabel(string value)
    {
        if (value.Length < 2) return value;
        var (head, tail) = value.Length switch
        {
            <= 3 => (1, 0),
            <= 5 => (1, 1),
            <= 8 => (2, 1),
            _ => (2, 2),
        };
        return value[..head] + "~" + Encrypt(value[head..^tail].ToLowerInvariant()) + "~" + value[^tail..];
    }

    // Deterministic and length-preserving: a short HMAC tag of the value seeds an AES-CTR keystream,
    // so a token is the tag plus one byte per hidden character.
    private static string Encrypt(string hidden)
    {
        if (s_encrypted.TryGetValue(hidden, out var cached)) return cached;
        var plain = Encoding.UTF8.GetBytes(hidden);
        var output = new byte[TagBytes + plain.Length];
        lock (s_aesLock)
        {
            HMACSHA256.HashData(s_key, plain).AsSpan(0, TagBytes).CopyTo(output);
            ApplyKeystream(output.AsSpan(0, TagBytes), plain, output.AsSpan(TagBytes));
        }
        var token = Convert.ToBase64String(output).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (s_encrypted.Count >= MaxCacheEntries) s_encrypted.Clear();
        s_encrypted[hidden] = token;
        return token;
    }

    private static string? Decrypt(string token)
    {
        if (s_decrypted.TryGetValue(token, out var cached)) return cached;
        string? plain = null;
        var b64 = token.Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - b64.Length % 4) % 4);
        try
        {
            var bytes = Convert.FromBase64String(b64);
            if (bytes.Length > TagBytes)
            {
                var decoded = new byte[bytes.Length - TagBytes];
                bool valid;
                lock (s_aesLock)
                {
                    ApplyKeystream(bytes.AsSpan(0, TagBytes), bytes.AsSpan(TagBytes), decoded);
                    valid = HMACSHA256.HashData(s_key, decoded).AsSpan(0, TagBytes).SequenceEqual(bytes.AsSpan(0, TagBytes));
                }
                if (valid) plain = Encoding.UTF8.GetString(decoded);
            }
        }
        catch (FormatException) { }
        if (s_decrypted.Count >= MaxCacheEntries) s_decrypted.Clear();
        s_decrypted[token] = plain;
        return plain;
    }

    private static void ApplyKeystream(ReadOnlySpan<byte> tag, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> counter = stackalloc byte[16];
        Span<byte> block = stackalloc byte[16];
        tag.CopyTo(counter);
        for (var offset = 0; offset < input.Length; offset += 16)
        {
            counter[15] = (byte)(offset / 16);
            s_aes.EncryptEcb(counter, block, PaddingMode.None);
            for (var i = 0; i < 16 && offset + i < input.Length; i++)
                output[offset + i] = (byte)(input[offset + i] ^ block[i]);
        }
    }

    private static Aes CreateAes(byte[] key)
    {
        var aes = Aes.Create();
        aes.Key = key;
        return aes;
    }
}
