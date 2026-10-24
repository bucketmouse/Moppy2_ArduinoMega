using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moppy.Core;

/// <summary>
/// Logging entry point for the Moppy library.  The library logs nowhere by default; assign
/// <see cref="LoggerFactory"/> to route its output somewhere useful.
/// </summary>
public static class MoppyLogging
{
    private static ILoggerFactory loggerFactory = NullLoggerFactory.Instance;

    public static ILoggerFactory LoggerFactory
    {
        get => loggerFactory;
        set => loggerFactory = value ?? NullLoggerFactory.Instance;
    }

    internal static ILogger CreateLogger<T>() => loggerFactory.CreateLogger<T>();

    internal static ILogger CreateLogger(Type type) => loggerFactory.CreateLogger(type);
}
