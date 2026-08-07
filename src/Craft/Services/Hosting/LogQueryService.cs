using Craft.Services;

namespace Craft.Hosting;

/// <summary>
/// File-log read/filter/purge/list over <see cref="FileLoggerProvider"/>.
/// Domain code and <c>LogBridge</c> both go through this service.
/// </summary>
public sealed class LogQueryService
{
    private readonly FileLoggerProvider _provider;

    public LogQueryService(FileLoggerProvider provider) => _provider = provider;

    /// <summary>Get the path to the currently active log file.</summary>
    public string GetCurrentLogPath() => _provider.CurrentFilePath ?? "";

    /// <summary>Get the log directory path.</summary>
    public string GetLogDirectory() => _provider.LogDirectory ?? "";

    /// <summary>Get metadata about all log files in the log directory.</summary>
    public LogFileInfo[] GetLogFiles()
    {
        var dir = _provider.LogDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return Array.Empty<LogFileInfo>();

        var prefix = _provider.FilePrefix;
        var currentPath = _provider.CurrentFilePath;

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
    /// <param name="level">Filter by log level(s): "ERR" or "ERR,CRT" (comma-separated).</param>
    /// <param name="search">Case-insensitive text search within log messages.</param>
    /// <param name="file">Specific log file name (e.g. "craft.1.log"). Null = current.</param>
    /// <param name="from">Include only entries at or after this UTC time. Null = no lower bound.</param>
    /// <param name="to">Include only entries at or before this UTC time. Null = no upper bound.</param>
    /// <param name="exclude">Case-insensitive text to exclude from results.</param>
    /// <param name="regexPattern">Regex pattern to match against the message portion of the line.</param>
    /// <param name="sortNewestFirst">When true, return results newest-first (default: false, oldest-first).</param>
    public string[] ReadLog(int tail = 0, string? level = null, string? search = null,
        string? file = null, DateTime? from = null, DateTime? to = null,
        string? exclude = null, string? regexPattern = null, bool sortNewestFirst = false)
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

        // Parse multi-level filter once
        string[]? levels = null;
        if (!string.IsNullOrEmpty(level))
            levels = level.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Compile regex once if provided
        System.Text.RegularExpressions.Regex? regex = null;
        if (!string.IsNullOrEmpty(regexPattern))
        {
            try { regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled); }
            catch { /* invalid regex — skip filter */ }
        }

        var hasFilter = levels != null || !string.IsNullOrEmpty(search)
            || from.HasValue || to.HasValue || !string.IsNullOrEmpty(exclude) || regex != null;

        if (!hasFilter && tail <= 0 && !sortNewestFirst)
            return allLines;

        if (!hasFilter && !sortNewestFirst)
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
            lastMainLineMatched = MatchesFilters(line, levels, search, from, to, exclude, regex);
            if (lastMainLineMatched) filtered.Add(line);
        }

        if (sortNewestFirst)
            filtered.Reverse();

        if (tail > 0 && filtered.Count > tail)
        {
            // When sorted newest-first, take from the start; otherwise from the end
            return sortNewestFirst
                ? filtered.GetRange(0, tail).ToArray()
                : filtered.GetRange(filtered.Count - tail, tail).ToArray();
        }

        return filtered.ToArray();
    }

    /// <summary>Search the current log for lines containing the specified text.</summary>
    public string[] SearchLog(string searchText, int tail = 0)
        => ReadLog(tail, null, searchText, null);

    /// <summary>Get error-level entries from the current log.</summary>
    public string[] GetErrors(int tail = 0)
        => ReadLog(tail, "ERR", null, null);

    /// <summary>Get warning-level entries from the current log.</summary>
    public string[] GetWarnings(int tail = 0)
        => ReadLog(tail, "WRN", null, null);

    /// <summary>
    /// Get log entries within a UTC date/time range, optionally filtered by level and search text.
    /// </summary>
    public string[] GetLogsBetween(DateTime from, DateTime to, string? level = null, string? search = null, string? file = null)
        => ReadLog(0, level, search, file, from, to);

    /// <summary>
    /// Get log entries from a UTC start time to now.
    /// </summary>
    public string[] GetLogsSince(DateTime from, string? level = null, string? search = null, string? file = null)
        => ReadLog(0, level, search, file, from, null);

    /// <summary>
    /// Get log entries from the last N minutes.
    /// </summary>
    public string[] GetRecentLogs(int minutes, string? level = null, string? search = null, string? file = null)
        => ReadLog(0, level, search, file, DateTime.UtcNow.AddMinutes(-minutes), null);

    /// <summary>
    /// Search across ALL log files (current + rotated) for matching entries.
    /// Returns results ordered oldest-to-newest. Useful for investigating issues across rotations.
    /// </summary>
    public string[] SearchAllFiles(string? search = null, string? level = null,
        DateTime? from = null, DateTime? to = null, int tail = 0,
        string? exclude = null, string? regexPattern = null, bool sortNewestFirst = false)
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
            var lines = ReadLog(0, level, search, logFile.Name, from, to, exclude, regexPattern);
            allResults.AddRange(lines);
        }

        if (sortNewestFirst)
            allResults.Reverse();

        if (tail > 0 && allResults.Count > tail)
        {
            return sortNewestFirst
                ? allResults.GetRange(0, tail).ToArray()
                : allResults.GetRange(allResults.Count - tail, tail).ToArray();
        }

        return allResults.ToArray();
    }

    /// <summary>Manually trigger log rotation on the current file.</summary>
    public void ForceRotation() => _provider.ForceRotate();

    /// <summary>
    /// Delete rotated log files older than the specified number of days.
    /// The current active log file is never deleted.
    /// </summary>
    /// <returns>Number of files deleted.</returns>
    public int PurgeOldFiles(int olderThanDays = 7)
    {
        var dir = _provider.LogDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return 0;

        var prefix = _provider.FilePrefix;
        var currentPath = _provider.CurrentFilePath;
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

    private string? ResolveLogFilePath(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return _provider.CurrentFilePath;

        var dir = _provider.LogDirectory;
        if (string.IsNullOrEmpty(dir))
            return null;

        // Sanitize: only allow filenames, prevent path traversal
        var sanitized = Path.GetFileName(fileName);
        var fullPath = Path.GetFullPath(Path.Combine(dir, sanitized));

        return fullPath.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    private static bool MatchesFilters(string line, string[]? levels, string? search,
        DateTime? from = null, DateTime? to = null,
        string? exclude = null, System.Text.RegularExpressions.Regex? regex = null)
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

        if (levels != null && levels.Length > 0)
        {
            var bracketStart = line.IndexOf('[');
            if (bracketStart < 0) return false;
            var bracketEnd = line.IndexOf(']', bracketStart);
            if (bracketEnd < 0) return false;
            var lineLevel = line.AsSpan(bracketStart + 1, bracketEnd - bracketStart - 1);
            bool matched = false;
            foreach (var lvl in levels)
            {
                if (lineLevel.Equals(lvl.AsSpan(), StringComparison.OrdinalIgnoreCase))
                { matched = true; break; }
            }
            if (!matched) return false;
        }

        if (!string.IsNullOrEmpty(search))
        {
            if (!line.Contains(search, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(exclude))
        {
            if (line.Contains(exclude, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (regex != null)
        {
            if (!regex.IsMatch(line))
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

        // Try ISO 8601 UTC format: "2026-05-13T10:30:00.000Z [...]"
        if (line.Length >= 24 && line[4] == '-' && line[7] == '-' && line[10] == 'T')
        {
            if (DateTime.TryParseExact(line.AsSpan(0, 24), "yyyy-MM-ddTHH:mm:ss.fffZ",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var fullDt))
                return fullDt;
        }

        // Legacy format: "2026-05-13 10:30:00.000 [...]"
        if (line.Length >= 23 && line[4] == '-' && line[7] == '-' && line[10] == ' ')
        {
            if (DateTime.TryParseExact(line.AsSpan(0, 23), "yyyy-MM-dd HH:mm:ss.fff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var fullDt2))
                return fullDt2;
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
