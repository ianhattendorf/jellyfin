using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class VideosControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;
    private static string? _accessToken;

    public VideosControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DeleteAlternateSources_NonexistentItemId_NotFound()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var response = await client.DeleteAsync($"Videos/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "Videos/{0}/BluRay/Playlists")]
    [InlineData("POST", "Videos/{0}/BluRay/Playlist")]
    [InlineData("DELETE", "Videos/{0}/BluRay/Playlist")]
    public async Task BluRayPlaylistEndpoints_Unauthorized_ReturnUnauthorized(string method, string route)
    {
        var client = _factory.CreateClient();
        var response = await SendBluRayRequestAsync(client, method, route, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "Videos/{0}/BluRay/Playlists")]
    [InlineData("POST", "Videos/{0}/BluRay/Playlist")]
    [InlineData("DELETE", "Videos/{0}/BluRay/Playlist")]
    public async Task BluRayPlaylistEndpoints_WithMissingItem_ReturnNotFound(string method, string route)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
        var response = await SendBluRayRequestAsync(client, method, route, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BluRayPlaylists_WithSeededPrimary_GetsSetsAndClearsSelectedPlaylist()
    {
        using var factory = CreateBluRayFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var discPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(discPath, "BDMV"));
        try
        {
            var primary = SeedPrimaryBluRay(factory, discPath);

            using var getResponse = await client.GetAsync($"Videos/{primary.Id}/BluRay/Playlists", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            var initial = await getResponse.Content.ReadFromJsonAsync<BluRayPlaylistListDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(initial);
            Assert.Null(initial.SelectedPlaylistName);
            Assert.Equal("00800.mpls", initial.EffectivePlaylistName);

            using var postResponse = await client.PostAsJsonAsync(
                $"Videos/{primary.Id}/BluRay/Playlist",
                new UpdateBluRayPlaylistDto
                {
                    PlaylistName = "00800.MPLS"
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
            var selected = await postResponse.Content.ReadFromJsonAsync<BluRayPlaylistListDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(selected);
            Assert.Equal("00800.mpls", selected.SelectedPlaylistName);
            Assert.Equal("00800.mpls", selected.EffectivePlaylistName);

            using var deleteResponse = await client.DeleteAsync($"Videos/{primary.Id}/BluRay/Playlist", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
            var cleared = await deleteResponse.Content.ReadFromJsonAsync<BluRayPlaylistListDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(cleared);
            Assert.Null(cleared.SelectedPlaylistName);
            Assert.Equal("00800.mpls", cleared.EffectivePlaylistName);
        }
        finally
        {
            if (Directory.Exists(discPath))
            {
                Directory.Delete(discPath, true);
            }
        }
    }

    private WebApplicationFactory<Startup> CreateBluRayFactory()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBlurayExaminer>();
                services.AddSingleton<IBlurayExaminer>(new TestBlurayExaminer());
            });
        });
    }

    private static Video SeedPrimaryBluRay(WebApplicationFactory<Startup> factory, string discPath)
    {
        var libraryManager = factory.Services.GetRequiredService<ILibraryManager>();
        var parent = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Seeded Library",
            Path = Path.GetDirectoryName(discPath)
        };
        libraryManager.CreateItem(parent, null);
        var primary = new Video
        {
            Id = Guid.NewGuid(),
            Name = "Seeded Blu-ray",
            ParentId = parent.Id,
            Path = discPath,
            VideoType = VideoType.BluRay
        };
        libraryManager.CreateItem(primary, parent);
        return primary;
    }

    private static Task<HttpResponseMessage> SendBluRayRequestAsync(
        HttpClient client,
        string method,
        string route,
        Guid itemId)
    {
        var requestUri = string.Format(CultureInfo.InvariantCulture, route, itemId);
        return method switch
        {
            "GET" => client.GetAsync(requestUri, TestContext.Current.CancellationToken),
            "POST" => client.PostAsJsonAsync(
                requestUri,
                new UpdateBluRayPlaylistDto
                {
                    PlaylistName = "00800.mpls"
                },
                TestContext.Current.CancellationToken),
            "DELETE" => client.DeleteAsync(requestUri, TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null)
        };
    }

    private sealed class TestBlurayExaminer : IBlurayExaminer
    {
        public BlurayDiscInfo GetDiscInfo(string path, string? playlistName = null)
        {
            return new BlurayDiscInfo
            {
                Files = [Path.Combine(path, "BDMV", "STREAM", "00001.m2ts")],
                PlaylistName = playlistName ?? "00800.mpls",
                RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
                MediaStreams =
                [
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Width = 1920,
                        Height = 1080
                    }
                ],
                Chapters = [0D]
            };
        }

        public IReadOnlyList<BluRayPlaylistInfoDto> GetDiscPlaylists(string path)
        {
            return
            [
                new BluRayPlaylistInfoDto
                {
                    Name = "00800.mpls",
                    RunTimeTicks = TimeSpan.FromMinutes(90).Ticks
                }
            ];
        }
    }
}
