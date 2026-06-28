using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Manages Blu-ray playlist discovery and primary playlist selection.
/// </summary>
public interface IBluRayPlaylistManager
{
    /// <summary>
    /// Gets the available playlists and current selection state for a video.
    /// </summary>
    /// <param name="video">The Blu-ray video.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The playlist selection state.</returns>
    Task<BluRayPlaylistListDto> GetPlaylistsAsync(Video video, CancellationToken cancellationToken);

    /// <summary>
    /// Updates or clears the selected playlist for a video.
    /// </summary>
    /// <param name="video">The Blu-ray video.</param>
    /// <param name="playlistName">The requested canonical playlist name, or <see langword="null"/> to clear it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated playlist selection state.</returns>
    /// <exception cref="System.ArgumentException">The playlist name does not match an available playlist.</exception>
    Task<BluRayPlaylistListDto> UpdatePlaylistAsync(Video video, string? playlistName, CancellationToken cancellationToken);
}
