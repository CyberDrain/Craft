using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Craft.Services;

/// <summary>
/// File-backed logger with size-based rotation. Logs are written to a configurable
/// directory with automatic rotation when files exceed the size limit.
///
/// File naming: {prefix}.log (current), {prefix}.1.log (previous), {prefix}.2.log, etc.
/// Older rotated files have higher numbers. Files beyond MaxFileCount are deleted.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _lock = new();
    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly long _maxFileBytes;
    private readonly int _maxFileCount;
    private readonly bool _verbose;
    private readonly string _timestampFormat;
    private readonly bool _includeCategory;

    private StreamWriter? _writer;
    private long _currentFileSize;

    public FileLoggerProvider(FileLoggingSettings settings, bool verbose = false)
    {
        _directory = settings.ResolvedDirectory;
        _filePrefix = settings.FilePrefix;
        _maxFileBytes = settings.MaxFileSizeMB * 1024L * 1024L;
        _maxFileCount = settings.MaxFileCount;
        _verbose = verbose;
        _timestampFormat = settings.TimestampFormat;
        _includeCategory = settings.IncludeCategory;

        Directory.CreateDirectory(_directory);
        OpenCurrentFile();
    }

    /// <summary>Path to the currently active log file.</summary>
    public string CurrentFilePath => Path.Combine(_directory, $"{_filePrefix}.log");

    /// <summary>Log directory path.</summary>
    public string LogDirectory => _directory;

    /// <summary>File name prefix used for log files.</summary>
    public string FilePrefix => _filePrefix;

    public ILogger CreateLogger(string categoryName) => new RotatingFileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }

    /// <summary>Force an immediate log rotation regardless of file size.</summary>
    internal void ForceRotate()
    {
        lock (_lock) { Rotate(); }
    }

    internal void WriteLine(string line, string? exceptionLine)
    {
        lock (_lock)
        {
            if (_writer == null) return;

            try
            {
                _writer.WriteLine(line);
                _currentFileSize += line.Length + Environment.NewLine.Length;

                if (exceptionLine != null)
                {
                    _writer.WriteLine(exceptionLine);
                    _currentFileSize += exceptionLine.Length + Environment.NewLine.Length;
                }

                if (_currentFileSize >= _maxFileBytes)
                    Rotate();
            }
            catch { /* don't let logging failures crash the app */ }
        }
    }

    private void OpenCurrentFile()
    {
        var path = CurrentFilePath;
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _currentFileSize = stream.Length;
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;

        try
        {
            // Delete the oldest file beyond the retention limit
            var oldest = Path.Combine(_directory, $"{_filePrefix}.{_maxFileCount}.log");
            if (File.Exists(oldest)) File.Delete(oldest);

            // Shift numbered files up: N-1 → N, N-2 → N-1, ..., 1 → 2
            for (int i = _maxFileCount - 1; i >= 1; i--)
            {
                var src = Path.Combine(_directory, $"{_filePrefix}.{i}.log");
                var dst = Path.Combine(_directory, $"{_filePrefix}.{i + 1}.log");
                if (File.Exists(src))
                    File.Move(src, dst, overwrite: true);
            }

            // Current → .1
            var currentPath = CurrentFilePath;
            var firstRotated = Path.Combine(_directory, $"{_filePrefix}.1.log");
            if (File.Exists(currentPath))
                File.Move(currentPath, firstRotated, overwrite: true);
        }
        catch
        {
            // Rotation failed (file locked, etc.) — continue writing to current file
        }

        OpenCurrentFile();
    }

    // ── Inner logger class ────────────────────────────────────────────

    private sealed class RotatingFileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        private static readonly HashSet<string> s_suppressedPrefixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.AspNetCore",
            "Microsoft.Hosting",
            "Microsoft.Extensions.Hosting"
        };

        public RotatingFileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (_provider._verbose) return logLevel >= LogLevel.Debug;
            // In non-verbose, suppress Debug entirely and suppress noisy ASP.NET categories
            if (logLevel <= LogLevel.Debug) return false;
            foreach (var prefix in s_suppressedPrefixes)
                if (_category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???"
            };

            var message = formatter(state, exception);
            var timestamp = DateTime.UtcNow.ToString(_provider._timestampFormat, CultureInfo.InvariantCulture);

            var line = _provider._includeCategory
                ? $"{timestamp} [{level}] [{_category}] {message}"
                : $"{timestamp} [{level}] {message}";

            var exLine = exception != null
                ? $"  {exception.GetType().Name}: {exception.Message}"
                : null;

            _provider.WriteLine(line, exLine);
        }
    }
}
