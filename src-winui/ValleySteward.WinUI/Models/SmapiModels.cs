namespace ValleySteward.WinUI.Models;

public enum SmapiReleaseSource
{
    GitHubApi,
    GitHubLatestRedirect,
}

public enum GitHubDownloadMode
{
    Direct,
    Custom,
}

public sealed record GitHubDownloadSettings(
    GitHubDownloadMode Mode,
    string? CustomPrefix)
{
    public static GitHubDownloadSettings Direct { get; } = new(
        GitHubDownloadMode.Direct,
        null);
}

public sealed record SmapiReleaseAsset(
    long? Id,
    string Name,
    long? Size,
    string DownloadUrl,
    string? Digest);

public sealed record SmapiReleaseInfo(
    string Version,
    string TagName,
    string PageUrl,
    DateTimeOffset? PublishedAt,
    string InstallerEntry,
    SmapiReleaseSource Source,
    SmapiReleaseAsset Asset);

public sealed record DownloadedSmapiInstaller(
    string Path,
    string FileName,
    string Version,
    long Size,
    string Sha256,
    bool DigestVerified,
    bool UsedMirror);

public enum SmapiOperationStage
{
    Checking,
    Downloading,
    Extracting,
    Installing,
    Uninstalling,
    Verifying,
    Completed,
}

public sealed record SmapiOperationProgress(
    SmapiOperationStage Stage,
    string Message,
    double? Percent = null);

public sealed record SmapiOperationResult(
    bool Installed,
    string? PreviousVersion,
    string? CurrentVersion,
    string ReleaseVersion,
    int InstallerExitCode,
    bool DigestVerified,
    bool UsedMirror,
    string Message);
