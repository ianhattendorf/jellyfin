using System.Collections.Generic;

namespace MediaBrowser.MediaEncoding.BdInfo;

internal sealed record MplsPlaylist(string Version, int SubPathCount, IReadOnlyList<MplsPlayItem> PlayItems);
