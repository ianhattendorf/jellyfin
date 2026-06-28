using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Chapters;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Chapters;

public class ChapterManagerTests
{
    [Fact]
    public async Task RefreshChapterImages_ForBluRay_UsesEffectivePlaylistForExtraction()
    {
        var outputDirectory = Path.Join(Path.GetTempPath(), "jellyfin-chapters-" + Guid.NewGuid().ToString("N"));
        var tempImage = Path.Join(Path.GetTempPath(), "jellyfin-chapter-" + Guid.NewGuid().ToString("N") + ".jpg");
        var cancellationToken = TestContext.Current.CancellationToken;
        var previousMediaSourceManager = BaseItem.MediaSourceManager;
        var previousRecordingsManager = Video.RecordingsManager;
        await File.WriteAllTextAsync(tempImage, "image", cancellationToken);

        try
        {
            var videoStream = new MediaStream
            {
                Index = 0,
                Type = MediaStreamType.Video
            };
            var video = new Video
            {
                Id = Guid.NewGuid(),
                Name = "Movie",
                Path = "/media/movie",
                Container = "mkv",
                VideoType = VideoType.BluRay,
                BluRayPlaylistName = "00801.mpls",
                BluRayPlaylistNameIsValid = true,
                BluRayLastProbedPlaylistName = "00801.mpls",
                BluRayPlaylistProbeVersion = Video.CurrentBluRayPlaylistProbeVersion,
                DefaultVideoStreamIndex = 0,
                RunTimeTicks = TimeSpan.FromMinutes(1).Ticks
            };
            var capturedMediaSource = default(MediaSourceInfo);
            var mediaEncoder = new Mock<IMediaEncoder>();
            mediaEncoder
                .Setup(i => i.ExtractVideoImage(
                    video.Path,
                    video.Container,
                    It.IsAny<MediaSourceInfo>(),
                    videoStream,
                    video.Video3DFormat,
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, string, MediaSourceInfo, MediaStream, Video3DFormat?, TimeSpan?, CancellationToken>((_, _, mediaSource, _, _, _, _) => capturedMediaSource = mediaSource)
                .ReturnsAsync(tempImage);
            var mediaSourceManager = new Mock<IMediaSourceManager>();
            mediaSourceManager
                .Setup(i => i.GetMediaStreams(It.IsAny<MediaStreamQuery>()))
                .Returns([videoStream]);
            BaseItem.MediaSourceManager = mediaSourceManager.Object;
            Video.RecordingsManager = Mock.Of<IRecordingsManager>(i => i.GetActiveRecordingInfo(video.Path) == null);

            var pathManager = new Mock<IPathManager>();
            pathManager.Setup(i => i.GetChapterImageFolderPath(video)).Returns(outputDirectory);
            pathManager.Setup(i => i.GetChapterImagePath(video, 0)).Returns(Path.Join(outputDirectory, "0.jpg"));
            var fileSystem = new Mock<IFileSystem>();
            fileSystem.Setup(i => i.GetLastWriteTimeUtc(It.IsAny<string>())).Returns(DateTime.UtcNow);
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager
                .Setup(i => i.GetLibraryOptions(video))
                .Returns(new LibraryOptions { EnableChapterImageExtraction = true });
            var chapterManager = new ChapterManager(
                NullLogger<ChapterManager>.Instance,
                fileSystem.Object,
                mediaEncoder.Object,
                Mock.Of<IChapterRepository>(),
                libraryManager.Object,
                pathManager.Object);

            await chapterManager.RefreshChapterImages(
                video,
                Mock.Of<IDirectoryService>(),
                [new ChapterInfo { StartPositionTicks = 0 }],
                true,
                false,
                cancellationToken);

            Assert.NotNull(capturedMediaSource);
            Assert.Equal("00801.mpls", capturedMediaSource.BluRayPlaylistName);
        }
        finally
        {
            BaseItem.MediaSourceManager = previousMediaSourceManager;
            Video.RecordingsManager = previousRecordingsManager;
            File.Delete(tempImage);
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, true);
            }
        }
    }
}
