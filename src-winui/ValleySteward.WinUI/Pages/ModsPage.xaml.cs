using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ValleySteward.WinUI.Models;
using ValleySteward.WinUI.Services;
using ValleySteward.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace ValleySteward.WinUI.Pages;

public sealed partial class ModsPage : Page
{
    private bool _suppressToggle;
    private readonly ModService _modService = new();
    private readonly ModUpdateBatchService _modUpdateBatchService = new();
    private readonly AiTranslationService _aiTranslationService = new(new CredentialService());
    private readonly HashSet<string> _translatingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _activeTranslationRequests = [];
    private ContentDialog? _trashDialog;
    private ListView? _trashList;
    private TextBlock? _trashEmptyText;
    private bool _trashMutationInProgress;
    private int _trashItemCount;
    private CancellationTokenSource? _updateChecksCancellation;
    private CancellationTokenSource? _updateInstallCancellation;
    private bool _checkingAllUpdates;
    private bool _updateOperationInProgress;
    private bool _startingBatchUpdate;
    private IReadOnlyList<ModUpdateBatchSelectionItem> _checkedBatchSelections =
        Array.Empty<ModUpdateBatchSelectionItem>();
    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    public ModsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DataContext = e.Parameter;
        if (ViewModel is not null)
        {
            ViewModel.Mods.CollectionChanged += OnModsChanged;
        }
        _aiTranslationService.RequestActivity += OnAiRequestActivity;
        ApplyFilter();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.Mods.CollectionChanged -= OnModsChanged;
        }
        _aiTranslationService.RequestActivity -= OnAiRequestActivity;
        _updateChecksCancellation?.Cancel();
        _updateInstallCancellation?.Cancel();
        base.OnNavigatedFrom(e);
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
                    TranslationInfoBar.Message = $"HTTP {activity.StatusCode} · {activity.ElapsedMilliseconds} ms";
                    TranslationInfoBar.Severity = InfoBarSeverity.Success;
                    break;
                case AiTranslationRequestStage.Completed:
                    _activeTranslationRequests.Remove(activity.RequestId);
                    TranslationInfoBar.Title = "翻译已替换并保存";
                    TranslationInfoBar.Message = $"{activity.Request.Endpoint} · 剩余 {_activeTranslationRequests.Count} 个请求";
                    TranslationInfoBar.Severity = InfoBarSeverity.Success;
                    break;
                case AiTranslationRequestStage.TimedOut:
                case AiTranslationRequestStage.Failed:
                case AiTranslationRequestStage.Canceled:
                    _activeTranslationRequests.Remove(activity.RequestId);
                    TranslationInfoBar.Title = activity.Stage switch
                    {
                        AiTranslationRequestStage.TimedOut => "上游模型请求超时",
                        AiTranslationRequestStage.Canceled => "翻译请求已取消",
                        _ => "上游模型请求失败",
                    };
                    TranslationInfoBar.Message = $"{activity.Request.Endpoint} · {activity.Detail}";
                    TranslationInfoBar.Severity = activity.Stage == AiTranslationRequestStage.Canceled
                        ? InfoBarSeverity.Informational
                        : InfoBarSeverity.Error;
                    break;
            }
            TranslationInfoBar.IsOpen = true;
        });
    }

    private void OnModsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyFilter();
    private void OnFilterChanged(object sender, object e) => ApplyFilter();

    private async void OnInstallArchiveClick(object sender, RoutedEventArgs e)
    {
        if (_updateOperationInProgress)
        {
            App.MainWindow.ShowMessage(
                "更新队列正在运行",
                "请等待队列完成后再安装其他 ZIP。",
                InfoBarSeverity.Informational);
            return;
        }
        if (await App.MainWindow.ChooseAndInstallModArchiveAsync())
        {
            InvalidateBatchUpdateCandidates();
        }
    }

    private void OnArchiveDragOver(object sender, DragEventArgs e)
    {
        if (_updateOperationInProgress || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "安装 Mod ZIP";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void OnArchiveDrop(object sender, DragEventArgs e)
    {
        if (_updateOperationInProgress || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            if (await App.MainWindow.InstallDroppedModArchiveAsync(e.DataView))
            {
                InvalidateBatchUpdateCandidates();
            }
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

    private void ApplyFilter()
    {
        if (ModsList is null || ViewModel is null)
        {
            return;
        }

        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        var filter = (FilterComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        ModsList.ItemsSource = ViewModel.Mods.Where(mod =>
            (string.IsNullOrWhiteSpace(query)
                || mod.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || mod.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || mod.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            && (filter switch
            {
                "enabled" => mod.Enabled,
                "disabled" => !mod.Enabled,
                "problem" => mod.Health != "healthy",
                _ => true,
            })).ToArray();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }
        if (_updateOperationInProgress)
        {
            App.MainWindow.ShowMessage(
                "更新队列正在运行",
                "请等待队列完成后再重新扫描。",
                InfoBarSeverity.Informational);
            return;
        }

        try
        {
            InvalidateBatchUpdateCandidates();
            await ViewModel.RefreshAllAsync();
            App.MainWindow.ShowMessage("扫描完成", $"已读取 {ViewModel.Mods.Count} 个 Mod。", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("扫描失败", error.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnCheckAllUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_checkingAllUpdates || ViewModel is null)
        {
            return;
        }
        if (_updateOperationInProgress)
        {
            App.MainWindow.ShowMessage(
                "更新队列正在运行",
                "请等待当前队列完成后再检查更新。",
                InfoBarSeverity.Informational);
            return;
        }

        _checkingAllUpdates = true;
        InvalidateBatchUpdateCandidates();
        CheckAllUpdatesButton.IsEnabled = false;
        _updateChecksCancellation?.Cancel();
        _updateChecksCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _updateChecksCancellation = cancellation;
        var mods = ViewModel.Mods.ToArray();
        foreach (var mod in mods)
        {
            mod.IsCheckingUpdate = mod.Health == "healthy" && mod.UpdateKeys.Count > 0;
        }

        try
        {
            TranslationInfoBar.Title = "正在检查 Mod 更新";
            TranslationInfoBar.Message = $"正在并行读取 {mods.Sum(mod => mod.UpdateKeys.Count)} 个 Nexus / GitHub 更新源。";
            TranslationInfoBar.Severity = InfoBarSeverity.Informational;
            TranslationInfoBar.IsOpen = true;
            var results = await ViewModel.CheckModUpdatesAsync(cancellation.Token);
            for (var index = 0; index < Math.Min(mods.Length, results.Count); index++)
            {
                mods[index].UpdateResult = results[index];
            }

            var available = results.Count(result => result.UpdateAvailable);
            var batchSelections = _modUpdateBatchService.CreateSelections(mods);
            var automatic = batchSelections.Count(item => item.CanAutoUpdate);
            SetBatchUpdateSelections(batchSelections);
            TranslationInfoBar.Title = "更新检查完成";
            TranslationInfoBar.Message = available == 0
                ? "当前没有发现可用更新。"
                : $"发现 {available} 个更新，其中 {automatic} 个可直接安装。";
            TranslationInfoBar.Severity = available == 0
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Informational;
            TranslationInfoBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            // Leaving the page or starting another check cancels quietly.
        }
        catch (Exception error)
        {
            InvalidateBatchUpdateCandidates();
            App.MainWindow.ShowMessage("检查 Mod 更新失败", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            foreach (var mod in mods)
            {
                mod.IsCheckingUpdate = false;
            }
            cancellation.Dispose();
            if (ReferenceEquals(_updateChecksCancellation, cancellation))
            {
                _updateChecksCancellation = null;
            }
            CheckAllUpdatesButton.IsEnabled = !_updateOperationInProgress;
            _checkingAllUpdates = false;
        }
    }

    private async void OnUpdateAllClick(object sender, RoutedEventArgs e)
    {
        if (_updateOperationInProgress
            || _startingBatchUpdate
            || _checkingAllUpdates
            || ViewModel is null)
        {
            return;
        }
        var selections = _checkedBatchSelections.ToArray();
        if (selections.Length == 0)
        {
            App.MainWindow.ShowMessage(
                "没有发现可用更新",
                "请先完成一次批量检查。",
                InfoBarSeverity.Informational);
            return;
        }

        _startingBatchUpdate = true;
        UpdateAllButton.IsEnabled = false;
        var rows = new List<(ModUpdateBatchSelectionItem Item, CheckBox CheckBox)>();
        var itemsPanel = new StackPanel { Spacing = 0 };
        foreach (var item in selections)
        {
            var checkBox = new CheckBox
            {
                IsChecked = item.CanAutoUpdate,
                IsEnabled = item.CanAutoUpdate,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0),
            };
            AutomationProperties.SetName(
                checkBox,
                item.CanAutoUpdate ? $"选择更新 {item.ModName}" : $"无法自动更新 {item.ModName}");
            var details = new StackPanel { Spacing = 3 };
            details.Children.Add(new TextBlock
            {
                Text = item.ModName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            details.Children.Add(new TextBlock
            {
                Text = $"{item.InstalledVersion} → {item.ExpectedVersion ?? "未知"} · {GetUpdateProviderText(item.Provider)}",
                FontSize = 12,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!item.CanAutoUpdate)
            {
                details.Children.Add(new TextBlock
                {
                    Text = item.CannotUpdateReason ?? "该项目不能安全自动更新。",
                    FontSize = 12,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            var row = new Grid
            {
                Padding = new Thickness(4, 10, 8, 10),
                ColumnSpacing = 10,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(details, 1);
            row.Children.Add(checkBox);
            row.Children.Add(details);
            itemsPanel.Children.Add(row);
            rows.Add((item, checkBox));
        }
        var content = new StackPanel { Spacing = 12, MinWidth = 500 };
        content.Children.Add(new TextBlock
        {
            Text = "已验证可自动安装的项目默认选中。你可以取消任意项目；置灰项仅供查看，不会进入更新队列。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new ScrollViewer
        {
            Content = itemsPanel,
            Height = Math.Clamp(selections.Length * 72, 180, 360),
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "选择要更新的 Mod",
            Content = content,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        void RefreshSelectedCount()
        {
            var selected = rows.Count(row =>
                row.Item.CanAutoUpdate && row.CheckBox.IsChecked == true);
            confirmation.PrimaryButtonText = selected > 0 ? $"更新 {selected} 个" : "请选择 Mod";
            confirmation.IsPrimaryButtonEnabled = selected > 0;
            UpdateAllButtonText.Text = $"选择更新 ({selected}/{selections.Length})";
        }
        foreach (var row in rows.Where(row => row.Item.CanAutoUpdate))
        {
            row.CheckBox.Checked += (_, _) => RefreshSelectedCount();
            row.CheckBox.Unchecked += (_, _) => RefreshSelectedCount();
        }
        RefreshSelectedCount();
        ContentDialogResult confirmationResult;
        try
        {
            confirmationResult = await confirmation.ShowAsync();
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("无法开始全部更新", error.Message, InfoBarSeverity.Error);
            SetBatchUpdateSelections(selections);
            return;
        }
        finally
        {
            _startingBatchUpdate = false;
        }
        if (confirmationResult != ContentDialogResult.Primary)
        {
            SetBatchUpdateSelections(selections);
            return;
        }
        var candidates = rows
            .Where(row => row.CheckBox.IsChecked == true && row.Item.Candidate is not null)
            .Select(row => row.Item.Candidate!)
            .ToArray();
        if (candidates.Length == 0)
        {
            SetBatchUpdateSelections(selections);
            return;
        }
        if (ViewModel.IsGameRunning)
        {
            SetBatchUpdateSelections(selections);
            App.MainWindow.ShowMessage("游戏正在运行", "请先关闭游戏，再批量更新 Mod。", InfoBarSeverity.Warning);
            return;
        }

        _updateInstallCancellation?.Cancel();
        _updateInstallCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _updateInstallCancellation = cancellation;
        _updateOperationInProgress = true;
        CheckAllUpdatesButton.IsEnabled = false;
        UpdateAllButton.IsEnabled = false;
        UpdateAllIcon.Visibility = Visibility.Collapsed;
        UpdateAllProgressRing.Visibility = Visibility.Visible;
        UpdateAllProgressRing.IsActive = true;
        ModsList.IsEnabled = false;
        TranslationInfoBar.Title = "全部更新已开始";
        TranslationInfoBar.Message = $"0/{candidates.Length} 完成；更新将逐项安装。";
        TranslationInfoBar.Severity = InfoBarSeverity.Informational;
        TranslationInfoBar.IsOpen = true;

        ModUpdateBatchResult? result = null;
        try
        {
            var batchProgress = new Progress<ModUpdateBatchProgress>(UpdateBatchProgress);
            result = await _modUpdateBatchService.RunAsync(
                candidates,
                async (candidate, token) =>
                {
                    var current = FindCurrentBatchMod(candidate);
                    current.IsUpdating = true;
                    try
                    {
                        var downloadProgress = new Progress<NexusDownloadProgress>(value =>
                        {
                            TranslationInfoBar.Title = $"正在更新 {candidate.ModName}";
                            TranslationInfoBar.Message = value.StatusText;
                            TranslationInfoBar.Severity = InfoBarSeverity.Informational;
                            TranslationInfoBar.IsOpen = true;
                        });
                        await DownloadInspectAndInstallUpdateAsync(
                            current,
                            candidate.Download,
                            downloadProgress,
                            isBatchUpdate: true,
                            token);
                    }
                    finally
                    {
                        current.IsUpdating = false;
                    }
                },
                batchProgress,
                cancellation.Token);

            await RecordBatchUpdateActivitiesAsync(result);
            TranslationInfoBar.Title = result.WasCancelled ? "全部更新已取消" : "全部更新完成";
            TranslationInfoBar.Message = result.WasCancelled
                ? $"已完成 {result.Completed}/{candidates.Length}；成功 {result.Succeeded}，失败 {result.Failed}。"
                : $"成功 {result.Succeeded} 个，失败 {result.Failed} 个。";
            TranslationInfoBar.Severity = result.Failed > 0
                ? InfoBarSeverity.Warning
                : result.WasCancelled
                    ? InfoBarSeverity.Informational
                    : InfoBarSeverity.Success;
            TranslationInfoBar.IsOpen = true;

            if (!result.WasCancelled && result.Failed > 0 && XamlRoot is not null)
            {
                await ShowBatchUpdateResultAsync(result);
            }
        }
        catch (OperationCanceledException)
        {
            TranslationInfoBar.Title = "全部更新已取消";
            TranslationInfoBar.Message = result is null
                ? "尚未完成任何更新。"
                : $"已完成 {result.Completed}/{candidates.Length}。";
            TranslationInfoBar.Severity = InfoBarSeverity.Informational;
            TranslationInfoBar.IsOpen = true;
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("无法运行全部更新", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_updateInstallCancellation, cancellation))
            {
                _updateInstallCancellation = null;
            }
            _updateOperationInProgress = false;
            CheckAllUpdatesButton.IsEnabled = true;
            UpdateAllProgressRing.IsActive = false;
            UpdateAllProgressRing.Visibility = Visibility.Collapsed;
            UpdateAllIcon.Visibility = Visibility.Visible;
            ModsList.IsEnabled = true;
            InvalidateBatchUpdateCandidates();
        }
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledMod mod
            || ViewModel is null
            || mod.IsCheckingUpdate
            || mod.IsUpdating)
        {
            return;
        }
        if (_updateOperationInProgress)
        {
            App.MainWindow.ShowMessage(
                "更新队列正在运行",
                "请等待当前队列完成后再操作单个 Mod。",
                InfoBarSeverity.Informational);
            return;
        }

        if (mod.UpdateResult?.UpdateAvailable != true)
        {
            InvalidateBatchUpdateCandidates();
            await CheckSingleUpdateAsync(mod);
            return;
        }
        if (!mod.UpdateResult.CanAutoUpdate || mod.UpdateResult.Download is null)
        {
            var pageUrl = mod.UpdateResult.Download?.PageUrl
                ?? mod.UpdateResult.Sources.FirstOrDefault(source => source.UpdateAvailable)?.PageUrl
                ?? mod.UpdateResult.Sources.FirstOrDefault(source => source.PageUrl is not null)?.PageUrl;
            if (pageUrl is not null)
            {
                ViewModel.OpenUrl(pageUrl);
            }
            else
            {
                App.MainWindow.ShowMessage(
                    "无法自动更新",
                    mod.UpdateResult.CannotUpdateReason ?? mod.UpdateResult.Message,
                    InfoBarSeverity.Warning);
            }
            return;
        }

        InvalidateBatchUpdateCandidates();
        await DownloadAndInstallUpdateAsync(mod, mod.UpdateResult.Download);
    }

    private async Task CheckSingleUpdateAsync(InstalledMod mod)
    {
        if (ViewModel is null)
        {
            return;
        }
        _updateChecksCancellation?.Cancel();
        _updateChecksCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _updateChecksCancellation = cancellation;
        mod.IsCheckingUpdate = true;
        try
        {
            mod.UpdateResult = await ViewModel.CheckModUpdateAsync(
                mod,
                cancellation.Token);
            var severity = mod.UpdateResult.UpdateAvailable
                ? InfoBarSeverity.Informational
                : InfoBarSeverity.Success;
            App.MainWindow.ShowMessage("更新检查完成", $"{mod.Name}：{mod.UpdateResult.Message}", severity);
        }
        catch (OperationCanceledException)
        {
            // A newer check superseded this request.
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("检查 Mod 更新失败", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            mod.IsCheckingUpdate = false;
            cancellation.Dispose();
            if (ReferenceEquals(_updateChecksCancellation, cancellation))
            {
                _updateChecksCancellation = null;
            }
        }
    }

    private async Task DownloadAndInstallUpdateAsync(
        InstalledMod mod,
        ModUpdateDownloadDescriptor descriptor)
    {
        if (ViewModel?.Installation is null)
        {
            return;
        }
        if (_updateOperationInProgress)
        {
            App.MainWindow.ShowMessage(
                "已有更新正在进行",
                "请等待当前 Mod 更新完成后再继续。",
                InfoBarSeverity.Informational);
            return;
        }
        if (ViewModel.IsGameRunning)
        {
            App.MainWindow.ShowMessage("游戏正在运行", "请先关闭游戏，再更新 Mod。", InfoBarSeverity.Warning);
            return;
        }

        _updateInstallCancellation?.Cancel();
        _updateInstallCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _updateInstallCancellation = cancellation;
        _updateOperationInProgress = true;
        CheckAllUpdatesButton.IsEnabled = false;
        mod.IsUpdating = true;
        var progress = new Progress<NexusDownloadProgress>(value =>
        {
            TranslationInfoBar.Title = $"正在更新 {mod.Name}";
            TranslationInfoBar.Message = value.StatusText;
            TranslationInfoBar.Severity = InfoBarSeverity.Informational;
            TranslationInfoBar.IsOpen = true;
        });

        try
        {
            var installed = await DownloadInspectAndInstallUpdateAsync(
                mod,
                descriptor,
                progress,
                isBatchUpdate: false,
                cancellation.Token);
            if (installed is null)
            {
                return;
            }
            App.MainWindow.ShowMessage(
                "Mod 更新完成",
                $"{mod.Name} 已更新到 {descriptor.ExpectedVersion}，配置和已有译文已保留。",
                InfoBarSeverity.Success);
            TranslationInfoBar.Title = "更新完成";
            TranslationInfoBar.Message = $"已替换 {installed.Replaced} 个 Mod；下载包保留在应用缓存。";
            TranslationInfoBar.Severity = InfoBarSeverity.Success;
            TranslationInfoBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            App.MainWindow.ShowMessage("更新已取消", mod.Name, InfoBarSeverity.Informational);
        }
        catch (Exception error)
        {
            await ViewModel.RecordActivityAsync(
                ActivityKind.Update,
                ActivityOutcome.Failed,
                $"Mod 更新失败：{mod.Name}",
                $"{mod.Id} · {mod.Version} → {descriptor.ExpectedVersion} · {error.Message}",
                descriptor.PageUrl,
                descriptor.ExpectedVersion,
                CancellationToken.None);
            App.MainWindow.ShowMessage("Mod 更新失败", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            mod.IsUpdating = false;
            cancellation.Dispose();
            if (ReferenceEquals(_updateInstallCancellation, cancellation))
            {
                _updateInstallCancellation = null;
            }
            _updateOperationInProgress = false;
            CheckAllUpdatesButton.IsEnabled = true;
        }
    }

    private async Task<ModInstallResult?> DownloadInspectAndInstallUpdateAsync(
        InstalledMod mod,
        ModUpdateDownloadDescriptor descriptor,
        IProgress<NexusDownloadProgress>? progress,
        bool isBatchUpdate,
        CancellationToken cancellationToken)
    {
        if (ViewModel?.Installation is not { } installation)
        {
            throw new InvalidOperationException("请先设置游戏目录。");
        }
        var downloaded = await ViewModel.DownloadModUpdateAsync(
            descriptor,
            progress,
            cancellationToken);
        var plan = await ViewModel.InspectModArchiveAsync(
            downloaded.Path,
            cancellationToken);
        ValidateUpdatePlan(mod, descriptor, installation, plan);

        var allowXnb = false;
        if (plan.RequiresXnbConfirmation)
        {
            if (isBatchUpdate)
            {
                throw new InvalidDataException(
                    $"更新包包含 {plan.XnbFiles.Count} 个 XNB 文件，需要单独人工确认，未执行自动更新。");
            }
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "更新包包含 XNB 文件",
                Content = new TextBlock
                {
                    Text = $"{mod.Name} 的更新包含 {plan.XnbFiles.Count} 个旧式 XNB 文件。只有确认上游可信时才继续。",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 440,
                },
                PrimaryButtonText = "确认更新",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }
            allowXnb = true;
        }

        var options = new ModInstallOptions { AllowXnbFiles = allowXnb };
        return isBatchUpdate
            ? await ViewModel.InstallModArchiveForBatchUpdateAsync(
                plan,
                options,
                cancellationToken)
            : await ViewModel.InstallModArchiveAsync(
                plan,
                options,
                cancellationToken);
    }

    private static void ValidateUpdatePlan(
        InstalledMod installedMod,
        ModUpdateDownloadDescriptor descriptor,
        GameInstallation installation,
        ModInstallPlan plan)
    {
        if (!plan.CanInstall || plan.Mods.Count != 1)
        {
            throw new InvalidDataException("更新包不是可安全安装的单一 SMAPI Mod。请打开上游页面手动核对。");
        }
        var packageMod = plan.Mods[0];
        if (!packageMod.UniqueId.Equals(installedMod.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"更新包 UniqueID 为 {packageMod.UniqueId}，与已安装的 {installedMod.Id} 不一致。");
        }
        if (ModUpdateService.CompareVersions(descriptor.ExpectedVersion, installedMod.Version) is not > 0)
        {
            throw new InvalidDataException("上游报告的版本不高于当前版本，已停止自动更新。");
        }
        var versionComparison = ModUpdateService.CompareVersions(
            packageMod.Version,
            descriptor.ExpectedVersion);
        if (versionComparison != 0)
        {
            throw new InvalidDataException(
                $"更新包版本为 {packageMod.Version}，与上游报告的 {descriptor.ExpectedVersion} 不一致。");
        }
        if (string.IsNullOrWhiteSpace(packageMod.ExistingVersion)
            || ModUpdateService.CompareVersions(packageMod.ExistingVersion, installedMod.Version) != 0)
        {
            throw new InvalidDataException("安装计划未能唯一匹配当前 Mod 版本，已停止自动更新。");
        }
        if (string.IsNullOrWhiteSpace(packageMod.TargetPath))
        {
            throw new InvalidDataException("安装计划没有明确的现有 Mod 目标目录，已停止自动更新。");
        }
        var currentPath = Path.GetFullPath(Path.Combine(installation.Path, installedMod.Path));
        if (!Path.GetFullPath(packageMod.TargetPath).Equals(
            currentPath,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新包将写入意外的 Mod 目录，已停止自动更新。");
        }
    }

    private void SetBatchUpdateSelections(IReadOnlyList<ModUpdateBatchSelectionItem> selections)
    {
        _checkedBatchSelections = selections;
        var safeCount = selections.Count(item => item.CanAutoUpdate);
        UpdateAllButton.Visibility = selections.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateAllButton.IsEnabled = selections.Count > 0 && !_updateOperationInProgress;
        UpdateAllButtonText.Text = selections.Count > 0
            ? $"选择更新 ({safeCount}/{selections.Count})"
            : "选择更新";
    }

    private void InvalidateBatchUpdateCandidates()
    {
        SetBatchUpdateSelections(Array.Empty<ModUpdateBatchSelectionItem>());
    }

    private static string GetUpdateProviderText(ModUpdateProvider provider)
    {
        return provider switch
        {
            ModUpdateProvider.Nexus => "Nexus Mods",
            ModUpdateProvider.GitHub => "GitHub Releases",
            _ => "上游来源未知",
        };
    }

    private InstalledMod FindCurrentBatchMod(ModUpdateBatchCandidate candidate)
    {
        if (ViewModel is null)
        {
            throw new InvalidOperationException("Mod 页面已关闭。");
        }
        var matches = ViewModel.Mods
            .Where(mod => mod.Id.Equals(candidate.ModId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("批量检查后 Mod 列表发生变化，请重新检查更新。");
        }
        var mod = matches[0];
        if (mod.Health != "healthy"
            || ModUpdateService.CompareVersions(mod.Version, candidate.InstalledVersion) != 0)
        {
            throw new InvalidDataException("本地 Mod 状态或版本已变化，请重新检查更新。");
        }
        return mod;
    }

    private void UpdateBatchProgress(ModUpdateBatchProgress progress)
    {
        var displayIndex = progress.ItemCompleted
            ? progress.Completed
            : Math.Min(progress.Completed + 1, progress.Total);
        UpdateAllButtonText.Text = $"更新 {displayIndex}/{progress.Total}";
        if (progress.Current is null)
        {
            return;
        }
        TranslationInfoBar.Title = progress.ItemCompleted
            ? $"已处理 {progress.Current.ModName}"
            : $"正在更新 {progress.Current.ModName}";
        TranslationInfoBar.Message =
            $"{progress.Completed}/{progress.Total} 完成 · 成功 {progress.Succeeded} · 失败 {progress.Failed}";
        TranslationInfoBar.Severity = progress.Failed > 0
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Informational;
        TranslationInfoBar.IsOpen = true;
    }

    private async Task RecordBatchUpdateActivitiesAsync(ModUpdateBatchResult result)
    {
        if (ViewModel is null)
        {
            return;
        }
        foreach (var item in result.Items)
        {
            var candidate = item.Candidate;
            var detail = item.Succeeded
                ? $"{candidate.ModId} · {candidate.InstalledVersion} → {candidate.ExpectedVersion} · 全部更新队列"
                : $"{candidate.ModId} · {candidate.InstalledVersion} → {candidate.ExpectedVersion} · {item.ErrorMessage}";
            await ViewModel.RecordActivityAsync(
                ActivityKind.Update,
                item.Succeeded ? ActivityOutcome.Success : ActivityOutcome.Failed,
                item.Succeeded
                    ? $"批量更新完成：{candidate.ModName}"
                    : $"批量更新失败：{candidate.ModName}",
                detail,
                candidate.Download.PageUrl,
                candidate.ExpectedVersion,
                CancellationToken.None);
        }
    }

    private async Task ShowBatchUpdateResultAsync(ModUpdateBatchResult result)
    {
        var failures = result.Items.Where(item => !item.Succeeded).ToArray();
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = $"成功 {result.Succeeded} 个，失败 {result.Failed} 个。失败项目没有阻止后续更新。",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var failure in failures)
        {
            var itemPanel = new StackPanel { Spacing = 3 };
            itemPanel.Children.Add(new TextBlock
            {
                Text = failure.Candidate.ModName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            itemPanel.Children.Add(new TextBlock
            {
                Text = failure.ErrorMessage ?? "更新失败，但未返回错误详情。",
                Opacity = 0.72,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(itemPanel);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "全部更新结果",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxWidth = 520,
                MaxHeight = 390,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    private async void OnTrashClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || _trashDialog is not null)
        {
            return;
        }
        if (_updateOperationInProgress)
        {
            App.MainWindow.ShowMessage(
                "更新队列正在运行",
                "请等待队列完成后再管理回收区。",
                InfoBarSeverity.Informational);
            return;
        }
        if (ViewModel.IsGameRunning)
        {
            App.MainWindow.ShowMessage(
                "游戏正在运行",
                "请先关闭游戏，再管理 Mod 回收区。",
                InfoBarSeverity.Warning);
            return;
        }

        IReadOnlyList<ModTrashItem> items;
        try
        {
            items = await ViewModel.ListModTrashAsync();
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("无法读取回收区", error.Message, InfoBarSeverity.Error);
            return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            ItemTemplate = Resources["ModTrashItemTemplate"] as DataTemplate,
            MaxHeight = 390,
        };
        list.ItemContainerStyle = new Style(typeof(ListViewItem))
        {
            Setters =
            {
                new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(ListViewItem.PaddingProperty, new Thickness(0)),
            },
        };
        var emptyText = new TextBlock
        {
            Text = "回收区是空的",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.68,
        };
        var content = new Grid
        {
            MinWidth = 520,
            MinHeight = 180,
        };
        content.Children.Add(list);
        content.Children.Add(emptyText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Mod 回收区",
            Content = content,
            PrimaryButtonText = "清空回收区",
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Close,
        };
        _trashDialog = dialog;
        _trashList = list;
        _trashEmptyText = emptyText;
        UpdateTrashList(items);

        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            _trashDialog = null;
            _trashList = null;
            _trashEmptyText = null;
        }

        if (result != ContentDialogResult.Primary || _trashItemCount == 0)
        {
            return;
        }

        await ConfirmAndEmptyTrashAsync(_trashItemCount);
    }

    private async void OnRestoreTrashItemClick(object sender, RoutedEventArgs e)
    {
        if (_trashMutationInProgress
            || sender is not Button button
            || button.DataContext is not ModTrashItem item
            || ViewModel is null)
        {
            return;
        }

        _trashMutationInProgress = true;
        button.IsEnabled = false;
        if (_trashDialog is not null)
        {
            _trashDialog.IsPrimaryButtonEnabled = false;
        }
        try
        {
            InvalidateBatchUpdateCandidates();
            var result = await ViewModel.RestoreModFromTrashAsync(item);
            var detail = result.Renamed
                ? $"原目录已有同名项目，已恢复为 {result.RestoredDirectoryName}。"
                : $"已恢复到 Mods\\{result.RestoredRelativePath}。";
            App.MainWindow.ShowMessage("Mod 已恢复", detail, InfoBarSeverity.Success);
            await RefreshTrashListAsync();
        }
        catch (Exception error)
        {
            button.IsEnabled = true;
            App.MainWindow.ShowMessage("无法恢复 Mod", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _trashMutationInProgress = false;
            if (_trashDialog is not null)
            {
                _trashDialog.IsPrimaryButtonEnabled = _trashItemCount > 0;
            }
        }
    }

    private async Task RefreshTrashListAsync()
    {
        if (ViewModel is null || _trashList is null)
        {
            return;
        }
        UpdateTrashList(await ViewModel.ListModTrashAsync());
    }

    private void UpdateTrashList(IReadOnlyList<ModTrashItem> items)
    {
        _trashItemCount = items.Count;
        if (_trashList is not null)
        {
            _trashList.ItemsSource = items;
            _trashList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_trashEmptyText is not null)
        {
            _trashEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_trashDialog is not null)
        {
            _trashDialog.IsPrimaryButtonEnabled = items.Count > 0 && !_trashMutationInProgress;
        }
    }

    private async Task ConfirmAndEmptyTrashAsync(int itemCount)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"永久删除回收区中的 {itemCount} 个 Mod？",
            Content = "此操作只会清理 Mods\\.mod-manager-trash 中的内容，但无法撤销。",
            PrimaryButtonText = "永久删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary || ViewModel is null)
        {
            return;
        }

        try
        {
            InvalidateBatchUpdateCandidates();
            var removedCount = await ViewModel.EmptyModTrashAsync();
            App.MainWindow.ShowMessage(
                "回收区已清空",
                $"已永久删除 {removedCount} 个回收区项目。",
                InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("无法清空回收区", error.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle
            || sender is not ToggleSwitch toggle
            || toggle.DataContext is not InstalledMod mod
            || ViewModel is null)
        {
            return;
        }

        var original = toggle.Tag is bool enabled && enabled;
        if (toggle.IsOn == original)
        {
            return;
        }

        try
        {
            toggle.IsEnabled = false;
            InvalidateBatchUpdateCandidates();
            await ViewModel.SetModEnabledAsync(mod, toggle.IsOn);
            toggle.Tag = toggle.IsOn;
            App.MainWindow.ShowMessage(
                toggle.IsOn ? "Mod 已启用" : "Mod 已停用",
                mod.Name,
                InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            _suppressToggle = true;
            toggle.IsOn = original;
            _suppressToggle = false;
            toggle.IsEnabled = true;
            App.MainWindow.ShowMessage("无法切换 Mod 状态", error.Message, InfoBarSeverity.Error);
        }
    }

    private void OnEnabledToggleLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }
        toggle.Toggled -= OnEnabledToggled;
        toggle.Tag = toggle.IsOn;
        toggle.Toggled += OnEnabledToggled;
    }

    private void OnEnabledToggleUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
        {
            toggle.Toggled -= OnEnabledToggled;
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledMod mod || ViewModel is null)
        {
            return;
        }

        try
        {
            ViewModel.OpenModFolder(mod);
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("无法打开目录", error.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnDetailsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledMod mod || ViewModel is null)
        {
            return;
        }

        var details = new StackPanel
        {
            Width = 540,
            MaxHeight = 510,
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
            Text = $"{mod.Author}  ·  {mod.Version}  ·  {mod.StatusText}",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        });
        details.Children.Add(CreateDetailValue("UniqueID", mod.Id));
        details.Children.Add(CreateDetailValue("安装目录", mod.Path));
        details.Children.Add(CreateDetailValue(
            "简介",
            string.IsNullOrWhiteSpace(mod.Description) ? "暂无简介" : mod.Description));
        if (mod.Dependencies.Count > 0)
        {
            details.Children.Add(CreateDetailValue("依赖", string.Join(Environment.NewLine, mod.Dependencies)));
        }
        if (mod.UpdateKeys.Count > 0)
        {
            details.Children.Add(CreateDetailValue("更新来源", string.Join(Environment.NewLine, mod.UpdateKeys)));
        }
        if (!string.IsNullOrWhiteSpace(mod.UpdateAvailable))
        {
            details.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Informational,
                Title = "发现更新",
                Message = mod.UpdateAvailable,
            });
        }
        if (!string.IsNullOrWhiteSpace(mod.Error) || !string.IsNullOrWhiteSpace(mod.UpdateKeysError))
        {
            details.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = "需要处理",
                Message = string.Join(
                    Environment.NewLine,
                    new[] { mod.Error, mod.UpdateKeysError }.Where(value => !string.IsNullOrWhiteSpace(value))),
            });
        }

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
            PrimaryButtonText = "打开目录",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                ViewModel.OpenModFolder(mod);
            }
            catch (Exception error)
            {
                App.MainWindow.ShowMessage("无法打开目录", error.Message, InfoBarSeverity.Error);
            }
        }
    }

    private static FrameworkElement CreateDetailValue(string label, string value)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.68,
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        return panel;
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledMod mod || ViewModel is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"移除 {mod.Name}？",
            Content = "Mod 将移动到 Mods\\.mod-manager-trash，不会立即永久删除。",
            PrimaryButtonText = "移到回收区",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            InvalidateBatchUpdateCandidates();
            await ViewModel.MoveModToTrashAsync(mod);
            App.MainWindow.ShowMessage("已移到回收区", mod.Name, InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("无法移除 Mod", error.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledMod mod
            || ViewModel?.Installation is not { } installation
            || mod.Health != "healthy"
            || !_translatingPaths.Add(mod.Path))
        {
            return;
        }

        mod.IsTranslating = true;
        try
        {
            var source = await _modService.ReadTranslationSourceAsync(installation.Path, mod);
            var summary = string.IsNullOrWhiteSpace(source.Description)
                ? $"Stardew Valley Mod: {source.Name}"
                : source.Description;
            var translated = await _aiTranslationService.TranslateAsync(source.Name, summary);
            await _modService.SaveTranslationAsync(
                installation.Path,
                mod,
                source,
                new InstalledModTranslation(translated.Name, translated.Summary));
            mod.ApplyTranslation(translated.Name, translated.Summary);
            App.MainWindow.ShowMessage(
                "Mod 翻译已保存",
                $"{translated.Name} · 已写入独立 sidecar，manifest.json 未修改。",
                InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("Mod 翻译失败", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _translatingPaths.Remove(mod.Path);
            mod.IsTranslating = false;
        }
    }

    private void OnToggleDescription(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Parent is not StackPanel panel)
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
}
