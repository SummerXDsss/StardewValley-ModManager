using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using ValleySteward.WinUI.Models;
using ValleySteward.WinUI.Services;
using ValleySteward.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;

namespace ValleySteward.WinUI.Pages;

public sealed partial class DownloadsPage : Page
{
    private readonly RemoteModService _service = new();
    private readonly NexusDownloadService _nexusDownloadService = new();
    private readonly ModUpdateService _modUpdateService = new();
    private readonly ModUpdateDownloadService _modUpdateDownloadService = new();
    private readonly AiTranslationService _aiTranslationService = new(new CredentialService());
    private readonly ObservableCollection<RemoteModItem> _results = [];
    private readonly HashSet<string> _translatingModIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quickInstallingModIds = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _activeTranslationRequests = [];
    private CancellationTokenSource? _detailsCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private CancellationTokenSource? _installCancellation;
    private NexusModDetails? _activeNexusMod;
    private RemoteModItem? _activeRemoteMod;
    private NexusDownloadResult? _lastDownload;
    private bool _downloading;
    private bool _installing;
    private bool _quickInstallInProgress;
    private bool _loaded;
    private bool _aiActivitySubscribed;
    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    public DownloadsPage()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _results;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DataContext = e.Parameter;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_aiActivitySubscribed)
        {
            _aiTranslationService.RequestActivity += OnAiRequestActivity;
            _aiActivitySubscribed = true;
        }
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        await SearchAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _detailsCancellation?.Cancel();
        _downloadCancellation?.Cancel();
        _installCancellation?.Cancel();
        if (_aiActivitySubscribed)
        {
            _aiTranslationService.RequestActivity -= OnAiRequestActivity;
            _aiActivitySubscribed = false;
        }
    }

    private void OnAiRequestActivity(object? sender, AiTranslationRequestActivityEventArgs activity)
    {
        if (activity.Kind != AiTranslationRequestKind.Translation)
        {
            return;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (activity.Stage)
            {
                case AiTranslationRequestStage.Queued:
                    _activeTranslationRequests.Add(activity.RequestId);
                    TranslationInfoBar.Title = "翻译请求已排队";
                    TranslationInfoBar.Message = $"目标：{activity.Request.Endpoint}";
                    TranslationInfoBar.Severity = InfoBarSeverity.Informational;
                    break;
                case AiTranslationRequestStage.Sending:
                    _activeTranslationRequests.Add(activity.RequestId);
                    TranslationInfoBar.Title = $"正在向 {activity.Request.Endpoint} 发起请求";
                    TranslationInfoBar.Message = $"{activity.Request.Method} · 当前 {_activeTranslationRequests.Count} 个翻译请求处理中";
                    TranslationInfoBar.Severity = InfoBarSeverity.Informational;
                    break;
                case AiTranslationRequestStage.ResponseReceived:
                    TranslationInfoBar.Title = "上游模型地址已响应";
                    TranslationInfoBar.Message = $"{activity.Request.Endpoint} · HTTP {activity.StatusCode} · {activity.ElapsedMilliseconds} ms";
                    TranslationInfoBar.Severity = InfoBarSeverity.Success;
                    break;
                case AiTranslationRequestStage.Completed:
                    _activeTranslationRequests.Remove(activity.RequestId);
                    TranslationInfoBar.Title = "翻译响应已处理";
                    TranslationInfoBar.Message = $"{activity.Request.Endpoint} · 剩余 {_activeTranslationRequests.Count} 个请求";
                    TranslationInfoBar.Severity = InfoBarSeverity.Success;
                    break;
                case AiTranslationRequestStage.TimedOut:
                    _activeTranslationRequests.Remove(activity.RequestId);
                    TranslationInfoBar.Title = "上游模型请求超时";
                    TranslationInfoBar.Message = $"{activity.Request.Endpoint} · {activity.Detail}";
                    TranslationInfoBar.Severity = InfoBarSeverity.Error;
                    break;
                case AiTranslationRequestStage.Failed:
                case AiTranslationRequestStage.Canceled:
                    _activeTranslationRequests.Remove(activity.RequestId);
                    TranslationInfoBar.Title = activity.Stage == AiTranslationRequestStage.Canceled
                        ? "翻译请求已取消"
                        : "上游模型请求失败";
                    TranslationInfoBar.Message = $"{activity.Request.Endpoint} · {activity.Detail}";
                    TranslationInfoBar.Severity = activity.Stage == AiTranslationRequestStage.Canceled
                        ? InfoBarSeverity.Informational
                        : InfoBarSeverity.Error;
                    break;
            }
            TranslationInfoBar.IsOpen = true;
        });
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnLocalArchiveClick(object sender, RoutedEventArgs e)
    {
        if (_installing)
        {
            return;
        }
        var archivePath = await App.MainWindow.ChooseModArchiveAsync();
        if (archivePath is not null)
        {
            await InstallFromArchiveAsync(archivePath);
        }
    }

    private void OnArchiveDragOver(object sender, DragEventArgs e)
    {
        if (!_installing && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "安装 Mod ZIP";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void OnArchiveDrop(object sender, DragEventArgs e)
    {
        if (_installing || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            var files = (await e.DataView.GetStorageItemsAsync())
                .OfType<StorageFile>()
                .ToArray();
            if (files.Length != 1
                || !files[0].FileType.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                App.MainWindow.ShowMessage(
                    "无法安装拖入内容",
                    "请一次拖入一个 .zip 格式的 SMAPI Mod 安装包。",
                    InfoBarSeverity.Warning);
                return;
            }
            await InstallFromArchiveAsync(files[0].Path);
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("无法读取拖入文件", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnNxmLinkClick(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            return;
        }

        var input = new TextBox
        {
            Header = "NXM 链接",
            PlaceholderText = "nxm://stardewvalley/mods/.../files/...",
            MinWidth = 440,
            TextWrapping = TextWrapping.Wrap,
        };
        try
        {
            var clipboard = Clipboard.GetContent();
            if (clipboard.Contains(StandardDataFormats.Text))
            {
                var text = (await clipboard.GetTextAsync()).Trim();
                if (text.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                {
                    input.Text = text;
                }
            }
        }
        catch
        {
            // Clipboard access is optional; the user can paste manually.
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "从 Nexus Mod Manager 链接下载",
            Content = input,
            PrimaryButtonText = "开始下载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        NexusNxmLink parsed;
        try
        {
            parsed = NexusDownloadService.ParseNxmLink(input.Text);
        }
        catch (Exception error)
        {
            SearchInfoBar.Title = "NXM 链接无效";
            SearchInfoBar.Message = error.Message;
            SearchInfoBar.Severity = InfoBarSeverity.Error;
            SearchInfoBar.IsOpen = true;
            return;
        }

        await DownloadFromNxmAsync(input.Text, parsed);
    }

    private async Task DownloadFromNxmAsync(string nxmLink, NexusNxmLink parsed)
    {
        NexusDownloadResult? completedDownload = null;
        _downloading = true;
        _activeNexusMod = null;
        _activeRemoteMod = null;
        _lastDownload = null;
        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        NexusDetailsTitle.Text = "NXM 下载";
        NexusDetailsSubtitle.Text = $"Nexus Mod #{parsed.ModId} · 文件 #{parsed.FileId}";
        NexusFilesList.ItemsSource = null;
        NexusDetailsPane.Visibility = Visibility.Visible;
        CloseNexusDetailsButton.IsEnabled = false;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = true;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = "正在验证 NXM 授权并请求安全下载地址…";
        DownloadInfoBar.IsOpen = false;
        InstallDownloadedFileButton.Visibility = Visibility.Collapsed;
        OpenDownloadedFileButton.Visibility = Visibility.Collapsed;
        OpenNexusFilePageButton.Visibility = Visibility.Collapsed;

        var progress = new Progress<NexusDownloadProgress>(value =>
        {
            DownloadStatusText.Text = value.StatusText;
            DownloadProgressBar.IsIndeterminate = value.Percentage is null;
            if (value.Percentage is { } percentage)
            {
                DownloadProgressBar.Value = percentage;
            }
        });

        try
        {
            _lastDownload = await _nexusDownloadService.DownloadFromNxmAsync(
                nxmLink,
                progress,
                _downloadCancellation.Token);
            completedDownload = _lastDownload;
            if (ViewModel is not null)
            {
                await ViewModel.RecordActivityAsync(
                    ActivityKind.Download,
                    ActivityOutcome.Success,
                    $"Nexus 下载完成：{_lastDownload.FileName}",
                    $"Nexus Mod #{parsed.ModId} · 文件 #{parsed.FileId}");
            }
            ShowDownloadMessage(
                "NXM 下载完成，正在准备安装",
                $"{_lastDownload.FileName} 已保存，接下来会检查压缩包内容。",
                InfoBarSeverity.Success,
                showDownloadedFile: true,
                showInstall: true);
        }
        catch (OperationCanceledException)
        {
            ShowDownloadMessage("下载已取消", "未完成的临时文件已清理。", InfoBarSeverity.Informational);
        }
        catch (Exception error)
        {
            ShowDownloadMessage("NXM 下载失败", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            CloseNexusDetailsButton.IsEnabled = true;
            _downloading = false;
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
        }
        if (completedDownload is not null)
        {
            await InstallFromArchiveAsync(completedDownload.Path, completedDownload.MetadataPath);
        }
    }

    private async void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await SearchAsync();
        }
    }

    private async Task SearchAsync()
    {
        SearchInfoBar.IsOpen = false;
        SearchButton.IsEnabled = false;
        SearchProgressRing.IsActive = true;
        SearchProgressRing.Visibility = Visibility.Visible;
        try
        {
            var source = SourceComboBox.SelectedIndex switch
            {
                1 => RemoteSearchSource.Nexus,
                2 => RemoteSearchSource.GitHub,
                _ => RemoteSearchSource.All,
            };
            var result = await _service.SearchAsync(SearchBox.Text, source);
            _results.Clear();
            foreach (var mod in result.Mods)
            {
                _results.Add(mod);
            }
            QueueRefreshRemoteSummaryToggles();

            if (result.Errors.Count > 0)
            {
                SearchInfoBar.Title = "部分来源请求失败";
                SearchInfoBar.Message = string.Join(Environment.NewLine, result.Errors);
                SearchInfoBar.Severity = result.Mods.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
                SearchInfoBar.IsOpen = true;
            }
            else if (result.Mods.Count == 0)
            {
                SearchInfoBar.Title = "没有找到结果";
                SearchInfoBar.Message = "换一个名称、作者或关键词再试。";
                SearchInfoBar.Severity = InfoBarSeverity.Informational;
                SearchInfoBar.IsOpen = true;
            }
        }
        catch (Exception error)
        {
            SearchInfoBar.Title = "搜索失败";
            SearchInfoBar.Message = error.Message;
            SearchInfoBar.Severity = InfoBarSeverity.Error;
            SearchInfoBar.IsOpen = true;
        }
        finally
        {
            SearchButton.IsEnabled = true;
            SearchProgressRing.IsActive = false;
            SearchProgressRing.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RemoteModItem mod)
        {
            ViewModel?.OpenUrl(mod.PageUrl);
        }
    }

    private void OnRemoteCoverLoaded(object sender, RoutedEventArgs e)
    {
        UpdateRemoteCover(sender as Image);
    }

    private void OnRemoteCoverDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        UpdateRemoteCover(sender as Image);
    }

    private static void UpdateRemoteCover(Image? image)
    {
        if (image is null)
        {
            return;
        }
        image.Source = null;
        image.Visibility = Visibility.Collapsed;
        if (image.DataContext is RemoteModItem mod
            && TryCreateRemoteImageUri(mod.ImageUrl, out var uri))
        {
            image.Source = new BitmapImage(uri);
            image.Visibility = Visibility.Visible;
        }
    }

    private void OnRemoteCoverFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image)
        {
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnQuickInstallClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RemoteModItem mod)
        {
            return;
        }
        if (_quickInstallInProgress || _downloading || _installing)
        {
            ShowSearchMessage(
                "已有任务正在处理",
                "请先完成当前下载或安装，再开始下一项。",
                InfoBarSeverity.Informational);
            return;
        }
        if (!_quickInstallingModIds.Add(mod.Id))
        {
            return;
        }

        _quickInstallInProgress = true;
        mod.IsQuickInstalling = true;
        try
        {
            if (ViewModel?.Installation is null)
            {
                ShowSearchMessage(
                    "请先设置游戏路径",
                    "下载并安装前需要确认 Stardew Valley 的游戏目录。",
                    InfoBarSeverity.Warning);
                return;
            }
            if (ViewModel.ProcessStatus.Running)
            {
                ShowSearchMessage(
                    "游戏正在运行",
                    "请先关闭游戏，再安装或更新 Mod。",
                    InfoBarSeverity.Warning);
                return;
            }
            if (_downloading || _installing)
            {
                ShowSearchMessage(
                    "已有任务正在处理",
                    "请先完成当前下载或安装，再开始下一项。",
                    InfoBarSeverity.Informational);
                return;
            }

            switch (mod.Source)
            {
                case "Nexus Mods":
                    await QuickInstallNexusAsync(mod);
                    break;
                case "GitHub":
                    await QuickInstallGitHubAsync(mod);
                    break;
                default:
                    ShowSearchMessage(
                        "暂不支持一键下载",
                        $"{mod.Source} 尚未提供可验证的自动下载链路。",
                        InfoBarSeverity.Warning);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            ShowDownloadMessage("操作已取消", "未完成的临时文件已清理。", InfoBarSeverity.Informational);
        }
        catch (NexusApiKeyMissingException error)
        {
            ShowDownloadMessage(
                "需要 Nexus API Key",
                $"{error.Message} 请先在设置中保存个人 API Key，再重试或使用 NXM 链接。",
                InfoBarSeverity.Warning,
                showFilePage: _activeNexusMod is not null);
        }
        catch (Exception error)
        {
            ShowSearchMessage("下载并安装失败", error.Message, InfoBarSeverity.Error);
            if (mod.Source == "Nexus Mods" && NexusDetailsPane.Visibility == Visibility.Visible)
            {
                ShowDownloadMessage(
                    "无法完成一键下载",
                    $"{error.Message} 可在文件面板手动选择，普通 Nexus 账户也可粘贴 NXM 链接。",
                    InfoBarSeverity.Error,
                    showFilePage: _activeNexusMod is not null);
            }
            if (ViewModel is not null)
            {
                await ViewModel.RecordActivityAsync(
                    ActivityKind.Download,
                    ActivityOutcome.Failed,
                    $"一键下载失败：{mod.Name}",
                    error.Message,
                    mod.PageUrl);
            }
        }
        finally
        {
            _quickInstallInProgress = false;
            _quickInstallingModIds.Remove(mod.Id);
            mod.IsQuickInstalling = false;
        }
    }

    private async Task QuickInstallNexusAsync(RemoteModItem mod)
    {
        if (!TryGetNexusModId(mod, out var modId))
        {
            throw new InvalidDataException("Nexus 搜索结果缺少有效的 Mod ID。");
        }

        _detailsCancellation?.Cancel();
        _detailsCancellation?.Dispose();
        _detailsCancellation = new CancellationTokenSource();
        _activeNexusMod = null;
        _activeRemoteMod = mod;
        _lastDownload = null;
        NexusFilesList.ItemsSource = null;
        NexusDetailsTitle.Text = mod.Name;
        NexusDetailsSubtitle.Text = $"Nexus Mod #{modId} · 正在确认唯一主文件";
        NexusDetailsPane.Visibility = Visibility.Visible;
        NexusDetailsProgressRing.IsActive = true;
        NexusDetailsProgressRing.Visibility = Visibility.Visible;
        DownloadInfoBar.IsOpen = false;
        InstallDownloadedFileButton.Visibility = Visibility.Collapsed;
        OpenDownloadedFileButton.Visibility = Visibility.Collapsed;
        OpenNexusFilePageButton.Visibility = Visibility.Collapsed;

        NexusModDetails details;
        try
        {
            details = await _nexusDownloadService.GetModDetailsAsync(
                modId,
                _detailsCancellation.Token);
        }
        finally
        {
            NexusDetailsProgressRing.IsActive = false;
            NexusDetailsProgressRing.Visibility = Visibility.Collapsed;
        }

        _activeNexusMod = details;
        NexusDetailsTitle.Text = mod.Translated ? mod.Name : details.Name;
        NexusDetailsSubtitle.Text = $"Nexus Mod #{details.GameScopedId} · {details.FileCountText}";
        NexusFilesList.ItemsSource = details.Files;
        var primaryVersions = details.Files
            .Where(file => file.IsActive)
            .SelectMany(file => file.Versions)
            .Where(version => version.IsPrimary)
            .DistinctBy(version => version.GameScopedId, StringComparer.Ordinal)
            .ToArray();
        if (primaryVersions.Length != 1)
        {
            ShowDownloadMessage(
                "需要选择文件",
                primaryVersions.Length == 0
                    ? "Nexus 没有返回唯一标记为主文件的版本，请在右侧文件面板中确认。"
                    : $"Nexus 返回了 {primaryVersions.Length} 个主文件候选，已停止自动选择，请手动确认。",
                InfoBarSeverity.Warning,
                showFilePage: true);
            return;
        }

        await DownloadAndInstallNexusAsync(mod, details, primaryVersions[0]);
    }

    private async Task DownloadAndInstallNexusAsync(
        RemoteModItem mod,
        NexusModDetails details,
        NexusFileVersion version)
    {
        _downloading = true;
        _activeNexusMod = details;
        _activeRemoteMod = mod;
        _lastDownload = null;
        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        CloseNexusDetailsButton.IsEnabled = false;
        NexusFilesList.IsEnabled = false;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = true;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = $"正在请求 {version.DisplayName} 的安全下载地址…";
        DownloadInfoBar.IsOpen = false;

        var progress = new Progress<NexusDownloadProgress>(value =>
        {
            DownloadStatusText.Text = value.StatusText;
            DownloadProgressBar.IsIndeterminate = value.Percentage is null;
            if (value.Percentage is { } percentage)
            {
                DownloadProgressBar.Value = percentage;
            }
        });

        NexusDownloadResult? completedDownload = null;
        try
        {
            completedDownload = await _nexusDownloadService.DownloadAsync(
                details.GameScopedId,
                version.GameScopedId,
                progress,
                _downloadCancellation.Token,
                translation: mod.Translated
                    ? new NexusDownloadTranslation(mod.Name, mod.Summary)
                    : null);
            _lastDownload = completedDownload;
            if (ViewModel is not null)
            {
                await ViewModel.RecordActivityAsync(
                    ActivityKind.Download,
                    ActivityOutcome.Success,
                    $"Nexus 下载完成：{completedDownload.FileName}",
                    $"{mod.Name} · 主文件 {version.DisplayName} · {completedDownload.BytesWritten} 字节",
                    details.PageUrl,
                    version.Version,
                    _downloadCancellation.Token);
            }
            ShowDownloadMessage(
                completedDownload.MetadataPath is null ? "下载完成，正在准备安装" : "下载完成，译文已绑定",
                $"{completedDownload.FileName} 已通过唯一主文件规则选中，接下来会检查安装内容。",
                InfoBarSeverity.Success,
                showDownloadedFile: true,
                showInstall: true);
        }
        finally
        {
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            NexusFilesList.IsEnabled = true;
            CloseNexusDetailsButton.IsEnabled = true;
            _downloading = false;
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
        }

        if (completedDownload is not null)
        {
            await InstallFromArchiveAsync(completedDownload.Path, completedDownload.MetadataPath);
        }
    }

    private async Task QuickInstallGitHubAsync(RemoteModItem mod)
    {
        if (!TryGetGitHubRepository(mod, out var repository))
        {
            throw new InvalidDataException("GitHub 搜索结果缺少可信的 owner/repository 地址。");
        }

        var resolution = await _modUpdateService.ResolveGitHubReleaseDownloadAsync(repository);
        if (resolution.Download is not GitHubModUpdateDownloadDescriptor descriptor)
        {
            await ShowGitHubSelectionRequiredAsync(
                mod,
                resolution.PageUrl,
                resolution.CannotDownloadReason
                    ?? "最新 Release 没有唯一可信的 ZIP 资产，无法安全自动选择。");
            return;
        }

        _downloading = true;
        _activeNexusMod = null;
        _activeRemoteMod = mod;
        _lastDownload = null;
        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        NexusDetailsTitle.Text = mod.Name;
        NexusDetailsSubtitle.Text = $"GitHub Release {resolution.LatestVersion} · 唯一 ZIP";
        NexusFilesList.ItemsSource = null;
        NexusDetailsPane.Visibility = Visibility.Visible;
        CloseNexusDetailsButton.IsEnabled = false;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = true;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = $"正在下载 {descriptor.AssetName}…";
        DownloadInfoBar.IsOpen = false;

        var progress = new Progress<NexusDownloadProgress>(value =>
        {
            DownloadStatusText.Text = value.StatusText;
            DownloadProgressBar.IsIndeterminate = value.Percentage is null;
            if (value.Percentage is { } percentage)
            {
                DownloadProgressBar.Value = percentage;
            }
        });

        NexusDownloadResult? completedDownload = null;
        try
        {
            var downloaded = await _modUpdateDownloadService.DownloadAsync(
                descriptor,
                progress,
                _downloadCancellation.Token);
            string? metadataPath = null;
            if (mod.Translated)
            {
                metadataPath = DownloadTranslationSidecarService.WriteForArchive(
                    downloaded.Path,
                    "GitHub",
                    repository,
                    new NexusDownloadTranslation(mod.Name, mod.Summary),
                    downloaded.Sha256
                        ?? throw new InvalidDataException("GitHub 下载结果缺少 SHA-256。"));
            }
            completedDownload = new NexusDownloadResult(
                downloaded.Path,
                downloaded.FileName,
                downloaded.BytesWritten,
                metadataPath);
            _lastDownload = completedDownload;
            if (ViewModel is not null)
            {
                await ViewModel.RecordActivityAsync(
                    ActivityKind.Download,
                    ActivityOutcome.Success,
                    $"GitHub 下载完成：{downloaded.FileName}",
                    $"{repository} · {downloaded.BytesWritten} 字节"
                    + (downloaded.UsedMirror ? " · 使用镜像" : string.Empty),
                    resolution.PageUrl,
                    resolution.LatestVersion,
                    _downloadCancellation.Token);
            }
            ShowDownloadMessage(
                metadataPath is null ? "下载完成，正在准备安装" : "下载完成，译文已绑定",
                $"{downloaded.FileName} 是最新 Release 中唯一可信的 ZIP，接下来会检查安装内容。",
                InfoBarSeverity.Success,
                showDownloadedFile: true,
                showInstall: true);
        }
        finally
        {
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            CloseNexusDetailsButton.IsEnabled = true;
            _downloading = false;
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
        }

        if (completedDownload is not null)
        {
            await InstallFromArchiveAsync(completedDownload.Path, completedDownload.MetadataPath);
        }
    }

    private async Task ShowGitHubSelectionRequiredAsync(
        RemoteModItem mod,
        string releasesUrl,
        string reason)
    {
        ShowSearchMessage("需要手动确认 GitHub 文件", reason, InfoBarSeverity.Warning);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"无法自动选择 {mod.Name} 的文件",
            Content = new TextBlock
            {
                Text = reason,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                MaxWidth = 500,
            },
            PrimaryButtonText = "打开 Releases",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel?.OpenUrl(releasesUrl);
        }
    }

    private void ShowSearchMessage(
        string title,
        string message,
        InfoBarSeverity severity)
    {
        SearchInfoBar.Title = title;
        SearchInfoBar.Message = message;
        SearchInfoBar.Severity = severity;
        SearchInfoBar.IsOpen = true;
    }

    private async void OnRemoteDetailsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RemoteModItem mod)
        {
            return;
        }

        var details = new StackPanel
        {
            Width = 520,
            MaxHeight = 500,
            Spacing = 12,
        };
        details.Children.Add(new TextBlock
        {
            Text = mod.Name,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        details.Children.Add(new TextBlock
        {
            Text = $"{mod.Source}  ·  {mod.Author}  ·  {mod.Version}  ·  {mod.Popularity}",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        });
        details.Children.Add(new TextBlock
        {
            Text = mod.Summary,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        details.Children.Add(new TextBlock
        {
            Text = $"更新时间：{mod.UpdatedText}",
            Opacity = 0.72,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Mod 详情",
            Content = new ScrollViewer
            {
                Content = details,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            PrimaryButtonText = "打开上游页面",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel?.OpenUrl(mod.PageUrl);
        }
    }

    private void OnNexusFilesButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.Visibility = button.DataContext is RemoteModItem { Source: "Nexus Mods" }
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private async void OnNexusFilesClick(object sender, RoutedEventArgs e)
    {
        if (_downloading
            || (sender as FrameworkElement)?.DataContext is not RemoteModItem mod
            || !TryGetNexusModId(mod, out var modId))
        {
            return;
        }

        _detailsCancellation?.Cancel();
        _detailsCancellation?.Dispose();
        _detailsCancellation = new CancellationTokenSource();
        _activeNexusMod = null;
        _activeRemoteMod = mod;
        _lastDownload = null;
        NexusFilesList.ItemsSource = null;
        NexusDetailsTitle.Text = mod.Name;
        NexusDetailsSubtitle.Text = $"Nexus Mod #{modId} · 正在读取文件";
        NexusDetailsPane.Visibility = Visibility.Visible;
        NexusDetailsProgressRing.IsActive = true;
        NexusDetailsProgressRing.Visibility = Visibility.Visible;
        DownloadInfoBar.IsOpen = false;
        InstallDownloadedFileButton.Visibility = Visibility.Collapsed;
        OpenDownloadedFileButton.Visibility = Visibility.Collapsed;
        OpenNexusFilePageButton.Visibility = Visibility.Collapsed;

        try
        {
            var details = await _nexusDownloadService.GetModDetailsAsync(
                modId,
                _detailsCancellation.Token);
            _activeNexusMod = details;
            NexusDetailsTitle.Text = mod.Translated ? mod.Name : details.Name;
            NexusDetailsSubtitle.Text = $"Nexus Mod #{details.GameScopedId} · {details.FileCountText}";
            NexusFilesList.ItemsSource = details.Files;
            if (details.Files.Count == 0)
            {
                ShowDownloadMessage(
                    "没有可用文件",
                    "此 Mod 当前没有通过 Nexus API 返回文件，请打开官方文件页查看。",
                    InfoBarSeverity.Informational,
                    showFilePage: true);
            }
        }
        catch (OperationCanceledException)
        {
            // Selecting another result or leaving the page cancels this request.
        }
        catch (NexusApiKeyMissingException error)
        {
            ShowDownloadMessage("需要 Nexus API Key", error.Message, InfoBarSeverity.Warning);
        }
        catch (Exception error)
        {
            ShowDownloadMessage(
                "无法读取 Nexus 文件",
                error.Message,
                InfoBarSeverity.Error,
                showFilePage: true);
        }
        finally
        {
            NexusDetailsProgressRing.IsActive = false;
            NexusDetailsProgressRing.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnDownloadVersionClick(object sender, RoutedEventArgs e)
    {
        if (_downloading
            || _activeNexusMod is null
            || (sender as FrameworkElement)?.DataContext is not NexusFileVersion version)
        {
            return;
        }

        NexusDownloadResult? completedDownload = null;
        _downloading = true;
        _lastDownload = null;
        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        CloseNexusDetailsButton.IsEnabled = false;
        NexusFilesList.IsEnabled = false;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = true;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = $"正在请求 {version.DisplayName} 的安全下载地址…";
        DownloadInfoBar.IsOpen = false;
        OpenDownloadedFileButton.Visibility = Visibility.Collapsed;
        OpenNexusFilePageButton.Visibility = Visibility.Collapsed;

        var progress = new Progress<NexusDownloadProgress>(value =>
        {
            DownloadStatusText.Text = value.StatusText;
            DownloadProgressBar.IsIndeterminate = value.Percentage is null;
            if (value.Percentage is { } percentage)
            {
                DownloadProgressBar.Value = percentage;
            }
        });

        try
        {
            var result = await _nexusDownloadService.DownloadAsync(
                _activeNexusMod.GameScopedId,
                version.GameScopedId,
                progress,
                _downloadCancellation.Token,
                translation: _activeRemoteMod is { Translated: true } translatedMod
                    ? new NexusDownloadTranslation(translatedMod.Name, translatedMod.Summary)
                    : null);
            _lastDownload = result;
            completedDownload = result;
            if (ViewModel is not null)
            {
                await ViewModel.RecordActivityAsync(
                    ActivityKind.Download,
                    ActivityOutcome.Success,
                    $"Nexus 下载完成：{result.FileName}",
                    $"Nexus Mod #{_activeNexusMod.GameScopedId} · {result.BytesWritten} 字节",
                    _activeNexusMod.PageUrl,
                    version.Version,
                    _downloadCancellation.Token);
            }
            ShowDownloadMessage(
                result.MetadataPath is null ? "下载完成，正在准备安装" : "下载完成，译文已绑定",
                result.MetadataPath is null
                    ? $"{result.FileName} 已保存，接下来会检查压缩包内容。"
                    : $"{result.FileName} 与译文已保存，安装后会应用翻译。",
                InfoBarSeverity.Success,
                showDownloadedFile: true,
                showInstall: true);
        }
        catch (OperationCanceledException)
        {
            ShowDownloadMessage("下载已取消", "未完成的临时文件已清理。", InfoBarSeverity.Informational);
        }
        catch (Exception error)
        {
            ShowDownloadMessage(
                "下载失败",
                error.Message,
                InfoBarSeverity.Error,
                showFilePage: true);
        }
        finally
        {
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            NexusFilesList.IsEnabled = true;
            CloseNexusDetailsButton.IsEnabled = true;
            _downloading = false;
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
        }
        if (completedDownload is not null)
        {
            await InstallFromArchiveAsync(completedDownload.Path, completedDownload.MetadataPath);
        }
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
    {
        CancelDownloadButton.IsEnabled = false;
        DownloadStatusText.Text = "正在取消并清理临时文件…";
        _downloadCancellation?.Cancel();
    }

    private void OnCloseNexusDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            return;
        }
        _detailsCancellation?.Cancel();
        NexusDetailsPane.Visibility = Visibility.Collapsed;
        _activeNexusMod = null;
        _activeRemoteMod = null;
    }

    private void OnOpenDownloadedFileClick(object sender, RoutedEventArgs e)
    {
        if (_lastDownload is null)
        {
            return;
        }
        try
        {
            _nexusDownloadService.OpenDownloadedFile(_lastDownload);
        }
        catch (Exception error)
        {
            ShowDownloadMessage("无法打开下载目录", error.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnInstallDownloadedFileClick(object sender, RoutedEventArgs e)
    {
        if (_lastDownload is not null)
        {
            await InstallFromArchiveAsync(_lastDownload.Path, _lastDownload.MetadataPath);
        }
    }

    private void OnOpenNexusFilePageClick(object sender, RoutedEventArgs e)
    {
        if (_activeNexusMod is not null)
        {
            ViewModel?.OpenUrl(_activeNexusMod.PageUrl);
        }
    }

    private void ShowDownloadMessage(
        string title,
        string message,
        InfoBarSeverity severity,
        bool showDownloadedFile = false,
        bool showFilePage = false,
        bool showInstall = false)
    {
        DownloadInfoBar.Title = title;
        DownloadInfoBar.Message = message;
        DownloadInfoBar.Severity = severity;
        InstallDownloadedFileButton.Visibility = showInstall && _lastDownload is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenDownloadedFileButton.Visibility = showDownloadedFile ? Visibility.Visible : Visibility.Collapsed;
        OpenNexusFilePageButton.Visibility = showFilePage && _activeNexusMod is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        DownloadInfoBar.IsOpen = true;
        CancelDownloadButton.IsEnabled = true;
    }

    private async Task InstallFromArchiveAsync(string archivePath, string? translationSidecarPath = null)
    {
        if (_installing)
        {
            return;
        }

        _installing = true;
        InstallDownloadedFileButton.IsEnabled = false;
        _installCancellation?.Cancel();
        _installCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _installCancellation = cancellation;
        try
        {
            var installed = await App.MainWindow.InstallModArchiveAsync(
                archivePath,
                translationSidecarPath,
                cancellation.Token);
            if (installed
                && _lastDownload is not null
                && Path.GetFullPath(_lastDownload.Path).Equals(
                    Path.GetFullPath(archivePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                ShowDownloadMessage(
                    "Mod 已安装",
                    "安装包已写入 Mods；更新时会保留用户配置和已有译文。",
                    InfoBarSeverity.Success,
                    showDownloadedFile: true);
            }
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_installCancellation, cancellation))
            {
                _installCancellation = null;
            }
            InstallDownloadedFileButton.IsEnabled = true;
            _installing = false;
        }
    }

    private static bool TryGetNexusModId(RemoteModItem mod, out string id)
    {
        const string prefix = "nexus:";
        if (mod.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            id = mod.Id[prefix.Length..];
            return id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit);
        }
        id = string.Empty;
        return false;
    }

    private static bool TryGetGitHubRepository(RemoteModItem mod, out string repository)
    {
        repository = string.Empty;
        if (!Uri.TryCreate(mod.PageUrl, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return false;
        }
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            return false;
        }
        var candidate = $"{Uri.UnescapeDataString(segments[0])}/{Uri.UnescapeDataString(segments[1])}";
        if (!ModUpdateService.TryParseUpdateKey(
                $"GitHub:{candidate}",
                out var parsed,
                out _)
            || parsed is not { Provider: ModUpdateProvider.GitHub })
        {
            return false;
        }
        repository = parsed.Identifier;
        return true;
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RemoteModItem mod
            || !_translatingModIds.Add(mod.Id))
        {
            return;
        }

        mod.IsTranslating = true;
        try
        {
            var translated = await _aiTranslationService.TranslateAsync(
                mod.OriginalName,
                mod.OriginalSummary);
            mod.ApplyTranslation(translated.Name, translated.Summary);
            QueueRefreshRemoteSummaryToggles();
            if (ReferenceEquals(_activeRemoteMod, mod))
            {
                NexusDetailsTitle.Text = translated.Name;
            }
            App.MainWindow.ShowMessage(
                "名称与简介已翻译",
                translated.Name,
                InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("Mod 翻译失败", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _translatingModIds.Remove(mod.Id);
            mod.IsTranslating = false;
        }
    }

    private void OnToggleDescription(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }
        var panel = button.Parent switch
        {
            StackPanel directParent => directParent,
            Grid { Parent: StackPanel descriptionParent } => descriptionParent,
            _ => null,
        };
        if (panel is null)
        {
            return;
        }
        var description = panel.Children.OfType<TextBlock>().FirstOrDefault();
        if (description is null)
        {
            return;
        }
        var expanding = description.MaxLines == 2;
        description.MaxLines = expanding ? 0 : 2;
        description.TextTrimming = expanding ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        button.Content = expanding ? "收起" : "展开";
    }

    private void OnRemoteSummaryLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock description)
        {
            QueueUpdateSummaryToggle(description);
        }
    }

    private void OnRemoteSummarySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is TextBlock description)
        {
            QueueUpdateSummaryToggle(description);
        }
    }

    private void OnSummaryToggleLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            QueueUpdateSummaryToggle(button);
        }
    }

    private void QueueRefreshRemoteSummaryToggles()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var description in FindVisualDescendants<TextBlock>(ResultsList))
            {
                if (description.DataContext is RemoteModItem mod
                    && description.Text == mod.Summary
                    && description.TextWrapping == TextWrapping.WrapWholeWords)
                {
                    UpdateSummaryToggle(description);
                }
            }
        });
    }

    private static void QueueUpdateSummaryToggle(FrameworkElement element)
    {
        element.DispatcherQueue.TryEnqueue(() => UpdateSummaryToggle(element));
    }

    private static void UpdateSummaryToggle(FrameworkElement element)
    {
        var (description, button) = FindSummaryControls(element);
        if (description is null || button is null)
        {
            return;
        }

        var needsToggle = DescriptionExceedsCollapsedHeight(description);
        if (!needsToggle)
        {
            description.MaxLines = 2;
            description.TextTrimming = TextTrimming.CharacterEllipsis;
            button.Content = "展开";
            button.Visibility = Visibility.Collapsed;
            return;
        }

        button.Visibility = Visibility.Visible;
        button.Content = description.MaxLines == 0 ? "收起" : "展开";
    }

    private static (TextBlock? Description, Button? Button) FindSummaryControls(FrameworkElement element)
    {
        var panel = element switch
        {
            TextBlock { Parent: StackPanel descriptionPanel } => descriptionPanel,
            Button { Parent: Grid { Parent: StackPanel descriptionPanel } } => descriptionPanel,
            _ => null,
        };
        if (panel is null)
        {
            return (null, null);
        }

        var description = panel.Children.OfType<TextBlock>().FirstOrDefault();
        var actions = panel.Children.OfType<Grid>().FirstOrDefault();
        var button = actions?.Children
            .OfType<Button>()
            .FirstOrDefault(candidate => Grid.GetColumn(candidate) == 0);
        return (description, button);
    }

    private static bool DescriptionExceedsCollapsedHeight(TextBlock description)
    {
        if (string.IsNullOrWhiteSpace(description.Text) || description.ActualWidth <= 1)
        {
            return false;
        }

        var full = CreateMeasurementTextBlock(description);
        full.MaxLines = 0;
        full.Measure(new Size(description.ActualWidth, double.PositiveInfinity));

        var collapsed = CreateMeasurementTextBlock(description);
        collapsed.MaxLines = 2;
        collapsed.TextTrimming = TextTrimming.CharacterEllipsis;
        collapsed.Measure(new Size(description.ActualWidth, double.PositiveInfinity));
        return full.DesiredSize.Height > collapsed.DesiredSize.Height + 0.5;
    }

    private static TextBlock CreateMeasurementTextBlock(TextBlock source)
    {
        return new TextBlock
        {
            Text = source.Text,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontStretch = source.FontStretch,
            FontStyle = source.FontStyle,
            FontWeight = source.FontWeight,
            TextWrapping = source.TextWrapping,
            TextTrimming = TextTrimming.None,
        };
    }

    private static bool TryCreateRemoteImageUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || !candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !candidate.IsDefaultPort
            || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }
        uri = candidate;
        return true;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
            }
            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
