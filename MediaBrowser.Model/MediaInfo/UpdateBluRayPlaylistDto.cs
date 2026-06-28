namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// Request to set a Blu-ray playlist selection.
/// </summary>
public class UpdateBluRayPlaylistDto
{
    /// <summary>
    /// Gets or sets the canonical playlist name.
    /// </summary>
    public string? PlaylistName { get; set; }
}
