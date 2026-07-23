using System.Collections.ObjectModel;
using System.Diagnostics;
using ValleySteward.WinUI.Common;
using ValleySteward.WinUI.Models;
using ValleySteward.WinUI.Services;
using ActivityKind = ValleySteward.WinUI.Models.ActivityKind;

namespace ValleySteward.WinUI.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private readonly GameDiscoveryService _gameDiscovery = new();
    private readonly SteamService _steamService = new();
    private readonly SmapiService _smapiService = new();
    private readonly ModService _modService = new();
    private readonly ModInstallerService _modInstallerService = new();
    private readonly ModUpdateService _modUpdateService = new();
    private readonly ModUpdateDownloadService _modUpdateDownloadService = new();
    private readonly GameProcessService _gameProcessService = new();
    private readonly UiSettingsService _settingsService = new();
    private readonly ActivityHistoryService _activityHistoryService = new();
    private readonly SemaphoreSlim _modWriteGate = new(1, 1);

    private GameInstallation? _installation;
    private SmapiStatus _smapi = SmapiStatus.Missing;
    private SteamStatus _steam = SteamStatus.Offline;
    private GameProcessStatus _processStatus = GameProcessStatus.Stopped;
    private bool _isBusy;
    private string? _lastError;
    private UiSettings _settings = new();

    public ObservableCollection<InstalledMod> Mods { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<ActivityEntry> RecentActivities { get; } = [];

    public GameInstallation? Installation
    {
        get => _installation;
        private set
        {
            if (SetProperty(ref _installation, value))
            {
                RaiseInstallationProperties();
            }
        }
    }

    public SmapiStatus Smapi
    {
        get => _smapi;
        private set
        {
            if (SetProperty(ref _smapi, value))
            {
                OnPropertyChanged(nameof(SmapiStatusText));
                OnPropertyChanged(nameof(EnvironmentHealthText));
            }
        }
    }

    public SteamStatus Steam
    {
        get => _steam;
        private set
        {
            if (SetProperty(ref _steam, value))
            {
                OnPropertyChanged(nameof(SteamStatusText));
                OnPropertyChanged(nameof(SteamDisplayName));
            }
        }
    }

    public GameProcessStatus ProcessStatus
    {
        get => _processStatus;
        private set
        {
            if (SetProperty(ref _processStatus, value))
            {
                OnPropertyChanged(nameof(ProcessStatusText));
                OnPropertyChanged(nameof(IsGameRunning));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public bool FirstRunCompleted => _settings.FirstRunCompleted;

    public bool AutoInstallStarterMod
    {
        get => _settings.AutoInstallStarterMod;
        set
        {
            if (_settings.AutoInstallStarterMod == value)
            {
                return;
            }
            _settings.AutoInstallStarterMod = value;
            OnPropertyChanged();
            _ = SaveSettingsAsync();
        }
    }

    public bool RememberLaunch
    {
        get => _settings.RememberLaunch;
        set
        {
            if (_settings.RememberLaunch == value)
            {
                return;
            }
            _settings.RememberLaunch = value;
            OnPropertyChanged();
            _ = SaveSettingsAsync();
        }
    }

    public LaunchTarget LaunchPreference
    {
        get => _settings.LaunchPreference;
        private set
        {
            if (_settings.LaunchPreference == value)
            {
                return;
            }
            _settings.LaunchPreference = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LaunchButtonText));
        }
    }

    public string SmapiArguments
    {
        get => _settings.SmapiArguments;
        set
        {
            if (_settings.SmapiArguments == value)
            {
                return;
            }
            _settings.SmapiArguments = value;
            OnPropertyChanged();
        }
    }

    public string VanillaArguments
    {
        get => _settings.VanillaArguments;
        set
        {
            if (_settings.VanillaArguments == value)
            {
                return;
            }
            _settings.VanillaArguments = value;
            OnPropertyChanged();
        }
    }

    public string Theme
    {
        get => _settings.Theme;
        set
        {
            var normalized = UiSettingsService.NormalizeTheme(value);
            if (_settings.Theme == normalized)
            {
                return;
            }
            _settings.Theme = normalized;
            OnPropertyChanged();
            _ = SaveSettingsAsync();
        }
    }

    public string AccentPreset
    {
        get => _settings.AccentPreset;
        set
        {
            var normalized = UiSettingsService.NormalizeAccentPreset(value);
            if (_settings.AccentPreset == normalized)
            {
                return;
            }
            _settings.AccentPreset = normalized;
            OnPropertyChanged();
            _ = SaveSettingsAsync();
        }
    }

    public bool IsGameRunning => ProcessStatus.Running;
    public string GamePathText => Installation?.Path ?? "未设置游戏路径";
    public string GamePathShort => Installation is null ? "未设置" : ShortenPath(Installation.Path);
    public string GameStoreText => Installation?.Store ?? "尚未识别";
    public string GameVersionText => Installation?.Version ?? "未知版本";
    public string SmapiStatusText => Smapi.Installed ? $"SMAPI {Smapi.Version ?? "已安装"}" : "SMAPI 未安装";
    public string SteamStatusText => Steam.Running ? "Steam 在线" : "Steam 未运行";
    public string SteamDisplayName => Steam.Identity?.PersonaName ?? Steam.Identity?.AccountName ?? SteamStatusText;
    public string ProcessStatusText => ProcessStatus.Running
        ? $"{(ProcessStatus.Target == LaunchTarget.Smapi ? "SMAPI" : "原版")} 运行中 · PID {ProcessStatus.ProcessId}"
        : ProcessStatus.State == GameProcessState.Exited ? "游戏已退出" : "游戏未运行";
    public string LaunchButtonText => LaunchPreference == LaunchTarget.Smapi ? "启动 SMAPI" : "启动原版";
    public int EnabledModCount => Mods.Count(mod => mod.Enabled);
    public int ProblemModCount => Mods.Count(mod => mod.Health != "healthy");
    public bool HasRecentActivities => RecentActivities.Count > 0;
    public string EnvironmentHealthText => Installation is null
        ? "需要设置游戏目录"
        : !Smapi.Installed
            ? "游戏可用，SMAPI 未安装"
            : ProblemModCount > 0
                ? $"发现 {ProblemModCount} 个 Mod 问题"
                : "环境状态良好";

    public async Task InitializeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        LastError = null;
        try
        {
            _settings = await _settingsService.LoadAsync();
            RaiseSettingsProperties();
            ReplaceActivities(await _activityHistoryService.ListAsync());
            Installation = await _gameDiscovery.LoadSavedAsync();
            if (Installation is null)
            {
                Installation = await _gameDiscovery.DetectAsync();
                if (Installation is not null)
                {
                    await _gameDiscovery.SaveAsync(Installation);
                }
            }
            await RefreshCoreAsync();
        }
        catch (Exception error)
        {
            LastError = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        LastError = null;
        try
        {
            if (Installation is not null)
            {
                var inspected = await _gameDiscovery.InspectAsync(Installation.Path);
                Installation = inspected;
            }
            await RefreshCoreAsync();
        }
        catch (Exception error)
        {
            LastError = error.Message;
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AutoDetectGameAsync()
    {
        var installation = await _gameDiscovery.DetectAsync()
            ?? throw new DirectoryNotFoundException("未在注册表、Steam 库、GOG 或常见目录中找到星露谷物语。");
        await ApplyInstallationAsync(installation);
    }

    public async Task SetGamePathAsync(string path)
    {
        var installation = await _gameDiscovery.InspectAsync(path)
            ?? throw new DirectoryNotFoundException("所选目录中没有找到 Stardew Valley 可执行文件。");
        await ApplyInstallationAsync(installation);
    }

    public async Task SetModEnabledAsync(InstalledMod mod, bool enabled)
    {
        EnsureCanModifyMods();
        var installation = Installation ?? throw new InvalidOperationException("请先设置游戏目录。");
        await _modService.SetEnabledAsync(installation.Path, mod, enabled);
        await RefreshModsAsync();
        await RecordActivityAsync(
            enabled ? ActivityKind.Enable : ActivityKind.Disable,
            ActivityOutcome.Success,
            enabled ? $"已启用 {mod.Name}" : $"已停用 {mod.Name}",
            $"{mod.Id} · {mod.Version}",
            version: mod.Version);
    }

    public async Task MoveModToTrashAsync(InstalledMod mod)
    {
        EnsureCanModifyMods();
        var installation = Installation ?? throw new InvalidOperationException("请先设置游戏目录。");
        await _modService.MoveToTrashAsync(installation.Path, mod);
        await RefreshModsAsync();
        await RecordActivityAsync(
            ActivityKind.Delete,
            ActivityOutcome.Success,
            $"已移到回收区：{mod.Name}",
            $"{mod.Id} · 可从管理器回收区恢复",
            version: mod.Version);
    }

    public Task<ModInstallPlan> InspectModArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        var installation = Installation ?? throw new InvalidOperationException("请先设置游戏目录。");
        return _modInstallerService.InspectAsync(archivePath, installation.Path, cancellationToken);
    }

    public async Task<ModInstallResult> InstallModArchiveAsync(
        ModInstallPlan plan,
        ModInstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await InstallModArchiveCoreAsync(
            plan,
            options,
            recordActivity: true,
            cancellationToken);
    }

    internal async Task<ModInstallResult> InstallModArchiveForBatchUpdateAsync(
        ModInstallPlan plan,
        ModInstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await InstallModArchiveCoreAsync(
            plan,
            options,
            recordActivity: false,
            cancellationToken);
    }

    private async Task<ModInstallResult> InstallModArchiveCoreAsync(
        ModInstallPlan plan,
        ModInstallOptions? options,
        bool recordActivity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _modWriteGate.WaitAsync(cancellationToken);
        try
        {
            var installation = Installation ?? throw new InvalidOperationException("请先设置游戏目录。");
            ProcessStatus = await _gameProcessService.RefreshAsync(installation);
            EnsureCanModifyMods();
            var result = await _modInstallerService.InstallAsync(
                installation.Path,
                plan,
                options,
                cancellationToken);
            await RefreshModsAsync();
            var names = string.Join("、", result.Mods.Select(mod => mod.Name).Take(4));
            if (result.Mods.Count > 4)
            {
                names += $" 等 {result.Mods.Count} 个 Mod";
            }
            if (recordActivity)
            {
                await RecordActivityAsync(
                    result.Replaced > 0 ? ActivityKind.Update : ActivityKind.Install,
                    ActivityOutcome.Success,
                    result.Replaced > 0 ? $"Mod 更新完成：{names}" : $"Mod 安装完成：{names}",
                    $"新增 {result.Installed} 个，覆盖更新 {result.Replaced} 个。",
                    version: result.Mods.Count == 1 ? result.Mods[0].Version : null,
                    cancellationToken: CancellationToken.None);
            }
            return result;
        }
        finally
        {
            _modWriteGate.Release();
        }
    }

    public Task<IReadOnlyList<InstalledModUpdateResult>> CheckModUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        return _modUpdateService.CheckAllAsync(Mods.ToArray(), cancellationToken);
    }

    public Task<InstalledModUpdateResult> CheckModUpdateAsync(
        InstalledMod mod,
        CancellationToken cancellationToken = default)
    {
        return _modUpdateService.CheckAsync(mod, cancellationToken);
    }

    public async Task<ModUpdateDownloadResult> DownloadModUpdateAsync(
        ModUpdateDownloadDescriptor descriptor,
        IProgress<NexusDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _modUpdateDownloadService.DownloadAsync(
                descriptor,
                progress,
                cancellationToken);
            await RecordActivityAsync(
                ActivityKind.Download,
                ActivityOutcome.Success,
                $"更新包下载完成：{result.FileName}",
                $"来源 {descriptor.Provider} · {result.BytesWritten} 字节",
                descriptor.PageUrl,
                descriptor.ExpectedVersion,
                cancellationToken);
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await RecordActivityAsync(
                ActivityKind.Download,
                ActivityOutcome.Failed,
                "Mod 更新包下载失败",
                error.Message,
                descriptor.PageUrl,
                descriptor.ExpectedVersion,
                CancellationToken.None);
            throw;
        }
    }

    public Task<IReadOnlyList<ModTrashItem>> ListModTrashAsync()
    {
        EnsureCanModifyMods();
        var installation = Installation ?? throw new InvalidOperationException("请先设置游戏目录。");
        return _modService.ListTrashAsync(installation.Path);
    }

    public async Task<ModRestoreResult> RestoreModFromTrashAsync(ModTrashItem item)
    {
        EnsureCanModifyMods();
        var installation = Installation ?? throw new InvalidOperationException("请先设置游戏目录。");
        var result = await _modService.RestoreFromTrashAsync(installation.Path, item);
        await RefreshModsAsync();
        await RecordActivityAsync(
            ActivityKind.Restore,
            ActivityOutcome.Success,
            $"已恢复 {item.DisplayName}",
            result.Renamed
                ? $"原目录冲突，已恢复为 {result.RestoredDirectoryName}"
                : "已恢复到原 Mod 目录",
            version: item.Version);
        return result;
    }

    public async Task<int> EmptyModTrashAsync()
    {
        EnsureCanModifyMods();
        var installation = Installation ?? throw new InvalidOperationException("请先设置游戏目录。");
        var removedCount = await _modService.EmptyTrashAsync(installation.Path);
        await RefreshModsAsync();
        await RecordActivityAsync(
            ActivityKind.Delete,
            ActivityOutcome.Success,
            "Mod 回收区已清空",
            $"永久删除 {removedCount} 个回收区项目。 ");
        return removedCount;
    }

    public void OpenModFolder(InstalledMod mod)
    {
        var installation = Installation ?? throw new InvalidOperationException("请先设置游戏目录。");
        _modService.OpenFolder(installation.Path, mod);
    }

    public async Task LaunchPreferredAsync()
    {
        await LaunchAsync(LaunchPreference);
    }

    public async Task LaunchAsync(LaunchTarget target, string? modsPath = null)
    {
        var installation = Installation ?? throw new InvalidOperationException("请先设置游戏目录。");
        if (target == LaunchTarget.Smapi && !Smapi.Installed)
        {
            throw new InvalidOperationException("请先安装 SMAPI。");
        }

        var argumentText = target == LaunchTarget.Smapi ? SmapiArguments : VanillaArguments;
        var arguments = UiSettingsService.ParseArguments(argumentText);
        ProcessStatus = await _gameProcessService.LaunchAsync(installation, target, arguments, modsPath);
        if (RememberLaunch)
        {
            LaunchPreference = target;
            await SaveSettingsAsync();
        }
        await RecordActivityAsync(
            ActivityKind.Launch,
            ActivityOutcome.Success,
            target == LaunchTarget.Smapi ? "已通过 SMAPI 启动游戏" : "已启动原版游戏",
            ProcessStatus.ProcessId is { } processId ? $"受管进程 PID {processId}" : "游戏进程已创建");
    }

    public async Task StopGameAsync()
    {
        ProcessStatus = await _gameProcessService.StopAsync();
        await RecordActivityAsync(
            ActivityKind.GameControl,
            ActivityOutcome.Success,
            "游戏已关闭",
            "管理器持有的游戏进程树已结束。");
    }

    public async Task RestartGameAsync()
    {
        ProcessStatus = await _gameProcessService.RestartAsync();
        await RecordActivityAsync(
            ActivityKind.GameControl,
            ActivityOutcome.Success,
            "游戏已重新启动",
            ProcessStatus.ProcessId is { } processId ? $"新受管进程 PID {processId}" : "已复用上次启动参数");
    }

    public async Task RecordActivityAsync(
        ActivityKind kind,
        ActivityOutcome outcome,
        string title,
        string detail,
        string? sourceUrl = null,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entry = await _activityHistoryService.AddAsync(
                kind,
                outcome,
                title,
                detail,
                sourceUrl,
                version,
                cancellationToken);
            RecentActivities.Insert(0, entry);
            while (RecentActivities.Count > 200)
            {
                RecentActivities.RemoveAt(RecentActivities.Count - 1);
            }
            OnPropertyChanged(nameof(HasRecentActivities));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Activity logging must not turn a completed user operation into a cancellation.
        }
        catch
        {
            // Activity logging is best effort and must not break the primary operation.
        }
    }

    public async Task ClearActivityHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _activityHistoryService.ClearAsync(cancellationToken);
        RecentActivities.Clear();
        OnPropertyChanged(nameof(HasRecentActivities));
    }

    public async Task CompleteFirstRunAsync(bool autoInstallStarterMod)
    {
        _settings.FirstRunCompleted = true;
        _settings.AutoInstallStarterMod = autoInstallStarterMod;
        OnPropertyChanged(nameof(FirstRunCompleted));
        OnPropertyChanged(nameof(AutoInstallStarterMod));
        await SaveSettingsAsync();
    }

    public async Task RefreshRuntimeAsync()
    {
        var steamTask = _steamService.ReadStatusAsync();
        var processTask = _gameProcessService.RefreshAsync(Installation);
        await Task.WhenAll(steamTask, processTask);
        Steam = steamTask.Result;
        ProcessStatus = processTask.Result;
    }

    public Task SaveSettingsAsync()
    {
        return _settingsService.SaveAsync(_settings);
    }

    public void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task ApplyInstallationAsync(GameInstallation installation)
    {
        Installation = installation;
        await _gameDiscovery.SaveAsync(installation);
        await RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        var steamTask = _steamService.ReadStatusAsync();
        var processTask = _gameProcessService.RefreshAsync(Installation);
        Task<SmapiStatus>? smapiTask = null;
        Task<IReadOnlyList<InstalledMod>>? modsTask = null;
        if (Installation is not null)
        {
            smapiTask = _smapiService.InspectAsync(Installation.Path);
            modsTask = _modService.ScanAsync(Installation.Path);
        }

        var tasks = new List<Task> { steamTask, processTask };
        if (smapiTask is not null)
        {
            tasks.Add(smapiTask);
        }
        if (modsTask is not null)
        {
            tasks.Add(modsTask);
        }
        await Task.WhenAll(tasks);

        Steam = steamTask.Result;
        ProcessStatus = processTask.Result;
        Smapi = smapiTask?.Result ?? SmapiStatus.Missing;
        ReplaceMods(modsTask?.Result ?? Array.Empty<InstalledMod>());
        RebuildWarnings();
    }

    private async Task RefreshModsAsync()
    {
        if (Installation is null)
        {
            ReplaceMods(Array.Empty<InstalledMod>());
            return;
        }
        ReplaceMods(await _modService.ScanAsync(Installation.Path));
        RebuildWarnings();
    }

    private void ReplaceMods(IEnumerable<InstalledMod> mods)
    {
        Mods.Clear();
        foreach (var mod in mods)
        {
            Mods.Add(mod);
        }
        OnPropertyChanged(nameof(EnabledModCount));
        OnPropertyChanged(nameof(ProblemModCount));
        OnPropertyChanged(nameof(EnvironmentHealthText));
    }

    private void ReplaceActivities(IEnumerable<ActivityEntry> entries)
    {
        RecentActivities.Clear();
        foreach (var entry in entries.Take(200))
        {
            RecentActivities.Add(entry);
        }
        OnPropertyChanged(nameof(HasRecentActivities));
    }

    private void RebuildWarnings()
    {
        Warnings.Clear();
        if (Installation is null)
        {
            Warnings.Add("尚未设置有效的游戏目录。");
        }
        else if (!Smapi.Installed)
        {
            Warnings.Add("未检测到 SMAPI，Mod 不会被加载。");
        }
        foreach (var mod in Mods.Where(mod => mod.Health != "healthy"))
        {
            Warnings.Add($"{mod.Name}：{mod.Error ?? "清单异常"}");
        }
    }

    private void EnsureCanModifyMods()
    {
        if (ProcessStatus.Running)
        {
            throw new InvalidOperationException("请先关闭游戏，再修改 Mod 文件。");
        }
    }

    private void RaiseInstallationProperties()
    {
        OnPropertyChanged(nameof(GamePathText));
        OnPropertyChanged(nameof(GamePathShort));
        OnPropertyChanged(nameof(GameStoreText));
        OnPropertyChanged(nameof(GameVersionText));
        OnPropertyChanged(nameof(EnvironmentHealthText));
    }

    private void RaiseSettingsProperties()
    {
        OnPropertyChanged(nameof(RememberLaunch));
        OnPropertyChanged(nameof(LaunchPreference));
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(SmapiArguments));
        OnPropertyChanged(nameof(VanillaArguments));
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(AccentPreset));
        OnPropertyChanged(nameof(FirstRunCompleted));
        OnPropertyChanged(nameof(AutoInstallStarterMod));
    }

    private static string ShortenPath(string path)
    {
        return path.Length <= 9 ? path : $"{path[..6]}…";
    }
}
