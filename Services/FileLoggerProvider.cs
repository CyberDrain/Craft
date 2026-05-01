using Microsoft.Extensions.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly bool _verbose;

    public FileLoggerProvider(StreamWriter writer, bool verbose = false)
    {
        _writer = writer;
        _verbose = verbose;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_writer, categoryName, _verbose);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger : ILogger
    {
        private readonly StreamWriter _writer;
        private readonly string _category;
        private readonly bool _verbose;

        // Categories to suppress entirely unless verbose
        private static readonly HashSet<string> s_suppressedPrefixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.AspNetCore",
            "Microsoft.Hosting",
            "Microsoft.Extensions.Hosting"
        };

        public FileLogger(StreamWriter writer, string category, bool verbose)
        {
            _writer = writer;
            _category = category;
            _verbose = verbose;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (_verbose) return logLevel >= LogLevel.Debug;
            // In non-verbose, suppress Debug entirely and suppress noisy ASP.NET categories
            if (logLevel <= LogLevel.Debug) return false;
            foreach (var prefix in s_suppressedPrefixes)
                if (_category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var level = logLevel switch
            {
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???"
            };
            var message = formatter(state, exception);
            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{level}] {message}";

            try
            {
                lock (_writer)
                {
                    _writer.WriteLine(line);
                    if (exception != null)
                        _writer.WriteLine($"  {exception.GetType().Name}: {exception.Message}");
                }
            }
            catch { }
        }
    }
}
