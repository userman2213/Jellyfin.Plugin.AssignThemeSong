using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>A client factory that fails if anything actually tries to make a request.</summary>
/// <remarks>Used where a test exercises logic that must not reach the network.</remarks>
internal sealed class ThrowingHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        throw new InvalidOperationException("This test must not make a request.");
}

/// <summary>A browser that is not there, which is the state most servers are in.</summary>
internal sealed class UnavailableBrowser : IHeadlessBrowser
{
    public string? FindBrowser() => null;

    public string DescribeSource() => "none";

    public Task<string?> GetVersionAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<string?> RenderAsync(
        string url,
        Func<string, bool> isComplete,
        CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
