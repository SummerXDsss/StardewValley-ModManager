using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ValleySteward.WinUI.Models;
using ValleySteward.WinUI.Services;
using ValleySteward.WinUI.ViewModels;

namespace ValleySteward.WinUI.Pages;

public sealed partial class SmapiPage : Page
{
    private readonly SmapiService _smapiService = new();
    private ShellViewModel? _subscribedViewModel;
    private SmapiReleaseInfo? _latestRelease;
    private bool _isBusy;

    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    public SmapiPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DataContext = e.Parameter;
        SubscribeToViewModel();
        UpdateState();
        await RefreshLatestReleaseAsync(forceRefresh: false, showError: false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }
        base.OnNavigatedFrom(e);
    }

    private void SubscribeToViewModel()
    {
        if (ReferenceEquals(_subscribedViewModel, ViewModel))
        {
            return;
        }
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Smapi)
            or nameof(ShellViewModel.Installation)
            or nameof(ShellViewModel.ProcessStatus)
            or nameof(ShellViewModel.IsGameRunning))
        {
            DispatcherQueue.TryEnqueue(UpdateState);
        }
    }

    private void UpdateState()
    {
        var hasGame = ViewModel?.Installation is not null;
        var installed = ViewModel?.Smapi.Installed == true;
        var gameRunning = ViewModel?.IsGameRunning == true;

        InstalledVersionText.Text = installed
            ? ViewModel?.Smapi.Version ?? "已安装，版本未识别"
            : "未安装";
        ExecutableText.Text = ViewModel?.Smapi.Executable ?? "—";
        InstallButton.IsEnabled = hasGame && !gameRunning && !_isBusy;
        UninstallButton.IsEnabled = hasGame && installed && !gameRunning && !_isBusy;
        LaunchSmapiButton.IsEnabled = hasGame && installed && !gameRunning && !_isBusy;
        CheckReleaseButton.IsEnabled = !_isBusy;
        RefreshButton.IsEnabled = !_isBusy;

        UpdateReleaseComparison();
        if (!_isBusy && !SmapiInfoBar.IsOpen && !hasGame)
        {
            ShowLocalMessage(
                "尚未设置游戏目录",
                "请先在设置中自动查找或选择 Stardew Valley 游戏目录。",
                InfoBarSeverity.Warning);
        }
        else if (!_isBusy && !SmapiInfoBar.IsOpen && hasGame && !installed)
        {
            ShowLocalMessage(
                "未检测到 SMAPI",
                "可以直接下载并运行与 Windows 匹配的官方稳定版安装器。",
                InfoBarSeverity.Warning);
        }
    }

    private async Task<bool> RefreshLatestReleaseAsync(bool forceRefresh, bool showError)
    {
        try
        {
            LatestVersionText.Text = "正在检查…";
            UpdateStatusText.Text = "正在连接 GitHub Releases";
            _latestRelease = await _smapiService.GetLatestReleaseAsync(forceRefresh);
            LatestVersionText.Text = $"SMAPI {_latestRelease.Version}";
            ReleaseSourceText.Text = _latestRelease.Source == SmapiReleaseSource.GitHubApi
                ? "GitHub Releases API（稳定版）"
                : "GitHub releases/latest 官方重定向（稳定版）";
            DigestStatusText.Text = _latestRelease.Asset.Digest?.StartsWith(
                "sha256:",
                StringComparison.OrdinalIgnoreCase) == true
                    ? "GitHub 提供官方 SHA-256；下载后将强制核验"
                    : "GitHub 未提供 SHA-256；仍会记录本地哈希，第三方镜像将被拒绝";
            UpdateReleaseComparison();
            return true;
        }
        catch (Exception error)
        {
            _latestRelease = null;
            LatestVersionText.Text = "获取失败";
            UpdateStatusText.Text = "无法读取官方稳定版";
            ReleaseSourceText.Text = "GitHub";
            DigestStatusText.Text = "未获取到安装包元数据";
            if (showError)
            {
                ShowLocalMessage("检查 SMAPI 更新失败", error.Message, InfoBarSeverity.Error);
                App.MainWindow.ShowMessage("检查 SMAPI 更新失败", error.Message, InfoBarSeverity.Error);
            }
            return false;
        }
        finally
        {
            UpdateState();
        }
    }

    private void UpdateReleaseComparison()
    {
        var installed = ViewModel?.Smapi;
        if (_latestRelease is null)
        {
            InstallButtonText.Text = installed?.Installed == true ? "重新安装 SMAPI" : "安装 SMAPI";
            return;
        }
        if (installed?.Installed != true)
        {
            UpdateStatusText.Text = $"可安装 {_latestRelease.Version}";
            InstallButtonText.Text = $"安装 {_latestRelease.Version}";
            return;
        }

        var local = SmapiService.NormalizeVersion(installed.Version);
        var comparison = SmapiService.CompareVersions(installed.Version, _latestRelease.Version);
        if (comparison == 0)
        {
            UpdateStatusText.Text = "已是最新稳定版";
            InstallButtonText.Text = "重新安装 SMAPI";
            return;
        }

        if (comparison > 0)
        {
            UpdateStatusText.Text = $"本机 {local} 比稳定版 {_latestRelease.Version} 更新";
            InstallButtonText.Text = "安装稳定版";
        }
        else
        {
            UpdateStatusText.Text = $"可更新：{installed.Version ?? "未知"} → {_latestRelease.Version}";
            InstallButtonText.Text = $"更新到 {_latestRelease.Version}";
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || _isBusy)
        {
            return;
        }
        SetBusy(true);
        try
        {
            await ViewModel.RefreshAllAsync();
            if (await RefreshLatestReleaseAsync(forceRefresh: true, showError: true))
            {
                ShowLocalMessage("检测完成", ViewModel.SmapiStatusText, InfoBarSeverity.Success);
            }
        }
        catch (Exception error)
        {
            ShowLocalMessage("SMAPI 检测失败", error.Message, InfoBarSeverity.Error);
            App.MainWindow.ShowMessage("SMAPI 检测失败", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnCheckReleaseClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }
        SetBusy(true);
        try
        {
            await RefreshLatestReleaseAsync(forceRefresh: true, showError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Installation is null || _isBusy)
        {
            return;
        }
        if (ViewModel.IsGameRunning)
        {
            ShowLocalMessage("无法安装 SMAPI", "请先关闭游戏。", InfoBarSeverity.Warning);
            return;
        }

        var actionText = ViewModel.Smapi.Installed ? "安装或更新" : "安装";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{actionText} SMAPI",
            Content = "将从 Pathoschild/SMAPI 的官方稳定版 Release 下载安装包，并静默运行官方 Windows 安装器。操作期间请不要启动游戏。",
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunOperationAsync(
            progress => _smapiService.InstallLatestAsync(
                ViewModel.Installation.Path,
                progress),
            "SMAPI 安装完成");
    }

    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Installation is null || !ViewModel.Smapi.Installed || _isBusy)
        {
            return;
        }
        if (ViewModel.IsGameRunning)
        {
            ShowLocalMessage("无法卸载 SMAPI", "请先关闭游戏。", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "卸载 SMAPI",
            Content = "将调用 SMAPI 官方卸载器移除运行时文件。你的 Mods 文件夹不会由管理器直接删除。",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunOperationAsync(
            progress => _smapiService.UninstallAsync(
                ViewModel.Installation.Path,
                progress),
            "SMAPI 卸载完成");
    }

    private async Task RunOperationAsync(
        Func<IProgress<SmapiOperationProgress>, Task<SmapiOperationResult>> operation,
        string successTitle)
    {
        if (ViewModel is null)
        {
            return;
        }
        SetBusy(true);
        OperationPanel.Visibility = Visibility.Visible;
        OperationProgressBar.IsIndeterminate = true;
        OperationPercentText.Text = string.Empty;
        var progress = new Progress<SmapiOperationProgress>(UpdateOperationProgress);
        try
        {
            var result = await operation(progress);
            await ViewModel.RefreshAllAsync();
            _latestRelease = await _smapiService.GetLatestReleaseAsync();
            UpdateState();

            var verification = result.DigestVerified
                ? "官方 SHA-256 已核验"
                : "已记录下载哈希";
            var route = result.UsedMirror ? "GitHub 镜像" : "GitHub 直连";
            var message = $"{result.Message} 下载：{route}；{verification}。";
            await ViewModel.RecordActivityAsync(
                ActivityKind.Smapi,
                ActivityOutcome.Success,
                successTitle,
                message,
                _latestRelease?.PageUrl,
                result.CurrentVersion ?? result.ReleaseVersion);
            ShowLocalMessage(successTitle, message, InfoBarSeverity.Success);
            App.MainWindow.ShowMessage(successTitle, message, InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            OperationStatusText.Text = "操作失败，未通过结果复检";
            OperationPercentText.Text = string.Empty;
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = 0;
            await ViewModel.RecordActivityAsync(
                ActivityKind.Smapi,
                ActivityOutcome.Failed,
                "SMAPI 操作失败",
                error.Message,
                _latestRelease?.PageUrl,
                _latestRelease?.Version);
            ShowLocalMessage("SMAPI 操作失败", error.Message, InfoBarSeverity.Error);
            App.MainWindow.ShowMessage("SMAPI 操作失败", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateOperationProgress(SmapiOperationProgress progress)
    {
        OperationPanel.Visibility = Visibility.Visible;
        OperationStatusText.Text = progress.Message;
        if (progress.Percent is double percent)
        {
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = Math.Clamp(percent, 0, 100);
            OperationPercentText.Text = $"{Math.Round(percent)}%";
        }
        else
        {
            OperationProgressBar.IsIndeterminate = true;
            OperationPercentText.Text = string.Empty;
        }
    }

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }
        try
        {
            await ViewModel.LaunchAsync(LaunchTarget.Smapi);
            App.MainWindow.ShowMessage("SMAPI 已启动", "正在监视游戏进程。", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("启动失败", error.Message, InfoBarSeverity.Error);
        }
        UpdateState();
    }

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenUrl(
            _latestRelease?.PageUrl
            ?? "https://github.com/Pathoschild/SMAPI/releases/latest");
    }

    private void OnOpenOfficialClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenUrl("https://smapi.io/");
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        UpdateState();
    }

    private void ShowLocalMessage(string title, string message, InfoBarSeverity severity)
    {
        SmapiInfoBar.Title = title;
        SmapiInfoBar.Message = message;
        SmapiInfoBar.Severity = severity;
        SmapiInfoBar.IsOpen = true;
    }
}
