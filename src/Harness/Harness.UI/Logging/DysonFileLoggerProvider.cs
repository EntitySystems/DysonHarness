using System.Globalization;
using DysonHarness;
using Microsoft.Extensions.Logging;

namespace Harness.UI.Logging;

/// <summary>
/// Error+ file logger that appends to a <see cref="DysonLineCappedLogFile"/>.
/// </summary>
internal sealed class DysonFileLoggerProvider : ILoggerProvider
{
    private readonly DysonLineCappedLogFile _file;

    public DysonFileLoggerProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _file = new DysonLineCappedLogFile(path);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _file);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly string _categoryName;
        private readonly DysonLineCappedLogFile _file;

        public Logger(string categoryName, DysonLineCappedLogFile file)
        {
            _categoryName = categoryName;
            _file = file;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel is LogLevel.Error or LogLevel.Critical;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            try
            {
                if (!IsEnabled(logLevel) || formatter is null)
                    return;

                var message = formatter(state, exception);
                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                var text = $"{timestamp} [{logLevel}] {_categoryName}: {message}\n";
                if (exception is not null)
                    text += exception + "\n";

                _file.Append(text);
            }
            catch
            {
                // ILogger.Log must not throw
            }
        }
    }
}
