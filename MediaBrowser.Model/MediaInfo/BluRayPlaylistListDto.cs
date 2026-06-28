using System;

namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// Blu-ray playlist candidates and selected/effective playlist state.
/// </summary>
public class BluRayPlaylistListDto
{
    /// <summary>
    /// Gets or sets the default playlist name.
    /// </summary>
    public string? DefaultPlaylistName { get; set; }

    /// <summary>
    /// Gets or sets the saved selected playlist name.
    /// </summary>
    public string? SelectedPlaylistName { get; set; }

    /// <summary>
    /// Gets or sets the effective playlist name used for playback/probing.
    /// </summary>
    public string? EffectivePlaylistName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the selected playlist is valid.
    /// </summary>
    public bool SelectedPlaylistIsValid { get; set; }

    /// <summary>
    /// Gets or sets the selected playlist error.
    /// </summary>
    public string? SelectedPlaylistError { get; set; }

    /// <summary>
    /// Gets or sets the refresh error from the last playlist update request.
    /// </summary>
    public string? RefreshError { get; set; }

    /// <summary>
    /// Gets or sets the available playlists.
    /// </summary>
    public BluRayPlaylistInfoDto[] Playlists { get; set; } = Array.Empty<BluRayPlaylistInfoDto>();
}
