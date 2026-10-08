using System.Management.Automation;
using System.Text;
using System.Text.Json;
using Craft.Configuration;
using Craft.Hosting.Endpoints;
using Craft.Realtime;
using Craft.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

public class RealtimeWatchTests
{
    private const string Job = "6f1c2b8e-1d2a-4c1e-9f0a-3b2c1d4e5f60";

    private static RealtimeService NewService() =>
        new(new CraftSettings { Realtime = new RealtimeSettings { Enabled = true } }, NullLogger<RealtimeService>.Instance);

    private static List<string> Drain(RealtimeService.Connection conn)
    {
        var frames = new List<string>();
        while (conn.Reader.TryRead(out var f)) frames.Add(f);
        return frames;
    }

    [Fact]
    public void Notify_ReachesOnlyGrantedUsers()
    {
        using var svc = NewService();
        var (_, alice) = svc.Connect("alice@contoso.com");
        var (_, mallory) = svc.Connect("mallory@contoso.com");

        svc.Notify(Job, "update", null);           // no grant yet
        svc.Watch("alice@contoso.com", Job, trackRun: false);
        svc.Notify(Job, "update", null);

        Assert.Single(Drain(alice!));
        Assert.Empty(Drain(mallory!));
    }

    [Theory]
    [InlineData("6f1c2b8e-1d2a-4c1e-9f0a-3b2c1d4e5f60")]
    [InlineData("BEC-20261008123000-ab12cd")]
    [InlineData("Orchestrator_contoso.com:1")]
    public void AnAppsOwnTokenIsAJobId(string jobId)
    {
        using var svc = NewService();
        var (_, alice) = svc.Connect("alice@contoso.com");
        svc.Watch("alice@contoso.com", jobId, trackRun: false);

        svc.Notify(jobId, "update", null);

        Assert.Contains($"\"jobId\":\"{jobId}\"", Assert.Single(Drain(alice!)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("x\0y")]
    [InlineData("line\r\ninjected")]
    [InlineData("<script>")]
    [InlineData("\"quoted\"")]
    [InlineData("ünïcode")]
    public void AnUnsafeJobIdIsDroppedWithoutThrowing(string jobId)
    {
        using var svc = NewService();
        var (_, alice) = svc.Connect("alice@contoso.com");

        svc.Watch("alice@contoso.com", jobId, trackRun: true);
        svc.Notify(jobId, "update", null);
        svc.Publish("alice@contoso.com", jobId, "update", null, null, null, null, null);
        Craft.Services.RealtimeBridge.Publish("alice@contoso.com", jobId);

        Assert.Empty(Drain(alice!));
        Assert.Empty(svc.CurrentFrames("alice@contoso.com"));
    }

    [Fact]
    public void AnOverlongJobIdIsDropped()
    {
        Assert.True(RealtimeService.IsSafeJobId(new string('a', 128)));
        Assert.False(RealtimeService.IsSafeJobId(new string('a', 129)));
        Assert.False(RealtimeService.IsSafeJobId(null));
    }

    [Fact]
    public void RunCounts_FollowACompleteTaskList_SoNumbersMatchTheChips()
    {
        // Storage has committed 13 of 16 finishes; the job manager already has 14 done, 1 running, 1 failed.
        var storage = new JobRunSummary { Name = "Run", Total = 16, Queued = 1, Running = 2, Completed = 12, Failed = 1 };
        var tasks = Enumerable.Repeat("Completed", 14).Append("Running").Append("Failed").ToList();

        var r = QueueStatusBridge.Reconcile(storage, tasks);

        Assert.Equal((0, 1, 14, 1), (r.Queued, r.Running, r.Completed, r.Failed));
        Assert.Equal(16, r.Total);
    }

    [Fact]
    public void RunCounts_FromAPartialTaskList_OnlyEverAddDoneTasks()
    {
        // Live shape from a 16-tenant run: storage has committed 3 finishes, the 14 tasks claimed here show 6.
        var storage = new JobRunSummary { Name = "Run", Total = 16, Queued = 11, Running = 2, Completed = 3 };
        var tasks = Enumerable.Repeat("Completed", 6).Concat(Enumerable.Repeat("Running", 2)).Concat(Enumerable.Repeat("Queued", 6)).ToList();

        var r = QueueStatusBridge.Reconcile(storage, tasks);

        Assert.Equal((8, 2, 6, 0), (r.Queued, r.Running, r.Completed, r.Failed));
        Assert.Equal(16, r.Queued + r.Running + r.Completed + r.Failed);
    }

    [Fact]
    public void RunCounts_KeepStorage_WhenItIsAhead()
    {
        // A parent whose total counts a child run slot: storage already has more done than the list shows.
        var storage = new JobRunSummary { Name = "Run", Total = 3, Queued = 1, Completed = 2 };

        var r = QueueStatusBridge.Reconcile(storage, Enumerable.Repeat("Completed", 1).ToList());

        Assert.Equal((1, 0, 2, 0), (r.Queued, r.Running, r.Completed, r.Failed));
    }

    private static QueueStatusBridge.RunRollup Run(string status, int completed) =>
        new(status, JsonSerializer.SerializeToElement(new { Status = status, CompletedTasks = completed }), $"{status}|{completed}");

    [Fact]
    public void PumpRuns_PushesChangesOnlyAndStopsAtCompletion()
    {
        using var svc = NewService();
        var (_, alice) = svc.Connect("alice@contoso.com");
        var run = Run("Running", 1);
        var calls = 0;
        svc.RunStatusSource = ids =>
        {
            calls++;
            return ids.Contains(Job) ? new() { [Job] = run } : new();
        };
        svc.Watch("alice@contoso.com", Job, trackRun: true);

        svc.PumpRuns();
        svc.PumpRuns(); // unchanged: nothing new
        run = Run("Completed", 2);
        svc.PumpRuns();
        var callsAtEnd = calls;
        svc.PumpRuns(); // finished: no longer tracked

        var frames = Drain(alice!);
        Assert.Equal(2, frames.Count);
        Assert.Contains("\"mode\":\"update\"", frames[0]);
        Assert.Contains("\"mode\":\"end\"", frames[1]);
        Assert.Contains("\"data\":{\"Status\":\"Completed\",\"CompletedTasks\":2}", frames[1]);
        Assert.Equal(callsAtEnd, calls);
    }

    [Fact]
    public void PumpRuns_EndsARunThatNeverAppears()
    {
        using var svc = NewService();
        var (_, alice) = svc.Connect("alice@contoso.com");
        svc.RunStatusSource = _ => new();
        svc.RunStartGrace = TimeSpan.Zero;
        svc.Watch("alice@contoso.com", Job, trackRun: true);

        svc.PumpRuns();
        svc.PumpRuns();

        var frame = Assert.Single(Drain(alice!));
        Assert.Contains("\"mode\":\"end\"", frame);
        Assert.Contains("\"status\":\"NotFound\"", frame);
    }

    [Fact]
    public void Notify_SendsAPowerShellObjectAsItsProperties()
    {
        using var svc = NewService();
        var (_, alice) = svc.Connect("alice@contoso.com");
        var step = new PSObject();
        step.Properties.Add(new PSNoteProperty("Title", "Revoke sessions"));
        step.Properties.Add(new PSNoteProperty("Status", "succeeded"));
        var row = new PSObject();
        row.Properties.Add(new PSNoteProperty("Name", "pat@contoso.com"));
        row.Properties.Add(new PSNoteProperty("Steps", new object[] { step }));
        svc.Watch("alice@contoso.com", Job, trackRun: false);

        svc.Notify(Job, "update", row);

        Assert.Contains("\"data\":{\"Name\":\"pat@contoso.com\",\"Steps\":[{\"Title\":\"Revoke sessions\",\"Status\":\"succeeded\"}]}",
            Assert.Single(Drain(alice!)));
    }

    [Fact]
    public void Reconnect_ReplaysTheFinalFrame()
    {
        using var svc = NewService();
        svc.Watch("alice@contoso.com", Job, trackRun: false);
        svc.Notify(Job, "end", new Dictionary<string, object?> { ["Status"] = "Completed" });

        var replay = Assert.Single(svc.CurrentFrames("alice@contoso.com"));
        Assert.Contains("\"mode\":\"end\"", replay);
    }

    private static DefaultHttpContext Request(string name, string principalJson)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["x-ms-client-principal-name"] = name;
        ctx.Request.Headers["x-ms-client-principal"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(principalJson));
        return ctx;
    }

    [Fact]
    public void ResolveSignedInUser_RequiresARealRole()
    {
        Assert.Equal("alice@contoso.com",
            RealtimeEndpoint.ResolveSignedInUser(Request("alice@contoso.com", "{\"userRoles\":[\"anonymous\",\"authenticated\"]}")));
        // App-only API clients are normalised with no roles.
        Assert.Null(RealtimeEndpoint.ResolveSignedInUser(Request("00000000-0000-0000-0000-000000000001", "{\"userRoles\":[]}")));
        Assert.Null(RealtimeEndpoint.ResolveSignedInUser(Request("alice@contoso.com", "{\"userRoles\":[\"anonymous\"]}")));
        Assert.Null(RealtimeEndpoint.ResolveSignedInUser(Request("alice@contoso.com", "{\"claims\":[]}")));
        Assert.Null(RealtimeEndpoint.ResolveSignedInUser(new DefaultHttpContext()));
    }
}
