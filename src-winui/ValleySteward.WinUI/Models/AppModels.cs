using ValleySteward.WinUI.Common;

namespace ValleySteward.WinUI.Models;

public enum LaunchTarget
{
    Smapi,
    Vanilla,
}

public enum GameProcessState
{
    Stopped,
    Running,
    Exited,
}

public sealed record GameInstallation(
    string Path,
    string Executable,
    string Store,
    string? Version);

public sealed record SmapiStatus(
    bool Installed,
    string? Version,
    string? Executable)
{
    public static SmapiStatus Missing { get; } = new(false, null, null);
}

public sealed record SteamIdentity(
    string SteamId64,
    string FriendCode,
    string? AccountName,
    string? PersonaName,
    string Source,
    bool Active,
    string? AvatarPath = null);

public sealed record SteamStatus(bool Running, SteamIdentity? Identity)
{
    public static SteamStatus Offline { get; } = new(false, null);
}

public sealed record GameProcessStatus(
    GameProcessState State,
    bool Running,
    int? ProcessId,
    LaunchTarget? Target,
    DateTimeOffset? StartedAt,
    int? ExitCode)
{
    public static GameProcessStatus Stopped { get; } = new(
        GameProcessState.Stopped,
        false,
        null,
        null,
        null,
        null);
}

public sealed class InstalledMod : ObservableObject
{
    private bool _enabled;
    private string _name = string.Empty;
    private string? _description;
    private bool _translated;
    private bool _isTranslating;
    private bool _isCheckingUpdate;
    private bool _isUpdating;
    private InstalledModUpdateResult? _updateResult;

    public required string Id { get; init; }
    public required string Name
    {
        get => _name;
        init => _name = value;
    }
    public string? Description
    {
        get => _description;
        init => _description = value;
    }
    public bool Translated
    {
        get => _translated;
        init => _translated = value;
    }
    public required string Author { get; init; }
    public required string Version { get; init; }
    public required string Path { get; init; }
    public required string Health { get; init; }
    public string? UpdateAvailable { get; init; }
    public IReadOnlyList<string> UpdateKeys { get; init; } = Array.Empty<string>();
    public string? UpdateKeysError { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => Health == "healthy" ? (Enabled ? "已启用" : "已停用") : "需要处理";

    public void ApplyTranslation(string name, string description)
    {
        if (SetProperty(ref _name, name, nameof(Name)))
        {
            OnPropertyChanged(nameof(TranslationActionText));
        }
        SetProperty(ref _description, description, nameof(Description));
        if (SetProperty(ref _translated, true, nameof(Translated)))
        {
            OnPropertyChanged(nameof(TranslationActionText));
        }
    }

    public bool IsTranslating
    {
        get => _isTranslating;
        set
        {
            if (SetProperty(ref _isTranslating, value))
            {
                OnPropertyChanged(nameof(CanTranslate));
                OnPropertyChanged(nameof(TranslationActionText));
            }
        }
    }

    public bool CanTranslate => Health == "healthy" && !IsTranslating;
    public string TranslationActionText => IsTranslating
        ? "翻译中"
        : Translated
            ? "重新翻译"
            : "一键翻译";

    public InstalledModUpdateResult? UpdateResult
    {
        get => _updateResult;
        set
        {
            if (SetProperty(ref _updateResult, value))
            {
                RaiseUpdateProperties();
            }
        }
    }

    public bool IsCheckingUpdate
    {
        get => _isCheckingUpdate;
        set
        {
            if (SetProperty(ref _isCheckingUpdate, value))
            {
                RaiseUpdateProperties();
            }
        }
    }

    public bool IsUpdating
    {
        get => _isUpdating;
        set
        {
            if (SetProperty(ref _isUpdating, value))
            {
                RaiseUpdateProperties();
            }
        }
    }

    public string UpdateStatusText
    {
        get
        {
            if (IsUpdating)
            {
                return "正在更新";
            }
            if (IsCheckingUpdate)
            {
                return "正在检查";
            }
            if (UpdateResult is { } result)
            {
                return result.UpdateAvailable && !string.IsNullOrWhiteSpace(result.LatestVersion)
                    ? $"可更新到 {result.LatestVersion}"
                    : result.Message;
            }
            if (!string.IsNullOrWhiteSpace(UpdateKeysError))
            {
                return UpdateKeysError;
            }
            return UpdateKeys.Count == 0 ? "没有 UpdateKey" : $"{UpdateKeys.Count} 个更新源";
        }
    }

    public string UpdateActionText => IsUpdating
        ? "更新中"
        : IsCheckingUpdate
            ? "检查中"
            : UpdateResult?.UpdateAvailable == true
                ? UpdateResult.CanAutoUpdate ? "更新" : "查看"
                : UpdateResult is null ? "检查" : "重新检查";

    public bool CanRunUpdateAction => Health == "healthy"
        && UpdateKeys.Count > 0
        && !IsCheckingUpdate
        && !IsUpdating;

    private void RaiseUpdateProperties()
    {
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(UpdateActionText));
        OnPropertyChanged(nameof(CanRunUpdateAction));
    }
}

public sealed record InstalledModTranslationSource(
    string UniqueId,
    string Version,
    string Name,
    string Description);

public sealed record InstalledModTranslation(string Name, string Description);

public sealed record ModTrashItem(
    string EntryName,
    string OriginalDirectoryName,
    string OriginalRelativePath,
    string DisplayName,
    string? Version,
    DateTimeOffset? TrashedAt)
{
    public string DetailsText
    {
        get
        {
            var version = string.IsNullOrWhiteSpace(Version) ? "版本未知" : $"v{Version}";
            var time = TrashedAt is null
                ? "移除时间未知"
                : $"移除于 {TrashedAt.Value.ToLocalTime():g}";
            return $"{OriginalDirectoryName} · {version} · {time}";
        }
    }
}

public sealed record ModRestoreResult(
    string OriginalRelativePath,
    string RestoredRelativePath,
    bool Renamed)
{
    public string RestoredDirectoryName => Path.GetFileName(RestoredRelativePath);
}
