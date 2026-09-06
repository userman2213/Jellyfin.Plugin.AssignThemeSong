using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ThemeForge.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers the decorator's routing behaviour.
/// </summary>
/// <remarks>
/// This is a decorator rather than an ILoggerProvider because Jellyfin registers Serilog without
/// <c>writeToProviders</c>, so a provider would never be called and the log file would sit
/// permanently empty. These tests pin the properties that make the decorator worth having.
/// </remarks>
public class ThemeForgeLoggerTests
{
    [Fact]
    public void MessagesStillReachJellyfinsLog()
    {
        var host = new RecordingLogger<ThemeForgeLoggerTests>();
        var logger = new ThemeForgeLogger<ThemeForgeLoggerTests>(host, new ThemeForgeLogFile());

        logger.LogInformation("hello");

        Assert.Single(host.Entries);
        Assert.Equal("hello", host.Entries[0].Message);
    }

    [Fact]
    public void HostLoggerIsNotCalledForLevelsItHasDisabled()
    {
        var host = new RecordingLogger<ThemeForgeLoggerTests> { Enabled = false };
        var logger = new ThemeForgeLogger<ThemeForgeLoggerTests>(host, new ThemeForgeLogFile());

        logger.LogDebug("quiet");

        Assert.Empty(host.Entries);
    }

    [Fact]
    public void IsEnabledIsTrueWhenEitherDestinationWantsTheMessage()
    {
        // Jellyfin usually logs at Information. Without this, turning ThemeForge's own level down
        // to Debug would change nothing, because call sites check IsEnabled before formatting.
        var host = new RecordingLogger<ThemeForgeLoggerTests> { Enabled = false };
        var logger = new ThemeForgeLogger<ThemeForgeLoggerTests>(host, new ThemeForgeLogFile());

        Assert.True(logger.IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void NoneIsNeverEnabled()
    {
        var host = new RecordingLogger<ThemeForgeLoggerTests> { Enabled = false };
        var logger = new ThemeForgeLogger<ThemeForgeLoggerTests>(host, new ThemeForgeLogFile());

        Assert.False(logger.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void AFailingFileSinkDoesNotBreakTheCaller()
    {
        var host = new RecordingLogger<ThemeForgeLoggerTests>();
        var logger = new ThemeForgeLogger<ThemeForgeLoggerTests>(host, new ThrowingLogFile());

        // A logger that can take down the operation it is describing is worse than no logger.
        var exception = Record.Exception(() => logger.LogInformation("still fine"));

        Assert.Null(exception);
        Assert.Single(host.Entries);
    }

    private sealed class ThrowingLogFile : IThemeForgeLogSink
    {
        public void Write(LogLevel level, string category, string message, Exception? exception) =>
            throw new InvalidOperationException("the disk is on fire");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public bool Enabled { get; set; } = true;

        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => Enabled;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}

public class ThemeForgeLogFileTests : IDisposable
{
    private readonly string _directory =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "themeforge-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(_directory))
            {
                System.IO.Directory.Delete(_directory, recursive: true);
            }
        }
        catch (System.IO.IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TailOfAnUnopenedLogIsEmptyRatherThanThrowing() =>
        Assert.Empty(new ThemeForgeLogFile().Tail(50));

    [Fact]
    public void WritingBeforeThePluginExistsIsSilentlyIgnored()
    {
        // Plugin.Instance is null in a test run, so there is no data directory to write into.
        // The sink must cope rather than throw on every line.
        var file = new ThemeForgeLogFile();
        var exception = Record.Exception(() => file.Write(LogLevel.Information, "Cat", "message", null));
        Assert.Null(exception);
    }

    [Fact]
    public void LinesAreWrittenAndCanBeReadBack()
    {
        using var file = new ThemeForgeLogFile(_directory);

        file.Write(LogLevel.Information, "Jellyfin.Plugin.ThemeForge.Engines.Foo", "first line", null);
        file.Write(LogLevel.Warning, "Jellyfin.Plugin.ThemeForge.Engines.Foo", "second line", null);

        var tail = file.Tail(50);

        Assert.Equal(2, tail.Length);
        Assert.Contains("first line", tail[0], StringComparison.Ordinal);
        Assert.Contains("[INF]", tail[0], StringComparison.Ordinal);
        Assert.Contains("[WRN]", tail[1], StringComparison.Ordinal);

        // The namespace is stripped, because every category here is this plugin's own.
        Assert.Contains("Foo:", tail[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionsAreRecordedWithTheirMessage()
    {
        using var file = new ThemeForgeLogFile(_directory);
        file.Write(LogLevel.Error, "Cat", "it broke", new InvalidOperationException("the specific reason"));

        var text = string.Join('\n', file.Tail(50));
        Assert.Contains("it broke", text, StringComparison.Ordinal);
        Assert.Contains("the specific reason", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TailReturnsOnlyTheRequestedNumberOfLines()
    {
        using var file = new ThemeForgeLogFile(_directory);
        for (var i = 0; i < 50; i++)
        {
            file.Write(LogLevel.Information, "Cat", "line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
        }

        var tail = file.Tail(10);

        Assert.Equal(10, tail.Length);
        // Oldest first, ending on the most recent line.
        Assert.Contains("line 40", tail[0], StringComparison.Ordinal);
        Assert.Contains("line 49", tail[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void WritingFromManyThreadsDoesNotCorruptTheFile()
    {
        // A run processes items concurrently, so the sink is written from several threads at once.
        using var file = new ThemeForgeLogFile(_directory);

        System.Threading.Tasks.Parallel.For(0, 200, i =>
            file.Write(LogLevel.Information, "Cat", "concurrent " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), null));

        var tail = file.Tail(500);
        Assert.Equal(200, tail.Length);
        Assert.All(tail, line => Assert.Contains("concurrent ", line, StringComparison.Ordinal));
    }
}
