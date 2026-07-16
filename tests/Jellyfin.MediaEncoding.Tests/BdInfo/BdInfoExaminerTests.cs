using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.MediaEncoding.BdInfo;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.BdInfo;

public class BdInfoExaminerTests
{
    [Fact]
    public void IBlurayExaminer_PreservesLegacyContract()
    {
        var methods = typeof(IBlurayExaminer).GetMethods();
        var legacyMethod = Assert.Single(methods, method =>
            method.Name == nameof(IBlurayExaminer.GetDiscInfo)
            && method.GetParameters().Length == 1);
        var selectedMethod = Assert.Single(methods, method =>
            method.Name == nameof(IBlurayExaminer.GetDiscInfo)
            && method.GetParameters().Length == 2);
        var revisionMethod = Assert.Single(methods, method =>
            method.Name == nameof(IBlurayExaminer.GetDiscInfo)
            && method.GetParameters().Length == 3);
        var listMethod = Assert.Single(methods, method => method.Name == nameof(IBlurayExaminer.GetDiscPlaylists));
        var fingerprintMethod = Assert.Single(methods, method => method.Name == nameof(IBlurayExaminer.GetDiscFingerprint));

        Assert.True(legacyMethod.IsAbstract);
        Assert.False(selectedMethod.IsAbstract);
        Assert.False(revisionMethod.IsAbstract);
        Assert.False(listMethod.IsAbstract);
        Assert.False(fingerprintMethod.IsAbstract);
    }

    [Fact]
    public void IBlurayExaminer_DefaultMethodsSupportLegacyImplementations()
    {
        IBlurayExaminer examiner = new LegacyBlurayExaminer();

        var result = examiner.GetDiscInfo("/media/movie", "00800.mpls");
        var revisionResult = examiner.GetDiscInfo("/media/movie", "00800.mpls", 3);

        Assert.Equal("legacy.mpls", result.PlaylistName);
        Assert.Equal("legacy.mpls", revisionResult.PlaylistName);
        Assert.Empty(examiner.GetDiscPlaylists("/media/movie"));
        Assert.Null(examiner.GetDiscFingerprint("/media/movie"));
    }

    [Fact]
    public void GetDiscFingerprint_IsStableAcrossEnumerationOrderAndChangesWithMetadata()
    {
        var files = new List<FileSystemMetadata>
        {
            File("/media/movie/BDMV/STREAM/00001.m2ts", 200, 2),
            File("/media/movie/BDMV/PLAYLIST/00800.mpls", 100, 1)
        };
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(i => i.GetDirectoryInfo("/media/movie/BDMV"))
            .Returns(new FileSystemMetadata { Exists = true, IsDirectory = true });
        fileSystem.Setup(i => i.GetFiles("/media/movie/BDMV", true)).Returns(() => files);
        var examiner = new BdInfoExaminer(fileSystem.Object);

        var first = examiner.GetDiscFingerprint("/media/movie");
        files.Reverse();
        var reordered = examiner.GetDiscFingerprint("/media/movie");
        files[0].Length++;
        var changed = examiner.GetDiscFingerprint("/media/movie");

        Assert.NotNull(first);
        Assert.Equal(first, reordered);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void GetDiscFingerprint_IgnoresFilesOutsideBdmvAndReturnsNullWhenMissing()
    {
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(i => i.GetDirectoryInfo("/media/movie/BDMV"))
            .Returns(new FileSystemMetadata { Exists = false, IsDirectory = true });
        var examiner = new BdInfoExaminer(fileSystem.Object);

        Assert.Null(examiner.GetDiscFingerprint("/media/movie"));
        fileSystem.Verify(i => i.GetFiles(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void SelectDefaultPlaylist_UsesLongestPlaylist()
    {
        var playlists = new[]
        {
            Playlist("00800.mpls", TimeSpan.FromMinutes(90)),
            Playlist("00801.mpls", TimeSpan.FromMinutes(95)),
            Playlist("00802.mpls", TimeSpan.FromMinutes(80))
        };

        var result = BdInfoExaminer.SelectDefaultPlaylist(playlists);

        Assert.NotNull(result);
        Assert.Equal("00801.mpls", result.Name);
    }

    [Fact]
    public void SelectDefaultPlaylist_UsesPlaylistNameTieBreaker()
    {
        var playlists = new[]
        {
            Playlist("00802.mpls", TimeSpan.FromMinutes(90)),
            Playlist("00800.mpls", TimeSpan.FromMinutes(90)),
            Playlist("00801.mpls", TimeSpan.FromMinutes(90))
        };

        var result = BdInfoExaminer.SelectDefaultPlaylist(playlists);

        Assert.NotNull(result);
        Assert.Equal("00800.mpls", result.Name);
    }

    [Fact]
    public void SelectDefaultPlaylist_PrefersEligiblePlaylistOverLongerRepeatedContent()
    {
        var playlists = new[]
        {
            new BdInfoPlaylistCandidate("00149.mpls", TimeSpan.FromMinutes(190).Ticks, IsDefaultEligible: false),
            Playlist("00040.mpls", TimeSpan.FromMinutes(119))
        };

        var result = BdInfoExaminer.SelectDefaultPlaylist(playlists);

        Assert.NotNull(result);
        Assert.Equal("00040.mpls", result.Name);
    }

    [Fact]
    public void SelectDefaultPlaylist_FallsBackWhenEveryPlaylistIsIneligible()
    {
        var playlists = new[]
        {
            new BdInfoPlaylistCandidate("00800.mpls", TimeSpan.FromMinutes(90).Ticks, IsDefaultEligible: false),
            new BdInfoPlaylistCandidate("00801.mpls", TimeSpan.FromMinutes(95).Ticks, IsDefaultEligible: false)
        };

        var result = BdInfoExaminer.SelectDefaultPlaylist(playlists);

        Assert.NotNull(result);
        Assert.Equal("00801.mpls", result.Name);
    }

    [Fact]
    public void SelectPlaylist_WithRequestedValidPlaylist_UsesRequestedPlaylist()
    {
        var playlists = new[]
        {
            Playlist("00800.mpls", TimeSpan.FromMinutes(95)),
            Playlist("00801.mpls", TimeSpan.FromMinutes(80))
        };

        var result = BdInfoExaminer.SelectPlaylist(playlists, "00801.mpls");

        Assert.NotNull(result);
        Assert.Equal("00801.mpls", result.Name);
    }

    [Fact]
    public void TryGetCanonicalPlaylistName_WithDifferentCasing_ReturnsExactCanonicalName()
    {
        var playlists = new[]
        {
            Playlist("00800.MPLS", TimeSpan.FromMinutes(90))
        };

        var result = BdInfoExaminer.TryGetCanonicalPlaylistName(playlists, "00800.mpls", out var canonicalName);

        Assert.True(result);
        Assert.Equal("00800.MPLS", canonicalName);
    }

    [Theory]
    [MemberData(nameof(InvalidPlaylistNames))]
    public void TryGetCanonicalPlaylistName_WithInvalidName_ReturnsFalse(string playlistName)
    {
        var playlists = new[]
        {
            Playlist("00800.mpls", TimeSpan.FromMinutes(90))
        };

        var result = BdInfoExaminer.TryGetCanonicalPlaylistName(playlists, playlistName, out var canonicalName);

        Assert.False(result);
        Assert.Null(canonicalName);
    }

    [Fact]
    public void TryGetCanonicalPlaylistName_WithNullName_ReturnsFalse()
    {
        var playlists = new[]
        {
            Playlist("00800.mpls", TimeSpan.FromMinutes(90))
        };

        var result = BdInfoExaminer.TryGetCanonicalPlaylistName(playlists, null, out var canonicalName);

        Assert.False(result);
        Assert.Null(canonicalName);
    }

    [Fact]
    public void SelectPlaylist_WithMissingRequestedPlaylist_FallsBackToDefault()
    {
        var playlists = new[]
        {
            Playlist("00800.mpls", TimeSpan.FromMinutes(95)),
            Playlist("00801.mpls", TimeSpan.FromMinutes(80))
        };

        var result = BdInfoExaminer.SelectPlaylist(playlists, "00999.mpls");

        Assert.NotNull(result);
        Assert.Equal("00800.mpls", result.Name);
    }

    [Fact]
    public void SelectPlaylist_WithNoValidPlaylists_ReturnsNull()
    {
        var result = BdInfoExaminer.SelectPlaylist(Array.Empty<BdInfoPlaylistCandidate>(), "00800.mpls");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("/media/movie/BDMV", "/media/movie")]
    [InlineData("/media/movie/bdmv", "/media/movie")]
    [InlineData("/media/movie", "/media/movie")]
    public void NormalizeDiscPath_WithBluRayDirectory_ReturnsDiscRoot(string path, string expected)
    {
        var result = BdInfoExaminer.NormalizeDiscPath(path);

        Assert.Equal(expected, result);
    }

    public static TheoryData<string> InvalidPlaylistNames()
        => new()
        {
            string.Empty,
            " ",
            "00800",
            "00800.foo",
            "BDMV/PLAYLIST/00800.mpls",
            @"BDMV\PLAYLIST\00800.mpls",
            "/media/movie/BDMV/PLAYLIST/00800.mpls",
            "../00800.mpls"
        };

    private static BdInfoPlaylistCandidate Playlist(string name, TimeSpan runTime)
        => new(name, runTime.Ticks);

    private static FileSystemMetadata File(string path, long length, long lastWriteTicks)
        => new()
        {
            Exists = true,
            FullName = path,
            Length = length,
            LastWriteTimeUtc = new DateTime(lastWriteTicks, DateTimeKind.Utc)
        };

    private sealed class LegacyBlurayExaminer : IBlurayExaminer
    {
        public BlurayDiscInfo GetDiscInfo(string path)
            => new() { PlaylistName = "legacy.mpls" };
    }
}
