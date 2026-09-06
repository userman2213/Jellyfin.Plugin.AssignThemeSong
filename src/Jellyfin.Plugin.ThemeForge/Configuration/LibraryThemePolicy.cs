namespace Jellyfin.Plugin.ThemeForge.Configuration;

/// <summary>
/// What ThemeForge may do when an item already has a theme file.
/// </summary>
/// <remarks>
/// A single "overwrite" checkbox cannot express the distinction that actually matters: replacing
/// a theme ThemeForge chose is routine, while replacing one a person placed by hand destroys a
/// deliberate decision. These are separate outcomes and need separate settings.
/// </remarks>
public enum ThemeOverwritePolicy
{
    /// <summary>Use the server-wide default. Only meaningful on a per-library rule.</summary>
    UseDefault = 0,

    /// <summary>Never replace an existing theme. An item that has one is left alone for good.</summary>
    Never = 1,

    /// <summary>
    /// Replace themes ThemeForge wrote, identified by the content hash recorded at the time, and
    /// leave anything else alone. Lets a library be re-scored after tuning without touching a
    /// single hand-placed theme.
    /// </summary>
    ReplaceOwn = 2,

    /// <summary>Replace any existing theme, including ones placed by hand.</summary>
    ReplaceAny = 3,
}

/// <summary>
/// A per-library override, so a shows library and a films library can be treated differently.
/// </summary>
public class LibraryThemePolicy
{
    /// <summary>Gets or sets the Jellyfin library (virtual folder) id this applies to.</summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library's name at the time the rule was saved.
    /// </summary>
    /// <remarks>
    /// Stored purely so the settings page can still label a rule whose library has been removed,
    /// rather than showing a bare identifier. Matching is always by id.
    /// </remarks>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether ThemeForge processes this library at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets what to do about existing themes in this library.</summary>
    public ThemeOverwritePolicy Overwrite { get; set; } = ThemeOverwritePolicy.UseDefault;
}
