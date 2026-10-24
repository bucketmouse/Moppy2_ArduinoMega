using Microsoft.Extensions.Logging;

namespace Moppy.Control;

/// <summary>
/// Writes log messages to a file, keeping up to <see cref="MaxFiles"/> files of <see cref="MaxFileSize"/> bytes.
/// </summary>
internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private const long MaxFileSize = 5_000_000;
    private const int MaxFiles = 3;

    private readonly object writeLock = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string message)
    {
        lock (writeLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileSize)
                {
                    for (int i = MaxFiles - 1; i >= 1; i--)
                    {
                        string from = i == 1 ? path : $"{path}.{i - 1}";
                        if (File.Exists(from))
                        {
                            File.Move(from, $"{path}.{i}", overwrite: true);
                        }
                    }
                }

                File.AppendAllText(path, message);
            }
            catch (IOException)
            {
                // Nowhere else to report it
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {logLevel,-11} {category}: {formatter(state, exception)}{Environment.NewLine}";
            if (exception is not null)
            {
                text += exception + Environment.NewLine;
            }

            provider.Write(text);
        }
    }
}
