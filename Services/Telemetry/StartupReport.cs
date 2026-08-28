namespace Craft.Telemetry;

// The boot-inventory envelope. Serialized with web defaults (camelCase). The ingest is tolerant, so
// this stays a small, stable core; new report types ride the same surface via reportType.
internal sealed record StartupReport(
    int SchemaVersion,
    string ReportType,
    string ReportId,
    string InstanceId,
    DateTimeOffset SentUtc,
    AppInfo App,
    CraftInfo Craft,
    HostInfo Host,
    SurfaceInfo Surface);

internal sealed record AppInfo(string Id, string? Version, string? Commit, string? ImageTag);

internal sealed record CraftInfo(string? Version);

internal sealed record HostInfo(string? Sku, string[] Roles, string Platform, string? Region);

internal sealed record SurfaceInfo(int RouteCount, int ScheduledTaskCount);
