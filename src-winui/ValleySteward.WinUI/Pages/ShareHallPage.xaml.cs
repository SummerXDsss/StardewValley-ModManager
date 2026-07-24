using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using ValleySteward.WinUI.Models;
using ValleySteward.WinUI.Services;
using ValleySteward.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace ValleySteward.WinUI.Pages;

public sealed partial class ShareHallPage : Page
{
    private readonly ModShareService _shareService = new();
    private readonly NexusDownloadService _nexusDownloadService = new();
    private readonly ModUpdateService _modUpdateService = new();
    private readonly ModUpdateDownloadService _modUpdateDownloadService = new();
    private readonly ObservableCollection<ModShareEntry> _shares = [];
    private readonly ObservableCollection<ModShareEntry> _myShares = [];
    private bool _loaded;
    private bool _directInstallInProgress;
    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    public ShareHallPage()
    {
        InitializeComponent();
        ShareList.ItemsSource = _shares;
        MyShareList.ItemsSource = _myShares;
        EndpointText.Text = $"HTTP 后端：{ModShareService.ApiBaseUrl}";
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DataContext = e.Parameter;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        await RefreshHallAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshHallAsync();
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        await RefreshHallAsync();
    }

    private async void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await RefreshHallAsync();
    }

    private void OnShowHallClick(object sender, RoutedEventArgs e)
    {
        ShowHallView();
    }

    private async void OnShowMySharesClick(object sender, RoutedEventArgs e)
    {
        ShowMySharesView();
        await LoadMySharesAsync();
    }

    private async void OnClaimMySharesClick(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var result = await _shareService.ClaimMineAsync();
            ReplaceMyShares(result.Shares);
            ShowMySharesView();
            ShowMessage(
                "IP 认领完成",
                $"当前出口 IP {result.MaskedIp} 已认领 {result.Count} 条分享。",
                result.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        });
    }

    private async void OnPublishClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }
        if (ViewModel.Mods.Count == 0)
        {
            ShowMessage("没有可分享的 Mod", "请先设置游戏目录并完成 Mod 扫描。", InfoBarSeverity.Warning);
            return;
        }

        await RunBusyAsync(async () =>
        {
            var result = await _shareService.PublishAsync(ViewModel.Mods.ToArray());
            UpsertShare(result.Entry);
            UpsertMyShare(result.Entry);
            ShareCodeBox.Text = result.Code;
            ShowMessage(
                "分享已发布",
                $"分享码 {result.Code} 已生成，来自 {Environment.MachineName} 的 Mod 列表已进入大厅。",
                InfoBarSeverity.Success);
        });
    }

    private async void OnFindCodeClick(object sender, RoutedEventArgs e)
    {
        await OpenShareCodeAsync();
    }

    private async void OnShareCodeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await OpenShareCodeAsync();
    }

    private void OnCopyCodeClick(object sender, RoutedEventArgs e)
    {
        var code = ShareCodeBox.Text.Trim();
        if (!ModShareService.IsValidShareCode(code))
        {
            ShowMessage("分享码格式不对", "请输入 10 位数字或字母分享码。", InfoBarSeverity.Warning);
            return;
        }

        CopyTextToClipboard(code);
        ShowMessage("分享码已复制", code, InfoBarSeverity.Success);
    }

    private static void CopyTextToClipboard(string text)
    {
        var package = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy,
        };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private async Task OpenShareCodeAsync()
    {
        var code = ShareCodeBox.Text.Trim();
        if (!ModShareService.IsValidShareCode(code))
        {
            ShowMessage("分享码格式不对", "请输入 10 位数字或字母分享码。", InfoBarSeverity.Warning);
            return;
        }

        await RunBusyAsync(async () =>
        {
            var entry = await _shareService.GetAsync(code);
            UpsertShare(entry);
            ShareCodeBox.Text = entry.Code;
            ShowMessage(
                "分享码已打开",
                $"{entry.ComputerName} · {entry.MaskedIp} · {entry.ModCount} 个 Mod",
                InfoBarSeverity.Success);
        });
    }

    private async Task RefreshHallAsync()
    {
        await RunBusyAsync(async () =>
        {
            var query = SearchBox.Text.Trim();
            var includeSearchOnly = IncludeSearchOnlyBox.IsChecked == true
                && !string.IsNullOrWhiteSpace(query);
            var entries = await _shareService.ListAsync(query, includeSearchOnly);
            _shares.Clear();
            foreach (var entry in entries)
            {
                _shares.Add(entry);
            }
            ShowHallView();
            ShowMessage(
                string.IsNullOrWhiteSpace(query) ? "分享大厅已刷新" : "搜索完成",
                string.IsNullOrWhiteSpace(query)
                    ? $"已读取 {_shares.Count} 条全部公开 Mod 列表。"
                    : $"已找到 {_shares.Count} 条匹配“{query}”的分享。",
                InfoBarSeverity.Success);
        });
    }

    private async Task LoadMySharesAsync()
    {
        await RunBusyAsync(async () =>
        {
            var entries = await _shareService.GetMineAsync();
            ReplaceMyShares(entries);
            ShowMessage("我的分享已刷新", $"当前 IP 下有 {_myShares.Count} 条可管理分享。", InfoBarSeverity.Success);
        });
    }

    private void UpsertShare(ModShareEntry entry)
    {
        for (var index = _shares.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_shares[index].Code, entry.Code, StringComparison.OrdinalIgnoreCase))
            {
                _shares.RemoveAt(index);
            }
        }
        _shares.Insert(0, entry);
    }

    private void UpsertMyShare(ModShareEntry entry)
    {
        for (var index = _myShares.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_myShares[index].Code, entry.Code, StringComparison.OrdinalIgnoreCase))
            {
                _myShares.RemoveAt(index);
            }
        }
        _myShares.Insert(0, entry);
    }

    private void RemoveShare(string code)
    {
        RemoveFromCollection(_shares, code);
        RemoveFromCollection(_myShares, code);
    }

    private static void RemoveFromCollection(ObservableCollection<ModShareEntry> collection, string code)
    {
        for (var index = collection.Count - 1; index >= 0; index--)
        {
            if (string.Equals(collection[index].Code, code, StringComparison.OrdinalIgnoreCase))
            {
                collection.RemoveAt(index);
            }
        }
    }

    private void ReplaceMyShares(IReadOnlyList<ModShareEntry> entries)
    {
        _myShares.Clear();
        foreach (var entry in entries)
        {
            _myShares.Add(entry);
        }
    }

    private void ShowHallView()
    {
        ShareList.Visibility = Visibility.Visible;
        MySharesPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowMySharesView()
    {
        ShareList.Visibility = Visibility.Collapsed;
        MySharesPanel.Visibility = Visibility.Visible;
    }

    private async Task RunBusyAsync(Func<Task> action, string errorTitle = "分享服务请求失败")
    {
        HallButton.IsEnabled = false;
        MySharesButton.IsEnabled = false;
        ClaimButton.IsEnabled = false;
        PublishButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        SearchButton.IsEnabled = false;
        FindCodeButton.IsEnabled = false;
        CopyCodeButton.IsEnabled = false;
        SearchBox.IsEnabled = false;
        IncludeSearchOnlyBox.IsEnabled = false;
        ShareCodeBox.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception error)
        {
            ShowMessage(errorTitle, error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            HallButton.IsEnabled = true;
            MySharesButton.IsEnabled = true;
            ClaimButton.IsEnabled = true;
            PublishButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            SearchButton.IsEnabled = true;
            FindCodeButton.IsEnabled = true;
            CopyCodeButton.IsEnabled = true;
            SearchBox.IsEnabled = true;
            IncludeSearchOnlyBox.IsEnabled = true;
            ShareCodeBox.IsEnabled = true;
        }
    }

    private void OnCopyEntryCodeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModShareEntry entry)
        {
            return;
        }

        ShareCodeBox.Text = entry.Code;
        CopyTextToClipboard(entry.Code);
        ShowMessage("分享码已复制", entry.Code, InfoBarSeverity.Success);
    }

    private async void OnToggleMyShareVisibilityClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModShareEntry entry)
        {
            return;
        }

        var next = entry.Visibility == ShareVisibility.Public
            ? ShareVisibility.SearchOnly
            : ShareVisibility.Public;
        await RunBusyAsync(async () =>
        {
            var updated = await _shareService.UpdateVisibilityAsync(entry.Code, next);
            UpsertMyShare(updated);
            if (updated.Visibility == ShareVisibility.Public)
            {
                UpsertShare(updated);
            }
            else
            {
                RemoveFromCollection(_shares, updated.Code);
            }
            ShowMessage("可见性已更新", $"{updated.Code} · {updated.DisplayVisibilityText}", InfoBarSeverity.Success);
        });
    }

    private async void OnDeleteMyShareClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModShareEntry entry)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除分享",
            Content = $"确定删除分享码 {entry.Code}？删除后其他用户将无法通过大厅、搜索或分享码打开它。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await _shareService.DeleteAsync(entry.Code);
            RemoveShare(entry.Code);
            ShowMessage("分享已删除", entry.Code, InfoBarSeverity.Success);
        });
    }

    private async void OnDetailsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModShareEntry entry)
        {
            return;
        }

        var content = new StackPanel
        {
            MaxWidth = 640,
            MaxHeight = 560,
            Spacing = 12,
        };
        var header = new Grid
        {
            ColumnSpacing = 10,
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = $"{entry.ComputerName} · {entry.MaskedIp} · {entry.ModCount} 个 Mod",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        var copyCodeButton = new Button
        {
            Content = "复制分享码",
            Tag = entry.Code,
            Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Top,
        };
        copyCodeButton.Click += (_, _) =>
        {
            CopyTextToClipboard(entry.Code);
            ShareCodeBox.Text = entry.Code;
            copyCodeButton.Content = "已复制";
        };
        Grid.SetColumn(copyCodeButton, 1);
        header.Children.Add(copyCodeButton);
        content.Children.Add(header);

        ContentDialog? dialog = null;
        foreach (var mod in entry.Mods.Take(80))
        {
            content.Children.Add(BuildModRow(mod, () => dialog?.Hide()));
        }
        if (entry.Mods.Count > 80)
        {
            content.Children.Add(new TextBlock { Text = $"另有 {entry.Mods.Count - 80} 个 Mod 未在详情弹窗中展开。" });
        }

        dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"分享码 {entry.Code}",
            Content = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    private FrameworkElement BuildModRow(SharedModItem mod, Action closeDetails)
    {
        var row = new Border
        {
            Padding = new Thickness(0, 0, 0, 10),
        };
        var layout = new StackPanel
        {
            Spacing = 8,
        };

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = mod.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{mod.AuthorText} · {mod.UniqueId} · {mod.MetaText}",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(mod.Description))
        {
            text.Children.Add(new TextBlock
            {
                Text = mod.Description,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
        }
        layout.Children.Add(text);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AddInstallButton(actions, mod, closeDetails);
        AddCopyPreferredLinkButton(actions, mod);
        AddLinkButton(actions, "原始页面", mod.OriginalUrl);
        AddLinkButton(actions, "GitHub Release", mod.GitHubReleaseUrl);
        AddLinkButton(actions, "打开下载", mod.DirectDownloadUrl);
        if (actions.Children.Count > 0)
        {
            layout.Children.Add(actions);
        }

        row.Child = layout;
        return row;
    }

    private void AddInstallButton(StackPanel panel, SharedModItem mod, Action closeDetails)
    {
        if (!CanAutoInstall(mod))
        {
            return;
        }

        var button = new Button
        {
            Content = "下载安装",
            Padding = new Thickness(8, 5, 8, 5),
        };
        button.Click += async (_, _) =>
        {
            closeDetails();
            await InstallSharedModAsync(mod);
        };
        panel.Children.Add(button);
    }

    private static void AddCopyPreferredLinkButton(StackPanel panel, SharedModItem mod)
    {
        var url = mod.OriginalUrl ?? mod.GitHubReleaseUrl ?? mod.DirectDownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var button = new Button
        {
            Content = "复制链接",
            Padding = new Thickness(8, 5, 8, 5),
            Tag = url,
        };
        button.Click += (_, _) =>
        {
            if (button.Tag is string target)
            {
                CopyTextToClipboard(target);
                button.Content = "已复制";
            }
        };
        panel.Children.Add(button);
    }

    private void AddLinkButton(StackPanel panel, string text, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var button = new Button
        {
            Content = text,
            Padding = new Thickness(8, 5, 8, 5),
            Tag = url,
        };
        button.Click += (_, _) =>
        {
            if (button.Tag is string target)
            {
                ViewModel?.OpenUrl(target);
            }
        };
        panel.Children.Add(button);
    }

    private void OnShareCoverLoaded(object sender, RoutedEventArgs e)
    {
        UpdateShareCover(sender as Image);
    }

    private void OnShareCoverDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        UpdateShareCover(sender as Image);
    }

    private static void UpdateShareCover(Image? image)
    {
        if (image is null)
        {
            return;
        }

        image.Source = null;
        image.Visibility = Visibility.Collapsed;
        if (image.DataContext is SharedModCoverTile tile
            && RemoteModService.NormalizeRemoteImageUrl(tile.CoverUrl) is { } normalized
            && Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            image.Source = new BitmapImage(uri);
            image.Visibility = Visibility.Visible;
        }
    }

    private void OnShareCoverFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image)
        {
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
        }
    }

    private async Task InstallSharedModAsync(SharedModItem mod)
    {
        if (_directInstallInProgress)
        {
            ShowMessage("已有安装任务", "请先完成当前下载或安装。", InfoBarSeverity.Informational);
            return;
        }
        if (ViewModel?.Installation is null)
        {
            ShowMessage("请先设置游戏路径", "下载安装共享 Mod 前需要确认 Stardew Valley 的游戏目录。", InfoBarSeverity.Warning);
            return;
        }
        if (ViewModel.IsGameRunning)
        {
            ShowMessage("游戏正在运行", "请先关闭游戏，再安装或更新 Mod。", InfoBarSeverity.Warning);
            return;
        }

        _directInstallInProgress = true;
        try
        {
            if (TryGetNexusModId(mod, out var nexusId))
            {
                await InstallSharedNexusModAsync(mod, nexusId);
                return;
            }
            if (TryGetGitHubRepository(mod, out var repository))
            {
                await InstallSharedGitHubModAsync(mod, repository);
                return;
            }

            ShowMessage("无法自动下载", "这个共享 Mod 没有可识别的 Nexus 或 GitHub 来源。", InfoBarSeverity.Warning);
        }
        catch (NexusApiKeyMissingException error)
        {
            ShowMessage("需要 Nexus API Key", error.Message, InfoBarSeverity.Warning);
        }
        catch (Exception error)
        {
            ShowMessage("共享 Mod 下载失败", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _directInstallInProgress = false;
        }
    }

    private async Task InstallSharedNexusModAsync(SharedModItem mod, string modId)
    {
        ShowMessage("正在读取 Nexus 文件", $"{mod.Name} · Nexus #{modId}", InfoBarSeverity.Informational);
        var details = await _nexusDownloadService.GetModDetailsAsync(modId);
        var primaryVersions = details.Files
            .Where(file => file.IsActive)
            .SelectMany(file => file.Versions)
            .Where(version => version.IsPrimary)
            .DistinctBy(version => version.GameScopedId, StringComparer.Ordinal)
            .ToArray();
        if (primaryVersions.Length != 1)
        {
            throw new InvalidDataException(primaryVersions.Length == 0
                ? "Nexus 没有返回唯一主文件，请打开原始页面手动确认。"
                : $"Nexus 返回 {primaryVersions.Length} 个主文件候选，请打开原始页面手动确认。");
        }

        var version = primaryVersions[0];
        var progress = new Progress<NexusDownloadProgress>(value =>
        {
            ShowMessage("正在下载共享 Mod", $"{details.Name} · {value.StatusText}", InfoBarSeverity.Informational);
        });
        var download = await _nexusDownloadService.DownloadAsync(
            details.GameScopedId,
            version.GameScopedId,
            progress);
        await App.MainWindow.InstallModArchiveAsync(download.Path, download.MetadataPath);
    }

    private async Task InstallSharedGitHubModAsync(SharedModItem mod, string repository)
    {
        ShowMessage("正在读取 GitHub Release", $"{mod.Name} · {repository}", InfoBarSeverity.Informational);
        var resolution = await _modUpdateService.ResolveGitHubReleaseDownloadAsync(repository);
        if (resolution.Download is not GitHubModUpdateDownloadDescriptor descriptor)
        {
            throw new InvalidDataException(
                resolution.CannotDownloadReason
                ?? "GitHub Release 没有唯一可信 ZIP，请打开 Release 页面手动确认。");
        }

        var progress = new Progress<NexusDownloadProgress>(value =>
        {
            ShowMessage("正在下载共享 Mod", $"{descriptor.AssetName} · {value.StatusText}", InfoBarSeverity.Informational);
        });
        var download = await _modUpdateDownloadService.DownloadAsync(descriptor, progress);
        await App.MainWindow.InstallModArchiveAsync(download.Path);
    }

    private static bool CanAutoInstall(SharedModItem mod)
    {
        return TryGetNexusModId(mod, out _) || TryGetGitHubRepository(mod, out _);
    }

    private static bool TryGetNexusModId(SharedModItem mod, out string id)
    {
        foreach (var key in mod.UpdateKeys)
        {
            if (key.StartsWith("Nexus:", StringComparison.OrdinalIgnoreCase))
            {
                id = key["Nexus:".Length..].Trim();
                if (id.All(char.IsDigit))
                {
                    return true;
                }
            }
        }

        id = string.Empty;
        return TryGetNexusModId(mod.OriginalUrl, out id);
    }

    private static bool TryGetNexusModId(string? url, out string id)
    {
        id = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith("nexusmods.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].Equals("mods", StringComparison.OrdinalIgnoreCase)
                && segments[index + 1].All(char.IsDigit))
            {
                id = segments[index + 1];
                return true;
            }
        }
        return false;
    }

    private static bool TryGetGitHubRepository(SharedModItem mod, out string repository)
    {
        foreach (var key in mod.UpdateKeys)
        {
            if (key.StartsWith("GitHub:", StringComparison.OrdinalIgnoreCase))
            {
                repository = key["GitHub:".Length..].Trim().Trim('/');
                if (IsValidGitHubRepository(repository))
                {
                    return true;
                }
            }
        }

        return TryGetGitHubRepository(mod.GitHubReleaseUrl, out repository)
            || TryGetGitHubRepository(mod.OriginalUrl, out repository);
    }

    private static bool TryGetGitHubRepository(string? url, out string repository)
    {
        repository = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }
        repository = $"{segments[0]}/{segments[1]}";
        return IsValidGitHubRepository(repository);
    }

    private static bool IsValidGitHubRepository(string repository)
    {
        return repository.Count(character => character == '/') == 1
            && repository.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/');
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        ShareInfoBar.Title = title;
        ShareInfoBar.Message = message;
        ShareInfoBar.Severity = severity;
        ShareInfoBar.IsOpen = true;
    }
}
