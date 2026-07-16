using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Encoder;

public class MediaEncoderBluRayPlaylistTests
{
    [Fact]
    public void IMediaEncoder_PreservesLegacyBluRayPlaylistContract()
    {
        var methods = typeof(IMediaEncoder).GetMethods();
        var legacyMethod = Assert.Single(methods, method =>
            method.Name == nameof(IMediaEncoder.GetPrimaryPlaylistM2tsFiles)
            && method.GetParameters().Length == 1);
        var selectedMethod = Assert.Single(methods, method =>
            method.Name == nameof(IMediaEncoder.GetPrimaryPlaylistM2tsFiles)
            && method.GetParameters().Length == 2);

        Assert.True(legacyMethod.IsAbstract);
        Assert.False(selectedMethod.IsAbstract);
        Assert.False(typeof(IMediaEncoder).GetMethod(nameof(IMediaEncoder.GetInputOptions))!.IsAbstract);
    }

    [Fact]
    public void IAttachmentExtractor_PreservesLegacyExternalAttachmentContract()
    {
        var method = typeof(IAttachmentExtractor).GetMethod(nameof(IAttachmentExtractor.ExtractAllAttachmentsFromExternalFile));

        Assert.NotNull(method);
        Assert.False(method.IsAbstract);
    }

    [Fact]
    public void GetInputOptions_WithBluRayPlan_ReturnsConcatOptions()
    {
        var mediaEncoder = CreateMediaEncoder();
        var mediaSource = new MediaSourceInfo
        {
            VideoType = VideoType.BluRay,
            BluRayPlaybackPlan = CreatePlan()
        };

        var result = mediaEncoder.GetInputOptions(mediaSource);

        Assert.Equal("-f concat -safe 0", result);
    }

    [Fact]
    public void GetInputPathArgument_WithBluRayPlan_GeneratesImmutableConcatManifest()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("jellyfin-bluray-concat-test-");
        try
        {
            var streamDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "movie", "BDMV", "STREAM"));
            File.WriteAllBytes(Path.Combine(streamDirectory.FullName, "00001.m2ts"), [0x47]);
            var cachePath = Path.Combine(tempDirectory.FullName, "cache");
            var mediaEncoder = CreateMediaEncoder(cachePath: cachePath);
            var mediaSource = new MediaSourceInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = Path.Combine(tempDirectory.FullName, "movie"),
                VideoType = VideoType.BluRay,
                Protocol = MediaProtocol.File,
                BluRayPlaybackPlan = CreatePlan()
            };

            var result = mediaEncoder.GetInputPathArgument(mediaSource.Path, mediaSource);
            var concatPath = MediaEncodingPathHelper.GetBluRayConcatConfigPath(cachePath, mediaSource);
            var manifest = File.ReadAllText(concatPath);

            Assert.Equal($"file:\"{concatPath}\"", result);
            Assert.Contains("ffconcat version 1.0", manifest, StringComparison.Ordinal);
            Assert.Contains("exact_stream_id 0x1011", manifest, StringComparison.Ordinal);
            Assert.Contains("stream_codec h264", manifest, StringComparison.Ordinal);
            Assert.Contains("inpoint 1", manifest, StringComparison.Ordinal);
            Assert.Contains("outpoint 3", manifest, StringComparison.Ordinal);
            Assert.Contains("duration 2", manifest, StringComparison.Ordinal);
        }
        finally
        {
            tempDirectory.Delete(true);
        }
    }

    [Fact]
    public void GetInputPathArgument_WithBluRaySeek_StartsManifestInsideContainingPlayItem()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("jellyfin-bluray-seek-concat-test-");
        try
        {
            var streamDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "movie", "BDMV", "STREAM"));
            File.WriteAllBytes(Path.Combine(streamDirectory.FullName, "00001.m2ts"), [0x47]);
            File.WriteAllBytes(Path.Combine(streamDirectory.FullName, "00002.m2ts"), [0x47]);
            var cachePath = Path.Combine(tempDirectory.FullName, "cache");
            var mediaEncoder = CreateMediaEncoder(cachePath: cachePath);
            var plan = CreatePlan();
            plan.Duration45Khz = 1_350_000;
            plan.PlayItems =
            [
                new BluRayPlaybackItem
                {
                    ClipFileName = "00001.m2ts",
                    InTime45Khz = 45_000,
                    OutTime45Khz = 495_000,
                    TimelineStart45Khz = 0
                },
                new BluRayPlaybackItem
                {
                    ClipFileName = "00002.m2ts",
                    InTime45Khz = 4_500_000,
                    OutTime45Khz = 5_400_000,
                    TimelineStart45Khz = 450_000
                }
            ];
            plan.UpdatePlanHash();
            var mediaSource = new MediaSourceInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = Path.Combine(tempDirectory.FullName, "movie"),
                VideoType = VideoType.BluRay,
                Protocol = MediaProtocol.File,
                BluRayPlaybackPlan = plan
            };
            var state = new EncodingJobInfo(TranscodingJobType.Hls)
            {
                MediaPath = mediaSource.Path,
                MediaSource = mediaSource,
                BaseRequest = new BaseEncodingJobOptions { StartTimeTicks = TimeSpan.FromSeconds(15).Ticks }
            };

            var result = mediaEncoder.GetInputPathArgument(state);
            var concatPath = MediaEncodingPathHelper.GetBluRayConcatConfigPath(
                cachePath,
                mediaSource,
                state.StartTimeTicks);
            var manifest = File.ReadAllText(concatPath);

            Assert.Equal($"file:\"{concatPath}\"", result);
            Assert.DoesNotContain("00001.m2ts", manifest, StringComparison.Ordinal);
            Assert.Contains("00002.m2ts", manifest, StringComparison.Ordinal);
            Assert.Contains("inpoint 105", manifest, StringComparison.Ordinal);
            Assert.Contains("outpoint 120", manifest, StringComparison.Ordinal);
            Assert.Contains("duration 15", manifest, StringComparison.Ordinal);
        }
        finally
        {
            tempDirectory.Delete(true);
        }
    }

    [Fact]
    public void GetMediaInfoInputArgument_WithDvd_ProbesRequestedFileWithoutEnumeratingDisc()
    {
        var fileSystem = new Mock<IFileSystem>();
        var mediaEncoder = CreateMediaEncoder(fileSystem.Object);
        var request = new MediaInfoRequest
        {
            MediaSource = new MediaSourceInfo
            {
                Path = "/media/dvd/VIDEO_TS/VTS_01_1.VOB",
                VideoType = VideoType.Dvd,
                Protocol = MediaProtocol.File
            }
        };

        var result = mediaEncoder.GetMediaInfoInputArgument(request);

        Assert.Equal("file:\"/media/dvd/VIDEO_TS/VTS_01_1.VOB\"", result);
        fileSystem.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetPrimaryPlaylistVobFiles_WithoutVobs_ReturnsEmptyList()
    {
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(i => i.GetFiles("/media/dvd", true)).Returns([]);
        var mediaEncoder = CreateMediaEncoder(fileSystem.Object);

        var result = mediaEncoder.GetPrimaryPlaylistVobFiles("/media/dvd", null);

        Assert.Empty(result);
    }

    [Fact]
    public void GetInputPathArgument_WithRegularVideoFile_PreservesFileInput()
    {
        var mediaEncoder = CreateMediaEncoder();
        var mediaSource = new MediaSourceInfo
        {
            VideoType = VideoType.VideoFile,
            Protocol = MediaProtocol.File
        };

        var result = mediaEncoder.GetInputPathArgument("/media/movie/movie.mkv", mediaSource);

        Assert.Equal("file:\"/media/movie/movie.mkv\"", result);
    }

    [Fact]
    public void GetInputPathArgument_WithoutPreparedPlan_FailsOnlyBluRayInput()
    {
        var mediaEncoder = CreateMediaEncoder();
        var bluRaySource = new MediaSourceInfo { VideoType = VideoType.BluRay, Protocol = MediaProtocol.File };
        var regularSource = new MediaSourceInfo { VideoType = VideoType.VideoFile, Protocol = MediaProtocol.File };

        var exception = Assert.Throws<FfmpegException>(() =>
            mediaEncoder.GetInputPathArgument("/media/movie", bluRaySource));

        Assert.Contains("has not been prepared", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            "file:\"/media/movie/movie.mkv\"",
            mediaEncoder.GetInputPathArgument("/media/movie/movie.mkv", regularSource));
    }

    [Fact]
    public void GetInputPathArgument_WithUnsupportedPlan_FailsClearly()
    {
        var mediaEncoder = CreateMediaEncoder();
        var mediaSource = new MediaSourceInfo
        {
            VideoType = VideoType.BluRay,
            Protocol = MediaProtocol.File,
            BluRayPlaybackPlan = new BluRayPlaybackPlan
            {
                PlaylistName = "00800.mpls",
                FailureReason = BluRayPlaybackPlanFailureReason.UnsupportedSubPath,
                FailureMessage = "authored subpaths are unsupported"
            }
        };

        var exception = Assert.Throws<FfmpegException>(() =>
            mediaEncoder.GetInputPathArgument("/media/movie", mediaSource));

        Assert.Contains("authored subpaths are unsupported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetInputOptions_WithRegularVideoFile_DoesNotUseBluRayPlaylistMetadata()
    {
        var mediaEncoder = CreateMediaEncoder();
        var mediaSource = new MediaSourceInfo
        {
            VideoType = VideoType.VideoFile,
            BluRayPlaylistName = "00800.mpls"
        };

        var result = mediaEncoder.GetInputOptions(mediaSource);

        Assert.Empty(result);
    }

    [Fact]
    public void GetInputOptions_WithBluRayWithoutPlan_ThrowsFfmpegException()
    {
        var mediaEncoder = CreateMediaEncoder();
        var mediaSource = new MediaSourceInfo
        {
            VideoType = VideoType.BluRay,
        };

        Assert.Throws<FfmpegException>(() => mediaEncoder.GetInputOptions(mediaSource));
    }

    private static Mock<IBlurayExaminer> CreateExaminer(int runtimeSeconds = 180)
    {
        var examiner = new Mock<IBlurayExaminer>();
        examiner
            .Setup(i => i.GetDiscPlaylists(It.IsAny<string>()))
            .Returns(
            [
                new BluRayPlaylistInfoDto
                {
                    Name = "00800.mpls",
                    RunTimeTicks = TimeSpan.FromSeconds(runtimeSeconds).Ticks
                }
            ]);
        return examiner;
    }

    private static MediaEncoder CreateMediaEncoder(
        IFileSystem? fileSystem = null,
        IBlurayExaminer? examiner = null,
        IEnumerable<string>? inputProtocols = null,
        string? cachePath = null)
    {
        var applicationPaths = Mock.Of<IApplicationPaths>(i => i.CachePath == (cachePath ?? "/cache"));
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.SetupGet(i => i.CommonApplicationPaths).Returns(applicationPaths);
        var mediaEncoder = new MediaEncoder(
            NullLogger<MediaEncoder>.Instance,
            configurationManager.Object,
            fileSystem ?? Mock.Of<IFileSystem>(),
            examiner ?? CreateExaminer().Object,
            Mock.Of<ILocalizationManager>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IServerConfigurationManager>());
        mediaEncoder.SetAvailableInputProtocols(inputProtocols ?? ["bluray"]);
        return mediaEncoder;
    }

    private static BluRayPlaybackPlan CreatePlan()
    {
        var plan = new BluRayPlaybackPlan
        {
            PlaylistName = "00800.mpls",
            PlaylistRevision = 1,
            DiscFingerprint = "disc",
            Duration45Khz = 90000,
            Streams =
            [
                new BluRayPlaybackStream
                {
                    Index = 0,
                    Pid = 0x1011,
                    Type = MediaStreamType.Video,
                    Codec = "h264",
                    Signature = "h264",
                    FirstPlayItemStart45Khz = 0
                }
            ],
            PlayItems =
            [
                new BluRayPlaybackItem
                {
                    ClipFileName = "00001.m2ts",
                    InTime45Khz = 45000,
                    OutTime45Khz = 135000
                }
            ]
        };
        plan.UpdatePlanHash();
        return plan;
    }
}
