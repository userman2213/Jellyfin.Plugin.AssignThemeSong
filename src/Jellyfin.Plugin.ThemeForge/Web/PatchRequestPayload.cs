using System;

namespace Jellyfin.Plugin.ThemeForge.Web;

/// <summary>
/// The payload the File Transformation plugin hands to a transformation callback.
/// Declared here rather than referenced, because that plugin is an optional dependency
/// resolved reflectively at runtime.
/// </summary>
public class PatchRequestPayload
{
    /// <summary>Gets or sets the id of the transformation being applied.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the current contents of the file being transformed.</summary>
    public string? Contents { get; set; }
}
