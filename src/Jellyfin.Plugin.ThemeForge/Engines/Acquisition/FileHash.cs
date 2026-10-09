using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ThemeForge.Engines.Acquisition;

/// <summary>Content hashing for theme files.</summary>
/// <remarks>
/// The hash recorded when a theme is written is what lets ThemeForge tell later whether a file
/// is still the one it produced. That distinction is what makes bulk removal safe: a file whose
/// hash has changed was replaced by the user and must be left alone.
/// </remarks>
public static class FileHash
{
    /// <summary>Computes the SHA-256 of a file.</summary>
    /// <param name="path">File to hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hash as lowercase hex.</returns>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
