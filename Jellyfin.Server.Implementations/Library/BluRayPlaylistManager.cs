using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Library;

/// <summary>
/// Manages Blu-ray playlist discovery and primary playlist selection.
/// </summary>
public sealed class BluRayPlaylistManager : IBluRayPlaylistManager, IDisposable
{
    private const string MaintenanceError = "Playlist selection was saved, but dependent media data could not be fully refreshed.";

    private readonly IBlurayExaminer _blurayExaminer;
    private readonly IExternalDataManager _externalDataManager;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<BluRayPlaylistManager> _logger;
    private readonly AsyncKeyedLocker<Guid> _itemLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BluRayPlaylistManager"/> class.
    /// </summary>
    /// <param name="blurayExaminer">The Blu-ray examiner.</param>
    /// <param name="externalDataManager">The external data manager.</param>
    /// <param name="applicationLifetime">The application lifetime.</param>
    /// <param name="logger">The logger.</param>
    public BluRayPlaylistManager(
        IBlurayExaminer blurayExaminer,
        IExternalDataManager externalDataManager,
        IHostApplicationLifetime applicationLifetime,
        ILogger<BluRayPlaylistManager> logger)
    {
        _blurayExaminer = blurayExaminer;
        _externalDataManager = externalDataManager;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BluRayPlaylistListDto> GetPlaylistsAsync(Video video, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(video);

        using (await _itemLocks.LockAsync(video.Id, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = GetPlaylistSnapshot(video);
            if (IsReconciled(video, snapshot))
            {
                return BuildPlaylistList(video, snapshot, null);
            }

            var maintenanceSucceeded = await TryRefreshDerivedDataAsync(video, cancellationToken).ConfigureAwait(false);
            snapshot = GetPlaylistSnapshot(video);
            return BuildPlaylistList(
                video,
                snapshot,
                maintenanceSucceeded && IsReconciled(video, snapshot) ? null : MaintenanceError);
        }
    }

    /// <inheritdoc />
    public async Task<BluRayPlaylistListDto> UpdatePlaylistAsync(Video video, string? playlistName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(video);

        using (await _itemLocks.LockAsync(video.Id, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = GetPlaylistSnapshot(video);
            var canonicalName = GetCanonicalPlaylistName(snapshot.Playlists, playlistName);
            var previousPlaylistName = video.BluRayPlaylistName;
            var previousPlaylistNameIsValid = video.BluRayPlaylistNameIsValid;
            var previousPlaylistRevision = video.BluRayPlaylistRevision;
            var selectionChanged = !string.Equals(previousPlaylistName, canonicalName, StringComparison.Ordinal);

            if (!selectionChanged && IsReconciled(video, snapshot))
            {
                return BuildPlaylistList(video, snapshot, null);
            }

            if (selectionChanged)
            {
                video.BluRayPlaylistName = canonicalName;
                video.BluRayPlaylistNameIsValid = canonicalName is null ? null : true;
                video.BluRayPlaylistRevision = unchecked(previousPlaylistRevision + 1);

                try
                {
                    await video.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    video.BluRayPlaylistName = previousPlaylistName;
                    video.BluRayPlaylistNameIsValid = previousPlaylistNameIsValid;
                    video.BluRayPlaylistRevision = previousPlaylistRevision;
                    throw;
                }
            }

            var maintenanceToken = selectionChanged ? _applicationLifetime.ApplicationStopping : cancellationToken;
            var maintenanceSucceeded = await TryRefreshDerivedDataAsync(video, maintenanceToken).ConfigureAwait(false);
            snapshot = GetPlaylistSnapshot(video);
            return BuildPlaylistList(
                video,
                snapshot,
                maintenanceSucceeded && IsReconciled(video, snapshot) ? null : MaintenanceError);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _itemLocks.Dispose();
    }

    private PlaylistSnapshot GetPlaylistSnapshot(Video video)
    {
        var playlists = _blurayExaminer.GetDiscPlaylists(video.Path)
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToArray();
        var defaultPlaylist = playlists.FirstOrDefault(i => i.IsDefault)
            ?? playlists
                .OrderByDescending(i => i.RunTimeTicks)
                .ThenBy(i => i.Name, StringComparer.Ordinal)
                .FirstOrDefault();

        return new PlaylistSnapshot(playlists, defaultPlaylist?.Name, _blurayExaminer.GetDiscFingerprint(video.Path));
    }

    private async Task<bool> TryRefreshDerivedDataAsync(Video video, CancellationToken cancellationToken)
    {
        try
        {
            await _externalDataManager.DeleteExternalItemDataAsync(video, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invalidate derived media data after changing the Blu-ray playlist for {ItemId}", video.Id);
            return false;
        }

        try
        {
            await video.RefreshMetadata(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh media data after changing the Blu-ray playlist for {ItemId}", video.Id);
            return false;
        }
    }

    private static bool IsReconciled(Video video, PlaylistSnapshot snapshot)
    {
        if (video.BluRayPlaylistProbeVersion != Video.CurrentBluRayPlaylistProbeVersion
            || video.BluRayPlaylistRevision != video.BluRayLastProbedPlaylistRevision
            || string.IsNullOrWhiteSpace(snapshot.Fingerprint)
            || !string.Equals(video.BluRayDiscFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(video.BluRayPlaylistName))
        {
            return string.IsNullOrWhiteSpace(video.BluRayLastProbedPlaylistName)
                && video.BluRayPlaylistNameIsValid is null;
        }

        var selectedPlaylist = snapshot.Playlists.FirstOrDefault(
            i => string.Equals(i.Name, video.BluRayPlaylistName, StringComparison.OrdinalIgnoreCase));
        return selectedPlaylist is null
            ? video.BluRayPlaylistNameIsValid == false && string.IsNullOrWhiteSpace(video.BluRayLastProbedPlaylistName)
            : video.BluRayPlaylistNameIsValid == true
                && string.Equals(selectedPlaylist.Name, video.BluRayLastProbedPlaylistName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCanonicalPlaylistName(BluRayPlaylistInfoDto[] playlists, string? playlistName)
    {
        if (playlistName is null)
        {
            return null;
        }

        var match = playlists.FirstOrDefault(i => string.Equals(i.Name, playlistName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new ArgumentException("PlaylistName must match a valid canonical Blu-ray playlist name.", nameof(playlistName));
        }

        return match.Name;
    }

    private static BluRayPlaylistListDto BuildPlaylistList(Video video, PlaylistSnapshot snapshot, string? refreshError)
    {
        var selectedPlaylistName = video.BluRayPlaylistName;
        var selectedPlaylist = string.IsNullOrWhiteSpace(selectedPlaylistName)
            ? null
            : snapshot.Playlists.FirstOrDefault(i => string.Equals(i.Name, selectedPlaylistName, StringComparison.OrdinalIgnoreCase));
        var selectedPlaylistIsValid = string.IsNullOrWhiteSpace(selectedPlaylistName) || selectedPlaylist is not null;
        var effectivePlaylistName = selectedPlaylist?.Name ?? snapshot.DefaultPlaylistName;

        foreach (var playlist in snapshot.Playlists)
        {
            playlist.IsDefault = string.Equals(playlist.Name, snapshot.DefaultPlaylistName, StringComparison.Ordinal);
            playlist.IsSelected = string.Equals(playlist.Name, effectivePlaylistName, StringComparison.Ordinal);
        }

        return new BluRayPlaylistListDto
        {
            DefaultPlaylistName = snapshot.DefaultPlaylistName,
            SelectedPlaylistName = selectedPlaylistName,
            EffectivePlaylistName = effectivePlaylistName,
            SelectedPlaylistIsValid = selectedPlaylistIsValid,
            SelectedPlaylistError = selectedPlaylistIsValid ? null : "Saved playlist is not valid for this Blu-ray item.",
            RefreshError = refreshError,
            Playlists = snapshot.Playlists
        };
    }

    private sealed record PlaylistSnapshot(BluRayPlaylistInfoDto[] Playlists, string? DefaultPlaylistName, string? Fingerprint);
}
