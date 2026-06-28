using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class VideosControllerBluRayPlaylistTests
{
    [Fact]
    public async Task GetBluRayPlaylists_WithNonBluRayItem_ReturnsBadRequest()
    {
        var itemId = Guid.NewGuid();
        var libraryManager = CreateLibraryManager(itemId, new Video { Id = itemId, VideoType = VideoType.VideoFile });
        var playlistManager = new Mock<IBluRayPlaylistManager>();
        var controller = CreateController(libraryManager.Object, playlistManager.Object);

        var result = await controller.GetBluRayPlaylists(itemId, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        playlistManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetBluRayPlaylists_WithBluRayItem_DelegatesToManager()
    {
        var itemId = Guid.NewGuid();
        var video = CreateBluRayVideo(itemId);
        var expected = new BluRayPlaylistListDto { EffectivePlaylistName = "00800.mpls" };
        var libraryManager = CreateLibraryManager(itemId, video);
        var playlistManager = new Mock<IBluRayPlaylistManager>();
        playlistManager
            .Setup(i => i.GetPlaylistsAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = CreateController(libraryManager.Object, playlistManager.Object);

        var result = await controller.GetBluRayPlaylists(itemId, CancellationToken.None);

        Assert.Same(expected, result.Value);
        playlistManager.Verify(i => i.GetPlaylistsAsync(video, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetBluRayPlaylist_WithEmptyPlaylistName_ReturnsBadRequestWithoutUpdating()
    {
        var playlistManager = new Mock<IBluRayPlaylistManager>();
        var controller = CreateController(Mock.Of<ILibraryManager>(), playlistManager.Object);

        var result = await controller.SetBluRayPlaylist(
            Guid.NewGuid(),
            new UpdateBluRayPlaylistDto(),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        playlistManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetBluRayPlaylist_WithInvalidPlaylist_ReturnsBadRequest()
    {
        var itemId = Guid.NewGuid();
        var video = CreateBluRayVideo(itemId);
        var libraryManager = CreateLibraryManager(itemId, video);
        var playlistManager = new Mock<IBluRayPlaylistManager>();
        playlistManager
            .Setup(i => i.UpdatePlaylistAsync(video, "invalid.mpls", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Invalid playlist."));
        var controller = CreateController(libraryManager.Object, playlistManager.Object);

        var result = await controller.SetBluRayPlaylist(
            itemId,
            new UpdateBluRayPlaylistDto { PlaylistName = "invalid.mpls" },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Invalid playlist.", badRequest.Value);
    }

    [Fact]
    public async Task SetBluRayPlaylist_WithBluRayItem_DelegatesToManager()
    {
        var itemId = Guid.NewGuid();
        var video = CreateBluRayVideo(itemId);
        var expected = new BluRayPlaylistListDto { SelectedPlaylistName = "00800.mpls" };
        var libraryManager = CreateLibraryManager(itemId, video);
        var playlistManager = new Mock<IBluRayPlaylistManager>();
        playlistManager
            .Setup(i => i.UpdatePlaylistAsync(video, "00800.mpls", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = CreateController(libraryManager.Object, playlistManager.Object);

        var result = await controller.SetBluRayPlaylist(
            itemId,
            new UpdateBluRayPlaylistDto { PlaylistName = "00800.mpls" },
            CancellationToken.None);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task ClearBluRayPlaylist_WithBluRayItem_DelegatesToManager()
    {
        var itemId = Guid.NewGuid();
        var video = CreateBluRayVideo(itemId);
        var expected = new BluRayPlaylistListDto { EffectivePlaylistName = "00800.mpls" };
        var libraryManager = CreateLibraryManager(itemId, video);
        var playlistManager = new Mock<IBluRayPlaylistManager>();
        playlistManager
            .Setup(i => i.UpdatePlaylistAsync(video, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = CreateController(libraryManager.Object, playlistManager.Object);

        var result = await controller.ClearBluRayPlaylist(itemId, CancellationToken.None);

        Assert.Same(expected, result.Value);
    }

    [Theory]
    [InlineData(LocationType.Virtual)]
    [InlineData(LocationType.Offline)]
    [InlineData(LocationType.Remote)]
    public async Task GetBluRayPlaylists_WithNonLocalItem_ReturnsBadRequest(LocationType locationType)
    {
        var itemId = Guid.NewGuid();
        var videoMock = new Mock<Video> { CallBase = true };
        videoMock.Object.Id = itemId;
        videoMock.Object.Path = "/media/movie";
        videoMock.Object.VideoType = VideoType.BluRay;
        videoMock.SetupGet(i => i.LocationType).Returns(locationType);
        var video = videoMock.Object;
        var libraryManager = CreateLibraryManager(itemId, video);
        var playlistManager = new Mock<IBluRayPlaylistManager>();
        var controller = CreateController(libraryManager.Object, playlistManager.Object);

        var result = await controller.GetBluRayPlaylists(itemId, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        playlistManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetBluRayPlaylists_WithRemoteProtocol_ReturnsBadRequest()
    {
        var itemId = Guid.NewGuid();
        var video = CreateBluRayVideo(itemId);
        var libraryManager = CreateLibraryManager(itemId, video);
        var playlistManager = new Mock<IBluRayPlaylistManager>();
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(i => i.GetPathProtocol(video.Path)).Returns(MediaProtocol.Http);
        var controller = CreateController(libraryManager.Object, playlistManager.Object, mediaSourceManager.Object);

        var result = await controller.GetBluRayPlaylists(itemId, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        playlistManager.VerifyNoOtherCalls();
    }

    private static Video CreateBluRayVideo(Guid itemId)
    {
        var video = new Mock<Video> { CallBase = true };
        video.Object.Id = itemId;
        video.Object.Path = "/media/movie";
        video.Object.VideoType = VideoType.BluRay;
        video.SetupGet(i => i.LocationType).Returns(LocationType.FileSystem);
        return video.Object;
    }

    private static Mock<ILibraryManager> CreateLibraryManager(Guid itemId, Video video)
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(i => i.GetItemById<Video>(itemId, Guid.Empty)).Returns(video);
        return libraryManager;
    }

    private static VideosController CreateController(
        ILibraryManager libraryManager,
        IBluRayPlaylistManager playlistManager,
        IMediaSourceManager? mediaSourceManager = null)
    {
        mediaSourceManager ??= Mock.Of<IMediaSourceManager>(i => i.GetPathProtocol(It.IsAny<string>()) == MediaProtocol.File);
        var controller = new VideosController(
            libraryManager,
            Mock.Of<IUserManager>(),
            Mock.Of<IDtoService>(),
            mediaSourceManager,
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<IMediaEncoder>(),
            playlistManager,
            Mock.Of<ITranscodeManager>(),
            null!,
            null!);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }
}
