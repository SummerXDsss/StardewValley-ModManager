namespace ValleySteward.WinUI.Models;

public enum ModInstallConflictKind
{
    ExistingMod,
    MissingManifest,
    TargetOccupied,
    DuplicateInstalledId,
    PackageTargetCollision,
    OverlappingTarget,
}

public sealed record ModInstallDependency(
    string UniqueId,
    bool IsRequired,
    string? MinimumVersion);

public sealed record ModArchiveMod(
    string Name,
    string UniqueId,
    string Version,
    string Author,
    string? Description,
    IReadOnlyList<ModInstallDependency> Dependencies,
    string ArchiveRoot,
    string TargetFolderName,
    string? TargetPath,
    string? ExistingVersion);

public sealed record ModInstallConflict(
    ModInstallConflictKind Kind,
    string? ModUniqueId,
    string? TargetPath,
    bool Blocking,
    string Message);

public sealed record ModInstallPlan(
    string ArchivePath,
    string ArchiveSha256,
    long ArchiveBytes,
    int EntryCount,
    long UncompressedBytes,
    IReadOnlyList<ModArchiveMod> Mods,
    IReadOnlyList<ModInstallConflict> Conflicts,
    IReadOnlyList<string> XnbFiles,
    int IgnoredEntryCount,
    IReadOnlyList<string> Warnings)
{
    public bool RequiresXnbConfirmation => XnbFiles.Count > 0;

    public bool CanInstall => Mods.Count > 0 && Conflicts.All(conflict => !conflict.Blocking);
}

public sealed record ModInstalledItem(
    string Name,
    string UniqueId,
    string Version,
    string TargetPath,
    bool Replaced,
    string? BackupPath,
    IReadOnlyList<string> PreservedFiles,
    bool TranslationApplied);

public sealed record ModInstallResult(
    IReadOnlyList<ModInstalledItem> Mods,
    int Installed,
    int Replaced,
    string? BackupPath,
    IReadOnlyList<string> Messages);

public sealed record ModInstallOptions
{
    public bool AllowXnbFiles { get; init; }

    public IReadOnlyList<string> PreserveRelativePaths { get; init; } = Array.Empty<string>();

    public string? TranslationSidecarPath { get; init; }

    public string? TranslationTargetUniqueId { get; init; }
}

public sealed record ModArchiveLimits
{
    public long MaximumArchiveBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 20_000;

    public long MaximumEntryBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public long MaximumExtractedBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    public int MaximumPathDepth { get; init; } = 20;

    public int MaximumPathCharacters { get; init; } = 1_024;

    public int MaximumMods { get; init; } = 512;

    public int MaximumManifestBytes { get; init; } = 256 * 1024;
}
