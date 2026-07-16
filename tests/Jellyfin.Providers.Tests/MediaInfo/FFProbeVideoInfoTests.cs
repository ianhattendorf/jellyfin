using System;
using System.Collections.Generic;
using AutoFixture;
using AutoFixture.AutoMoq;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Providers.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.MediaInfo;

public class FFProbeVideoInfoTests
{
    private readonly FFProbeVideoInfo _fFProbeVideoInfo;

    public FFProbeVideoInfoTests()
    {
        var serverConfiguration = new ServerConfiguration()
        {
            DummyChapterDuration = (int)TimeSpan.FromMinutes(5).TotalSeconds
        };
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(c => c.Configuration)
            .Returns(serverConfiguration);

        IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject(serverConfig);
        _fFProbeVideoInfo = fixture.Create<FFProbeVideoInfo>();
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void CreateDummyChapters_InvalidRuntime_ThrowsArgumentException(long? runtime)
    {
        Assert.Throws<ArgumentException>(
            () => _fFProbeVideoInfo.CreateDummyChapters(new Video()
            {
                RunTimeTicks = runtime
            }));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0L, 0)]
    [InlineData(1L, 1)]
    [InlineData(TimeSpan.TicksPerMinute * 3, 1)]
    [InlineData(TimeSpan.TicksPerMinute * 5, 1)]
    [InlineData((TimeSpan.TicksPerMinute * 5) + 1, 1)]
    [InlineData(TimeSpan.TicksPerMinute * 50, 10)]
    public void CreateDummyChapters_ValidRuntime_CorrectChaptersCount(long? runtime, int chaptersCount)
    {
        var chapters = _fFProbeVideoInfo.CreateDummyChapters(new Video()
        {
            RunTimeTicks = runtime
        });

        Assert.Equal(chaptersCount, chapters.Length);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(TimeSpan.TicksPerMinute * 3)]
    [InlineData(TimeSpan.TicksPerMinute * 5)]
    [InlineData((TimeSpan.TicksPerMinute * 5) + 1)]
    [InlineData((TimeSpan.TicksPerMinute * 50) + 1)]
    public void CreateDummyChapters_PositiveRuntime_NoChapterBeyondRuntime(long runtime)
    {
        var chapters = _fFProbeVideoInfo.CreateDummyChapters(new Video()
        {
            RunTimeTicks = runtime
        });

        Assert.All(chapters, chapter => Assert.True(chapter.StartPositionTicks < runtime));
    }

    [Fact]
    public void FetchBdInfo_WithValidManualPlaylist_MarksSelectionValid()
    {
        var video = new Video
        {
            BluRayPlaylistName = "00801.mpls"
        };
        var chapters = Array.Empty<ChapterInfo>();
        var streams = new List<MediaStream>();

        _fFProbeVideoInfo.FetchBdInfo(
            video,
            ref chapters,
            streams,
            new BlurayDiscInfo { PlaylistName = "00801.MPLS" });

        Assert.True(video.BluRayPlaylistNameIsValid);
        Assert.Equal("00801.mpls", video.BluRayLastProbedPlaylistName);
    }

    [Fact]
    public void FetchBdInfo_WithInvalidManualPlaylist_MarksSelectionInvalidWithoutPersistingDefault()
    {
        var video = new Video
        {
            BluRayPlaylistName = "00999.mpls"
        };
        var chapters = Array.Empty<ChapterInfo>();
        var streams = new List<MediaStream>();

        _fFProbeVideoInfo.FetchBdInfo(
            video,
            ref chapters,
            streams,
            new BlurayDiscInfo { PlaylistName = "00802.mpls" });

        Assert.False(video.BluRayPlaylistNameIsValid);
        Assert.Null(video.BluRayLastProbedPlaylistName);
        Assert.Null(video.EffectiveBluRayPlaylistName);
    }

    [Fact]
    public void FetchBdInfo_WithoutManualPlaylist_ClearsValidityAndLeavesSelectionNull()
    {
        var video = new Video
        {
            BluRayPlaylistNameIsValid = false
        };
        var chapters = Array.Empty<ChapterInfo>();
        var streams = new List<MediaStream>();

        _fFProbeVideoInfo.FetchBdInfo(
            video,
            ref chapters,
            streams,
            new BlurayDiscInfo { PlaylistName = "00803.mpls" });

        Assert.Null(video.BluRayPlaylistNameIsValid);
        Assert.Null(video.BluRayLastProbedPlaylistName);
        Assert.Null(video.EffectiveBluRayPlaylistName);
    }

    [Fact]
    public void FetchBdInfo_WithSelectedPlaylist_ReplacesRuntimeAndMediaStreams()
    {
        var video = new Video
        {
            BluRayPlaylistName = "00100.mpls",
            RunTimeTicks = TimeSpan.FromHours(2).Ticks
        };
        var chapters = Array.Empty<ChapterInfo>();
        var streams = new List<MediaStream>
        {
            new()
            {
                Type = MediaStreamType.Video,
                Width = 720,
                Height = 480
            }
        };
        var selectedRuntime = TimeSpan.FromMinutes(95).Ticks;

        _fFProbeVideoInfo.FetchBdInfo(
            video,
            ref chapters,
            streams,
            new BlurayDiscInfo
            {
                PlaylistName = "00100.mpls",
                RunTimeTicks = selectedRuntime,
                MediaStreams =
                [
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Width = 1920,
                        Height = 1080
                    }
                ]
            });

        Assert.Equal(selectedRuntime, video.RunTimeTicks);
        var videoStream = Assert.Single(streams);
        Assert.Equal(1920, videoStream.Width);
        Assert.Equal(1080, videoStream.Height);
        Assert.Equal("00100.mpls", video.BluRayLastProbedPlaylistName);
    }
}
