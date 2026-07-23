namespace ValleySteward.WinUI.Models;

public enum ActivityKind
{
    Download,
    Install,
    Update,
    Enable,
    Disable,
    Delete,
    Restore,
    Smapi,
    Launch,
    GameControl,
}

public enum ActivityOutcome
{
    Success,
    Warning,
    Failed,
}

public sealed record ActivityEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    ActivityKind Kind,
    ActivityOutcome Outcome,
    string Title,
    string Detail,
    string? SourceUrl,
    string? Version)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("MM-dd HH:mm");

    public string KindText => Kind switch
    {
        ActivityKind.Download => "下载",
        ActivityKind.Install => "安装",
        ActivityKind.Update => "更新",
        ActivityKind.Enable => "启用",
        ActivityKind.Disable => "停用",
        ActivityKind.Delete => "移除",
        ActivityKind.Restore => "恢复",
        ActivityKind.Smapi => "SMAPI",
        ActivityKind.Launch => "启动",
        ActivityKind.GameControl => "游戏控制",
        _ => "操作",
    };

    public string OutcomeText => Outcome switch
    {
        ActivityOutcome.Success => "成功",
        ActivityOutcome.Warning => "注意",
        ActivityOutcome.Failed => "失败",
        _ => "未知",
    };

    public string MetadataText
    {
        get
        {
            var version = string.IsNullOrWhiteSpace(Version) ? string.Empty : $" · {Version}";
            return $"{KindText} · {OutcomeText}{version} · {TimeText}";
        }
    }
}
