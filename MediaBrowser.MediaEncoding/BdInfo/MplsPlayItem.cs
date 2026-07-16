namespace MediaBrowser.MediaEncoding.BdInfo;

internal sealed record MplsPlayItem(
    string ClipId,
    string CodecIdentifier,
    int ConnectionCondition,
    int StcId,
    long InTime45Khz,
    long OutTime45Khz,
    int StillMode,
    int StillTime,
    bool IsMultiAngle,
    int AngleCount);
