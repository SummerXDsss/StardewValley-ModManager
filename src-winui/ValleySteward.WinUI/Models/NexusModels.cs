namespace ValleySteward.WinUI.Models;

public sealed record NexusModDetails(
    string Id,
    string GameScopedId,
    string Name,
    string PageUrl,
    IReadOnlyList<NexusModFile> Files)
{
    public string FileCountText => Files.Count == 1 ? "1 个文件" : $"{Files.Count} 个文件";
}

public sealed record NexusModFile(
    string Id,
    string Name,
    bool IsActive,
    DateTimeOffset? LastFileUploadedAt,
    long VersionsCount,
    IReadOnlyList<NexusFileVersion> Versions)
{
    public string StatusText => IsActive ? "可用" : "已归档";
    public string LastUploadedText => LastFileUploadedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        ?? "上传时间未知";
    public string VersionsSummary => Versions.Count > 0
        ? $"{Versions.Count} 个可下载版本"
        : VersionsCount > 0
            ? $"上游报告 {VersionsCount} 个版本，但没有返回版本详情"
            : "暂无可下载版本";
}

public sealed record NexusFileVersion(
    string Id,
    string GameScopedId,
    string Name,
    string Version,
    string Category,
    DateTimeOffset? UploadedAt,
    bool IsPrimary)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"文件 #{GameScopedId}" : Name;
    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "版本未知" : Version;
    public string CategoryText => string.IsNullOrWhiteSpace(Category) ? "未分类" : Category;
    public string UploadedText => UploadedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "日期未知";
    public string PrimaryText => IsPrimary ? "主文件" : string.Empty;
}

public sealed record NexusDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;

    public string StatusText => TotalBytes is > 0
        ? $"已下载 {FormatBytes(BytesReceived)} / {FormatBytes(TotalBytes.Value)}"
        : $"已下载 {FormatBytes(BytesReceived)}";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.#} {units[unit]}";
    }
}

public sealed record NexusDownloadAuthorization(string Key, long Expires);

public sealed record NexusNxmLink(
    string GameDomain,
    string ModId,
    string FileId,
    NexusDownloadAuthorization Authorization);

public sealed record NexusDownloadTranslation(string Name, string Description);

public sealed record NexusDownloadResult(
    string Path,
    string FileName,
    long BytesWritten,
    string? MetadataPath = null);
