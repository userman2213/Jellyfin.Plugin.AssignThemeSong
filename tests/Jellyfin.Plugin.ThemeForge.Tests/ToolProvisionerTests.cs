using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Marks a test that reaches the internet. Opt in with <c>THEMEFORGE_NETWORK_TESTS=1</c> so an
/// ordinary build stays offline and fast, and CI does not pull a 30 MB binary on every push.
/// </summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    public NetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("THEMEFORGE_NETWORK_TESTS") != "1")
        {
            Skip = "Set THEMEFORGE_NETWORK_TESTS=1 to run tests that download from the internet.";
        }
    }
}

public class ToolProvisionerTests
{
    [Fact]
    public void PicksAPlatformAppropriateReleaseAsset()
    {
        var asset = YtDlpProvisioner.AssetName;

        Assert.False(string.IsNullOrWhiteSpace(asset));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("yt-dlp.exe", asset);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("yt-dlp_macos", asset);
        }
        else
        {
            Assert.StartsWith("yt-dlp_linux", asset, StringComparison.Ordinal);
        }
    }

    [NetworkFact]
    public async Task DownloadsYtDlpAndItRuns()
    {
        var tools = Path.Combine(Path.GetTempPath(), "themeforge-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tools);

        try
        {
            // Exercise the same download path the plugin uses, then prove the binary works.
            var provisioner = new YtDlpProvisioner(
                new SimpleHttpClientFactory(),
                TestEngines.Runner(),
                new FfmpegLocator(NullLogger<FfmpegLocator>.Instance),
                NullLogger<YtDlpProvisioner>.Instance);

            var target = Path.Combine(tools, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
            await DownloadDirectlyAsync(provisioner, target).ConfigureAwait(false);

            Assert.True(File.Exists(target), "yt-dlp was not downloaded");
            Assert.True(new FileInfo(target).Length > 100_000, "the downloaded file is implausibly small");

            var version = await TestEngines.Runner()
                .RunAsync(target, new[] { "--version" }, TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(version.Success, "the downloaded yt-dlp would not run: " + version.ErrorSummary);
            Assert.Matches(@"^\d{4}\.\d{2}\.\d{2}", version.StandardOutput.Trim());
        }
        finally
        {
            try
            {
                Directory.Delete(tools, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    /// <summary>Calls the provisioner's private download step against an explicit path.</summary>
    private static Task DownloadDirectlyAsync(YtDlpProvisioner provisioner, string target)
    {
        var method = typeof(YtDlpProvisioner).GetMethod(
            "DownloadAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(method);
        return (Task)method!.Invoke(provisioner, new object[] { target, CancellationToken.None })!;
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
