using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ValleySteward.WinUI.Models;
using ValleySteward.WinUI.Pages;
using ValleySteward.WinUI.Services;
using ValleySteward.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace ValleySteward.WinUI;

public sealed partial class MainWindow : Window
{
    private const string StarterNexusModId = "41846";
    private const string StarterNexusModPageUrl = "https://www.nexusmods.com/stardewvalley/mods/41846";

    private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(255, 16, 124, 16));
    private static readonly SolidColorBrush NeutralBrush = new(Color.FromArgb(255, 128, 128, 128));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromArgb(255, 157, 93, 0));

    private readonly DispatcherTimer _runtimeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly NexusDownloadService _starterNexusDownloadService = new();
    private AppWindow? _appWindow;
    private bool _initialized;
    private bool _modInstallUiBusy;
    private string? _displayedSteamAvatarPath;

    public ShellViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        App.WriteStartupLog("MainWindow.ctor before InitializeComponent");
        InitializeComponent();
        App.WriteStartupLog("MainWindow.ctor after InitializeComponent");
        Title = "Valley Steward";
        RootGrid.DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
        _runtimeTimer.Tick += OnRuntimeTimerTick;
        RootGrid.ActualThemeChanged += OnActualThemeChanged;
        RootGrid.Unloaded += OnRootUnloaded;

        App.WriteStartupLog("MainWindow.ctor before ConfigureWindow");
        ConfigureWindow();
        App.WriteStartupLog("MainWindow.ctor before initial NavigateTo");
        NavigateTo("overview");
        RootNavigation.SelectedItem = OverviewNavigationItem;
        App.WriteStartupLog("MainWindow.ctor completed");
    }

    public void ShowMessage(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            GlobalInfoBar.Title = title;
            GlobalInfoBar.Message = message;
            GlobalInfoBar.Severity = severity;
            GlobalInfoBar.IsOpen = true;
        });
    }

    public async Task<bool> ChooseGamePathAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return false;
        }

        await RunActionAsync(
            () => ViewModel.SetGamePathAsync(folder.Path),
            "游戏路径已获取",
            "已读取并保存游戏目录。");
        return ViewModel.Installation is not null;
    }

    public async Task<bool> ChooseAndInstallModArchiveAsync()
    {
        var archivePath = await ChooseModArchiveAsync();
        return archivePath is not null && await InstallModArchiveAsync(archivePath);
    }

    public async Task<bool> InstallDroppedModArchiveAsync(DataPackageView dataView)
    {
        if (!dataView.Contains(StandardDataFormats.StorageItems))
        {
            return false;
        }

        var files = (await dataView.GetStorageItemsAsync())
            .OfType<StorageFile>()
            .ToArray();
        if (files.Length != 1 || !IsZipStorageFile(files[0]))
        {
            ShowMessage(
                "无法安装拖入内容",
                "请一次拖入一个 .zip 格式的 SMAPI Mod 安装包。",
                InfoBarSeverity.Warning);
            return false;
        }

        return await InstallModArchiveAsync(files[0].Path);
    }

    public async Task<bool> InstallModArchiveAsync(
        string archivePath,
        string? translationSidecarPath = null,
        CancellationToken cancellationToken = default,
        bool allowSafeAutoInstall = false)
    {
        if (_modInstallUiBusy)
        {
            ShowMessage("已有安装正在进行", "请先完成当前 Mod 安装确认。", InfoBarSeverity.Informational);
            return false;
        }
        if (ViewModel.Installation is null)
        {
            ShowMessage("请先设置游戏路径", "安装 Mod 前需要确认 Stardew Valley 的游戏目录。", InfoBarSeverity.Warning);
            return false;
        }
        if (ViewModel.IsGameRunning)
        {
            ShowMessage("游戏正在运行", "请先关闭游戏，再安装或更新 Mod。", InfoBarSeverity.Warning);
            return false;
        }

        _modInstallUiBusy = true;
        try
        {
            ShowMessage("正在检查 Mod 压缩包", Path.GetFileName(archivePath));
            var plan = await ViewModel.InspectModArchiveAsync(archivePath, cancellationToken);
            var xnbConsent = new CheckBox
            {
                Content = "我了解 XNB 内容替换风险，并确认继续安装",
                Visibility = plan.RequiresXnbConfirmation ? Visibility.Visible : Visibility.Collapsed,
            };
            ComboBox? translationTarget = null;
            if (!string.IsNullOrWhiteSpace(translationSidecarPath) && plan.Mods.Count > 1)
            {
                translationTarget = new ComboBox
                {
                    Header = "将下载页译文应用到",
                    PlaceholderText = "请选择对应的 Mod",
                    DisplayMemberPath = nameof(ModArchiveMod.Name),
                    ItemsSource = plan.Mods,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
            }
            var canInstallWithoutPrompt = allowSafeAutoInstall
                && plan.CanInstall
                && !plan.RequiresXnbConfirmation
                && plan.Conflicts.Count == 0
                && plan.Warnings.Count == 0
                && translationTarget is null;
            if (!canInstallWithoutPrompt)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = plan.Mods.Count == 1
                        ? $"安装 {plan.Mods[0].Name}"
                        : $"安装 {plan.Mods.Count} 个 Mod",
                    Content = BuildModInstallPreview(plan, xnbConsent, translationTarget),
                    PrimaryButtonText = "安装",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                };
                void UpdateInstallAvailability()
                {
                    dialog.IsPrimaryButtonEnabled = plan.CanInstall
                        && (!plan.RequiresXnbConfirmation || xnbConsent.IsChecked == true)
                        && (translationTarget is null || translationTarget.SelectedItem is not null);
                }
                void OnConsentChanged(object sender, RoutedEventArgs e) => UpdateInstallAvailability();
                void OnTranslationTargetChanged(object sender, SelectionChangedEventArgs e) => UpdateInstallAvailability();
                xnbConsent.Checked += OnConsentChanged;
                xnbConsent.Unchecked += OnConsentChanged;
                if (translationTarget is not null)
                {
                    translationTarget.SelectionChanged += OnTranslationTargetChanged;
                }
                UpdateInstallAvailability();
                var choice = await dialog.ShowAsync();
                xnbConsent.Checked -= OnConsentChanged;
                xnbConsent.Unchecked -= OnConsentChanged;
                if (translationTarget is not null)
                {
                    translationTarget.SelectionChanged -= OnTranslationTargetChanged;
                }
                if (choice != ContentDialogResult.Primary)
                {
                    return false;
                }
            }

            ShowMessage("正在安装 Mod", Path.GetFileName(archivePath));
            var result = await ViewModel.InstallModArchiveAsync(
                plan,
                new ModInstallOptions
                {
                    AllowXnbFiles = xnbConsent.IsChecked == true,
                    TranslationSidecarPath = translationSidecarPath,
                    TranslationTargetUniqueId = (translationTarget?.SelectedItem as ModArchiveMod)?.UniqueId
                        ?? (plan.Mods.Count == 1 ? plan.Mods[0].UniqueId : null),
                },
                cancellationToken);
            var summary = result.Replaced > 0
                ? $"新增 {result.Installed} 个，更新 {result.Replaced} 个；旧版本已备份。"
                : $"已安装 {result.Installed} 个 Mod。";
            ShowMessage(canInstallWithoutPrompt ? "默认 Mod 安装完成" : "Mod 安装完成", summary, InfoBarSeverity.Success);
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Mod 安装已取消", "没有继续写入 Mod 目录。", InfoBarSeverity.Informational);
            return false;
        }
        catch (Exception error)
        {
            ShowMessage("无法安装 Mod", error.Message, InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            _modInstallUiBusy = false;
        }
    }

    private static FrameworkElement BuildModInstallPreview(
        ModInstallPlan plan,
        CheckBox xnbConsent,
        ComboBox? translationTarget)
    {
        var content = new StackPanel
        {
            Width = 560,
            MaxHeight = 520,
            Spacing = 12,
        };
        content.Children.Add(new TextBlock
        {
            Text = $"{Path.GetFileName(plan.ArchivePath)}  ·  {FormatBytes(plan.ArchiveBytes)}  ·  解压后 {FormatBytes(plan.UncompressedBytes)}",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var mod in plan.Mods.Take(12))
        {
            var requiredDependencies = mod.Dependencies
                .Where(dependency => dependency.IsRequired)
                .Select(dependency => string.IsNullOrWhiteSpace(dependency.MinimumVersion)
                    ? dependency.UniqueId
                    : $"{dependency.UniqueId} >= {dependency.MinimumVersion}")
                .ToArray();
            var details = new StackPanel { Spacing = 3 };
            details.Children.Add(new TextBlock
            {
                Text = $"{mod.Name}  {mod.Version}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            details.Children.Add(new TextBlock
            {
                Text = $"{mod.Author}  ·  {mod.UniqueId}"
                    + (mod.ExistingVersion is null ? string.Empty : $"  ·  将覆盖 {mod.ExistingVersion}"),
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
            });
            if (requiredDependencies.Length > 0)
            {
                details.Children.Add(new TextBlock
                {
                    Text = $"必需依赖：{string.Join("、", requiredDependencies)}",
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            content.Children.Add(details);
        }
        if (plan.Mods.Count > 12)
        {
            content.Children.Add(new TextBlock { Text = $"另有 {plan.Mods.Count - 12} 个 Mod。" });
        }

        var blocking = plan.Conflicts.Where(conflict => conflict.Blocking).Select(conflict => conflict.Message).ToArray();
        var notices = plan.Conflicts.Where(conflict => !conflict.Blocking).Select(conflict => conflict.Message)
            .Concat(plan.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (blocking.Length > 0)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Title = "当前无法安全安装",
                Message = string.Join(Environment.NewLine, blocking),
            });
        }
        if (notices.Length > 0)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = "安装提示",
                Message = string.Join(Environment.NewLine, notices),
            });
        }
        if (plan.RequiresXnbConfirmation)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"检测到 {plan.XnbFiles.Count} 个 XNB 文件。此类文件可能直接替换游戏内容，请确认压缩包来源可信。",
                TextWrapping = TextWrapping.Wrap,
            });
        }
        content.Children.Add(xnbConsent);
        if (translationTarget is not null)
        {
            content.Children.Add(translationTarget);
        }
        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "松开以检查并安装 Mod ZIP";
        e.DragUIOverride.IsContentVisible = true;
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            await InstallDroppedModArchiveAsync(e.DataView);
        }
        catch (Exception error)
        {
            ShowMessage("无法读取拖入的文件", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool IsZipStorageFile(StorageFile file)
    {
        return file.FileType.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(file.Path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, bytes);
        var display = (double)value;
        var unit = 0;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.#} {units[unit]}";
    }

    public async Task<string?> ChooseModArchiveAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".zip");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private void ConfigureWindow()
    {
        App.WriteStartupLog("ConfigureWindow before GetWindowHandle");
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        App.WriteStartupLog($"ConfigureWindow handle=0x{windowHandle.ToInt64():X}");
        _appWindow.Resize(new SizeInt32(1280, 820));

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Mica is optional on unsupported Windows builds.
        }

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            App.WriteStartupLog("ConfigureWindow titlebar customization unsupported");
            ExtendsContentIntoTitleBar = false;
            TitleBarRow.Height = new GridLength(0);
            return;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        var titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        RootGrid.SizeChanged += (_, _) => UpdateTitleBarInsets();
        UpdateTitleBarInsets();
        App.WriteStartupLog("ConfigureWindow completed");
    }

    private void UpdateTitleBarInsets()
    {
        if (_appWindow is null)
        {
            return;
        }
        LeftInsetColumn.Width = new GridLength(_appWindow.TitleBar.LeftInset);
        RightInsetColumn.Width = new GridLength(_appWindow.TitleBar.RightInset);
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        App.WriteStartupLog("OnRootLoaded before ViewModel.InitializeAsync");
        await ViewModel.InitializeAsync();
        App.WriteStartupLog("OnRootLoaded after ViewModel.InitializeAsync");
        ApplyAppearance();
        UpdateShellState();
        _runtimeTimer.Start();
        App.WriteStartupLog("OnRootLoaded runtime started");

        if (!string.IsNullOrWhiteSpace(ViewModel.LastError))
        {
            ShowMessage("读取本机状态失败", ViewModel.LastError, InfoBarSeverity.Error);
        }
        if (!ViewModel.FirstRunCompleted || ViewModel.Installation is null)
        {
            App.WriteStartupLog("OnRootLoaded before first-run dialog");
            await ShowFirstRunDialogAsync();
            App.WriteStartupLog("OnRootLoaded after first-run dialog");
        }
        App.WriteStartupLog("OnRootLoaded completed");
    }

    private async Task ShowFirstRunDialogAsync()
    {
        var hasGamePath = ViewModel.Installation is not null;
        var starterModCheckBox = new CheckBox
        {
            Content = $"默认安装 Nexus #{StarterNexusModId} 推荐 Mod",
            IsChecked = ViewModel.AutoInstallStarterMod,
        };
        var content = new StackPanel
        {
            Width = 460,
            Spacing = 12,
        };
        content.Children.Add(new TextBlock
        {
            Text = hasGamePath
                ? $"游戏路径已获取：{ViewModel.Installation!.Path}\n可以直接完成首次设置，也可以修改目录。"
                : "需要先确认《星露谷物语》的安装目录。可以从注册表与 Steam 库自动寻找，也可以手动选择。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(starterModCheckBox);
        content.Children.Add(new TextBlock
        {
            Text = $"来源：{StarterNexusModPageUrl}",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "首次设置",
            Content = content,
            PrimaryButtonText = hasGamePath ? "完成设置" : "自动寻找",
            SecondaryButtonText = hasGamePath ? "修改目录" : "选择目录",
            CloseButtonText = "稍后设置",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        var pathConfirmed = false;
        if (result == ContentDialogResult.Primary)
        {
            if (hasGamePath)
            {
                pathConfirmed = true;
            }
            else
            {
                await RunActionAsync(
                    ViewModel.AutoDetectGameAsync,
                    "游戏路径已获取",
                    "已从本机安装信息中识别游戏目录。");
                pathConfirmed = ViewModel.Installation is not null;
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            pathConfirmed = await ChooseGamePathAsync();
        }
        else
        {
            ViewModel.AutoInstallStarterMod = starterModCheckBox.IsChecked == true;
        }

        if (!pathConfirmed || ViewModel.Installation is null)
        {
            return;
        }

        var autoInstallStarterMod = starterModCheckBox.IsChecked == true;
        await ViewModel.CompleteFirstRunAsync(autoInstallStarterMod);
        if (autoInstallStarterMod)
        {
            await InstallStarterNexusModAsync();
        }
    }

    private async Task InstallStarterNexusModAsync()
    {
        if (ViewModel.Installation is null)
        {
            return;
        }
        if (IsStarterNexusModInstalled())
        {
            ShowMessage(
                "默认 Mod 已存在",
                $"已检测到 Nexus:{StarterNexusModId}，不会重复安装。",
                InfoBarSeverity.Success);
            return;
        }
        if (ViewModel.IsGameRunning)
        {
            ShowMessage(
                "游戏正在运行",
                $"已跳过默认安装 Nexus #{StarterNexusModId}，请关闭游戏后在下载中心手动安装。",
                InfoBarSeverity.Warning);
            return;
        }
        if (!_starterNexusDownloadService.HasApiKey())
        {
            ShowMessage(
                "需要 Nexus API Key",
                $"默认安装 Nexus #{StarterNexusModId} 需要先在设置中保存 Personal API Key。",
                InfoBarSeverity.Warning);
            SelectNavigationItem("settings");
            return;
        }

        try
        {
            ShowMessage("正在准备默认 Mod", $"读取 Nexus #{StarterNexusModId} 文件信息。");
            var details = await _starterNexusDownloadService.GetModDetailsAsync(StarterNexusModId);
            var primaryVersions = details.Files
                .Where(file => file.IsActive)
                .SelectMany(file => file.Versions)
                .Where(version => version.IsPrimary)
                .DistinctBy(version => version.GameScopedId, StringComparer.Ordinal)
                .ToArray();
            if (primaryVersions.Length != 1)
            {
                ShowMessage(
                    "需要手动选择默认 Mod 文件",
                    primaryVersions.Length == 0
                        ? $"Nexus #{StarterNexusModId} 没有返回唯一主文件，请在下载中心或官网文件页确认。"
                        : $"Nexus #{StarterNexusModId} 返回 {primaryVersions.Length} 个主文件候选，请手动确认。",
                    InfoBarSeverity.Warning);
                SelectNavigationItem("downloads");
                return;
            }

            var version = primaryVersions[0];
            var progress = new Progress<NexusDownloadProgress>(value =>
            {
                ShowMessage("正在下载默认 Mod", $"{details.Name} · {value.StatusText}");
            });
            var download = await _starterNexusDownloadService.DownloadAsync(
                details.GameScopedId,
                version.GameScopedId,
                progress);
            await ViewModel.RecordActivityAsync(
                ActivityKind.Download,
                ActivityOutcome.Success,
                $"默认 Mod 下载完成：{download.FileName}",
                $"{details.Name} · Nexus #{StarterNexusModId} · {download.BytesWritten} 字节",
                details.PageUrl,
                version.Version);
            await InstallModArchiveAsync(
                download.Path,
                download.MetadataPath,
                allowSafeAutoInstall: true);
        }
        catch (NexusApiKeyMissingException)
        {
            ShowMessage(
                "需要 Nexus API Key",
                $"默认安装 Nexus #{StarterNexusModId} 需要先在设置中保存 Personal API Key。",
                InfoBarSeverity.Warning);
            SelectNavigationItem("settings");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowMessage(
                "默认 Mod 自动安装失败",
                $"{error.Message} 可在下载中心手动重试：{StarterNexusModPageUrl}",
                InfoBarSeverity.Error);
            await ViewModel.RecordActivityAsync(
                ActivityKind.Download,
                ActivityOutcome.Failed,
                $"默认 Mod 自动安装失败：Nexus #{StarterNexusModId}",
                error.Message,
                StarterNexusModPageUrl);
        }
    }

    private bool IsStarterNexusModInstalled()
    {
        var updateKey = $"Nexus:{StarterNexusModId}";
        return ViewModel.Mods.Any(mod => mod.UpdateKeys.Any(
            key => string.Equals(key, updateKey, StringComparison.OrdinalIgnoreCase)));
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        var pageType = tag switch
        {
            "mods" => typeof(ModsPage),
            "downloads" => typeof(DownloadsPage),
            "share" => typeof(ShareHallPage),
            "smapi" => typeof(SmapiPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(OverviewPage),
        };
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, ViewModel);
        }
    }

    private void SelectNavigationItem(string tag)
    {
        var items = RootNavigation.MenuItems.Concat(RootNavigation.FooterMenuItems);
        RootNavigation.SelectedItem = items
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
        NavigateTo(tag);
    }

    private void OnPathClick(object sender, RoutedEventArgs e) => SelectNavigationItem("settings");
    private void OnSmapiClick(object sender, RoutedEventArgs e) => SelectNavigationItem("smapi");

    private async void OnLaunchPreferredClick(SplitButton sender, SplitButtonClickEventArgs args)
    {
        await RunActionAsync(ViewModel.LaunchPreferredAsync, "游戏已启动", ViewModel.LaunchButtonText);
    }

    private async void OnLaunchSmapiClick(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(() => ViewModel.LaunchAsync(LaunchTarget.Smapi), "SMAPI 已启动", "正在监视游戏进程。");
    }

    private async void OnLaunchVanillaClick(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(() => ViewModel.LaunchAsync(LaunchTarget.Vanilla), "原版游戏已启动", "正在监视游戏进程。");
    }

    private async void OnLaunchProfileClick(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(
            () => ViewModel.LaunchAsync(LaunchTarget.Smapi, "Mods (multiplayer)"),
            "多人 Mod 配置已启动",
            "正在监视游戏进程。");
    }

    private void OnRememberLaunchClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RememberLaunch = RememberLaunchItem.IsChecked;
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(ViewModel.StopGameAsync, "游戏已关闭", "游戏进程树已结束。");
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(ViewModel.RestartGameAsync, "游戏已重新启动", "沿用上次启动方式与参数。");
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Theme = ViewModel.Theme switch
        {
            "system" => "light",
            "light" => "dark",
            _ => "system",
        };
        ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        RootGrid.RequestedTheme = ViewModel.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        App.ApplyAccentPreset(ViewModel.AccentPreset, RootGrid.ActualTheme);
        ThemeIcon.Glyph = ViewModel.Theme switch
        {
            "light" => "\uE706",
            "dark" => "\uE708",
            _ => "\uE771",
        };
        ToolTipService.SetToolTip(ThemeButton, $"主题：{(ViewModel.Theme == "system" ? "跟随系统" : ViewModel.Theme == "light" ? "浅色" : "深色")}");
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        App.ApplyAccentPreset(ViewModel.AccentPreset, RootGrid.ActualTheme);
    }

    private async void OnRuntimeTimerTick(object? sender, object e)
    {
        try
        {
            await ViewModel.RefreshRuntimeAsync();
        }
        catch (Exception error)
        {
            ShowMessage("状态监视失败", error.Message, InfoBarSeverity.Warning);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (e.PropertyName is nameof(ShellViewModel.Theme) or nameof(ShellViewModel.AccentPreset))
            {
                ApplyAppearance();
            }
            UpdateShellState();
        });
    }

    private void UpdateShellState()
    {
        PathLabel.Text = ViewModel.GamePathShort;
        ToolTipService.SetToolTip(PathButton, ViewModel.GamePathText);
        SteamLabel.Text = ViewModel.SteamStatusText;
        SmapiLabel.Text = ViewModel.Smapi.Installed ? $"SMAPI {ViewModel.Smapi.Version ?? "已安装"}" : "SMAPI 未安装";
        ProcessStatusLabel.Text = ViewModel.ProcessStatusText;
        LaunchButtonLabel.Text = ViewModel.LaunchButtonText;
        RememberLaunchItem.IsChecked = ViewModel.RememberLaunch;
        BusyProgressBar.Visibility = ViewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;

        SteamStatusDot.Fill = ViewModel.Steam.Running ? SuccessBrush : NeutralBrush;
        SteamFlyoutDot.Fill = SteamStatusDot.Fill;
        ProcessStatusDot.Fill = ViewModel.ProcessStatus.Running ? SuccessBrush : NeutralBrush;
        if (ViewModel.Smapi.Installed)
        {
            SmapiButton.ClearValue(Control.ForegroundProperty);
        }
        else
        {
            SmapiButton.Foreground = WarningBrush;
        }

        LaunchButton.Visibility = ViewModel.IsGameRunning ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = ViewModel.IsGameRunning ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.Visibility = ViewModel.IsGameRunning ? Visibility.Visible : Visibility.Collapsed;
        LaunchButton.IsEnabled = !ViewModel.IsBusy && ViewModel.Installation is not null;
        StopButton.IsEnabled = !ViewModel.IsBusy;
        RestartButton.IsEnabled = !ViewModel.IsBusy;

        var identity = ViewModel.Steam.Identity;
        SteamFlyoutTitle.Text = ViewModel.SteamDisplayName;
        SteamAccountText.Text = identity?.AccountName ?? "未读取";
        SteamIdText.Text = identity?.SteamId64 ?? "未读取";
        FriendCodeText.Text = identity?.FriendCode ?? "未读取";
        SteamSourceText.Text = identity?.Source ?? "未读取";
        UpdateSteamAvatar(identity?.AvatarPath);
    }

    private void UpdateSteamAvatar(string? avatarPath)
    {
        if (string.Equals(_displayedSteamAvatarPath, avatarPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _displayedSteamAvatarPath = avatarPath;
        SteamAvatarImage.Source = null;
        if (string.IsNullOrWhiteSpace(avatarPath))
        {
            return;
        }

        try
        {
            var uri = new Uri(avatarPath, UriKind.Absolute);
            if (uri.IsFile)
            {
                SteamAvatarImage.Source = new BitmapImage(uri);
            }
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException)
        {
            // The default Fluent user glyph remains visible behind the image.
        }
    }

    private void OnSteamAvatarImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        SteamAvatarImage.Source = null;
    }

    private void OnCopySteamId(object sender, RoutedEventArgs e) => CopyText(ViewModel.Steam.Identity?.SteamId64, "SteamID64 已复制");
    private void OnCopyFriendCode(object sender, RoutedEventArgs e) => CopyText(ViewModel.Steam.Identity?.FriendCode, "好友码已复制");

    private void CopyText(string? value, string title)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
        ShowMessage(title, value, InfoBarSeverity.Success);
    }

    private async Task RunActionAsync(Func<Task> action, string successTitle, string successMessage)
    {
        try
        {
            await action();
            UpdateShellState();
            ShowMessage(successTitle, successMessage, InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            ShowMessage("操作未完成", error.Message, InfoBarSeverity.Error);
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        App.WriteStartupLog("MainWindow.Closed");
        _runtimeTimer.Stop();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        RootGrid.ActualThemeChanged -= OnActualThemeChanged;
        RootGrid.Unloaded -= OnRootUnloaded;
        Activated -= OnWindowActivated;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        App.WriteStartupLog($"MainWindow.Activated state={args.WindowActivationState}");
    }

    private void OnRootUnloaded(object sender, RoutedEventArgs e)
    {
        App.WriteStartupLog("RootGrid.Unloaded");
    }
}
