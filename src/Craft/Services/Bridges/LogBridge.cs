using Craft.Hosting;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static bridge exposing file log access to PowerShell and HTTP endpoints.
/// Provides filtered log reading, file listing, and log management.
///
/// PS usage:
///   $lines = [Craft.Services.LogBridge]::ReadLog()                                  # all from current log
///   $lines = [Craft.Services.LogBridge]::ReadLog(100)                               # last 100 lines
///   $lines = [Craft.Services.LogBridge]::ReadLog(100, 'ERR')                        # last 100 error lines
///   $lines = [Craft.Services.LogBridge]::ReadLog(100, 'ERR', 'timeout')             # errors containing "timeout"
///   $lines = [Craft.Services.LogBridge]::ReadLog(0, $null, $null, 'craft.1.log')    # from rotated file
///   $files = [Craft.Services.LogBridge]::GetLogFiles()                              # list all log files
///   $path  = [Craft.Services.LogBridge]::GetCurrentLogPath()                        # active log path
///   $dir   = [Craft.Services.LogBridge]::GetLogDirectory()                          # log directory path
///   $lines = [Craft.Services.LogBridge]::SearchLog('timeout')                       # search current log
///   $lines = [Craft.Services.LogBridge]::GetErrors(50)                              # last 50 errors
///   $lines = [Craft.Services.LogBridge]::GetLogsBetween($from, $to)                 # date range
///   $lines = [Craft.Services.LogBridge]::GetLogsSince([DateTime]::UtcNow.AddHours(-1))  # last hour
///   $lines = [Craft.Services.LogBridge]::GetLogsBetween($from, $to, 'ERR')          # errors in range
///   [Craft.Services.LogBridge]::ForceRotation()                                     # manually rotate now
///   $count = [Craft.Services.LogBridge]::PurgeOldFiles(7)                           # delete files older than 7 days
/// </summary>
/// <remarks>
/// Uninitialized policy: read/query APIs soft no-op (empty path/array);
/// mutating APIs (<see cref="ForceRotation"/>, <see cref="PurgeOldFiles"/>) throw.
/// </remarks>
public static class LogBridge
{
    private static LogQueryService? s_service;

    public static void Initialize(LogQueryService service) => s_service = service;

    /// <summary>Get the path to the currently active log file.</summary>
    public static string GetCurrentLogPath() => s_service?.GetCurrentLogPath() ?? "";

    /// <summary>Get the log directory path.</summary>
    public static string GetLogDirectory() => s_service?.GetLogDirectory() ?? "";

    /// <summary>Get metadata about all log files in the log directory.</summary>
    public static LogFileInfo[] GetLogFiles() =>
        s_service?.GetLogFiles() ?? Array.Empty<LogFileInfo>();

    /// <summary>
    /// Read log entries with optional filtering.
    /// Continuation lines (exception details starting with whitespace) are kept with their parent entry.
    /// </summary>
    /// <param name="tail">Number of matching lines to return from end (0 = all).</param>
    /// <param name="level">Filter by log level(s): "ERR" or "ERR,CRT" (comma-separated).</param>
    /// <param name="search">Case-insensitive text search within log messages.</param>
    /// <param name="file">Specific log file name (e.g. "craft.1.log"). Null = current.</param>
    /// <param name="from">Include only entries at or after this UTC time. Null = no lower bound.</param>
    /// <param name="to">Include only entries at or before this UTC time. Null = no upper bound.</param>
    /// <param name="exclude">Case-insensitive text to exclude from results.</param>
    /// <param name="regexPattern">Regex pattern to match against the message portion of the line.</param>
    /// <param name="sortNewestFirst">When true, return results newest-first (default: false, oldest-first).</param>
    public static string[] ReadLog(int tail = 0, string? level = null, string? search = null,
        string? file = null, DateTime? from = null, DateTime? to = null,
        string? exclude = null, string? regexPattern = null, bool sortNewestFirst = false) =>
        s_service?.ReadLog(tail, level, search, file, from, to, exclude, regexPattern, sortNewestFirst)
            ?? Array.Empty<string>();

    /// <summary>Search the current log for lines containing the specified text.</summary>
    public static string[] SearchLog(string searchText, int tail = 0) =>
        s_service?.SearchLog(searchText, tail) ?? Array.Empty<string>();

    /// <summary>Get error-level entries from the current log.</summary>
    public static string[] GetErrors(int tail = 0) =>
        s_service?.GetErrors(tail) ?? Array.Empty<string>();

    /// <summary>Get warning-level entries from the current log.</summary>
    public static string[] GetWarnings(int tail = 0) =>
        s_service?.GetWarnings(tail) ?? Array.Empty<string>();

    /// <summary>
    /// Get log entries within a UTC date/time range, optionally filtered by level and search text.
    /// PS: [Craft.Services.LogBridge]::GetLogsBetween([DateTime]'2026-05-13 08:00', [DateTime]'2026-05-13 12:00')
    /// </summary>
    public static string[] GetLogsBetween(DateTime from, DateTime to, string? level = null, string? search = null, string? file = null) =>
        s_service?.GetLogsBetween(from, to, level, search, file) ?? Array.Empty<string>();

    /// <summary>
    /// Get log entries from a UTC start time to now.
    /// PS: [Craft.Services.LogBridge]::GetLogsSince([DateTime]::UtcNow.AddHours(-1))
    /// </summary>
    public static string[] GetLogsSince(DateTime from, string? level = null, string? search = null, string? file = null) =>
        s_service?.GetLogsSince(from, level, search, file) ?? Array.Empty<string>();

    /// <summary>
    /// Get log entries from the last N minutes.
    /// PS: [Craft.Services.LogBridge]::GetRecentLogs(30)        # last 30 minutes
    ///     [Craft.Services.LogBridge]::GetRecentLogs(30, 'ERR') # errors in last 30 min
    /// </summary>
    public static string[] GetRecentLogs(int minutes, string? level = null, string? search = null, string? file = null) =>
        s_service?.GetRecentLogs(minutes, level, search, file) ?? Array.Empty<string>();

    /// <summary>
    /// Search across ALL log files (current + rotated) for matching entries.
    /// Returns results ordered oldest-to-newest. Useful for investigating issues across rotations.
    /// PS: [Craft.Services.LogBridge]::SearchAllFiles('timeout', 'ERR')
    /// </summary>
    public static string[] SearchAllFiles(string? search = null, string? level = null,
        DateTime? from = null, DateTime? to = null, int tail = 0,
        string? exclude = null, string? regexPattern = null, bool sortNewestFirst = false) =>
        s_service?.SearchAllFiles(search, level, from, to, tail, exclude, regexPattern, sortNewestFirst)
            ?? Array.Empty<string>();

    /// <summary>Manually trigger log rotation on the current file.</summary>
    public static void ForceRotation()
    {
        var service = s_service ?? throw new InvalidOperationException("LogBridge not initialized");
        service.ForceRotation();
    }

    /// <summary>
    /// Delete rotated log files older than the specified number of days.
    /// The current active log file is never deleted.
    /// </summary>
    /// <returns>Number of files deleted.</returns>
    public static int PurgeOldFiles(int olderThanDays = 7)
    {
        var service = s_service ?? throw new InvalidOperationException("LogBridge not initialized");
        return service.PurgeOldFiles(olderThanDays);
    }
}
