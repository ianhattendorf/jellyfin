using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

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

        var fileName = mediaSource.Id + ".concat";
        if (mediaSource.VideoType == VideoType.BluRay && !string.IsNullOrWhiteSpace(mediaSource.BluRayPlaylistName))
        {
            fileName = string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1}.concat",
                mediaSource.Id,
                GetStableHash(mediaSource.BluRayPlaylistName));
        }

        return Path.Join(cachePath, "concat", fileName);
    }

    private static string GetStableHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
