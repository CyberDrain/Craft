using System.Text.Json;

namespace Craft.Hosting;

/// <summary>An orchestrator fan-out requested by a PowerShell HTTP handler's response.</summary>
public sealed record OrchestratorTrigger(string Command, string PlannerScript, string TaskScript, int Priority);

/// <summary>A single background script requested by a PowerShell HTTP handler's response.</summary>
public sealed record ScriptTrigger(string Command, int Priority);

/// <summary>Cancellation of a running orchestration requested by a handler's response.</summary>
public sealed record CancelTrigger(string Command);

/// <summary>
/// Out-of-band instructions a PowerShell HTTP handler can embed in its JSON response body.
/// <para>
/// A handler runs on an HTTP runspace and must return promptly, so it cannot itself wait on a
/// long-running fan-out. Instead it returns a normal response carrying a marker property, and the host
/// picks the work up after the response is written. The markers are <c>_orchestratorTrigger</c>,
/// <c>_scriptTrigger</c> and <c>_cancelTrigger</c>.
/// </para>
/// </summary>
public static class ResponseTriggers
{
    /// <summary>Marker for an orchestrator fan-out.</summary>
    public const string OrchestratorMarker = "_orchestratorTrigger";

    /// <summary>Marker for a single background script.</summary>
    public const string ScriptMarker = "_scriptTrigger";

    /// <summary>Marker for cancelling a run.</summary>
    public const string CancelMarker = "_cancelTrigger";

    /// <summary>
    /// Cheap pre-filter: whether <paramref name="body"/> is worth parsing as JSON at all.
    /// </summary>
    /// <remarks>
    /// Almost every response carries no trigger, and this runs on the hot path for every API call —
    /// a substring scan is far cheaper than parsing a potentially large JSON document to discover
    /// there was nothing to find.
    /// </remarks>
    public static bool MayContain(string? body, string marker) =>
        !string.IsNullOrEmpty(body) && body.Contains(marker, StringComparison.Ordinal);

    /// <summary>
    /// Parses an orchestrator trigger, or returns <see langword="null"/> when the marker is absent or
    /// explicitly false.
    /// </summary>
    /// <exception cref="JsonException">The body is not valid JSON.</exception>
    /// <exception cref="KeyNotFoundException">A required property is missing.</exception>
    public static OrchestratorTrigger? ParseOrchestrator(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!IsTriggerSet(root, OrchestratorMarker)) return null;

        return new OrchestratorTrigger(
            Command: RequiredString(root, "command"),
            PlannerScript: RequiredString(root, "plannerScript"),
            TaskScript: RequiredString(root, "taskScript"),
            Priority: OptionalInt(root, "priority", defaultValue: 2));
    }

    /// <inheritdoc cref="ParseOrchestrator"/>
    public static ScriptTrigger? ParseScript(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!IsTriggerSet(root, ScriptMarker)) return null;

        return new ScriptTrigger(
            Command: RequiredString(root, "command"),
            // Background scripts default to a lower priority than orchestrations.
            Priority: OptionalInt(root, "priority", defaultValue: 5));
    }

    /// <inheritdoc cref="ParseOrchestrator"/>
    public static CancelTrigger? ParseCancel(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!IsTriggerSet(root, CancelMarker)) return null;

        return new CancelTrigger(RequiredString(root, "command"));
    }

    private static bool IsTriggerSet(JsonElement root, string marker) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(marker, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.GetString() is not { } text)
            throw new KeyNotFoundException($"Trigger is missing the required '{name}' property.");
        return text;
    }

    private static int OptionalInt(JsonElement root, string name, int defaultValue) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : defaultValue;
}
