using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BDInfo;
using Jellyfin.Extensions;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.MediaEncoding.BdInfo;

/// <summary>
/// Class BdInfoExaminer.
/// </summary>
public class BdInfoExaminer : IBlurayExaminer
{
    private const int MaxCachedDiscs = 8;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly IFileSystem _fileSystem;
    private readonly ConcurrentDictionary<DiscCacheKey, Lazy<CachedDisc>> _discCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BdInfoExaminer" /> class.
    /// </summary>
    /// <param name="fileSystem">The filesystem.</param>
    public BdInfoExaminer(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Gets the disc info.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>BlurayDiscInfo.</returns>
    public BlurayDiscInfo GetDiscInfo(string path)
        => GetDiscInfo(path, null);

    /// <inheritdoc />
    public BlurayDiscInfo GetDiscInfo(string path, string? playlistName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        var playlists = GetPlaylists(path);
        var selectedPlaylist = SelectPlaylist(playlists, playlistName)?.Playlist;

        return selectedPlaylist is null ? new BlurayDiscInfo() : GetDiscInfo(selectedPlaylist);
    }

    /// <inheritdoc />
    public IReadOnlyList<BluRayPlaylistInfoDto> GetDiscPlaylists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        var playlists = GetPlaylists(path);
        var defaultPlaylist = SelectDefaultPlaylist(playlists);

        return playlists
            .Select(i =>
            {
                var info = GetPlaylistInfo(i.Playlist!);
                info.IsDefault = string.Equals(info.Name, defaultPlaylist?.Name, StringComparison.Ordinal);
                return info;
            })
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc />
    public string? GetDiscFingerprint(string path)
    {
        var root = NormalizeDiscPath(path);
        var bdmvPath = Path.Combine(root, "BDMV");
        var directory = _fileSystem.GetDirectoryInfo(bdmvPath);
        if (!directory.Exists)
        {
            return null;
        }

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in _fileSystem.GetFiles(bdmvPath, true)
                         .Where(i => !i.IsDirectory)
                         .OrderBy(i => Path.GetRelativePath(bdmvPath, i.FullName), StringComparer.Ordinal))
            {
                var relativePath = Path.GetRelativePath(bdmvPath, file.FullName).Replace('\\', '/');
                var value = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{relativePath}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}\0");
                hash.AppendData(Encoding.UTF8.GetBytes(value));
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private IReadOnlyList<BdInfoPlaylistCandidate> GetPlaylists(string path)
    {
        var root = NormalizeDiscPath(path);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var fingerprint = GetDiscFingerprint(root);
            if (fingerprint is null)
            {
                try
                {
                    return ScanDisc(root, null);
                }
                catch (DiscChangedException) when (attempt == 0)
                {
                    continue;
                }
            }

            var key = new DiscCacheKey(root, fingerprint);
            var lazy = _discCache.GetOrAdd(
                key,
                static (cacheKey, examiner) => new Lazy<CachedDisc>(
                    () => new CachedDisc(examiner.ScanDisc(cacheKey.Path, cacheKey.Fingerprint), DateTime.UtcNow),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                this);

            while (true)
            {
                try
                {
                    var cached = lazy.Value;
                    if (DateTime.UtcNow - cached.CreatedUtc <= CacheDuration)
                    {
                        TrimCache();
                        return cached.Playlists;
                    }
                }
                catch (DiscChangedException) when (attempt == 0)
                {
                    _discCache.TryRemove(new KeyValuePair<DiscCacheKey, Lazy<CachedDisc>>(key, lazy));
                    break;
                }
                catch
                {
                    _discCache.TryRemove(new KeyValuePair<DiscCacheKey, Lazy<CachedDisc>>(key, lazy));
                    throw;
                }

                _discCache.TryRemove(new KeyValuePair<DiscCacheKey, Lazy<CachedDisc>>(key, lazy));
                lazy = _discCache.GetOrAdd(
                    key,
                    static (cacheKey, examiner) => new Lazy<CachedDisc>(
                        () => new CachedDisc(examiner.ScanDisc(cacheKey.Path, cacheKey.Fingerprint), DateTime.UtcNow),
                        LazyThreadSafetyMode.ExecutionAndPublication),
                    this);
            }
        }

        throw new DiscChangedException();
    }

    private IReadOnlyList<BdInfoPlaylistCandidate> ScanDisc(string root, string? expectedFingerprint)
    {
        var before = GetDiscFingerprint(root);
        if (expectedFingerprint is not null && !string.Equals(before, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new DiscChangedException();
        }

        var bdrom = new BDROM(BdInfoDirectoryInfo.FromFileSystemPath(_fileSystem, root));
        bdrom.Scan();
        var playlists = GetValidPlaylists(bdrom);
        var after = GetDiscFingerprint(root);
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            throw new DiscChangedException();
        }

        return playlists;
    }

    private void TrimCache()
    {
        if (_discCache.Count <= MaxCachedDiscs)
        {
            return;
        }

        foreach (var entry in _discCache
                     .Where(i => i.Value.IsValueCreated)
                     .OrderBy(i => i.Value.Value.CreatedUtc)
                     .Take(_discCache.Count - MaxCachedDiscs))
        {
            _discCache.TryRemove(new KeyValuePair<DiscCacheKey, Lazy<CachedDisc>>(entry.Key, entry.Value));
        }
    }

    internal static string NormalizeDiscPath(string path)
    {
        if (string.Equals(Path.GetFileName(path), "BDMV", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(path) ?? path;
        }

        return path;
    }

    private BlurayDiscInfo GetDiscInfo(TSPlaylistFile playlist)
    {
        var outputStream = new BlurayDiscInfo();

        outputStream.Chapters = playlist.Chapters.ToArray();

        outputStream.RunTimeTicks = TimeSpan.FromSeconds(playlist.TotalLength).Ticks;

        outputStream.MediaStreams = GetMediaStreams(playlist);

        outputStream.PlaylistName = playlist.Name;

        if (playlist.StreamClips is not null && playlist.StreamClips.Count > 0)
        {
            // Get the files in the playlist
            outputStream.Files = playlist.StreamClips.Select(i => i.StreamFile.FileInfo.FullName).ToArray();
        }

        return outputStream;
    }

    private BluRayPlaylistInfoDto GetPlaylistInfo(TSPlaylistFile playlist)
    {
        var streams = GetMediaStreams(playlist);

        return new BluRayPlaylistInfoDto
        {
            Name = playlist.Name,
            RunTimeTicks = TimeSpan.FromSeconds(playlist.TotalLength).Ticks,
            ChapterCount = playlist.Chapters.Count,
            ClipCount = playlist.StreamClips?.Count ?? 0,
            ClipFileNames = playlist.StreamClips is null
                ? Array.Empty<string>()
                : playlist.StreamClips.Select(i => Path.GetFileName(i.StreamFile.FileInfo.FullName)!).ToArray(),
            VideoStreams = streams.Where(i => i.Type == MediaStreamType.Video).ToArray(),
            AudioStreams = streams.Where(i => i.Type == MediaStreamType.Audio).ToArray(),
            SubtitleStreams = streams.Where(i => i.Type == MediaStreamType.Subtitle).ToArray()
        };
    }

    private MediaStream[] GetMediaStreams(TSPlaylistFile playlist)
    {
        var sortedStreams = playlist.SortedStreams;
        var mediaStreams = new List<MediaStream>(sortedStreams.Count);

        for (int i = 0; i < sortedStreams.Count; i++)
        {
            var stream = sortedStreams[i];
            switch (stream)
            {
                case TSVideoStream videoStream:
                    AddVideoStream(mediaStreams, i, videoStream);
                    break;
                case TSAudioStream audioStream:
                    AddAudioStream(mediaStreams, i, audioStream);
                    break;
                case TSTextStream:
                case TSGraphicsStream:
                    AddSubtitleStream(mediaStreams, i, stream);
                    break;
            }
        }

        return mediaStreams.ToArray();
    }

    private static IReadOnlyList<BdInfoPlaylistCandidate> GetValidPlaylists(BDROM bdrom)
        => bdrom.PlaylistFiles.Values
            .Where(i => i.IsValid)
            .Select(i => new BdInfoPlaylistCandidate(i.Name, TimeSpan.FromSeconds(i.TotalLength).Ticks, i))
            .ToArray();

    internal static BdInfoPlaylistCandidate? SelectPlaylist(IReadOnlyList<BdInfoPlaylistCandidate> playlists, string? playlistName)
    {
        if (TryGetCanonicalPlaylistName(playlists, playlistName, out var canonicalName))
        {
            return playlists.First(i => string.Equals(i.Name, canonicalName, StringComparison.Ordinal));
        }

        return SelectDefaultPlaylist(playlists);
    }

    internal static BdInfoPlaylistCandidate? SelectDefaultPlaylist(IReadOnlyList<BdInfoPlaylistCandidate> playlists)
        => playlists
            .OrderByDescending(i => i.RunTimeTicks)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .FirstOrDefault();

    internal static bool TryGetCanonicalPlaylistName(
        IReadOnlyList<BdInfoPlaylistCandidate> playlists,
        string? playlistName,
        out string? canonicalName)
    {
        canonicalName = null;

        if (string.IsNullOrWhiteSpace(playlistName)
            || !string.Equals(Path.GetExtension(playlistName), ".mpls", StringComparison.OrdinalIgnoreCase)
            || playlistName.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0
            || Path.IsPathFullyQualified(playlistName))
        {
            return false;
        }

        var match = playlists.FirstOrDefault(i => string.Equals(i.Name, playlistName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        canonicalName = match.Name;
        return true;
    }

    /// <summary>
    /// Adds the video stream.
    /// </summary>
    /// <param name="streams">The streams.</param>
    /// <param name="index">The stream index.</param>
    /// <param name="videoStream">The video stream.</param>
    private void AddVideoStream(List<MediaStream> streams, int index, TSVideoStream videoStream)
    {
        var mediaStream = new MediaStream
        {
            BitRate = Convert.ToInt32(videoStream.BitRate),
            Width = videoStream.Width,
            Height = videoStream.Height,
            Codec = GetNormalizedCodec(videoStream),
            IsInterlaced = videoStream.IsInterlaced,
            Type = MediaStreamType.Video,
            Index = index
        };

        if (videoStream.FrameRateDenominator > 0)
        {
            float frameRateEnumerator = videoStream.FrameRateEnumerator;
            float frameRateDenominator = videoStream.FrameRateDenominator;

            mediaStream.AverageFrameRate = mediaStream.RealFrameRate = frameRateEnumerator / frameRateDenominator;
        }

        streams.Add(mediaStream);
    }

    /// <summary>
    /// Adds the audio stream.
    /// </summary>
    /// <param name="streams">The streams.</param>
    /// <param name="index">The stream index.</param>
    /// <param name="audioStream">The audio stream.</param>
    private void AddAudioStream(List<MediaStream> streams, int index, TSAudioStream audioStream)
    {
        var stream = new MediaStream
        {
            Codec = GetNormalizedCodec(audioStream),
            Language = audioStream.LanguageCode,
            ChannelLayout = string.Format(CultureInfo.InvariantCulture, "{0:D}.{1:D}", audioStream.ChannelCount, audioStream.LFE),
            Channels = audioStream.ChannelCount + audioStream.LFE,
            SampleRate = audioStream.SampleRate,
            Type = MediaStreamType.Audio,
            Index = index
        };

        var bitrate = Convert.ToInt32(audioStream.BitRate);

        if (bitrate > 0)
        {
            stream.BitRate = bitrate;
        }

        streams.Add(stream);
    }

    /// <summary>
    /// Adds the subtitle stream.
    /// </summary>
    /// <param name="streams">The streams.</param>
    /// <param name="index">The stream index.</param>
    /// <param name="stream">The stream.</param>
    private void AddSubtitleStream(List<MediaStream> streams, int index, TSStream stream)
    {
        streams.Add(new MediaStream
        {
            Language = stream.LanguageCode,
            Codec = GetNormalizedCodec(stream),
            Type = MediaStreamType.Subtitle,
            Index = index
        });
    }

    private string GetNormalizedCodec(TSStream stream)
        => stream.StreamType switch
        {
            TSStreamType.MPEG1_VIDEO => "mpeg1video",
            TSStreamType.MPEG2_VIDEO => "mpeg2video",
            TSStreamType.VC1_VIDEO => "vc1",
            TSStreamType.AC3_PLUS_AUDIO or TSStreamType.AC3_PLUS_SECONDARY_AUDIO => "eac3",
            TSStreamType.DTS_AUDIO or TSStreamType.DTS_HD_AUDIO or TSStreamType.DTS_HD_MASTER_AUDIO or TSStreamType.DTS_HD_SECONDARY_AUDIO => "dts",
            TSStreamType.PRESENTATION_GRAPHICS => "pgssub",
            _ => stream.CodecShortName
        };

    private sealed record DiscCacheKey(string Path, string Fingerprint);

    private sealed class DiscChangedException : IOException
    {
        public DiscChangedException()
            : base("Blu-ray structure changed while it was being scanned.")
        {
        }
    }

    private sealed record CachedDisc(IReadOnlyList<BdInfoPlaylistCandidate> Playlists, DateTime CreatedUtc);
}
