namespace Craft.Configuration;

public class FileLoggingSettings
{
    /// <summary>
    /// Directory for log files. On Linux defaults to {home}/logs (e.g. /home/app/logs
    /// for the non-root container), on Windows to {BaseDirectory}/logs.
    /// Override via App__FileLogging__Directory env var.
    /// </summary>
    public string Directory { get; set; } = "";

    /// <summary>
    /// Filename prefix for log files. Files are named: {prefix}.log (current),
    /// {prefix}.1.log (previous), {prefix}.2.log, etc.
    /// </summary>
    public string FilePrefix { get; set; } = "craft";

    /// <summary>Maximum size in MB before rotating the current log file.</summary>
    public int MaxFileSizeMB { get; set; } = 25;

    /// <summary>Maximum number of rotated log files to retain. Oldest are deleted first.</summary>
    public int MaxFileCount { get; set; } = 10;

    /// <summary>
    /// Timestamp format for log entries. Must be a valid .NET DateTime format string.
    /// Default includes full date for accurate log filtering.
    /// </summary>
    public string TimestampFormat { get; set; } = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>
    /// Include the logger category name in log output.
    /// When true:  "2026-05-13 10:30:00.000 [INF] [Microsoft.AspNetCore.Routing] Matched endpoint"
    /// When false: "2026-05-13 10:30:00.000 [INF] Matched endpoint"
    /// </summary>
    public bool IncludeCategory { get; set; }

    /// <summary>
    /// Minimum log level for file and console output AND PowerShell stream capture.
    /// Controls which PS preference variables are set to 'Continue' in runspaces:
    ///   - "Error"       → only Write-Error captured
    ///   - "Warning"     → Write-Error, Write-Warning
    ///   - "Information" → (default) Write-Error, Write-Warning, Write-Information/Write-Host
    ///   - "Debug"       → all above + Write-Debug (also suppresses ASP.NET framework noise filtering)
    ///   - "Trace"       → all above + Write-Verbose
    /// Also overridable via CRAFT_LOG_LEVEL environment variable.
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>Resolved directory path, applying platform defaults when Directory is empty.</summary>
    internal string ResolvedDirectory => !string.IsNullOrEmpty(Directory)
        ? Directory
        : OperatingSystem.IsLinux()
            ? Path.Combine(RuntimePaths.Home, "logs")
            : Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>Parse the configured LogLevel string into a .NET LogLevel enum value.</summary>
    internal Microsoft.Extensions.Logging.LogLevel ParsedLogLevel
    {
        get
        {
            // Allow env var override
            var envLevel = Environment.GetEnvironmentVariable("CRAFT_LOG_LEVEL");
            var level = !string.IsNullOrEmpty(envLevel) ? envLevel : LogLevel;
            return Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(level, ignoreCase: true, out var parsed)
                ? parsed
                : Microsoft.Extensions.Logging.LogLevel.Information;
        }
    }
}
