using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class BluRayPlaylistManagerTests : IDisposable
{
    private const string DiscFingerprint = "disc-fingerprint";
    private readonly IProviderManager _previousProviderManager = BaseItem.ProviderManager;
    private readonly IFileSystem _previousFileSystem = BaseItem.FileSystem;

    public BluRayPlaylistManagerTests()
    {
        BaseItem.FileSystem = Mock.Of<IFileSystem>();
    }

    [Fact]
    public async Task GetPlaylistsAsync_WithInvalidSavedSelection_UsesDefaultFromSingleScan()
    {
        var video = CreateVideo().Object;
        video.BluRayPlaylistName = "00999.mpls";
        var examiner = CreateExaminer();
        var providerManager = new Mock<IProviderManager>();
        providerManager
            .Setup(i => i.RefreshSingleItem(video, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => MarkReconciled(video))
            .ReturnsAsync(ItemUpdateType.MetadataImport);
        BaseItem.ProviderManager = providerManager.Object;
        using var manager = CreateManager(examiner.Object, Mock.Of<IExternalDataManager>());

        var result = await manager.GetPlaylistsAsync(video, CancellationToken.None);

        Assert.False(result.SelectedPlaylistIsValid);
        Assert.Equal("00800.mpls", result.EffectivePlaylistName);
        Assert.False(video.BluRayPlaylistNameIsValid);
        Assert.Null(video.BluRayLastProbedPlaylistName);
        examiner.Verify(i => i.GetDiscPlaylists(video.Path), Times.Exactly(2));
    }

    [Fact]
    public async Task GetPlaylistsAsync_WithReconciledState_DoesNotInvalidateOrRefresh()
    {
        var video = CreateVideo().Object;
        MarkReconciled(video);
        var externalDataManager = new Mock<IExternalDataManager>();
        var providerManager = new Mock<IProviderManager>();
        BaseItem.ProviderManager = providerManager.Object;
        using var manager = CreateManager(CreateExaminer().Object, externalDataManager.Object);

        var result = await manager.GetPlaylistsAsync(video, CancellationToken.None);

        Assert.Equal("00800.mpls", result.EffectivePlaylistName);
        Assert.Null(result.SelectedPlaylistName);
        Assert.Null(result.RefreshError);
        externalDataManager.VerifyNoOtherCalls();
        providerManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdatePlaylistAsync_PersistsCanonicalSelectionInvalidatesAndRefreshes()
    {
        var videoMock = CreateVideo();
        var video = videoMock.Object;
        var examiner = CreateExaminer();
        var externalDataManager = new Mock<IExternalDataManager>();
        var refreshCalls = 0;
        var providerManager = new Mock<IProviderManager>();
        providerManager
            .Setup(i => i.RefreshSingleItem(video, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                Assert.NotEqual(video.BluRayPlaylistName, video.BluRayLastProbedPlaylistName);
                MarkReconciled(video);
                refreshCalls++;
            })
            .ReturnsAsync(ItemUpdateType.MetadataEdit);
        BaseItem.ProviderManager = providerManager.Object;
        using var manager = CreateManager(examiner.Object, externalDataManager.Object);

        var result = await manager.UpdatePlaylistAsync(video, "00801.MPLS", CancellationToken.None);

        Assert.Equal("00801.mpls", video.BluRayPlaylistName);
        Assert.True(video.BluRayPlaylistNameIsValid);
        Assert.Equal("00801.mpls", video.BluRayLastProbedPlaylistName);
        Assert.Equal(1, video.BluRayPlaylistRevision);
        Assert.Equal(video.BluRayPlaylistRevision, video.BluRayLastProbedPlaylistRevision);
        videoMock.Verify(
            i => i.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(1, refreshCalls);
        Assert.Null(result.RefreshError);
        externalDataManager.Verify(i => i.DeleteExternalItemDataAsync(video, It.IsAny<CancellationToken>()), Times.Once);
        examiner.Verify(i => i.GetDiscPlaylists(video.Path), Times.Exactly(2));
    }

    [Fact]
    public async Task UpdatePlaylistAsync_WhenPersistenceFails_RestoresPreviousState()
    {
        var videoMock = CreateVideo();
        var video = videoMock.Object;
        video.BluRayPlaylistName = "00800.mpls";
        video.BluRayPlaylistNameIsValid = true;
        video.BluRayPlaylistRevision = 4;
        videoMock
            .Setup(i => i.UpdateToRepositoryAsync(It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var externalDataManager = new Mock<IExternalDataManager>();
        using var manager = CreateManager(CreateExaminer().Object, externalDataManager.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.UpdatePlaylistAsync(video, "00801.mpls", CancellationToken.None));

        Assert.Equal("00800.mpls", video.BluRayPlaylistName);
        Assert.True(video.BluRayPlaylistNameIsValid);
        Assert.Equal(4, video.BluRayPlaylistRevision);
        externalDataManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdatePlaylistAsync_WhenRefreshFails_ReturnsGenericPostSaveError()
    {
        var video = CreateVideo().Object;
        BaseItem.ProviderManager = Mock.Of<IProviderManager>(i =>
            i.RefreshSingleItem(video, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>())
                == Task.FromException<ItemUpdateType>(new InvalidOperationException("private path")));
        using var manager = CreateManager(CreateExaminer().Object, Mock.Of<IExternalDataManager>());

        var result = await manager.UpdatePlaylistAsync(video, "00801.mpls", CancellationToken.None);

        Assert.Equal("Playlist selection was saved, but dependent media data could not be fully refreshed.", result.RefreshError);
        Assert.DoesNotContain("private path", result.RefreshError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatePlaylistAsync_WhenInvalidationFails_DoesNotRefresh()
    {
        var video = CreateVideo().Object;
        var externalDataManager = new Mock<IExternalDataManager>();
        externalDataManager
            .Setup(i => i.DeleteExternalItemDataAsync(video, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cleanup failed"));
        var refreshCalls = 0;
        var providerManager = new Mock<IProviderManager>();
        providerManager
            .Setup(i => i.RefreshSingleItem(video, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => refreshCalls++)
            .ReturnsAsync(ItemUpdateType.MetadataEdit);
        BaseItem.ProviderManager = providerManager.Object;
        using var manager = CreateManager(CreateExaminer().Object, externalDataManager.Object);

        var result = await manager.UpdatePlaylistAsync(video, "00801.mpls", CancellationToken.None);

        Assert.Equal(0, refreshCalls);
        Assert.NotNull(result.RefreshError);
    }

    [Fact]
    public async Task UpdatePlaylistAsync_WithSameValidSelection_DoesNotSaveOrRefresh()
    {
        var videoMock = CreateVideo();
        var video = videoMock.Object;
        video.BluRayPlaylistName = "00801.mpls";
        video.BluRayPlaylistNameIsValid = true;
        video.BluRayLastProbedPlaylistName = "00801.mpls";
        MarkReconciled(video);
        var externalDataManager = new Mock<IExternalDataManager>();
        var providerManager = new Mock<IProviderManager>();
        BaseItem.ProviderManager = providerManager.Object;
        using var manager = CreateManager(CreateExaminer().Object, externalDataManager.Object);

        var result = await manager.UpdatePlaylistAsync(video, "00801.mpls", CancellationToken.None);

        videoMock.Verify(
            i => i.UpdateToRepositoryAsync(It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal("00801.mpls", result.SelectedPlaylistName);
        externalDataManager.VerifyNoOtherCalls();
        providerManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdatePlaylistAsync_ClearingSelection_PersistsNullAndComputesDefaultTransiently()
    {
        var videoMock = CreateVideo();
        var video = videoMock.Object;
        video.BluRayPlaylistName = "00801.mpls";
        MarkReconciled(video);
        var providerManager = new Mock<IProviderManager>();
        providerManager
            .Setup(i => i.RefreshSingleItem(video, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => MarkReconciled(video))
            .ReturnsAsync(ItemUpdateType.MetadataImport);
        BaseItem.ProviderManager = providerManager.Object;
        using var manager = CreateManager(CreateExaminer().Object, Mock.Of<IExternalDataManager>());

        var result = await manager.UpdatePlaylistAsync(video, null, CancellationToken.None);

        Assert.Null(video.BluRayPlaylistName);
        Assert.Null(video.BluRayPlaylistNameIsValid);
        Assert.Null(video.BluRayLastProbedPlaylistName);
        Assert.Null(result.SelectedPlaylistName);
        Assert.Equal("00800.mpls", result.DefaultPlaylistName);
        Assert.Equal("00800.mpls", result.EffectivePlaylistName);
        videoMock.Verify(i => i.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylistAsync_WithSameSelectionAndStaleMediaInfo_RetriesRefresh()
    {
        var videoMock = CreateVideo();
        var video = videoMock.Object;
        video.BluRayPlaylistName = "00801.mpls";
        video.BluRayPlaylistNameIsValid = true;
        video.BluRayLastProbedPlaylistName = "00800.mpls";
        var externalDataManager = new Mock<IExternalDataManager>();
        var providerManager = new Mock<IProviderManager>();
        providerManager
            .Setup(i => i.RefreshSingleItem(video, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => MarkReconciled(video))
            .ReturnsAsync(ItemUpdateType.MetadataImport);
        BaseItem.ProviderManager = providerManager.Object;
        using var manager = CreateManager(CreateExaminer().Object, externalDataManager.Object);

        var result = await manager.UpdatePlaylistAsync(video, "00801.mpls", CancellationToken.None);

        videoMock.Verify(
            i => i.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal("00801.mpls", video.BluRayLastProbedPlaylistName);
        Assert.Null(result.RefreshError);
        externalDataManager.Verify(i => i.DeleteExternalItemDataAsync(video, It.IsAny<CancellationToken>()), Times.Once);
        providerManager.Verify(
            i => i.RefreshSingleItem(video, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylistAsync_AfterPersistence_UsesApplicationStoppingToken()
    {
        using var requestCancellation = new CancellationTokenSource();
        using var applicationStopping = new CancellationTokenSource();
        var videoMock = CreateVideo();
        var video = videoMock.Object;
        videoMock
            .Setup(i => i.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, requestCancellation.Token))
            .Callback(requestCancellation.Cancel)
            .Returns(Task.CompletedTask);
        var externalDataManager = new Mock<IExternalDataManager>();
        externalDataManager
            .Setup(i => i.DeleteExternalItemDataAsync(video, applicationStopping.Token))
            .Returns(Task.CompletedTask);
        var providerManager = new Mock<IProviderManager>();
        providerManager
            .Setup(i => i.RefreshSingleItem(video, It.IsAny<MetadataRefreshOptions>(), applicationStopping.Token))
            .Callback(() => MarkReconciled(video))
            .ReturnsAsync(ItemUpdateType.MetadataImport);
        BaseItem.ProviderManager = providerManager.Object;
        var lifetime = Mock.Of<IHostApplicationLifetime>(i => i.ApplicationStopping == applicationStopping.Token);
        using var manager = CreateManager(CreateExaminer().Object, externalDataManager.Object, lifetime);

        var result = await manager.UpdatePlaylistAsync(video, "00801.mpls", requestCancellation.Token);

        Assert.Null(result.RefreshError);
        externalDataManager.Verify(i => i.DeleteExternalItemDataAsync(video, applicationStopping.Token), Times.Once);
        providerManager.Verify(
            i => i.RefreshSingleItem(video, It.IsAny<MetadataRefreshOptions>(), applicationStopping.Token),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylistAsync_WhenRefreshDoesNotApplySelection_ReturnsGenericPostSaveError()
    {
        var video = CreateVideo().Object;
        BaseItem.ProviderManager = Mock.Of<IProviderManager>(i =>
            i.RefreshSingleItem(video, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>())
                == Task.FromResult(ItemUpdateType.None));
        using var manager = CreateManager(CreateExaminer().Object, Mock.Of<IExternalDataManager>());

        var result = await manager.UpdatePlaylistAsync(video, "00801.mpls", CancellationToken.None);

        Assert.Equal("Playlist selection was saved, but dependent media data could not be fully refreshed.", result.RefreshError);
        Assert.Equal(1, video.BluRayPlaylistRevision);
        Assert.Equal(0, video.BluRayLastProbedPlaylistRevision);
    }

    public void Dispose()
    {
        BaseItem.ProviderManager = _previousProviderManager;
        BaseItem.FileSystem = _previousFileSystem;
    }

    private static Mock<Video> CreateVideo()
    {
        var video = new Mock<Video> { CallBase = true };
        video.Object.Id = Guid.NewGuid();
        video.Object.Path = "/media/movie";
        video.Object.VideoType = VideoType.BluRay;
        video
            .Setup(i => i.UpdateToRepositoryAsync(It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return video;
    }

    private static Mock<IBlurayExaminer> CreateExaminer()
    {
        var examiner = new Mock<IBlurayExaminer>();
        examiner
            .Setup(i => i.GetDiscPlaylists(It.IsAny<string>()))
            .Returns(
            [
                new BluRayPlaylistInfoDto
                {
                    Name = "00800.mpls",
                    RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
                    IsDefault = true
                },
                new BluRayPlaylistInfoDto
                {
                    Name = "00801.mpls",
                    RunTimeTicks = TimeSpan.FromMinutes(80).Ticks
                }
            ]);
        examiner.Setup(i => i.GetDiscFingerprint(It.IsAny<string>())).Returns(DiscFingerprint);
        return examiner;
    }

    private static void MarkReconciled(Video video)
    {
        video.BluRayPlaylistProbeVersion = Video.CurrentBluRayPlaylistProbeVersion;
        video.BluRayLastProbedPlaylistRevision = video.BluRayPlaylistRevision;
        video.BluRayDiscFingerprint = DiscFingerprint;
        if (string.IsNullOrWhiteSpace(video.BluRayPlaylistName))
        {
            video.BluRayPlaylistNameIsValid = null;
            video.BluRayLastProbedPlaylistName = null;
        }
        else if (string.Equals(video.BluRayPlaylistName, "00800.mpls", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(video.BluRayPlaylistName, "00801.mpls", StringComparison.OrdinalIgnoreCase))
        {
            video.BluRayPlaylistNameIsValid = true;
            video.BluRayLastProbedPlaylistName = video.BluRayPlaylistName;
        }
        else
        {
            video.BluRayPlaylistNameIsValid = false;
            video.BluRayLastProbedPlaylistName = null;
        }
    }

    private static BluRayPlaylistManager CreateManager(
        IBlurayExaminer examiner,
        IExternalDataManager externalDataManager,
        IHostApplicationLifetime? applicationLifetime = null)
        => new(
            examiner,
            externalDataManager,
            applicationLifetime ?? Mock.Of<IHostApplicationLifetime>(i => i.ApplicationStopping == CancellationToken.None),
            NullLogger<BluRayPlaylistManager>.Instance);
}
