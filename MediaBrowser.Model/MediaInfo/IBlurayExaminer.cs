using System.Collections.Generic;

namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// Interface IBlurayExaminer.
/// </summary>
public interface IBlurayExaminer
{
    /// <summary>
    /// Gets the disc info.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>BlurayDiscInfo.</returns>
    BlurayDiscInfo GetDiscInfo(string path);

    /// <summary>
    /// Gets the disc info for a selected playlist.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="playlistName">The optional canonical playlist name to use.</param>
    /// <returns>BlurayDiscInfo.</returns>
    BlurayDiscInfo GetDiscInfo(string path, string? playlistName) => GetDiscInfo(path);

    /// <summary>
    /// Gets the valid playlists for a disc.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The valid disc playlists.</returns>
    IReadOnlyList<BluRayPlaylistInfoDto> GetDiscPlaylists(string path) => [];

    /// <summary>
    /// Gets a fingerprint for the current Blu-ray structure.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The fingerprint, or <see langword="null" /> when it cannot be calculated.</returns>
    string? GetDiscFingerprint(string path) => null;
}
