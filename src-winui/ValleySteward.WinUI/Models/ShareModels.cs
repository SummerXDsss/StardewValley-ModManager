namespace ValleySteward.WinUI.Models;

public sealed record ModShareRequest(
    string ComputerName,
    IReadOnlyList<ModShareModRequest> Mods);

public sealed record ModShareModRequest(
    string Name,
    string UniqueId,
    string? Author,
    string? Version,
    string? Description,
    bool Enabled,
    IReadOnlyList<string> UpdateKeys,
    string? SourceUrl,
    string? GitHubReleaseUrl,
    string? CoverUrl);

public sealed class ModShareEntry
{
    public string Code { get; init; } = string.Empty;
    public string ComputerName { get; init; } = string.Empty;
    public string MaskedIp { get; init; } = string.Empty;
    public DateTimeOffset UploadedAt { get; init; }
    public int ModCount { get; init; }
    public IReadOnlyList<string> CoverUrls { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SharedModItem> Mods { get; init; } = Array.Empty<SharedModItem>();

    public string HeaderText => $"{ComputerName} · {MaskedIp}";
    public string SummaryText => $"{ModCount} 个 Mod · {UploadedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    public IReadOnlyList<SharedModCoverTile> CoverTiles => Mods
        .Take(10)
        .Select(SharedModCoverTile.FromMod)
        .ToArray();
}

public sealed class SharedModItem
{
    public string Name { get; init; } = string.Empty;
    public string UniqueId { get; init; } = string.Empty;
    public string? Author { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public IReadOnlyList<string> UpdateKeys { get; init; } = Array.Empty<string>();
    public string Provider { get; init; } = "用户分享";
    public string? OriginalUrl { get; init; }
    public string? DirectDownloadUrl { get; init; }
    public string? GitHubReleaseUrl { get; init; }
    public string? CoverUrl { get; init; }

    public string MetaText => $"{Provider} · {(Enabled ? "启用" : "停用")} · {VersionText}";
    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "版本未知" : Version!;
    public string AuthorText => string.IsNullOrWhiteSpace(Author) ? "作者未知" : Author!;
    public string LinkText => GitHubReleaseUrl is not null
        ? "GitHub Release"
        : OriginalUrl is not null
            ? "原始页面"
            : "无上游链接";
}

public sealed record ModSharePublishResult(
    string Code,
    string PageUrl,
    ModShareEntry Entry);

public sealed record SharedModCoverTile(
    string? CoverUrl,
    string Initial,
    string ToolTip)
{
    public static SharedModCoverTile FromMod(SharedModItem mod)
    {
        var initial = string.IsNullOrWhiteSpace(mod.Name)
            ? "?"
            : mod.Name.Trim()[0].ToString().ToUpperInvariant();
        return new SharedModCoverTile(
            mod.CoverUrl,
            initial,
            $"{mod.Name} · {mod.Provider}");
    }
}
