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
using MediaBrowser.Controller.MediaEncoding;
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
        => GetDiscInfo(path, null, 0);

    /// <inheritdoc />
    public BlurayDiscInfo GetDiscInfo(string path, string? playlistName)
        => GetDiscInfo(path, playlistName, 0);

    /// <inheritdoc />
    public BlurayDiscInfo GetDiscInfo(string path, string? playlistName, long playlistRevision)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        var root = NormalizeDiscPath(path);
        var fingerprint = GetDiscFingerprint(root);
        var playlists = GetPlaylists(root, fingerprint);
        BdInfoPlaylistCandidate? selectedCandidate;
        if (string.IsNullOrWhiteSpace(playlistName))
        {
            selectedCandidate = SelectDefaultPlaylist(playlists);
        }
        else
        {
            selectedCandidate = TryGetCanonicalPlaylistName(playlists, playlistName, out var canonicalName)
                ? playlists.First(i => string.Equals(i.Name, canonicalName, StringComparison.Ordinal))
                : null;
        }

        if (selectedCandidate is null)
        {
            var plan = CreateFailedPlan(
                playlistName,
                playlistRevision,
                fingerprint,
                BluRayPlaybackPlanFailureReason.PlaylistNotFound,
                string.IsNullOrWhiteSpace(playlistName)
                    ? "The Blu-ray folder does not contain a playable playlist."
                    : $"The selected Blu-ray playlist '{playlistName}' is no longer present on the disc.");
            return new BlurayDiscInfo { PlaybackPlan = plan };
        }

        TSPlaylistFile selectedPlaylist;
        try
        {
            selectedPlaylist = ScanSelectedPlaylist(root, selectedCandidate.Name, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
        {
            var plan = CreateFailedPlan(
                selectedCandidate.Name,
                playlistRevision,
                fingerprint,
                BluRayPlaybackPlanFailureReason.MalformedPlaylist,
                ex.Message);
            return new BlurayDiscInfo { PlaybackPlan = plan };
        }

        return GetDiscInfo(root, selectedPlaylist, playlistRevision, fingerprint);
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

    private IReadOnlyList<BdInfoPlaylistCandidate> GetPlaylists(string path, string? knownFingerprint = null)
    {
        var root = NormalizeDiscPath(path);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var fingerprint = attempt == 0 && knownFingerprint is not null
                ? knownFingerprint
                : GetDiscFingerprint(root);
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

            try
            {
                var cached = lazy.Value;
                TrimCache();
                return cached.Playlists;
            }
            catch (DiscChangedException) when (attempt == 0)
            {
                _discCache.TryRemove(new KeyValuePair<DiscCacheKey, Lazy<CachedDisc>>(key, lazy));
            }
            catch
            {
                _discCache.TryRemove(new KeyValuePair<DiscCacheKey, Lazy<CachedDisc>>(key, lazy));
                throw;
            }
        }

        throw new DiscChangedException();
    }

    private IReadOnlyList<BdInfoPlaylistCandidate> ScanDisc(string root, string? expectedFingerprint)
    {
        var before = expectedFingerprint ?? GetDiscFingerprint(root);
        var bdrom = ScanDiscMetadata(root);
        var playlists = GetCatalogPlaylists(bdrom);
        var after = GetDiscFingerprint(root);
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            throw new DiscChangedException();
        }

        return playlists;
    }

    private BDROM ScanDiscMetadata(string root)
    {
        var bdrom = new BDROM(BdInfoDirectoryInfo.FromFileSystemPath(_fileSystem, root));
        var streamPath = Path.Combine(root, "BDMV", "STREAM");
        foreach (var file in _fileSystem.GetFiles(streamPath, false)
                     .Where(i => string.Equals(i.Extension, ".m2ts", StringComparison.OrdinalIgnoreCase)))
        {
            var streamFile = new TSStreamFile(new BdInfoFileInfo(file));
            bdrom.StreamFiles[streamFile.Name] = streamFile;
        }

        var clipInfoPath = Path.Combine(root, "BDMV", "CLIPINF");
        foreach (var file in _fileSystem.GetFiles(clipInfoPath, false)
                     .Where(i => string.Equals(i.Extension, ".clpi", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var clipFile = new TSStreamClipFile(new BdInfoFileInfo(file));
                clipFile.Scan();
                bdrom.StreamClipFiles[clipFile.Name] = clipFile;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
            {
                // Match BDInfo's best-effort catalog behavior: one malformed CLPI must not
                // prevent unrelated playlists on the disc from being listed or selected.
            }
        }

        var playlistPath = Path.Combine(root, "BDMV", "PLAYLIST");
        foreach (var file in _fileSystem.GetFiles(playlistPath, false)
                     .Where(i => string.Equals(i.Extension, ".mpls", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var playlist = new TSPlaylistFile(bdrom, new BdInfoFileInfo(file));
                playlist.Scan(bdrom.StreamFiles, bdrom.StreamClipFiles);
                if (playlist.StreamClips.Count > 0)
                {
                    bdrom.PlaylistFiles[playlist.Name] = playlist;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
            {
                // Keep cataloging the remaining independently-authored playlists.
            }
        }

        return bdrom;
    }

    private TSPlaylistFile ScanSelectedPlaylist(string root, string playlistName, string? expectedFingerprint)
    {
        var before = expectedFingerprint ?? GetDiscFingerprint(root);
        var bdrom = ScanDiscMetadata(root);
        if (!bdrom.PlaylistFiles.TryGetValue(playlistName, out var playlist))
        {
            throw new InvalidDataException($"The selected Blu-ray playlist '{playlistName}' is not readable.");
        }

        foreach (var streamFile in playlist.StreamClips.Select(i => i.StreamFile).Distinct())
        {
            streamFile.Scan([playlist], false);
        }

        playlist.Initialize();
        if (!playlist.IsValid)
        {
            throw new InvalidDataException($"The selected Blu-ray playlist '{playlistName}' is invalid.");
        }

        var after = GetDiscFingerprint(root);
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            throw new DiscChangedException();
        }

        return playlist;
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
        => MediaEncodingPathHelper.NormalizeBluRayPath(path);

    private BlurayDiscInfo GetDiscInfo(string root, TSPlaylistFile playlist, long playlistRevision, string? fingerprint)
    {
        var outputStream = new BlurayDiscInfo
        {
            PlaybackPlan = BuildPlaybackPlan(root, playlist, playlistRevision, fingerprint)
        };

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

    private BluRayPlaybackPlan BuildPlaybackPlan(string root, TSPlaylistFile playlist, long playlistRevision, string? fingerprint)
    {
        var playlistPath = FindPlaylistPath(root, playlist.Name);
        if (playlistPath is null)
        {
            return CreateFailedPlan(
                playlist.Name,
                playlistRevision,
                fingerprint,
                BluRayPlaybackPlanFailureReason.PlaylistNotFound,
                $"The selected Blu-ray playlist '{playlist.Name}' is not readable.");
        }

        MplsPlaylist rawPlaylist;
        try
        {
            rawPlaylist = MplsPlaylistParser.Parse(playlistPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
        {
            return CreateFailedPlan(
                playlist.Name,
                playlistRevision,
                fingerprint,
                BluRayPlaybackPlanFailureReason.MalformedPlaylist,
                ex.Message);
        }

        // Auxiliary SubPaths are not part of the primary PlayItem timeline. They can describe
        // dependent or interactive material; the stream-presence checks below ensure that the
        // selected primary streams are independently available from the primary clips.
        if (playlist.MVCBaseViewR)
        {
            return CreateFailedPlan(
                playlist.Name,
                playlistRevision,
                fingerprint,
                BluRayPlaybackPlanFailureReason.UnsupportedThreeDimensionalLayout,
                "MVC/SSIF Blu-ray playlists are not supported by the concat input.");
        }

        if (playlist.StreamClips is null || rawPlaylist.PlayItems.Count != playlist.StreamClips.Count)
        {
            return CreateFailedPlan(
                playlist.Name,
                playlistRevision,
                fingerprint,
                BluRayPlaybackPlanFailureReason.PlaylistMismatch,
                "The MPLS PlayItem list does not match the clips reported by BDInfo.");
        }

        var mediaStreams = GetMediaStreams(playlist);
        var playbackStreams = new List<BluRayPlaybackStream>(mediaStreams.Length);
        foreach (var mediaStream in mediaStreams.OrderBy(i => i.Index))
        {
            var bdStream = playlist.SortedStreams[mediaStream.Index];
            var codec = GetFfmpegCodecName(bdStream);
            if (string.IsNullOrWhiteSpace(codec))
            {
                return CreateFailedPlan(
                    playlist.Name,
                    playlistRevision,
                    fingerprint,
                    BluRayPlaybackPlanFailureReason.UnsupportedStreamCodec,
                    $"Blu-ray stream PID 0x{bdStream.PID:X4} has unsupported type {bdStream.StreamType}.");
            }

            playbackStreams.Add(new BluRayPlaybackStream
            {
                Index = mediaStream.Index,
                Pid = bdStream.PID,
                Type = mediaStream.Type,
                Codec = codec,
                Signature = GetStreamSignature(bdStream),
                FirstPlayItemStart45Khz = -1
            });
        }

        var primaryVideo = playbackStreams.FirstOrDefault(i => i.Type == MediaStreamType.Video);
        if (primaryVideo is null)
        {
            return CreateFailedPlan(
                playlist.Name,
                playlistRevision,
                fingerprint,
                BluRayPlaybackPlanFailureReason.MissingPrimaryVideo,
                "The selected playlist does not declare a primary video stream.");
        }

        var playbackItems = new List<BluRayPlaybackItem>(rawPlaylist.PlayItems.Count);
        long timelineStart = 0;
        for (var index = 0; index < rawPlaylist.PlayItems.Count; index++)
        {
            var rawItem = rawPlaylist.PlayItems[index];
            var clip = playlist.StreamClips[index];
            var fullPath = Path.GetFullPath(clip.StreamFile.FileInfo.FullName);
            var streamRoot = Path.GetFullPath(Path.Combine(root, "BDMV", "STREAM"));
            var relativePath = Path.GetRelativePath(streamRoot, fullPath);
            var clipFileName = Path.GetFileName(fullPath);

            if (Path.IsPathFullyQualified(relativePath)
                || relativePath.StartsWith("..", StringComparison.Ordinal)
                || !string.Equals(relativePath, clipFileName, StringComparison.Ordinal)
                || !string.Equals(Path.GetExtension(clipFileName), ".m2ts", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileNameWithoutExtension(clipFileName), rawItem.ClipId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(rawItem.CodecIdentifier, "M2TS", StringComparison.Ordinal))
            {
                return CreateFailedPlan(
                    playlist.Name,
                    playlistRevision,
                    fingerprint,
                    BluRayPlaybackPlanFailureReason.InvalidClipPath,
                    $"MPLS PlayItem {index} does not resolve to a canonical BDMV/STREAM M2TS clip.");
            }

            if (!_fileSystem.FileExists(fullPath))
            {
                return CreateFailedPlan(
                    playlist.Name,
                    playlistRevision,
                    fingerprint,
                    BluRayPlaybackPlanFailureReason.MissingClip,
                    $"Blu-ray clip '{clipFileName}' is missing or unreadable.");
            }

            if (rawItem.ConnectionCondition is not (1 or 5 or 6))
            {
                return CreateFailedPlan(
                    playlist.Name,
                    playlistRevision,
                    fingerprint,
                    BluRayPlaybackPlanFailureReason.UnsupportedConnectionCondition,
                    $"MPLS PlayItem {index} uses unsupported connection condition {rawItem.ConnectionCondition}.");
            }

            if (rawItem.StillMode != 0)
            {
                return CreateFailedPlan(
                    playlist.Name,
                    playlistRevision,
                    fingerprint,
                    BluRayPlaybackPlanFailureReason.UnsupportedStillMode,
                    $"MPLS PlayItem {index} uses still mode {rawItem.StillMode}.");
            }

            var duration = (long)rawItem.OutTime45Khz - rawItem.InTime45Khz;
            if (duration <= 0)
            {
                return CreateFailedPlan(
                    playlist.Name,
                    playlistRevision,
                    fingerprint,
                    BluRayPlaybackPlanFailureReason.InvalidPlayItemDuration,
                    $"MPLS PlayItem {index} has a non-positive duration.");
            }

            if (!clip.StreamFile.Streams.ContainsKey((ushort)primaryVideo.Pid))
            {
                return CreateFailedPlan(
                    playlist.Name,
                    playlistRevision,
                    fingerprint,
                    BluRayPlaybackPlanFailureReason.MissingPrimaryVideo,
                    $"Blu-ray clip '{clipFileName}' does not contain the primary video PID 0x{primaryVideo.Pid:X4}.");
            }

            foreach (var canonicalStream in playbackStreams)
            {
                if (!clip.StreamFile.Streams.TryGetValue((ushort)canonicalStream.Pid, out var clipStream))
                {
                    continue;
                }

                if (canonicalStream.FirstPlayItemStart45Khz < 0)
                {
                    canonicalStream.FirstPlayItemStart45Khz = timelineStart;
                }

                if (!string.Equals(
                    GetCompatibilitySignature(playlist.SortedStreams[canonicalStream.Index]),
                    GetCompatibilitySignature(clipStream),
                    StringComparison.Ordinal))
                {
                    return CreateFailedPlan(
                        playlist.Name,
                        playlistRevision,
                        fingerprint,
                        BluRayPlaybackPlanFailureReason.IncompatibleStream,
                        $"Blu-ray clip '{clipFileName}' reuses PID 0x{canonicalStream.Pid:X4} with incompatible stream parameters.");
                }
            }

            playbackItems.Add(new BluRayPlaybackItem
            {
                ClipFileName = clipFileName,
                InTime45Khz = rawItem.InTime45Khz,
                OutTime45Khz = rawItem.OutTime45Khz,
                TimelineStart45Khz = timelineStart,
                ConnectionCondition = rawItem.ConnectionCondition,
                StcId = rawItem.StcId,
                IsMultiAngle = rawItem.IsMultiAngle
            });
            timelineStart = checked(timelineStart + duration);
        }

        var missingStream = playbackStreams.FirstOrDefault(i => i.FirstPlayItemStart45Khz < 0);
        if (missingStream is not null)
        {
            return CreateFailedPlan(
                playlist.Name,
                playlistRevision,
                fingerprint,
                BluRayPlaybackPlanFailureReason.MissingDeclaredStream,
                $"Blu-ray stream PID 0x{missingStream.Pid:X4} is declared by the playlist but does not occur in any PlayItem.");
        }

        var plan = new BluRayPlaybackPlan
        {
            PlaylistName = playlist.Name,
            PlaylistRevision = playlistRevision,
            DiscFingerprint = fingerprint,
            Duration45Khz = timelineStart,
            Streams = playbackStreams.ToArray(),
            PlayItems = playbackItems.ToArray()
        };
        plan.UpdatePlanHash();
        return plan;
    }

    private string? FindPlaylistPath(string root, string playlistName)
    {
        foreach (var directory in new[]
                 {
                     Path.Combine(root, "BDMV", "PLAYLIST"),
                     Path.Combine(root, "BDMV", "BACKUP", "PLAYLIST")
                 })
        {
            if (!_fileSystem.DirectoryExists(directory))
            {
                continue;
            }

            var file = _fileSystem.GetFiles(directory, false)
                .FirstOrDefault(i => string.Equals(i.Name, playlistName, StringComparison.OrdinalIgnoreCase));
            if (file is not null)
            {
                return file.FullName;
            }
        }

        return null;
    }

    private static BluRayPlaybackPlan CreateFailedPlan(
        string? playlistName,
        long playlistRevision,
        string? fingerprint,
        BluRayPlaybackPlanFailureReason reason,
        string message)
    {
        var plan = new BluRayPlaybackPlan
        {
            PlaylistName = playlistName,
            PlaylistRevision = playlistRevision,
            DiscFingerprint = fingerprint,
            FailureReason = reason,
            FailureMessage = message
        };
        plan.UpdatePlanHash();
        return plan;
    }

    private string? GetFfmpegCodecName(TSStream stream)
        => stream.StreamType switch
        {
            TSStreamType.MPEG1_VIDEO => "mpeg1video",
            TSStreamType.MPEG2_VIDEO => "mpeg2video",
            TSStreamType.AVC_VIDEO => "h264",
            TSStreamType.HEVC_VIDEO => "hevc",
            TSStreamType.VC1_VIDEO => "vc1",
            TSStreamType.MPEG1_AUDIO or TSStreamType.MPEG2_AUDIO => "mp2",
            TSStreamType.MPEG2_AAC_AUDIO or TSStreamType.MPEG4_AAC_AUDIO => "aac",
            TSStreamType.LPCM_AUDIO => "pcm_bluray",
            TSStreamType.AC3_AUDIO => "ac3",
            TSStreamType.AC3_TRUE_HD_AUDIO => "truehd",
            TSStreamType.AC3_PLUS_AUDIO or TSStreamType.AC3_PLUS_SECONDARY_AUDIO => "eac3",
            TSStreamType.DTS_AUDIO or TSStreamType.DTS_HD_AUDIO or TSStreamType.DTS_HD_MASTER_AUDIO or TSStreamType.DTS_HD_SECONDARY_AUDIO => "dts",
            TSStreamType.PRESENTATION_GRAPHICS => "hdmv_pgs_subtitle",
            TSStreamType.SUBTITLE => "hdmv_text_subtitle",
            _ => null
        };

    private string GetStreamSignature(TSStream stream)
        => stream switch
        {
            TSVideoStream video => string.Create(
                CultureInfo.InvariantCulture,
                $"{GetNormalizedCodec(stream)}|{video.Width}|{video.Height}|{video.FrameRateEnumerator}|{video.FrameRateDenominator}|{video.IsInterlaced}"),
            TSAudioStream audio => string.Create(
                CultureInfo.InvariantCulture,
                $"{GetNormalizedCodec(stream)}|{audio.ChannelCount}|{audio.LFE}|{audio.SampleRate}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{GetNormalizedCodec(stream)}|{stream.LanguageCode}")
        };

    private static string GetCompatibilitySignature(TSStream stream)
        => stream.StreamType.ToString();

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

    private static IReadOnlyList<BdInfoPlaylistCandidate> GetCatalogPlaylists(BDROM bdrom)
        => bdrom.PlaylistFiles.Values
            .Where(i => i.StreamClips.Count > 0 && i.TotalLength > 0)
            .Select(i => new BdInfoPlaylistCandidate(
                i.Name,
                TimeSpan.FromSeconds(i.TotalLength).Ticks,
                i,
                !ContainsRepeatedPlayItem(i)))
            .ToArray();

    private static bool ContainsRepeatedPlayItem(TSPlaylistFile playlist)
    {
        var items = new HashSet<(string Name, double TimeIn, double TimeOut, int AngleIndex)>();
        foreach (var clip in playlist.StreamClips)
        {
            if (!items.Add((clip.StreamFile.Name, clip.TimeIn, clip.TimeOut, clip.AngleIndex)))
            {
                return true;
            }
        }

        return false;
    }

    internal static BdInfoPlaylistCandidate? SelectPlaylist(IReadOnlyList<BdInfoPlaylistCandidate> playlists, string? playlistName)
    {
        if (TryGetCanonicalPlaylistName(playlists, playlistName, out var canonicalName))
        {
            return playlists.First(i => string.Equals(i.Name, canonicalName, StringComparison.Ordinal));
        }

        return SelectDefaultPlaylist(playlists);
    }

    internal static BdInfoPlaylistCandidate? SelectDefaultPlaylist(IReadOnlyList<BdInfoPlaylistCandidate> playlists)
    {
        var candidates = playlists.Any(i => i.IsDefaultEligible)
            ? playlists.Where(i => i.IsDefaultEligible)
            : playlists;
        return candidates
            .OrderByDescending(i => i.RunTimeTicks)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

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
