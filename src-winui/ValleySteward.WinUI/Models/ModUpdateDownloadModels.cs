namespace ValleySteward.WinUI.Models;

public sealed record ModUpdateDownloadResult(
    string Path,
    string FileName,
    long BytesWritten,
    ModUpdateProvider Provider,
    string? Sha256,
    bool DigestVerified,
    bool UsedMirror);
