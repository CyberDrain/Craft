using System.Text.Json;
using Craft.Hosting;
using Craft.Hosting.Endpoints;

namespace Craft.Tests;

/// <summary>
/// Triggers are how a short PowerShell HTTP handler hands long-running work to the host after its
/// response is written. Misparsing one silently drops an orchestration — the caller still gets its
/// 200, and nothing runs.
/// </summary>
public class ResponseTriggersTests
{
    [Theory]
    [InlineData("""{"_orchestratorTrigger":true}""", "_orchestratorTrigger", true)]
    [InlineData("""{"ok":1}""", "_orchestratorTrigger", false)]
    [InlineData("", "_orchestratorTrigger", false)]
    [InlineData(null, "_orchestratorTrigger", false)]
    public void MayContain_IsACheapPreFilter(string? body, string marker, bool expected) =>
        Assert.Equal(expected, ResponseTriggers.MayContain(body, marker));

    [Fact]
    public void Orchestrator_ParsesAllFields()
    {
        var trigger = ResponseTriggers.ParseOrchestrator("""
            {
              "_orchestratorTrigger": true,
              "command": "Start-Sync",
              "plannerScript": "Plan-Sync.ps1",
              "taskScript": "Do-Sync.ps1",
              "priority": 1
            }
            """);

        Assert.NotNull(trigger);
        Assert.Equal("Start-Sync", trigger!.Command);
        Assert.Equal("Plan-Sync.ps1", trigger.PlannerScript);
        Assert.Equal("Do-Sync.ps1", trigger.TaskScript);
        Assert.Equal(1, trigger.Priority);
    }

    [Fact]
    public void Orchestrator_PriorityDefaultsToTwo()
    {
        var trigger = ResponseTriggers.ParseOrchestrator("""
            {"_orchestratorTrigger":true,"command":"c","plannerScript":"p","taskScript":"t"}
            """);

        Assert.Equal(2, trigger!.Priority);
    }

    [Theory]
    [InlineData("""{"_orchestratorTrigger":false,"command":"c","plannerScript":"p","taskScript":"t"}""")]
    [InlineData("""{"command":"c","plannerScript":"p","taskScript":"t"}""")]
    public void Orchestrator_MarkerAbsentOrFalse_IsNotATrigger(string body) =>
        Assert.Null(ResponseTriggers.ParseOrchestrator(body));

    [Fact]
    public void Orchestrator_MarkerMustBeBooleanTrue_NotTruthy()
    {
        // A string "true" is not a trigger. Being strict here avoids firing a fan-out off a response
        // that merely happens to carry a similarly named field.
        Assert.Null(ResponseTriggers.ParseOrchestrator("""{"_orchestratorTrigger":"true","command":"c"}"""));
        Assert.Null(ResponseTriggers.ParseOrchestrator("""{"_orchestratorTrigger":1,"command":"c"}"""));
    }

    [Fact]
    public void Orchestrator_MissingRequiredField_Throws()
    {
        // The caller catches this and logs; the alternative is a null-reference deep inside the
        // orchestrator with no indication of which response caused it.
        Assert.Throws<KeyNotFoundException>(() =>
            ResponseTriggers.ParseOrchestrator("""{"_orchestratorTrigger":true,"command":"c"}"""));
    }

    [Fact]
    public void Script_ParsesAndDefaultsPriorityToFive()
    {
        var explicitPriority = ResponseTriggers.ParseScript(
            """{"_scriptTrigger":true,"command":"Invoke-Thing","priority":9}""");
        Assert.Equal("Invoke-Thing", explicitPriority!.Command);
        Assert.Equal(9, explicitPriority.Priority);

        // Background scripts sit below orchestrations by default.
        var defaulted = ResponseTriggers.ParseScript("""{"_scriptTrigger":true,"command":"Invoke-Thing"}""");
        Assert.Equal(5, defaulted!.Priority);
    }

    [Fact]
    public void Cancel_ParsesCommand()
    {
        var trigger = ResponseTriggers.ParseCancel("""{"_cancelTrigger":true,"command":"Start-Sync"}""");
        Assert.Equal("Start-Sync", trigger!.Command);
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        // ThrowsAny, not Throws: System.Text.Json raises JsonReaderException, a JsonException subclass.
        // The handler's `when (ex is JsonException ...)` filter matches subclasses, so this is the
        // assertion that reflects real catch behaviour.
        Assert.ThrowsAny<JsonException>(() => ResponseTriggers.ParseOrchestrator("not json"));
    }

    [Fact]
    public void NonObjectJson_IsNotATrigger()
    {
        // A handler returning a bare array is normal; it must not blow up the trigger scan.
        Assert.Null(ResponseTriggers.ParseOrchestrator("[1,2,3]"));
    }
}

public class PowerShellDispatchTests
{
    [Theory]
    [InlineData("GET", "ListUsers", true)]
    [InlineData("GET", "listusers", true)]     // convention check is case-insensitive
    [InlineData("GET", "GetUser", false)]      // not a List* read
    [InlineData("POST", "ListUsers", false)]   // writes are never cached
    [InlineData("DELETE", "ListUsers", false)]
    public void OnlyGetsToListEndpointsAreCached(string method, string endpoint, bool expected) =>
        Assert.Equal(expected, PowerShellDispatchEndpoint.IsCacheableRead(method, endpoint));
}

public class FrontendFallbackTests
{
    [Theory]
    [InlineData("/api/users")]
    [InlineData("/API/users")]
    [InlineData("/.auth/me")]
    public void ApiAndAuthPaths_NeverFallBackToHtml(string path)
    {
        // Serving index.html for an unmatched /api path turns a 404 into a soft 200 that a caller
        // parses as garbage and an edge cache will happily store against the API URL.
        Assert.False(FrontendFallbackEndpoint.IsFallbackEligible(path));
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/")]
    [InlineData("/tenants/contoso")]
    public void ApplicationRoutes_AreEligible(string path) =>
        Assert.True(FrontendFallbackEndpoint.IsFallbackEligible(path));

    [Theory]
    [InlineData("/dashboard", true)]
    [InlineData("/tenants/contoso", true)]
    [InlineData("/logo.png", false)]   // a missing asset, not a route
    [InlineData("/", false)]
    [InlineData("", false)]
    public void PrerenderedRouteCandidates_AreExtensionlessNonRootPaths(string path, bool expected) =>
        Assert.Equal(expected, FrontendFallbackEndpoint.IsPrerenderedRouteCandidate(path));
}

public class RequestCounterTests
{
    [Fact]
    public void CountsUpAndDown()
    {
        var counter = new RequestCounter();

        Assert.Equal(0, counter.Active);
        Assert.Equal(1, counter.Increment());
        Assert.Equal(2, counter.Increment());
        Assert.Equal(2, counter.Active);
        Assert.Equal(1, counter.Decrement());
        Assert.Equal(1, counter.Active);
    }

    [Fact]
    public void IsSafeUnderConcurrency()
    {
        var counter = new RequestCounter();

        Parallel.For(0, 1000, _ => counter.Increment());
        Assert.Equal(1000, counter.Active);

        Parallel.For(0, 1000, _ => counter.Decrement());
        Assert.Equal(0, counter.Active);
    }
}
