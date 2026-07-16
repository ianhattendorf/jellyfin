#pragma warning disable CS1591

namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// An ordered MPLS PlayItem.
/// </summary>
public sealed class BluRayPlaybackItem
{
    public string? ClipFileName { get; set; }

    public long InTime45Khz { get; set; }

    public long OutTime45Khz { get; set; }

    public long TimelineStart45Khz { get; set; }

    public int ConnectionCondition { get; set; }

    public int StcId { get; set; }

    public bool IsMultiAngle { get; set; }
}
