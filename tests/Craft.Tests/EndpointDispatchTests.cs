using Craft.Configuration;
using Craft.Endpoints;
using Craft.Hosting.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Craft.Tests;

/// <summary>
/// The central handler and the Central/Direct dispatch split.
/// <para>
/// This is authorization plumbing, so the tests bias toward the failure that matters: a route that
/// was supposed to be wrapped by the handler running bare. The inverse mistake — a Direct route
/// accidentally wrapped — merely 401s a webhook, which is loud; an unwrapped Central route serves
/// unauthenticated traffic, which is silent.
/// </para>
/// </summary>
public class EndpointDispatchTests
{
    // ── test doubles ──────────────────────────────────────────────────────────────────────────────

    private sealed class RecordingEndpoint : ICraftEndpoint
    {
        public int Invocations;

        public ValueTask<CraftResult> HandleAsync(CraftRequest r, CancellationToken ct)
        {
            Invocations++;
            return new(CraftResult.RawJson("""{"who":"endpoint"}"""));
        }
    }

    private sealed class PassThroughHandler : ICraftEndpointHandler
    {
        public int Invocations;

        public async ValueTask<CraftResult> HandleAsync(
            CraftRequest request, CraftEndpointNext invokeEndpoint, CancellationToken ct)
        {
            Invocations++;
            return await invokeEndpoint();
        }
    }

    private sealed class BlockingHandler : ICraftEndpointHandler
    {
        public ValueTask<CraftResult> HandleAsync(
            CraftRequest request, CraftEndpointNext invokeEndpoint, CancellationToken ct) =>
            new(CraftResult.Problem(401, "Sign in required."));
    }

    private static CraftRequest Request(EndpointDispatch dispatch) =>
        new(new DefaultHttpContext(), "TestRoute",
            new CraftEndpointAttribute("TestRoute") { Dispatch = dispatch });

    // ── the attribute default ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void DispatchDefaultsToCentral()
    {
        // The safe direction: an endpoint that never thought about dispatch gets the application's
        // authorization, not an accidentally-public route.
        Assert.Equal(EndpointDispatch.Central, new CraftEndpointAttribute("Thing").Dispatch);
    }

    // ── ExecuteAsync semantics ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CentralDispatchRunsThroughTheHandler()
    {
        var endpoint = new RecordingEndpoint();
        var handler = new PassThroughHandler();

        var result = await NativeDispatchEndpoint.ExecuteAsync(
            Request(EndpointDispatch.Central), endpoint, handler, CancellationToken.None);

        Assert.Equal(1, handler.Invocations);
        Assert.Equal(1, endpoint.Invocations);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task HandlerShortCircuitNeverInvokesTheEndpoint()
    {
        // The point of the mechanism: a 401 from the handler means the endpoint code — and whatever
        // side effects it has — never ran.
        var endpoint = new RecordingEndpoint();

        var result = await NativeDispatchEndpoint.ExecuteAsync(
            Request(EndpointDispatch.Central), endpoint, new BlockingHandler(), CancellationToken.None);

        Assert.Equal(0, endpoint.Invocations);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task DirectDispatchBypassesTheHandler()
    {
        // A webhook verifying its own signature must not also need a session: even a registered
        // handler is skipped for Direct routes.
        var endpoint = new RecordingEndpoint();
        var handler = new PassThroughHandler();

        var result = await NativeDispatchEndpoint.ExecuteAsync(
            Request(EndpointDispatch.Direct), endpoint, handler, CancellationToken.None);

        Assert.Equal(0, handler.Invocations);
        Assert.Equal(1, endpoint.Invocations);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task NoHandlerBehavesExactlyAsBefore()
    {
        // Upgrade safety: an existing application that ships no handler must see zero behaviour
        // change, whatever its endpoints' (defaulted) dispatch mode says.
        var endpoint = new RecordingEndpoint();

        var result = await NativeDispatchEndpoint.ExecuteAsync(
            Request(EndpointDispatch.Central), endpoint, handler: null, CancellationToken.None);

        Assert.Equal(1, endpoint.Invocations);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task RequestExposesTheEndpointMetadataToTheHandler()
    {
        // The handler's per-endpoint decisions key off the attribute — Role is the native
        // equivalent of the .ROLE doc tag the PowerShell router reads.
        CraftEndpointAttribute? seen = null;
        var request = new CraftRequest(new DefaultHttpContext(), "Thing",
            new CraftEndpointAttribute("Thing") { Role = "qr.edit" });

        var endpoint = new RecordingEndpoint();
        var handler = new InspectingHandler(r => seen = r.Endpoint);
        await NativeDispatchEndpoint.ExecuteAsync(request, endpoint, handler, CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal("qr.edit", seen.Role);
    }

    private sealed class InspectingHandler : ICraftEndpointHandler
    {
        private readonly Action<CraftRequest> _inspect;
        public InspectingHandler(Action<CraftRequest> inspect) => _inspect = inspect;

        public async ValueTask<CraftResult> HandleAsync(
            CraftRequest request, CraftEndpointNext invokeEndpoint, CancellationToken ct)
        {
            _inspect(request);
            return await invokeEndpoint();
        }
    }
}

/// <summary>
/// Handler discovery: exactly one, or startup refuses.
/// </summary>
public class HandlerDiscoveryTests
{
    private sealed class HandlerA : ICraftEndpointHandler
    {
        public ValueTask<CraftResult> HandleAsync(
            CraftRequest request, CraftEndpointNext invokeEndpoint, CancellationToken ct) =>
            invokeEndpoint();
    }

    private sealed class HandlerB : ICraftEndpointHandler
    {
        public ValueTask<CraftResult> HandleAsync(
            CraftRequest request, CraftEndpointNext invokeEndpoint, CancellationToken ct) =>
            invokeEndpoint();
    }

    [Fact]
    public void NoHandlerIsLegal()
    {
        Assert.Null(NativeEndpointRegistry.SelectHandler([]));
    }

    [Fact]
    public void OneHandlerIsSelected()
    {
        Assert.Equal(typeof(HandlerA), NativeEndpointRegistry.SelectHandler([typeof(HandlerA)]));
    }

    [Fact]
    public void TwoHandlersFailStartupNamingBoth()
    {
        // Which of the two authorized requests would be assembly scan order, and the losing handler
        // would never run with no log line to say so — this must not boot.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NativeEndpointRegistry.SelectHandler([typeof(HandlerA), typeof(HandlerB)]));

        Assert.Contains(nameof(HandlerA), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(HandlerB), ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// <c>App:Endpoints:RequireHandler</c> — the CI gate for applications whose authorization lives in
/// the central handler.
/// </summary>
public class RequireHandlerPolicyTests
{
    private static NativeEndpointDescriptor Endpoint(string route, EndpointDispatch dispatch) =>
        new(route, typeof(object), new CraftEndpointAttribute(route) { Dispatch = dispatch });

    private static void Ensure(bool require, bool handlerPresent, params NativeEndpointDescriptor[] endpoints) =>
        NativeDispatchEndpoint.EnsureHandlerRequirement(
            new EndpointSettings { RequireHandler = require }, handlerPresent, endpoints);

    [Fact]
    public void OffByDefaultNothingIsEnforced()
    {
        Ensure(require: false, handlerPresent: false, Endpoint("Thing", EndpointDispatch.Central));
    }

    [Fact]
    public void SatisfiedWhenTheHandlerExists()
    {
        Ensure(require: true, handlerPresent: true, Endpoint("Thing", EndpointDispatch.Central));
    }

    [Fact]
    public void AllDirectEndpointsNeedNoHandler()
    {
        // Nothing for a handler to protect: an all-Direct app is already exactly what it claims.
        Ensure(require: true, handlerPresent: false, Endpoint("Webhook", EndpointDispatch.Direct));
    }

    [Fact]
    public void MissingHandlerWithCentralEndpointsRefusesToStart()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Ensure(require: true, handlerPresent: false,
                Endpoint("Thing", EndpointDispatch.Central),
                Endpoint("Webhook", EndpointDispatch.Direct)));

        // The message must name the setting (so the reader can find it) and the exposed routes (so
        // the reader knows what would have served unprotected).
        Assert.Contains("RequireHandler", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Thing", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Webhook", ex.Message, StringComparison.Ordinal);
    }
}
