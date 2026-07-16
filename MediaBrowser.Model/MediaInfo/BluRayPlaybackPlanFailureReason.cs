#pragma warning disable CS1591

namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// Structural reasons for rejecting a selected Blu-ray playlist.
/// </summary>
public enum BluRayPlaybackPlanFailureReason
{
    None,
    PlaylistNotFound,
    MalformedPlaylist,
    PlaylistMismatch,
    UnsupportedConnectionCondition,
    UnsupportedStillMode,
    UnsupportedSubPath,
    UnsupportedThreeDimensionalLayout,
    InvalidClipPath,
    MissingClip,
    InvalidPlayItemDuration,
    MissingPrimaryVideo,
    MissingDeclaredStream,
    IncompatibleStream,
    UnsupportedStreamCodec
}
