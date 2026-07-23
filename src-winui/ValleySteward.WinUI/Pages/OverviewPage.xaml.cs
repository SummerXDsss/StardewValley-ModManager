using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.Specialized;
using ValleySteward.WinUI.ViewModels;

namespace ValleySteward.WinUI.Pages;

public sealed partial class OverviewPage : Page
{
    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    public OverviewPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DataContext = e.Parameter;
        if (ViewModel is not null)
        {
            ViewModel.RecentActivities.CollectionChanged += OnActivitiesChanged;
        }
        UpdateWarningState();
        UpdateActivityState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.RecentActivities.CollectionChanged -= OnActivitiesChanged;
        }
        base.OnNavigatedFrom(e);
    }

    private void OnActivitiesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateActivityState();

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        try
        {
            await ViewModel.RefreshAllAsync();
            UpdateWarningState();
            App.MainWindow.ShowMessage("扫描完成", $"已读取 {ViewModel.Mods.Count} 个 Mod。", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("扫描失败", error.Message, InfoBarSeverity.Error);
        }
    }

    private void UpdateWarningState()
    {
        var hasWarnings = ViewModel?.Warnings.Count > 0;
        NoWarningsText.Visibility = hasWarnings ? Visibility.Collapsed : Visibility.Visible;
        WarningsItems.Visibility = hasWarnings ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateActivityState()
    {
        var hasActivities = ViewModel?.RecentActivities.Count > 0;
        EmptyActivitiesText.Visibility = hasActivities ? Visibility.Collapsed : Visibility.Visible;
        ActivityList.Visibility = hasActivities ? Visibility.Visible : Visibility.Collapsed;
        ClearActivityButton.IsEnabled = hasActivities;
    }

    private async void OnClearActivityClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RecentActivities.Count is null or 0)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空最近活动？",
            Content = "这只会删除管理器的本地操作记录，不会删除下载文件、Mod 或游戏数据。",
            PrimaryButtonText = "清空记录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            await ViewModel.ClearActivityHistoryAsync();
            App.MainWindow.ShowMessage("活动历史已清空", string.Empty, InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("无法清空活动历史", error.Message, InfoBarSeverity.Error);
        }
    }
}
