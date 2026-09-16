using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Jellyfin.Plugin.ThemeForge.Engines.Placement;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// The theme file's extension now follows what is inside it, so replacing a theme has to clear
/// whatever theme file was there before, whatever it was called. Jellyfin plays every
/// <c>theme.*</c> audio file it finds; two of them would alternate.
/// </summary>
public class ThemePlacementTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "themeforge-placement-" + Guid.NewGuid().ToString("N"));

    public ThemePlacementTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    private static ThemePlacementEngine Engine() =>
        new(null!, NullThemeForgeLogger<ThemePlacementEngine>.Instance);

    private static ResolvedThemePolicy Policy(ThemeOverwritePolicy overwrite) => new(true, overwrite, "Films");

    private (Movie Item, string Folder) MovieIn(string name)
    {
        var folder = Path.Combine(_workspace, name);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name + ".mkv");
        File.WriteAllText(file, "not really a film");
        return (new Movie { Name = name, Path = file }, folder);
    }

    private AcquiredAudio Staged(string fileName)
    {
        var staging = Path.Combine(_workspace, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var path = Path.Combine(staging, fileName);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });

        return new AcquiredAudio
        {
            StagingPath = path,
            DurationSeconds = 10,
            Sha256 = "irrelevant",
            SizeBytes = 4,
            SourceUrl = "https://example.invalid/theme",
        };
    }

    private static string[] ThemeFilesIn(string folder) =>
        Directory.GetFiles(folder, "theme.*").Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray()!;

    [Fact]
    public async Task ReplacingAThemeThatHasAnotherExtensionLeavesOnlyTheNewOne()
    {
        var (item, folder) = MovieIn("Alien");
        File.WriteAllText(Path.Combine(folder, "theme.mp3"), "the old normalised theme");
        var configuration = TestData.Config();
        configuration.BackupExistingThemes = false;

        var result = await Engine()
            .PlaceAsync(item, Staged("theme.opus"), configuration, Policy(ThemeOverwritePolicy.ReplaceAny), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(Path.Combine(folder, "theme.opus"), result.Path);
        Assert.Equal(new[] { "theme.opus" }, ThemeFilesIn(folder));
    }

    [Fact]
    public async Task WithBackupsOnTheOldThemeIsKeptAsideUnderANameJellyfinIgnores()
    {
        var (item, folder) = MovieIn("Aliens");
        File.WriteAllText(Path.Combine(folder, "theme.mp3"), "the old normalised theme");
        var configuration = TestData.Config();
        configuration.BackupExistingThemes = true;

        var result = await Engine()
            .PlaceAsync(item, Staged("theme.opus"), configuration, Policy(ThemeOverwritePolicy.ReplaceOwn), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.True(result.Success, result.Reason);

        var files = ThemeFilesIn(folder);
        Assert.Equal(2, files.Length);
        Assert.Contains("theme.opus", files);
        Assert.Contains(files, name => name.StartsWith("theme.mp3.themeforge-backup-", StringComparison.Ordinal));

        // Only one of them is a theme as far as Jellyfin, or ThemeForge, is concerned.
        Assert.Equal(new[] { Path.Combine(folder, "theme.opus") }, ThemePlacementEngine.ExistingThemeFiles(folder));
    }

    [Fact]
    public async Task ReplacingAThemeWithTheSameExtensionStillWorks()
    {
        var (item, folder) = MovieIn("Alien 3");
        File.WriteAllText(Path.Combine(folder, "theme.mp3"), "old");
        var configuration = TestData.Config();
        configuration.BackupExistingThemes = false;

        var result = await Engine()
            .PlaceAsync(item, Staged("theme.mp3"), configuration, Policy(ThemeOverwritePolicy.ReplaceAny), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(new[] { "theme.mp3" }, ThemeFilesIn(folder));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(folder, "theme.mp3")));
    }

    [Fact]
    public async Task NeverRefusesWhenAThemeOfAnyKindIsAlreadyThere()
    {
        var (item, folder) = MovieIn("Prometheus");
        File.WriteAllText(Path.Combine(folder, "theme.flac"), "somebody's own theme");

        var result = await Engine()
            .PlaceAsync(item, Staged("theme.opus"), TestData.Config(), Policy(ThemeOverwritePolicy.Never), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.False(result.Success);
        Assert.Contains("never", result.Reason, StringComparison.Ordinal);
        Assert.Equal(new[] { "theme.flac" }, ThemeFilesIn(folder));
    }

    [Fact]
    public void ABackupIsNotMistakenForATheme()
    {
        var (item, folder) = MovieIn("Covenant");
        File.WriteAllText(Path.Combine(folder, "theme.mp3.themeforge-backup-20240101000000"), "kept aside");

        Assert.Empty(ThemePlacementEngine.ExistingThemeFiles(folder));
        Assert.False(Engine().HasExistingTheme(item, TestData.Config()));
    }

    [Fact]
    public void AFileThatMerelySharesTheNameIsNotATheme()
    {
        var (item, folder) = MovieIn("Romulus");
        File.WriteAllText(Path.Combine(folder, "theme.txt"), "notes");
        File.WriteAllText(Path.Combine(folder, "theme.jpg"), "not a picture either");

        Assert.Empty(ThemePlacementEngine.ExistingThemeFiles(folder));
        Assert.False(Engine().HasExistingTheme(item, TestData.Config()));
    }
}
