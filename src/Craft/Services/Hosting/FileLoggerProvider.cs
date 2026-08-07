using System.Globalization;
using System.Threading.Channels;
using Craft.Configuration;

namespace Craft.Hosting;

/// <summary>
/// File-backed logger with size-based rotation. Logs are written to a configurable
/// directory with automatic rotation when files exceed the size limit.
///
/// File naming: {prefix}.log (current), {prefix}.1.log (previous), {prefix}.2.log, etc.
/// Older rotated files have higher numbers. Files beyond MaxFileCount are deleted.
///
/// Producer-side <see cref="WriteLine"/> is non-blocking: log entries are pushed onto
/// an unbounded channel and a single background consumer task performs the actual
/// file writes. The consumer flushes once per drain burst, so a 1000-line burst
/// yields a single flush — under sustained load the queue grows briefly while the
/// consumer drains it as fast as the disk allows. Nothing is ever dropped.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _filePrefix;
    private long _maxFileBytes;
    private int _maxFileCount;
    private readonly LogLevel _minLevel;
    private string _timestampFormat;
    private bool _includeCategory;

    private StreamWriter? _writer;
    private long _currentFileSize;

    private readonly Channel<LogItem> _channel;
    private readonly Task _consumerTask;

    private readonly record struct LogItem(string Line, string? ExLine, TaskCompletionSource? RotateSignal);

    /// <summary>
    /// Creates the provider from an early-bound <see cref="FileLoggingSettings"/> so logging works
    /// before <c>Build()</c>. Call <see cref="SyncFrom"/> after Build with
    /// <c>CraftSettings.FileLogging</c> so rotation/format knobs match the Options-bound settings.
    /// Directory and file prefix are fixed at construction (the active log file is already open).
    /// </summary>
    public FileLoggerProvider(FileLoggingSettings settings, LogLevel minLevel = LogLevel.Information)
    {
        _directory = settings.ResolvedDirectory;
        _filePrefix = settings.FilePrefix;
        _maxFileBytes = settings.MaxFileSizeMB * 1024L * 1024L;
        _maxFileCount = settings.MaxFileCount;
        _minLevel = minLevel;
        _timestampFormat = settings.TimestampFormat;
        _includeCategory = settings.IncludeCategory;

        Directory.CreateDirectory(_directory);
        OpenCurrentFile();

        _channel = Channel.CreateUnbounded<LogItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _consumerTask = Task.Run(ConsumeLoopAsync);
    }

    /// <summary>
    /// Refresh rotation/format settings from the Options-bound <see cref="FileLoggingSettings"/>
    /// after <c>Build()</c>. Does not relocate the open log file (directory/prefix stay as constructed).
    /// </summary>
    public void SyncFrom(FileLoggingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _maxFileBytes = settings.MaxFileSizeMB * 1024L * 1024L;
        _maxFileCount = settings.MaxFileCount;
        _timestampFormat = settings.TimestampFormat;
        _includeCategory = settings.IncludeCategory;
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
        try
        {
            _channel.Writer.TryComplete();
            _consumerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* swallow — shutdown must not throw */ }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    /// <summary>
    /// Force an immediate log rotation regardless of file size. The request is
    /// dispatched to the consumer thread; this call blocks up to 5 seconds for it
    /// to complete so callers (admin endpoints) can rely on the rotation being done.
    /// </summary>
    internal void ForceRotate()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new LogItem(string.Empty, null, tcs)))
            return; // channel completed (shutdown) — nothing to do
        try { tcs.Task.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    /// <summary>
    /// Producer-side enqueue. Non-blocking; safe to call from any thread under any
    /// lock. Silently no-ops if the channel has been completed (shutdown).
    /// </summary>
    internal void WriteLine(string line, string? exceptionLine)
    {
        _channel.Writer.TryWrite(new LogItem(line, exceptionLine, null));
    }

    /// <summary>
    /// Single-consumer drain loop. Flushes the writer once per drain burst so a
    /// large logging burst maps to one flush, while a slow trickle still gets
    /// per-message durability — without ever blocking the producers.
    /// </summary>
    private async Task ConsumeLoopAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    if (item.RotateSignal is not null)
                    {
                        try { Rotate(); }
                        finally { item.RotateSignal.TrySetResult(); }
                        continue;
                    }
                    WriteToFile(item.Line, item.ExLine);
                }
                try { _writer?.Flush(); } catch { }
            }
        }
        catch { /* logging failures must never crash the host */ }
        finally { try { _writer?.Flush(); } catch { } }
    }

    private void WriteToFile(string line, string? exceptionLine)
    {
        try
        {
            if (_writer == null) return;
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

    private void OpenCurrentFile()
    {
        var path = CurrentFilePath;
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _currentFileSize = stream.Length;
        // AutoFlush removed: the consumer loop flushes once per drain burst.
        _writer = new StreamWriter(stream);
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
            if (logLevel < _provider._minLevel) return false;
            // Suppress noisy framework categories unless at Debug or lower
            if (_provider._minLevel > LogLevel.Debug)
            {
                foreach (var prefix in s_suppressedPrefixes)
                    if (_category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            }
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
