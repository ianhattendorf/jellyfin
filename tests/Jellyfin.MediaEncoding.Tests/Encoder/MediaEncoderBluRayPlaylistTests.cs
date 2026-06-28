using System;
using System.IO;
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
    public void GetPrimaryPlaylistM2tsFiles_PassesPlaylistNameToBlurayExaminer()
    {
        var blurayExaminer = new Mock<IBlurayExaminer>(MockBehavior.Strict);
        blurayExaminer
            .Setup(i => i.GetDiscInfo("/media/movie", "00800.mpls"))
            .Returns(new BlurayDiscInfo
            {
                Files = ["/media/movie/BDMV/STREAM/00001.m2ts"]
            });

        var mediaEncoder = CreateMediaEncoder(blurayExaminer.Object);

        var result = mediaEncoder.GetPrimaryPlaylistM2tsFiles("/media/movie", "00800.mpls");

        Assert.Equal(["/media/movie/BDMV/STREAM/00001.m2ts"], result);
        blurayExaminer.Verify(i => i.GetDiscInfo("/media/movie", "00800.mpls"), Times.Once);
    }

    [Fact]
    public void GetConcatConfigPath_ForBluRayPlaylist_IncludesPlaylistHash()
    {
        var first = new MediaSourceInfo
        {
            Id = "source",
            VideoType = VideoType.BluRay,
            BluRayPlaylistName = "00800.mpls"
        };
        var second = new MediaSourceInfo
        {
            Id = "source",
            VideoType = VideoType.BluRay,
            BluRayPlaylistName = "00801.mpls"
        };
        var automatic = new MediaSourceInfo
        {
            Id = "source",
            VideoType = VideoType.BluRay
        };

        var firstPath = MediaEncodingPathHelper.GetConcatConfigPath("/cache", first);
        var secondPath = MediaEncodingPathHelper.GetConcatConfigPath("/cache", second);
        var automaticPath = MediaEncodingPathHelper.GetConcatConfigPath("/cache", automatic);

        Assert.NotEqual(firstPath, secondPath);
        Assert.EndsWith(".concat", firstPath, StringComparison.Ordinal);
        Assert.Equal(Path.Join("/cache", "concat", "source.concat"), automaticPath);
    }

    private static MediaEncoder CreateMediaEncoder(IBlurayExaminer blurayExaminer)
        => new(
            NullLogger<MediaEncoder>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<IFileSystem>(),
            blurayExaminer,
            Mock.Of<ILocalizationManager>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IServerConfigurationManager>());
}
