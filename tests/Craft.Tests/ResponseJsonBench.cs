using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using System.Text;
using System.Text.Json;
using Craft.Services;

namespace Craft.Tests;

/// <summary>
/// Response body serialization cost on CIPP-shaped output. Runs only with CRAFT_BENCH=1; results go to CRAFT_BENCH_OUT.
/// </summary>
public class ResponseJsonBench
{
    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = false };
    private static readonly string[] s_variants = ["old", "new"];

    // One MailboxRules report row: the cached rule parsed with ConvertFrom-Json, plus the report's Add-Member columns.
    private const string RulesScript = """
        param($N)
        $Pad = 'x' * 3000
        $Json = '[' + ((0..($N - 1) | ForEach-Object { '{"Name":"Rule ' + $_ + '","Identity":"user' + ($_ % 97) + '@contoso.com\\' + $_ + '","UserPrincipalName":"user' + ($_ % 97) + '@contoso.com","Enabled":true,"Priority":' + $_ + ',"From":["sender' + $_ + '@external.test"],"ForwardTo":[],"MoveToFolder":"Inbox\\Archive","DeleteMessage":false,"StopProcessingRules":true,"Description":"If the message: is received from sender then move to Archive ' + $Pad + '"}' }) -join ',') + ']'
        $Stamp = [DateTimeOffset]::new(2026, 10, 26, 1, 2, 3, [TimeSpan]::Zero)
        $Rows = foreach ($Rule in ($Json | ConvertFrom-Json)) { $Rule | Add-Member -NotePropertyMembers ([ordered]@{ CacheTimestamp = $Stamp; Tenant = 'contoso.com' }) -Force; $Rule }
        [Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext]@{ StatusCode = 200; Body = @($Rows) }
        """;

    // A Graph beta user: nested license and plan arrays, dates parsed to DateTime, a null-heavy extension object.
    private const string UsersScript = """
        param($N)
        $Plans = (0..9 | ForEach-Object { '{"assignedDateTime":"2024-0' + (($_ % 9) + 1) + '-15T10:20:30Z","capabilityStatus":"Enabled","service":"exchange","servicePlanId":"' + [guid]::new($_, 0, 0, [byte[]]::new(8)) + '"}' }) -join ','
        $Ext = (1..15 | ForEach-Object { '"extensionAttribute' + $_ + '":null' }) -join ','
        $Json = '[' + ((0..($N - 1) | ForEach-Object { '{"id":"' + [guid]::new($_, 1, 1, [byte[]]::new(8)) + '","displayName":"User ' + $_ + '","userPrincipalName":"user' + $_ + '@contoso.com","accountEnabled":true,"createdDateTime":"2023-05-0' + (($_ % 9) + 1) + 'T08:00:00Z","businessPhones":["+1 555 0100"],"proxyAddresses":["SMTP:user' + $_ + '@contoso.com","smtp:user' + $_ + '@contoso.onmicrosoft.com"],"assignedLicenses":[{"disabledPlans":[],"skuId":"c42b9cae-ea4f-4ab7-9717-81576235ccac"}],"assignedPlans":[' + $Plans + '],"onPremisesExtensionAttributes":{' + $Ext + '},"jobTitle":null,"department":"Ops","usageLocation":"AU"}' }) -join ',') + ']'
        [Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext]@{ StatusCode = 200; Body = @($Json | ConvertFrom-Json) }
        """;

    [Fact]
    public void Run()
    {
        if (Environment.GetEnvironmentVariable("CRAFT_BENCH") != "1") return;
        var outPath = Environment.GetEnvironmentVariable("CRAFT_BENCH_OUT") ?? Path.Combine(Path.GetTempPath(), "craft-response-bench.txt");
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"runtime {Environment.Version}, server GC {System.Runtime.GCSettings.IsServerGC}");
        report.AppendLine(CultureInfo.InvariantCulture, $"{"shape",-8} {"rows",7} {"variant",-13} {"wall ms",9} {"alloc MB",9} {"peak MB",8} {"out MB",7}  same");

        var only = Environment.GetEnvironmentVariable("CRAFT_BENCH_ONLY");
        var runs = int.TryParse(Environment.GetEnvironmentVariable("CRAFT_BENCH_RUNS"), out var r) ? r : 4;
        foreach (var (shape, script, rows) in new[] { ("rules", RulesScript, 20000), ("users", UsersScript, 20000) })
        {
            string? reference = null;
            foreach (var variant in s_variants.Where(v => only == null || only == $"{shape}:{v}"))
            {
                var samples = new List<(double Wall, double Alloc, double Peak, long Out, bool Same)>();
                for (var run = 0; run < runs; run++)
                {
                    var results = Build(script, rows);
                    var body = BodyOf(results);
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var baseline = GC.GetTotalMemory(true);
                    var alloc0 = GC.GetTotalAllocatedBytes(true);
                    long peak = 0;
                    using var stop = new CancellationTokenSource();
                    var sampler = new Thread(() => { while (!stop.IsCancellationRequested) { peak = Math.Max(peak, GC.GetTotalMemory(false)); Thread.Sleep(2); } }) { IsBackground = true };
                    sampler.Start();
                    var sw = Stopwatch.StartNew();
                    object? output = null;
                    switch (variant)
                    {
                        case "old": output = PowerShellRunnerService.ConvertPsObjectToJson(body); break;
                        case "new": output = PsJsonWriter.TrySerialize(body, s_options); break;
                    }
                    sw.Stop();
                    stop.Cancel(); sampler.Join();
                    peak = Math.Max(peak, GC.GetTotalMemory(false));
                    var alloc = GC.GetTotalAllocatedBytes(true) - alloc0;
                    var text = output switch { string t => t, byte[] b => Encoding.UTF8.GetString(b), _ => "" };
                    reference ??= text;
                    if (run > 0) samples.Add((sw.Elapsed.TotalMilliseconds, alloc / 1048576.0, (peak - baseline) / 1048576.0, Encoding.UTF8.GetByteCount(text), string.Equals(text, reference, StringComparison.Ordinal)));
                    GC.KeepAlive(results);
                }
                var m = samples.OrderBy(s => s.Wall).ElementAt(samples.Count / 2);
                report.AppendLine(CultureInfo.InvariantCulture, $"{shape,-8} {rows,7} {variant,-13} {m.Wall,9:N0} {samples.Select(s => s.Alloc).Median(),9:N0} {samples.Select(s => s.Peak).Median(),8:N0} {m.Out / 1048576.0,7:N1}  {samples.All(s => s.Same)}");
            }
        }
        File.WriteAllText(outPath, report.ToString());
    }

    private static Collection<PSObject> Build(string script, int rows)
    {
        using var ps = PowerShell.Create();
        ps.AddScript(script).AddArgument(rows);
        var results = ps.Invoke();
        if (ps.HadErrors) throw new InvalidOperationException(string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
        return results;
    }

    private static object? BodyOf(Collection<PSObject> results) =>
        results[^1].Properties["Body"].Value is PSObject p ? p : results[^1].Properties["Body"].Value;
}

internal static class BenchStats
{
    public static double Median(this IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
