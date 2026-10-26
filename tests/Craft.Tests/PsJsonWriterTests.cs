using System.Management.Automation;
using System.Text;
using System.Text.Json;
using Craft.Services;

namespace Craft.Tests;

/// <summary>
/// The streaming response writer must produce exactly the text of the old copy-then-serialize path for every body a
/// handler can return, and hand back null where the old path throws so the caller fails the same way.
/// </summary>
public class PsJsonWriterTests
{
    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = false };

    public static TheoryData<string, string> Bodies => new()
    {
        { "ConvertFrom-Json rows", """@('[{"a":1,"big":9007199254740993,"f":1.5,"t":true,"n":null,"s":"<a href=''x''>&é😀\"\\","when":"2024-01-02T03:04:05Z","arr":[1,"x",null,{"k":[]}],"empty":{},"none":[],"nested":{"deep":{"deeper":{"v":"x"}}}},{"a":2}]' | ConvertFrom-Json)""" },
        { "pscustomobject with hashtables", "[pscustomobject]@{ A = 1; B = $null; C = @(); D = @{ x = 1; y = @(1, 2) }; E = [ordered]@{ z = 'q'; a = @{} } }" },
        { "hashtable body", "@{ Results = @([pscustomobject]@{ a = 1 }, [pscustomobject]@{ a = 2 }); Metadata = @{ nextLink = $null } }" },
        { "ordered body", "[ordered]@{ b = 1; a = 2; c = [ordered]@{} }" },
        { "empty object", "[pscustomobject]@{}" },
        { "mixed array", "@([pscustomobject]@{}, $null, 1, 'x', $true, 1.5, [decimal]2.25, [long]9007199254740993, [byte]7, [single]0.5, @(), @(@()))" },
        { "list types", "$l = [System.Collections.Generic.List[object]]::new(); $l.Add(1); $l.Add([pscustomobject]@{ a = 'b' }); [pscustomobject]@{ L = $l; A = [System.Collections.ArrayList]@(1, 'two') }" },
        { "throwing script property", "$o = [pscustomobject]@{ a = 1 }; $o | Add-Member -MemberType ScriptProperty -Name Bad -Value { throw 'no' }; $o | Add-Member -MemberType ScriptProperty -Name Good -Value { 42 }; $o" },
        { "only property throws", "$o = [pscustomobject]@{}; $o | Add-Member -MemberType ScriptProperty -Name Bad -Value { throw 'no' }; @($o, [pscustomobject]@{ inner = $o })" },
        { "type data", "Update-TypeData -TypeName 'Craft.Test.Row' -MemberType ScriptProperty -MemberName Extra -Value { 42 } -Force; @([pscustomobject]@{ PSTypeName = 'Craft.Test.Row'; a = 1 }, [pscustomobject]@{ a = 2 })" },
        { "alias and note properties", "$o = [pscustomobject]@{ a = 1 }; $o | Add-Member -MemberType AliasProperty -Name b -Value a; $o | Add-Member -NotePropertyName c -NotePropertyValue @{ d = 'e' }; $o" },
        { "non-string keys", "@{ 1 = 'a'; '2' = 'b'; Inner = @{ 3 = 'c' } }" },
        { "value types", "[pscustomobject]@{ s = [System.Net.HttpStatusCode]::OK; g = [guid]'0f3b63d6-2cdd-41db-95d9-508f29745060'; o = [datetimeoffset]'2026-10-26T01:02:03+08:00'; t = [timespan]'01:02:03'; d = [datetime]::new(2026, 1, 2, 3, 4, 5, 678, [DateTimeKind]::Utc); l = [datetime]::new(2026, 1, 2, 3, 4, 5, [DateTimeKind]::Local); u = [datetime]::new(2026, 1, 2) }" },
        { ".NET objects", "[pscustomobject]@{ v = [version]'1.2.3.4'; u = [uri]'https://example.com/a?b=c'; c = [char]'x' }" },
        { "wrapped values", "[pscustomobject]@{ D = [psobject]([datetime]::new(2024, 1, 2, 0, 0, 0, [DateTimeKind]::Utc)); S = [psobject]'text'; I = [psobject]5; H = [psobject]@{ k = 1 }; A = [psobject]@(1, 2) }" },
        { "deep nesting", "$x = [pscustomobject]@{ leaf = 1 }; foreach ($i in 1..30) { $x = [pscustomobject]@{ level = $i; child = $x } }; $x" },
        { "too deep", "$x = [pscustomobject]@{ leaf = 1 }; foreach ($i in 1..80) { $x = [pscustomobject]@{ child = $x } }; $x" },
        { "not a number", "[pscustomobject]@{ n = [double]::NaN }" },
        { "scalar", "42" },
        { "null", "$null" },
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public void WritesTheSameJsonAsTheOldPath(string name, string script)
    {
        Assert.NotNull(name);
        using var ps = PowerShell.Create();

        // Fresh objects for each order, then the writer again on an object the old path has already walked.
        var oldFirst = BodyFrom(ps, script);
        var expected = OldJson(oldFirst);
        var newFirst = BodyFrom(ps, script);
        var actual = Text(PsJsonWriter.TrySerialize(newFirst, s_options));
        var afterOld = Text(PsJsonWriter.TrySerialize(oldFirst, s_options));

        if (expected.Error != null)
        {
            Assert.Null(actual);
            Assert.Null(afterOld);
            return;
        }
        if (s_fallsBack.Contains(name))
        {
            // ExtractResponse serializes these the old way.
            Assert.Null(actual);
            Assert.Equal(expected.Json, OldJson(newFirst).Json);
            return;
        }
        Assert.Equal(expected.Json, actual);
        Assert.Equal(expected.Json, afterOld);
    }

    private static readonly HashSet<string> s_fallsBack = ["non-string keys"];

    [Fact]
    public void AResponseBodyStillRoundTripsThroughExtractResponse()
    {
        using var ps = PowerShell.Create();
        ps.AddScript("[Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext]@{ StatusCode = 207; Body = @([pscustomobject]@{ resultText = 'ok'; state = 'success' }) }");
        var result = PowerShellRunnerService.ExtractResponse(ps.Invoke());
        Assert.Equal(207, result.StatusCode);
        Assert.Equal("""[{"resultText":"ok","state":"success"}]""", result.Body);
    }

    private static string? Text(byte[]? utf8) => utf8 is null ? null : Encoding.UTF8.GetString(utf8);

    private static (string? Json, Exception? Error) OldJson(object? body)
    {
        try { return (PowerShellRunnerService.ConvertPsObjectToJson(body), null); }
        catch (Exception ex) { return (null, ex); }
    }

    // The body as ExtractResponse sees it: the Body property of the HttpResponseContext a handler returns.
    private static object? BodyFrom(PowerShell ps, string script)
    {
        ps.Commands.Clear();
        ps.AddScript($"[Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext]@{{ StatusCode = 200; Body = $({script}) }}");
        var results = ps.Invoke();
        Assert.False(ps.HadErrors, string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
        return results[^1].Properties["Body"].Value;
    }
}
