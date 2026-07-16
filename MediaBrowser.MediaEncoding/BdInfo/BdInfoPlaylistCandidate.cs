using BDInfo;

namespace MediaBrowser.MediaEncoding.BdInfo;

internal sealed record BdInfoPlaylistCandidate(
    string Name,
    long? RunTimeTicks,
    TSPlaylistFile? Playlist = null,
    bool IsDefaultEligible = true);
