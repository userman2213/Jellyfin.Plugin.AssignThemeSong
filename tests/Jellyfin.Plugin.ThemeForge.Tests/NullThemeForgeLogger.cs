using System;
using Jellyfin.Plugin.ThemeForge.Logging;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// A logger that discards everything, for engines under test.
/// </summary>
/// <remarks>
/// The real <see cref="ThemeForgeLogger{T}"/> also writes to the plugin's log file, which in a
/// test run would mean touching the filesystem and depending on plugin state that is not
/// initialised. Tests are about the engine, not its logging.
/// </remarks>
internal sealed class NullThemeForgeLogger<T> : IThemeForgeLogger<T>
{
    public static readonly NullThemeForgeLogger<T> Instance = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // Intentionally discarded.
    }
}
