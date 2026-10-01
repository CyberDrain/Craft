using Microsoft.Extensions.Logging.Console;

namespace Craft.Hosting;

/// <summary>
/// Puts the console sink behind <see cref="LogRedactor"/>, leaving its output format unchanged.
/// </summary>
[ProviderAlias("Console")]
public sealed class RedactingConsoleLoggerProvider(ConsoleLoggerProvider inner) : ILoggerProvider, ISupportExternalScope
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => inner.SetScopeProvider(scopeProvider);

    // The container owns and disposes the inner provider.
    public void Dispose() { }

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!inner.IsEnabled(logLevel)) return;
            inner.Log(logLevel, eventId, LogRedactor.Redact(formatter(state, exception)),
                exception is null ? null : new RedactedException(exception), static (message, _) => message);
        }
    }

    private sealed class RedactedException(Exception original) : Exception(LogRedactor.Redact(original.Message))
    {
        public override string ToString() => LogRedactor.Redact(original.ToString());
    }
}
