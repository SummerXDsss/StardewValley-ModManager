namespace ValleySteward.WinUI.Models;

public enum ModUpdateProvider
{
    Unknown,
    Nexus,
    GitHub,
}

public enum ModUpdateCheckStatus
{
    Unavailable,
    Failed,
    Unknown,
    UpToDate,
    UpdateAvailable,
}

public sealed record ParsedModUpdateKey(
    string RawValue,
    ModUpdateProvider Provider,
    string Identifier,
    string PageUrl);

public abstract record ModUpdateDownloadDescriptor(
    ModUpdateProvider Provider,
    string ExpectedVersion,
    string PageUrl);

public sealed record NexusModUpdateDownloadDescriptor(
    string ModId,
    string FileId,
    string FileName,
    string ExpectedVersion,
    string PageUrl)
    : ModUpdateDownloadDescriptor(ModUpdateProvider.Nexus, ExpectedVersion, PageUrl);

public sealed record GitHubModUpdateDownloadDescriptor(
    Uri AssetUri,
    string AssetName,
    long? AssetSize,
    string? Sha256,
    string ExpectedVersion,
    string PageUrl)
    : ModUpdateDownloadDescriptor(ModUpdateProvider.GitHub, ExpectedVersion, PageUrl);

public sealed record ModUpdateSourceResult(
    string UpdateKey,
    ModUpdateProvider Provider,
    ModUpdateCheckStatus Status,
    string InstalledVersion,
    string? LatestVersion,
    string? PageUrl,
    string Message,
    ModUpdateDownloadDescriptor? Download,
    string? CannotUpdateReason)
{
    public bool UpdateAvailable => Status == ModUpdateCheckStatus.UpdateAvailable;
    public bool CanAutoUpdate => UpdateAvailable && Download is not null;
}

public sealed record InstalledModUpdateResult(
    string ModId,
    string ModName,
    string InstalledVersion,
    ModUpdateCheckStatus Status,
    string? LatestVersion,
    string Message,
    IReadOnlyList<ModUpdateSourceResult> Sources,
    ModUpdateDownloadDescriptor? Download,
    string? CannotUpdateReason)
{
    public bool UpdateAvailable => Status == ModUpdateCheckStatus.UpdateAvailable;
    public bool CanAutoUpdate => UpdateAvailable && Download is not null;
}

public sealed record RemoteModDownloadResolution(
    ModUpdateProvider Provider,
    string LatestVersion,
    string PageUrl,
    ModUpdateDownloadDescriptor? Download,
    string? CannotDownloadReason)
{
    public bool CanDownload => Download is not null;
}

public sealed record ModUpdateBatchCandidate(
    string ModId,
    string ModName,
    string InstalledVersion,
    ModUpdateDownloadDescriptor Download)
{
    public string ExpectedVersion => Download.ExpectedVersion;
}

public sealed record ModUpdateBatchSelectionItem(
    string ModId,
    string ModName,
    string InstalledVersion,
    string? ExpectedVersion,
    ModUpdateProvider Provider,
    ModUpdateBatchCandidate? Candidate,
    string? CannotUpdateReason)
{
    public bool CanAutoUpdate => Candidate is not null;
}

public enum ModUpdateBatchItemOutcome
{
    Success,
    Failed,
}

public sealed record ModUpdateBatchItemResult(
    ModUpdateBatchCandidate Candidate,
    ModUpdateBatchItemOutcome Outcome,
    string? ErrorMessage)
{
    public bool Succeeded => Outcome == ModUpdateBatchItemOutcome.Success;
}

public sealed record ModUpdateBatchProgress(
    int Completed,
    int Total,
    int Succeeded,
    int Failed,
    ModUpdateBatchCandidate? Current,
    bool ItemCompleted);

public sealed record ModUpdateBatchResult(
    IReadOnlyList<ModUpdateBatchItemResult> Items,
    bool WasCancelled)
{
    public int Succeeded => Items.Count(item => item.Succeeded);

    public int Failed => Items.Count(item => !item.Succeeded);

    public int Completed => Items.Count;
}
