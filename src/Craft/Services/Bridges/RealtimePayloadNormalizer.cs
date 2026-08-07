using System.Collections;
using System.Management.Automation;

namespace Craft.Services;

/// <summary>
/// Converts PowerShell payloads (PSObject / Hashtable / PS collections) into CLR shapes
/// <see cref="Craft.Realtime.RealtimeService"/> can serialize. Called by <see cref="RealtimeBridge"/>
/// before publish so the service stays free of the PowerShell SDK.
/// </summary>
internal static class RealtimePayloadNormalizer
{
    public static object? Normalize(object? v) => v switch
    {
        null => null,
        string or bool or int or long or double or float or decimal or DateTime or DateTimeOffset or Guid => v,
        PSObject ps => Normalize(ps.BaseObject),
        IDictionary d => NormalizeDict(d),
        IEnumerable e => NormalizeList(e),
        _ => v.ToString()
    };

    private static Dictionary<string, object?> NormalizeDict(IDictionary d)
    {
        var r = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in d)
            r[e.Key?.ToString() ?? ""] = Normalize(e.Value);
        return r;
    }

    private static List<object?> NormalizeList(IEnumerable e)
    {
        var r = new List<object?>();
        foreach (var i in e) r.Add(Normalize(i));
        return r;
    }
}
