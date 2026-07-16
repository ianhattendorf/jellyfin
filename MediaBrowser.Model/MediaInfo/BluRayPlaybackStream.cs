#pragma warning disable CS1591

using MediaBrowser.Model.Entities;

namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// A canonical stream declaration for a Blu-ray concat timeline.
/// </summary>
public sealed class BluRayPlaybackStream
{
    public int Index { get; set; }

    public int Pid { get; set; }

    public MediaStreamType Type { get; set; }

    public string? Codec { get; set; }

    public string? Signature { get; set; }

    /// <summary>
    /// Gets or sets the selected-playlist time at which this stream first appears, in MPLS navigation ticks.
    /// </summary>
    public long FirstPlayItemStart45Khz { get; set; } = -1;
}
