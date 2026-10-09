using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Logging;

/// <summary>
/// A logger that writes to Jellyfin's log and to ThemeForge's own file.
/// </summary>
/// <typeparam name="T">The category type, exactly as with <see cref="ILogger{T}"/>.</typeparam>
/// <remarks>
/// This is a decorator rather than an <see cref="ILoggerProvider"/> because Jellyfin configures
/// logging with Serilog's <c>AddSerilog()</c> without <c>writeToProviders: true</c>. Under that
/// setup a provider registered by a plugin is never invoked, so the obvious approach would have
/// produced a log file that silently stayed empty. Decorating the injected logger is the only
/// arrangement that actually receives the messages.
/// </remarks>
public interface IThemeForgeLogger<out T> : ILogger<T>;

/// <inheritdoc cref="IThemeForgeLogger{T}" />
/// <typeparam name="T">The category type.</typeparam>
public sealed class ThemeForgeLogger<T> : IThemeForgeLogger<T>
{
    private readonly ILogger<T> _host;
    private readonly IThemeForgeLogSink _file;

    /// <summary>Initializes a new instance of the <see cref="ThemeForgeLogger{T}"/> class.</summary>
    /// <param name="host">The logger Jellyfin provides, which keeps working exactly as before.</param>
    public ThemeForgeLogger(ILogger<T> host)
        : this(host, ThemeForgeLogFile.Shared)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ThemeForgeLogger{T}"/> class.</summary>
    /// <param name="host">The logger Jellyfin provides.</param>
    /// <param name="file">The sink, injectable so tests can supply their own.</param>
    public ThemeForgeLogger(ILogger<T> host, IThemeForgeLogSink file)
    {
        _host = host;
        _file = file;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => _host.BeginScope(state);

    /// <summary>
    /// Reports whether a level is enabled.
    /// </summary>
    /// <remarks>
    /// True when <em>either</em> destination wants the message. Jellyfin's log level is usually
    /// Information, so without this a user who turns ThemeForge's file logging down to Debug to
    /// diagnose a run would never see those lines: the call site would skip them before either
    /// logger was consulted.
    /// </remarks>
    /// <param name="logLevel">The level to test.</param>
    /// <returns><c>true</c> when the message should be produced.</returns>
    public bool IsEnabled(LogLevel logLevel)
    {
        if (_host.IsEnabled(logLevel))
        {
            return true;
        }

        var configuration = Plugin.Config;
        return configuration.EnableFileLogging && logLevel >= configuration.FileLogLevel && logLevel != LogLevel.None;
    }

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (_host.IsEnabled(logLevel))
        {
            _host.Log(logLevel, eventId, state, exception, formatter);
        }

        try
        {
            _file.Write(logLevel, typeof(T).FullName ?? typeof(T).Name, formatter(state, exception), exception);
        }
        catch (Exception)
        {
            // Never let the log file break the operation being logged.
        }
    }
}
