using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Providers.MediaInfo;
using Xunit;

namespace Jellyfin.Providers.Tests.MediaInfo;

public class ProbeProviderTests
{
    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("00800.mpls", "00800.MPLS", true, false)]
    [InlineData("00999.mpls", null, false, false)]
    [InlineData("00999.mpls", "00800.mpls", false, true)]
    [InlineData("00800.mpls", null, null, true)]
    [InlineData(null, "00800.mpls", null, true)]
    public void IsBluRayPlaylistProbeRequired_TracksAppliedSelection(
        string? selectedPlaylistName,
        string? lastProbedPlaylistName,
        bool? selectionIsValid,
        bool expected)
    {
        var video = new Video
        {
            VideoType = VideoType.BluRay,
            BluRayPlaylistName = selectedPlaylistName,
            BluRayPlaylistNameIsValid = selectionIsValid,
            BluRayLastProbedPlaylistName = lastProbedPlaylistName,
            BluRayPlaylistProbeVersion = Video.CurrentBluRayPlaylistProbeVersion,
            BluRayPlaylistRevision = 2,
            BluRayLastProbedPlaylistRevision = 2,
            BluRayDiscFingerprint = "fingerprint"
        };

        Assert.Equal(expected, ProbeProvider.IsBluRayPlaylistProbeRequired(video, "fingerprint"));
    }

    [Theory]
    [InlineData(0, 2, 2, "fingerprint")]
    [InlineData(Video.CurrentBluRayPlaylistProbeVersion, 2, 1, "fingerprint")]
    [InlineData(Video.CurrentBluRayPlaylistProbeVersion, 2, 2, "changed")]
    public void IsBluRayPlaylistProbeRequired_TracksProbeVersionRevisionAndFingerprint(
        int probeVersion,
        long revision,
        long lastRevision,
        string fingerprint)
    {
        var video = new Video
        {
            VideoType = VideoType.BluRay,
            BluRayPlaylistProbeVersion = probeVersion,
            BluRayPlaylistRevision = revision,
            BluRayLastProbedPlaylistRevision = lastRevision,
            BluRayDiscFingerprint = "fingerprint"
        };

        Assert.True(ProbeProvider.IsBluRayPlaylistProbeRequired(video, fingerprint));
    }

    [Fact]
    public void IsBluRayPlaylistProbeRequired_WithVideoFile_ReturnsFalse()
    {
        var video = new Video
        {
            VideoType = VideoType.VideoFile,
            BluRayPlaylistName = "00800.mpls"
        };

        Assert.False(ProbeProvider.IsBluRayPlaylistProbeRequired(video, null));
    }
}
