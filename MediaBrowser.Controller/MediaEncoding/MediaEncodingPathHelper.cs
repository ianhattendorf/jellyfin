using System;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Shared media encoding path helpers.
/// </summary>
public static class MediaEncodingPathHelper
{
    /// <summary>
    /// Gets the concat config path for a media source.
    /// </summary>
    /// <param name="cachePath">The cache path.</param>
    /// <param name="mediaSource">The media source.</param>
    /// <returns>The concat config path.</returns>
    public static string GetConcatConfigPath(string cachePath, MediaSourceInfo mediaSource)
    {
        ArgumentException.ThrowIfNullOrEmpty(cachePath);
        ArgumentNullException.ThrowIfNull(mediaSource);

        return Path.Join(cachePath, "concat", mediaSource.Id + ".concat");
    }

    /// <summary>
    /// Gets the immutable FFconcat manifest path for a prepared Blu-ray media source.
    /// </summary>
    /// <param name="cachePath">The cache path.</param>
    /// <param name="mediaSource">The prepared media source.</param>
    /// <param name="startTimeTicks">The optional playlist seek position.</param>
    /// <returns>The manifest path.</returns>
    public static string GetBluRayConcatConfigPath(
        string cachePath,
        MediaSourceInfo mediaSource,
        long? startTimeTicks = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cachePath);
        ArgumentNullException.ThrowIfNull(mediaSource);

        var planHash = mediaSource.BluRayPlaybackPlan?.PlanHash;
        if (string.IsNullOrWhiteSpace(planHash)
            || planHash.Length != 64
            || planHash.Any(i => !char.IsAsciiHexDigit(i)))
        {
            throw new ArgumentException("The Blu-ray media source does not have a valid prepared playback plan hash.", nameof(mediaSource));
        }

        var itemDirectory = Guid.TryParse(mediaSource.Id, out var itemId)
            ? itemId.ToString("N")
            : "shared";
        var seekSuffix = startTimeTicks > 0
            ? "." + startTimeTicks.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        return Path.Join(cachePath, "bluray", itemDirectory, planHash + seekSuffix + ".ffconcat");
    }

    /// <summary>
    /// Normalizes a Blu-ray path to the disc root expected by libbluray.
    /// </summary>
    /// <param name="path">The Blu-ray item path.</param>
    /// <returns>The Blu-ray disc root.</returns>
    public static string NormalizeBluRayPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var normalizedPath = path;
        var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        while (normalizedPath.Length > rootLength && Path.EndsInDirectorySeparator(normalizedPath))
        {
            normalizedPath = normalizedPath[..^1];
        }

        return string.Equals(Path.GetFileName(normalizedPath), "BDMV", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(normalizedPath) ?? normalizedPath
            : normalizedPath;
    }
}
