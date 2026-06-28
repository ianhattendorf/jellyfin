using System;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// Safe display metadata for a Blu-ray playlist.
/// </summary>
public class BluRayPlaylistInfoDto
{
    /// <summary>
    /// Gets or sets the canonical playlist name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the playlist runtime in ticks.
    /// </summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the chapter count.
    /// </summary>
    public int ChapterCount { get; set; }

    /// <summary>
    /// Gets or sets the clip count.
    /// </summary>
    public int ClipCount { get; set; }

    /// <summary>
    /// Gets or sets the safe clip file names.
    /// </summary>
    public string[] ClipFileNames { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the video stream summaries.
    /// </summary>
    public MediaStream[] VideoStreams { get; set; } = Array.Empty<MediaStream>();

    /// <summary>
    /// Gets or sets the audio stream summaries.
    /// </summary>
    public MediaStream[] AudioStreams { get; set; } = Array.Empty<MediaStream>();

    /// <summary>
    /// Gets or sets the subtitle stream summaries.
    /// </summary>
    public MediaStream[] SubtitleStreams { get; set; } = Array.Empty<MediaStream>();

    /// <summary>
    /// Gets or sets a value indicating whether this playlist is the default playlist.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this playlist is the selected playlist.
    /// </summary>
    public bool IsSelected { get; set; }
}
