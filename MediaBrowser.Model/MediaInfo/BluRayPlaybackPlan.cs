#pragma warning disable CS1591

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// Describes the deterministic, selected-playlist timeline used to read a Blu-ray folder.
/// </summary>
public sealed class BluRayPlaybackPlan
{
    /// <summary>
    /// The current serialized plan schema version.
    /// </summary>
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string? PlaylistName { get; set; }

    public long PlaylistRevision { get; set; }

    public string? DiscFingerprint { get; set; }

    public string? PlanHash { get; set; }

    /// <summary>
    /// Gets or sets the total playlist duration in MPLS navigation ticks (1/45000 second).
    /// </summary>
    public long Duration45Khz { get; set; }

    public BluRayPlaybackPlanFailureReason FailureReason { get; set; }

    public string? FailureMessage { get; set; }

    public BluRayPlaybackStream[] Streams { get; set; } = Array.Empty<BluRayPlaybackStream>();

    public BluRayPlaybackItem[] PlayItems { get; set; } = Array.Empty<BluRayPlaybackItem>();

    public bool IsSupported => FailureReason == BluRayPlaybackPlanFailureReason.None
        && !string.IsNullOrWhiteSpace(PlaylistName)
        && Streams is { Length: > 0 }
        && PlayItems is { Length: > 0 };

    /// <summary>
    /// Recomputes the immutable manifest identity after the plan has been populated.
    /// </summary>
    public void UpdatePlanHash()
    {
        var value = new StringBuilder()
            .Append(SchemaVersion).Append('\n')
            .Append(PlaylistName).Append('\n')
            .Append(PlaylistRevision.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(DiscFingerprint).Append('\n')
            .Append(Duration45Khz.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append((int)FailureReason).Append('\n');

        foreach (var stream in Streams ?? Array.Empty<BluRayPlaybackStream>())
        {
            value.Append(stream.Index).Append('|')
                .Append(stream.Pid).Append('|')
                .Append((int)stream.Type).Append('|')
                .Append(stream.Codec).Append('|')
                .Append(stream.Signature).Append('|')
                .Append(stream.FirstPlayItemStart45Khz).Append('\n');
        }

        foreach (var item in PlayItems ?? Array.Empty<BluRayPlaybackItem>())
        {
            value.Append(item.ClipFileName).Append('|')
                .Append(item.InTime45Khz).Append('|')
                .Append(item.OutTime45Khz).Append('|')
                .Append(item.TimelineStart45Khz).Append('|')
                .Append(item.ConnectionCondition).Append('|')
                .Append(item.StcId).Append('|')
                .Append(item.IsMultiAngle ? '1' : '0').Append('\n');
        }

        PlanHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }
}
