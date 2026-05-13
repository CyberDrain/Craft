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
public static class LogBridge
{
    private static FileLoggerProvider? s_provider;

    public static void Initialize(FileLoggerProvider provider) => s_provider = provider;

    /// <summary>Get the path to the currently active log file.</summary>
    public static string GetCurrentLogPath() => s_provider?.CurrentFilePath ?? "";

    /// <summary>Get the log directory path.</summary>
    public static string GetLogDirectory() => s_provider?.LogDirectory ?? "";

    /// <summary>Get metadata about all log files in the log directory.</summary>
    public static LogFileInfo[] GetLogFiles()
    {
        var dir = s_provider?.LogDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return Array.Empty<LogFileInfo>();

        var prefix = s_provider!.FilePrefix;
        var currentPath = s_provider.CurrentFilePath;

        return Directory.GetFiles(dir, $"{prefix}*.log")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name)
            .Select(f => new LogFileInfo
            {
                Name = f.Name,
                FullPath = f.FullName,
                SizeBytes = f.Length,
                SizeFormatted = FormatSize(f.Length),
                LastModifiedUtc = f.LastWriteTimeUtc,
                IsCurrent = string.Equals(f.FullName, currentPath, StringComparison.OrdinalIgnoreCase)
            })
            .ToArray();
    }

    /// <summary>
    /// Read log entries with optional filtering.
    /// Continuation lines (exception details starting with whitespace) are kept with their parent entry.
    /// </summary>
    /// <param name="tail">Number of matching lines to return from end (0 = all).</param>
    /// <param name="level">Filter by log level: DBG, INF, WRN, ERR, CRT.</param>
    /// <param name="search">Case-insensitive text search within log messages.</param>
    /// <param name="file">Specific log file name (e.g. "craft.1.log"). Null = current.</param>
    /// <param name="from">Include only entries at or after this UTC time. Null = no lower bound.</param>
    /// <param name="to">Include only entries at or before this UTC time. Null = no upper bound.</param>
    public static string[] ReadLog(int tail = 0, string? level = null, string? search = null,
        string? file = null, DateTime? from = null, DateTime? to = null)
    {
        var filePath = ResolveLogFilePath(file);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return Array.Empty<string>();

        string[] allLines;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            allLines = reader.ReadToEnd().Split('\n');
        }
        catch
        {
            return Array.Empty<string>();
        }

        var hasFilter = !string.IsNullOrEmpty(level) || !string.IsNullOrEmpty(search)
            || from.HasValue || to.HasValue;

        if (!hasFilter && tail <= 0)
            return allLines;

        if (!hasFilter)
        {
            var start = Math.Max(0, allLines.Length - tail);
            return allLines[start..];
        }

        // Apply filters, keeping continuation lines (exception details start with whitespace)
        var filtered = new List<string>();
        bool lastMainLineMatched = false;

        foreach (var line in allLines)
        {
            if (string.IsNullOrEmpty(line))
            {
                if (lastMainLineMatched) filtered.Add(line);
                continue;
            }

            // Continuation line (exception details, indented output)
            if (char.IsWhiteSpace(line[0]))
            {
                if (lastMainLineMatched) filtered.Add(line);
                continue;
            }

            // Main log line — check filters
            lastMainLineMatched = MatchesFilters(line, level, search, from, to);
            if (lastMainLineMatched) filtered.Add(line);
        }

        if (tail > 0 && filtered.Count > tail)
            return filtered.GetRange(filtered.Count - tail, tail).ToArray();

        return filtered.ToArray();
    }

    /// <summary>Search the current log for lines containing the specified text.</summary>
    public static string[] SearchLog(string searchText, int tail = 0)
        => ReadLog(tail, null, searchText, null);

    /// <summary>Get error-level entries from the current log.</summary>
    public static string[] GetErrors(int tail = 0)
        => ReadLog(tail, "ERR", null, null);

    /// <summary>Get warning-level entries from the current log.</summary>
    public static string[] GetWarnings(int tail = 0)
        => ReadLog(tail, "WRN", null, null);

    /// <summary>
    /// Get log entries within a UTC date/time range, optionally filtered by level and search text.
    /// PS: [Craft.Services.LogBridge]::GetLogsBetween([DateTime]'2026-05-13 08:00', [DateTime]'2026-05-13 12:00')
    /// </summary>
    public static string[] GetLogsBetween(DateTime from, DateTime to, string? level = null, string? search = null, string? file = null)
        => ReadLog(0, level, search, file, from, to);

    /// <summary>
    /// Get log entries from a UTC start time to now.
    /// PS: [Craft.Services.LogBridge]::GetLogsSince([DateTime]::UtcNow.AddHours(-1))
    /// </summary>
    public static string[] GetLogsSince(DateTime from, string? level = null, string? search = null, string? file = null)
        => ReadLog(0, level, search, file, from, null);

    /// <summary>
    /// Get log entries from the last N minutes.
    /// PS: [Craft.Services.LogBridge]::GetRecentLogs(30)        # last 30 minutes
    ///     [Craft.Services.LogBridge]::GetRecentLogs(30, 'ERR') # errors in last 30 min
    /// </summary>
    public static string[] GetRecentLogs(int minutes, string? level = null, string? search = null, string? file = null)
        => ReadLog(0, level, search, file, DateTime.UtcNow.AddMinutes(-minutes), null);

    /// <summary>
    /// Search across ALL log files (current + rotated) for matching entries.
    /// Returns results ordered oldest-to-newest. Useful for investigating issues across rotations.
    /// PS: [Craft.Services.LogBridge]::SearchAllFiles('timeout', 'ERR')
    /// </summary>
    public static string[] SearchAllFiles(string? search = null, string? level = null,
        DateTime? from = null, DateTime? to = null, int tail = 0)
    {
        var files = GetLogFiles();
        if (files.Length == 0) return Array.Empty<string>();

        // Read rotated files in order (highest number = oldest first), then current
        var allResults = new List<string>();
        var orderedFiles = files.OrderByDescending(f => f.IsCurrent ? 0 : 1)
            .ThenByDescending(f => f.Name)
            .Reverse();

        foreach (var logFile in orderedFiles)
        {
            var lines = ReadLog(0, level, search, logFile.Name, from, to);
            allResults.AddRange(lines);
        }

        if (tail > 0 && allResults.Count > tail)
            return allResults.GetRange(allResults.Count - tail, tail).ToArray();

        return allResults.ToArray();
    }

    /// <summary>Manually trigger log rotation on the current file.</summary>
    public static void ForceRotation()
        => s_provider?.ForceRotate();

    /// <summary>
    /// Delete rotated log files older than the specified number of days.
    /// The current active log file is never deleted.
    /// </summary>
    /// <returns>Number of files deleted.</returns>
    public static int PurgeOldFiles(int olderThanDays = 7)
    {
        var dir = s_provider?.LogDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return 0;

        var prefix = s_provider!.FilePrefix;
        var currentPath = s_provider.CurrentFilePath;
        var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
        var deleted = 0;

        foreach (var filePath in Directory.GetFiles(dir, $"{prefix}*.log"))
        {
            if (string.Equals(filePath, currentPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var info = new FileInfo(filePath);
            if (info.LastWriteTimeUtc < cutoff)
            {
                try { info.Delete(); deleted++; }
                catch { /* file in use, skip */ }
            }
        }

        return deleted;
    }

    // ── Private helpers ───────────────────────────────────────────────

    private static string? ResolveLogFilePath(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return s_provider?.CurrentFilePath;

        var dir = s_provider?.LogDirectory;
        if (string.IsNullOrEmpty(dir))
            return null;

        // Sanitize: only allow filenames, prevent path traversal
        var sanitized = Path.GetFileName(fileName);
        var fullPath = Path.GetFullPath(Path.Combine(dir, sanitized));

        return fullPath.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    private static bool MatchesFilters(string line, string? level, string? search,
        DateTime? from = null, DateTime? to = null)
    {
        // Time range filter — parse timestamp from start of line
        if (from.HasValue || to.HasValue)
        {
            var ts = ParseTimestamp(line);
            if (ts.HasValue)
            {
                if (from.HasValue && ts.Value < from.Value) return false;
                if (to.HasValue && ts.Value > to.Value) return false;
            }
            else
            {
                // Line without parseable timestamp — exclude from time-filtered results
                return false;
            }
        }

        if (!string.IsNullOrEmpty(level))
        {
            var bracketStart = line.IndexOf('[');
            if (bracketStart < 0) return false;
            var bracketEnd = line.IndexOf(']', bracketStart);
            if (bracketEnd < 0) return false;
            var lineLevel = line.AsSpan(bracketStart + 1, bracketEnd - bracketStart - 1);
            if (!lineLevel.Equals(level.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(search))
        {
            if (!line.Contains(search, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Parse the UTC timestamp from the beginning of a log line.
    /// Supports the default format "yyyy-MM-dd HH:mm:ss.fff" and the legacy "HH:mm:ss.fff".
    /// </summary>
    private static DateTime? ParseTimestamp(string line)
    {
        if (line.Length < 12) return null;

        // Try full datetime format: "2026-05-13 10:30:00.000 [...]"
        if (line.Length >= 23 && line[4] == '-' && line[7] == '-' && line[10] == ' ')
        {
            if (DateTime.TryParseExact(line.AsSpan(0, 23), "yyyy-MM-dd HH:mm:ss.fff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var fullDt))
                return fullDt;
        }

        // Try time-only format (legacy): "10:30:00.000 [...]"
        if (line.Length >= 12 && line[2] == ':' && line[5] == ':')
        {
            if (DateTime.TryParseExact(line.AsSpan(0, 12), "HH:mm:ss.fff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var timeDt))
                return timeDt;
        }

        return null;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}

/// <summary>Metadata about a log file on disk.</summary>
public class LogFileInfo
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string SizeFormatted { get; set; } = "";
    public DateTime LastModifiedUtc { get; set; }
    public bool IsCurrent { get; set; }
}
