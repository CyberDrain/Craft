namespace Craft.Storage;

/// <summary>
/// Key hygiene for the table backend.
///
/// Azure Table refuses four literal characters and the two control ranges inside a PartitionKey or a
/// RowKey, and it reports the refusal as "Bad Request - Error in query syntax" on a 400 — a message
/// that points at a query even when the operation was a write, and names neither the key nor the
/// character. That mismatch is the whole reason this lives in one named place: the failure is cheap to
/// prevent and expensive to read backwards from a log.
///
/// The write is not retryable, so an offending key does not fail transiently — it fails identically
/// forever, and any caller that re-drives on failure will do so indefinitely.
/// </summary>
public static class TableKeys
{
    /// <summary>Characters the backend will not accept inside a key: four literals, then C0 and C1.</summary>
    public static bool IsIllegal(char c) =>
        c is '/' or '\\' or '#' or '?'
        || c < (char)0x20
        || (c >= (char)0x7f && c <= (char)0x9f);

    public static bool IsSafe(string value)
    {
        foreach (var c in value)
            if (IsIllegal(c)) return false;
        return true;
    }

    /// <summary>
    /// Replace anything the backend rejects with '_', returning an already-safe value untouched.
    ///
    /// Deliberately lossy. A key is read back whole and never split into its parts again, so
    /// legibility in a log or the run view is worth more here than an exactly reversible encoding.
    /// The cost is that two distinct inputs can fold onto one output, so a caller that relies on keys
    /// being distinct MUST de-duplicate after sanitizing rather than before.
    /// </summary>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value) || IsSafe(value)) return value;

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (IsIllegal(chars[i])) chars[i] = '_';
        return new string(chars);
    }
}
