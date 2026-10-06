namespace Glacier.Sql.Diagnostics;

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using static System.Console;

/// <summary>
/// Defines logging severity levels for Glacier.Sql diagnostics.
/// </summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
    None = 6
}

/// <summary>
/// Pluggable logging abstraction for Glacier.Sql diagnostics.
/// Hosts (CLI, tests, servers) implement this to control or redirect diagnostic output.
/// </summary>
public interface IGlacierLogger
{
    /// <summary>
    /// Checks if the given log level is enabled.
    /// </summary>
    bool IsEnabled(LogLevel level);

    /// <summary>
    /// Writes a diagnostic log entry.
    /// </summary>
    void Log(LogLevel level, string message, Exception? exception = null);
}

/// <summary>
/// Silent no-op logger instance that discards all diagnostic entries.
/// </summary>
public sealed class NullGlacierLogger : IGlacierLogger
{
    public static NullGlacierLogger Instance { get; } = new();

    private NullGlacierLogger() { }

    public bool IsEnabled(LogLevel level) => false;

    public void Log(LogLevel level, string message, Exception? exception = null) { }
}

/// <summary>
/// Formats and routes diagnostic messages to console standard output and error streams.
/// </summary>
public sealed class ConsoleGlacierLogger : IGlacierLogger
{
    private readonly LogLevel _minLevel;
    private readonly bool _useColors;
    private static readonly Lock _syncLock = new();

    public ConsoleGlacierLogger(LogLevel minLevel = LogLevel.Information, bool useColors = true)
    {
        _minLevel = minLevel;
        _useColors = useColors;
    }

    public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minLevel;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
            return;

        string prefix = level switch
        {
            LogLevel.Trace => "[TRACE]",
            LogLevel.Debug => "[DEBUG]",
            LogLevel.Information => "[INFO]",
            LogLevel.Warning => "[WARN]",
            LogLevel.Error => "[ERROR]",
            LogLevel.Critical => "[CRIT]",
            _ => "[LOG]"
        };

        lock (_syncLock)
        {
            TextWriter writer = level >= LogLevel.Warning ? Error : Out;

            if (_useColors)
            {
                var prevColor = ForegroundColor;
                try
                {
                    ForegroundColor = level switch
                    {
                        LogLevel.Trace => ConsoleColor.DarkGray,
                        LogLevel.Debug => ConsoleColor.Gray,
                        LogLevel.Information => ConsoleColor.Cyan,
                        LogLevel.Warning => ConsoleColor.Yellow,
                        LogLevel.Error => ConsoleColor.Red,
                        LogLevel.Critical => ConsoleColor.DarkRed,
                        _ => prevColor
                    };

                    writer.WriteLine($"{prefix} {message}");
                    if (exception != null)
                    {
                        writer.WriteLine(exception.ToString());
                    }
                }
                finally
                {
                    ForegroundColor = prevColor;
                }
            }
            else
            {
                writer.WriteLine($"{prefix} {message}");
                if (exception != null)
                {
                    writer.WriteLine(exception.ToString());
                }
            }
        }
    }
}

/// <summary>
/// Routes diagnostic log messages to a delegate callback.
/// Ideal for unit testing, test output capture, or custom host adapters.
/// </summary>
public sealed class DelegateGlacierLogger : IGlacierLogger
{
    private readonly Action<LogLevel, string, Exception?> _logAction;
    private readonly LogLevel _minLevel;

    public DelegateGlacierLogger(Action<LogLevel, string, Exception?> logAction, LogLevel minLevel = LogLevel.Trace)
    {
        _logAction = logAction ?? throw new ArgumentNullException(nameof(logAction));
        _minLevel = minLevel;
    }

    public DelegateGlacierLogger(Action<string> logAction, LogLevel minLevel = LogLevel.Trace)
        : this((level, message, ex) =>
        {
            if (ex != null)
                logAction($"[{level}] {message}: {ex}");
            else
                logAction($"[{level}] {message}");
        }, minLevel)
    {
    }

    public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minLevel;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (IsEnabled(level))
        {
            _logAction(level, message, exception);
        }
    }
}

/// <summary>
/// Static ambient diagnostic logger for Glacier.Sql.
/// Dispatches log messages to the configured ambient <see cref="IGlacierLogger"/>.
/// </summary>
public static class GlacierDiagnostics
{
    private static volatile IGlacierLogger _logger = NullGlacierLogger.Instance;

    /// <summary>
    /// Gets or sets the ambient logger. Defaults to <see cref="NullGlacierLogger.Instance"/>.
    /// </summary>
    public static IGlacierLogger Logger
    {
        get => _logger;
        set => _logger = value ?? NullGlacierLogger.Instance;
    }

    /// <summary>
    /// Sets the ambient logger. Passing null resets to <see cref="NullGlacierLogger.Instance"/>.
    /// </summary>
    public static void SetLogger(IGlacierLogger? logger) => _logger = logger ?? NullGlacierLogger.Instance;

    /// <summary>
    /// Resets the ambient logger back to the default <see cref="NullGlacierLogger.Instance"/>.
    /// </summary>
    public static void Reset() => _logger = NullGlacierLogger.Instance;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(LogLevel level) => _logger.IsEnabled(level);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (_logger.IsEnabled(level))
            _logger.Log(level, message, exception);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogTrace(string message)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.Log(LogLevel.Trace, message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogDebug(string message)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.Log(LogLevel.Debug, message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogInformation(string message)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.Log(LogLevel.Information, message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(string message, Exception? exception = null)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
            _logger.Log(LogLevel.Warning, message, exception);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(string message, Exception? exception = null)
    {
        if (_logger.IsEnabled(LogLevel.Error))
            _logger.Log(LogLevel.Error, message, exception);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogCritical(string message, Exception? exception = null)
    {
        if (_logger.IsEnabled(LogLevel.Critical))
            _logger.Log(LogLevel.Critical, message, exception);
    }
}

/// <summary>
/// Domain-specific alias for <see cref="GlacierDiagnostics"/> to maintain naming parity
/// with GlacierSqlDiagnostics.
/// </summary>
public static class GlacierSqlDiagnostics
{
    public static IGlacierLogger Logger
    {
        get => GlacierDiagnostics.Logger;
        set => GlacierDiagnostics.Logger = value;
    }

    public static void SetLogger(IGlacierLogger? logger) => GlacierDiagnostics.SetLogger(logger);

    public static void Reset() => GlacierDiagnostics.Reset();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(LogLevel level) => GlacierDiagnostics.IsEnabled(level);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Log(LogLevel level, string message, Exception? exception = null) => GlacierDiagnostics.Log(level, message, exception);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogTrace(string message) => GlacierDiagnostics.LogTrace(message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogDebug(string message) => GlacierDiagnostics.LogDebug(message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogInformation(string message) => GlacierDiagnostics.LogInformation(message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(string message, Exception? exception = null) => GlacierDiagnostics.LogWarning(message, exception);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(string message, Exception? exception = null) => GlacierDiagnostics.LogError(message, exception);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogCritical(string message, Exception? exception = null) => GlacierDiagnostics.LogCritical(message, exception);
}
