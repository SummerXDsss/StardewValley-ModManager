using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ValleySteward.WinUI.Models;
using ValleySteward.WinUI.Services;
using ValleySteward.WinUI.ViewModels;

namespace ValleySteward.WinUI.Pages;

public sealed partial class SettingsPage : Page
{
    private const string NexusApiKeysUrl = "https://next.nexusmods.com/settings/api-keys";

    private readonly CredentialService _credentialService = new();
    private readonly ObservableCollection<AiTranslationModel> _availableModels = new();
    private readonly AiTranslationService _aiTranslationService;
    private readonly NexusDownloadService _nexusDownloadService;
    private readonly GitHubDownloadSettingsService _gitHubDownloadSettingsService = new();
    private bool _settingTheme;
    private bool _settingAccentPreset;
    private bool _settingGitHubDownloadMode;
    private bool _gitHubDownloadBusy;
    private bool _nexusConfigured;
    private bool _aiConfigured;
    private bool _aiApiKeyConfigured;
    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    public SettingsPage()
    {
        _aiTranslationService = new AiTranslationService(_credentialService);
        _nexusDownloadService = new NexusDownloadService(_credentialService);
        InitializeComponent();
        AiModelPicker.ItemsSource = _availableModels;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DataContext = e.Parameter;
        SyncFields();
        _ = LoadServiceSettingsAsync();
    }

    private async Task LoadServiceSettingsAsync()
    {
        SetNexusBusy(true);
        SetAiBusy(true);
        SetGitHubDownloadBusy(true);
        try
        {
            try
            {
                _nexusConfigured = !string.IsNullOrWhiteSpace(
                    _credentialService.Read(CredentialService.NexusApiKeyTarget));
                UpdateNexusStatus();
            }
            catch (Exception error)
            {
                ShowNexusMessage("无法读取 Nexus API Key", FormatError(error), InfoBarSeverity.Error);
            }

            try
            {
                var status = await _aiTranslationService.GetStatusAsync();
                ApplyAiStatus(status);
            }
            catch (Exception error)
            {
                ShowAiMessage("无法读取 AI 翻译配置", FormatError(error), InfoBarSeverity.Error);
            }

            try
            {
                ApplyGitHubDownloadSettings(await _gitHubDownloadSettingsService.LoadAsync());
            }
            catch (Exception error)
            {
                ShowGitHubDownloadMessage("无法读取 GitHub 下载设置", FormatError(error), InfoBarSeverity.Error);
            }
        }
        finally
        {
            SetNexusBusy(false);
            SetAiBusy(false);
            SetGitHubDownloadBusy(false);
        }
    }

    private void ApplyGitHubDownloadSettings(GitHubDownloadSettings settings)
    {
        _settingGitHubDownloadMode = true;
        var isPreset = settings.Mode == GitHubDownloadMode.Custom
            && string.Equals(
                settings.CustomPrefix,
                GitHubDownloadSettingsService.GhProxyPreset,
                StringComparison.OrdinalIgnoreCase);
        GitHubDownloadModeComboBox.SelectedIndex = settings.Mode == GitHubDownloadMode.Direct
            ? 0
            : isPreset ? 1 : 2;
        GitHubMirrorPrefixTextBox.Text = settings.CustomPrefix ?? string.Empty;
        _settingGitHubDownloadMode = false;
        UpdateGitHubDownloadModeFields();
        GitHubDownloadStatusTextBlock.Text = settings.Mode == GitHubDownloadMode.Direct
            ? "GitHub 直连"
            : isPreset ? "预置镜像" : "自定义镜像";
    }

    private void OnGitHubDownloadModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingGitHubDownloadMode)
        {
            return;
        }
        var mode = (GitHubDownloadModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (mode == "gh-proxy")
        {
            GitHubMirrorPrefixTextBox.Text = GitHubDownloadSettingsService.GhProxyPreset;
        }
        else if (mode == "direct")
        {
            GitHubMirrorPrefixTextBox.Text = string.Empty;
        }
        UpdateGitHubDownloadModeFields();
    }

    private void UpdateGitHubDownloadModeFields()
    {
        var mode = (GitHubDownloadModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        GitHubMirrorPrefixTextBox.Visibility = mode == "direct" ? Visibility.Collapsed : Visibility.Visible;
        GitHubMirrorPrefixTextBox.IsEnabled = !_gitHubDownloadBusy && mode == "custom";
    }

    private async void OnGitHubDownloadSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetGitHubDownloadBusy(true);
            var mode = (GitHubDownloadModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "direct";
            var settings = mode == "direct"
                ? GitHubDownloadSettings.Direct
                : new GitHubDownloadSettings(
                    GitHubDownloadMode.Custom,
                    mode == "gh-proxy"
                        ? GitHubDownloadSettingsService.GhProxyPreset
                        : GitHubMirrorPrefixTextBox.Text);
            var saved = await _gitHubDownloadSettingsService.SaveAsync(settings);
            ApplyGitHubDownloadSettings(saved);
            ShowGitHubDownloadMessage(
                "GitHub 下载方式已保存",
                saved.Mode == GitHubDownloadMode.Direct
                    ? "SMAPI 安装包将从 GitHub 官方地址下载。"
                    : $"SMAPI 安装包将通过 {saved.CustomPrefix} 代理下载。",
                InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            ShowGitHubDownloadMessage("GitHub 下载设置保存失败", FormatError(error), InfoBarSeverity.Error);
        }
        finally
        {
            SetGitHubDownloadBusy(false);
        }
    }

    private async void OnGitHubDownloadResetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetGitHubDownloadBusy(true);
            ApplyGitHubDownloadSettings(await _gitHubDownloadSettingsService.ClearAsync());
            ShowGitHubDownloadMessage("已恢复 GitHub 直连", string.Empty, InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            ShowGitHubDownloadMessage("无法恢复 GitHub 直连", FormatError(error), InfoBarSeverity.Error);
        }
        finally
        {
            SetGitHubDownloadBusy(false);
        }
    }

    private void SetGitHubDownloadBusy(bool busy)
    {
        _gitHubDownloadBusy = busy;
        GitHubDownloadModeComboBox.IsEnabled = !busy;
        GitHubDownloadSaveButton.IsEnabled = !busy;
        GitHubDownloadResetButton.IsEnabled = !busy;
        if (busy)
        {
            GitHubDownloadStatusTextBlock.Text = "处理中";
        }
        UpdateGitHubDownloadModeFields();
    }

    private void ShowGitHubDownloadMessage(string title, string message, InfoBarSeverity severity)
    {
        GitHubDownloadInfoBar.Title = title;
        GitHubDownloadInfoBar.Message = message;
        GitHubDownloadInfoBar.Severity = severity;
        GitHubDownloadInfoBar.IsOpen = true;
    }

    private void ApplyAiStatus(AiTranslationStatus status)
    {
        _aiConfigured = status.Configured;
        _aiApiKeyConfigured = status.ApiKeyConfigured;
        AiBaseUrlTextBox.Text = status.BaseUrl ?? string.Empty;
        AiModelIdTextBox.Text = status.ModelId ?? string.Empty;
        AiStatusTextBlock.Text = status.Configured
            ? "已配置"
            : status.ApiKeyConfigured
                ? "仅密钥"
                : "未配置";
        UpdateHttpWarning();
        AiClearButton.IsEnabled = status.Configured || status.ApiKeyConfigured;
    }

    private async void OnNexusSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetNexusBusy(true);
            var key = NormalizeNexusApiKey(NexusApiKeyPasswordBox.Password);
            await _nexusDownloadService.ValidateApiKeyAsync(key);
            _credentialService.Write(
                CredentialService.NexusApiKeyTarget,
                key,
                "nexus-api-key");
            _nexusConfigured = true;
            NexusApiKeyPasswordBox.Password = string.Empty;
            UpdateNexusStatus();
            ShowNexusMessage(
                "Nexus API Key 已保存",
                "密钥已写入 Windows 凭据管理器，不会保存到配置文件。",
                InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            ShowNexusMessage("Nexus API Key 保存失败", FormatError(error), InfoBarSeverity.Error);
        }
        finally
        {
            SetNexusBusy(false);
        }
        await Task.CompletedTask;
    }

    private async void OnNexusClearClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetNexusBusy(true);
            _credentialService.Delete(CredentialService.NexusApiKeyTarget);
            _nexusConfigured = false;
            NexusApiKeyPasswordBox.Password = string.Empty;
            UpdateNexusStatus();
            ShowNexusMessage("Nexus API Key 已清除", string.Empty, InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            ShowNexusMessage("无法清除 Nexus API Key", FormatError(error), InfoBarSeverity.Error);
        }
        finally
        {
            SetNexusBusy(false);
        }
        await Task.CompletedTask;
    }

    private void OnOpenNexusKeysClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(NexusApiKeysUrl) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            ShowNexusMessage("无法打开 Nexus 设置页", FormatError(error), InfoBarSeverity.Error);
        }
    }

    private void OnAiBaseUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHttpWarning();
        _availableModels.Clear();
        AiModelPicker.SelectedItem = null;
        AiModelCountTextBlock.Visibility = Visibility.Collapsed;
    }

    private void OnAiModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiModelPicker.SelectedItem is AiTranslationModel model)
        {
            AiModelIdTextBox.Text = model.Id;
        }
    }

    private async void OnAiGetModelsClick(object sender, RoutedEventArgs e)
    {
        AiTranslationRequestMetadata? preview = null;
        try
        {
            preview = _aiTranslationService.CreateModelsRequestPreview(
                AiBaseUrlTextBox.Text,
                EmptyToNull(AiApiKeyPasswordBox.Password));
            ShowActivityLoading(preview);
            SetAiBusy(true);
            var result = await _aiTranslationService.ListModelsAsync(
                AiBaseUrlTextBox.Text,
                EmptyToNull(AiApiKeyPasswordBox.Password));

            _availableModels.Clear();
            foreach (var model in result.Models)
            {
                _availableModels.Add(model);
            }
            var requestedModel = AiModelIdTextBox.Text.Trim();
            AiModelPicker.SelectedItem = _availableModels.FirstOrDefault(
                model => model.Id.Equals(requestedModel, StringComparison.Ordinal));
            AiModelCountTextBlock.Text = result.Models.Count == 0
                ? "接口未返回可用模型，仍可手动填写 Model ID。"
                : $"已加载 {result.Models.Count} 个模型，可直接选择。";
            AiModelCountTextBlock.Visibility = Visibility.Visible;
            ShowActivitySuccess(result.Request, result.Response);
            ShowAiMessage(
                result.Models.Count == 0 ? "未发现模型" : "模型列表已更新",
                AiModelCountTextBlock.Text,
                result.Models.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            ShowActivityError(preview, error);
            ShowAiMessage("模型列表请求失败", FormatError(error), InfoBarSeverity.Error);
        }
        finally
        {
            SetAiBusy(false);
        }
    }

    private async void OnAiTestClick(object sender, RoutedEventArgs e)
    {
        AiTranslationRequestMetadata? preview = null;
        try
        {
            preview = _aiTranslationService.CreateTestRequestPreview(
                AiBaseUrlTextBox.Text,
                AiModelIdTextBox.Text,
                EmptyToNull(AiApiKeyPasswordBox.Password));
            ShowActivityLoading(preview);
            SetAiBusy(true);
            var result = await _aiTranslationService.TestConnectionAsync(
                AiBaseUrlTextBox.Text,
                AiModelIdTextBox.Text,
                EmptyToNull(AiApiKeyPasswordBox.Password));
            AiModelIdTextBox.Text = result.ModelId;
            AiModelPicker.SelectedItem = _availableModels.FirstOrDefault(
                model => model.Id.Equals(result.ModelId, StringComparison.Ordinal));
            var savedStatus = await _aiTranslationService.SaveSettingsAsync(
                AiBaseUrlTextBox.Text,
                result.ModelId,
                EmptyToNull(AiApiKeyPasswordBox.Password));
            AiApiKeyPasswordBox.Password = string.Empty;
            ApplyAiStatus(savedStatus);
            ShowActivitySuccess(result.Request, result.Response);
            ShowAiMessage(
                "AI 接口测试成功，配置已应用",
                $"{result.Message}；下载页翻译会使用刚才测试的 Base URL 与模型。",
                InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            ShowActivityError(preview, error);
            ShowAiMessage("AI 接口测试失败", FormatError(error), InfoBarSeverity.Error);
        }
        finally
        {
            SetAiBusy(false);
        }
    }

    private async void OnAiSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetAiBusy(true);
            var status = await _aiTranslationService.SaveSettingsAsync(
                AiBaseUrlTextBox.Text,
                AiModelIdTextBox.Text,
                EmptyToNull(AiApiKeyPasswordBox.Password));
            AiApiKeyPasswordBox.Password = string.Empty;
            ApplyAiStatus(status);
            ShowAiMessage(
                "AI 翻译配置已保存",
                "Base URL 与 Model ID 已保存；API Key 位于 Windows 凭据管理器。",
                InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            ShowAiMessage("AI 翻译配置保存失败", FormatError(error), InfoBarSeverity.Error);
        }
        finally
        {
            SetAiBusy(false);
        }
    }

    private async void OnAiClearClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetAiBusy(true);
            var status = await _aiTranslationService.ClearSettingsAsync();
            ApplyAiStatus(status);
            AiApiKeyPasswordBox.Password = string.Empty;
            _availableModels.Clear();
            AiModelPicker.SelectedItem = null;
            AiModelCountTextBlock.Visibility = Visibility.Collapsed;
            AiActivityPanel.Visibility = Visibility.Collapsed;
            ShowAiMessage("AI 翻译配置已清除", string.Empty, InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            ShowAiMessage("无法清除 AI 翻译配置", FormatError(error), InfoBarSeverity.Error);
        }
        finally
        {
            SetAiBusy(false);
        }
    }

    private void ShowActivityLoading(AiTranslationRequestMetadata request)
    {
        AiActivityPanel.Visibility = Visibility.Visible;
        AiActivityProgressRing.IsActive = true;
        AiActivityProgressRing.Visibility = Visibility.Visible;
        AiActivityTitleTextBlock.Text = $"正在向 {request.Endpoint} 发起请求...";
        AiRequestUrlTextBlock.Text = request.Endpoint;
        AiMaskedRequestTextBox.Text = request.MaskedRequest;
        AiResponseTextBox.Text = string.Empty;
        AiResponsePanel.Visibility = Visibility.Collapsed;
    }

    private void ShowActivitySuccess(
        AiTranslationRequestMetadata request,
        AiTranslationResponseMetadata response)
    {
        AiActivityPanel.Visibility = Visibility.Visible;
        AiActivityProgressRing.IsActive = false;
        AiActivityProgressRing.Visibility = Visibility.Collapsed;
        AiActivityTitleTextBlock.Text = "请求完成";
        AiRequestUrlTextBlock.Text = request.Endpoint;
        AiMaskedRequestTextBox.Text = request.MaskedRequest;
        AiResponseTextBox.Text = string.IsNullOrWhiteSpace(response.Content)
            ? response.Summary
            : $"{response.Summary}\r\n\r\n{response.Content}";
        AiResponsePanel.Visibility = Visibility.Visible;
    }

    private void ShowActivityError(AiTranslationRequestMetadata? request, Exception error)
    {
        AiActivityPanel.Visibility = Visibility.Visible;
        AiActivityProgressRing.IsActive = false;
        AiActivityProgressRing.Visibility = Visibility.Collapsed;
        AiActivityTitleTextBlock.Text = "请求失败";
        AiRequestUrlTextBlock.Text = request?.Endpoint
            ?? RedactSensitiveText(AiBaseUrlTextBox.Text.Trim());
        AiMaskedRequestTextBox.Text = request?.MaskedRequest ?? "请求未发出：参数校验失败";
        AiResponseTextBox.Text = FormatError(error);
        AiResponsePanel.Visibility = Visibility.Visible;
    }

    private void SetNexusBusy(bool busy)
    {
        NexusApiKeyPasswordBox.IsEnabled = !busy;
        NexusSaveButton.IsEnabled = !busy;
        NexusClearButton.IsEnabled = !busy && _nexusConfigured;
        if (busy)
        {
            NexusStatusTextBlock.Text = "处理中";
        }
        else
        {
            UpdateNexusStatus();
        }
    }

    private void UpdateNexusStatus()
    {
        NexusStatusTextBlock.Text = _nexusConfigured ? "已配置" : "未配置";
        NexusClearButton.IsEnabled = _nexusConfigured;
    }

    private void SetAiBusy(bool busy)
    {
        AiBaseUrlTextBox.IsEnabled = !busy;
        AiApiKeyPasswordBox.IsEnabled = !busy;
        AiModelIdTextBox.IsEnabled = !busy;
        AiModelPicker.IsEnabled = !busy && _availableModels.Count > 0;
        AiGetModelsButton.IsEnabled = !busy;
        AiTestButton.IsEnabled = !busy;
        AiSaveButton.IsEnabled = !busy;
        AiClearButton.IsEnabled = !busy && (_aiConfigured || _aiApiKeyConfigured);
        if (busy)
        {
            AiStatusTextBlock.Text = "处理中";
        }
        else
        {
            AiStatusTextBlock.Text = _aiConfigured
                ? "已配置"
                : _aiApiKeyConfigured
                    ? "仅密钥"
                    : "未配置";
        }
    }

    private void UpdateHttpWarning()
    {
        AiHttpWarningInfoBar.IsOpen = Uri.TryCreate(
                AiBaseUrlTextBox.Text.Trim(),
                UriKind.Absolute,
                out var uri)
            && uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase);
    }

    private void ShowNexusMessage(string title, string message, InfoBarSeverity severity)
    {
        NexusInfoBar.Title = title;
        NexusInfoBar.Message = message;
        NexusInfoBar.Severity = severity;
        NexusInfoBar.IsOpen = true;
    }

    private void ShowAiMessage(string title, string message, InfoBarSeverity severity)
    {
        AiInfoBar.Title = title;
        AiInfoBar.Message = message;
        AiInfoBar.Severity = severity;
        AiInfoBar.IsOpen = true;
    }

    private static string NormalizeNexusApiKey(string value)
    {
        value = value.Trim();
        if (value.Length is < 20 or > 512)
        {
            throw new ArgumentException("Nexus Personal API Key 长度应为 20-512 个字符");
        }
        if (value.Any(character => character is < (char)0x21 or > (char)0x7e))
        {
            throw new ArgumentException("Nexus Personal API Key 只能包含可见 ASCII 字符且不能包含空格");
        }
        return value;
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private string FormatError(Exception error)
    {
        if (error is AggregateException aggregate)
        {
            return RedactSensitiveText(
                string.Join("；", aggregate.Flatten().InnerExceptions.Select(item => item.Message)));
        }
        return RedactSensitiveText(error.Message);
    }

    private string RedactSensitiveText(string value)
    {
        var secrets = new List<string?>
        {
            EmptyToNull(NexusApiKeyPasswordBox.Password),
            EmptyToNull(AiApiKeyPasswordBox.Password),
        };
        try
        {
            secrets.Add(_credentialService.Read(CredentialService.NexusApiKeyTarget));
            secrets.Add(_credentialService.Read(CredentialService.AiTranslationApiKeyTarget));
        }
        catch
        {
            // The original operation already reports credential-store failures.
        }
        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)).Distinct())
        {
            value = value.Replace(secret!, "[已隐藏]", StringComparison.Ordinal);
        }
        return value;
    }

    private void SyncFields()
    {
        if (ViewModel is null)
        {
            return;
        }
        GamePathTextBox.Text = ViewModel.Installation?.Path ?? string.Empty;
        SmapiArgumentsTextBox.Text = ViewModel.SmapiArguments;
        VanillaArgumentsTextBox.Text = ViewModel.VanillaArguments;
        _settingTheme = true;
        ThemeComboBox.SelectedIndex = ViewModel.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        _settingTheme = false;

        _settingAccentPreset = true;
        AccentPresetComboBox.SelectedItem = AccentPresetComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                ViewModel.AccentPreset,
                StringComparison.Ordinal))
            ?? AccentPresetComboBox.Items[0];
        _settingAccentPreset = false;
    }

    private async void OnChoosePathClick(object sender, RoutedEventArgs e)
    {
        if (await App.MainWindow.ChooseGamePathAsync())
        {
            SyncFields();
            ShowPathSuccess();
        }
    }

    private async void OnAutoDetectClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }
        try
        {
            await ViewModel.AutoDetectGameAsync();
            SyncFields();
            ShowPathSuccess();
        }
        catch (Exception error)
        {
            ShowPathError(error.Message);
        }
    }

    private async void OnSavePathClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }
        try
        {
            await ViewModel.SetGamePathAsync(GamePathTextBox.Text);
            SyncFields();
            ShowPathSuccess();
        }
        catch (Exception error)
        {
            ShowPathError(error.Message);
        }
    }

    private async void OnArgumentsLostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }
        ViewModel.SmapiArguments = SmapiArgumentsTextBox.Text;
        ViewModel.VanillaArguments = VanillaArgumentsTextBox.Text;
        try
        {
            await ViewModel.SaveSettingsAsync();
        }
        catch (Exception error)
        {
            App.MainWindow.ShowMessage("无法保存启动参数", error.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnSmapiArgumentPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null
            || SmapiArgumentPresetComboBox.SelectedItem is not ComboBoxItem item
            || item.Tag is not string preset)
        {
            return;
        }

        var current = UiSettingsService.ParseArguments(SmapiArgumentsTextBox.Text);
        if (!current.Contains(preset, StringComparer.Ordinal))
        {
            SmapiArgumentsTextBox.Text = string.IsNullOrWhiteSpace(SmapiArgumentsTextBox.Text)
                ? preset
                : $"{SmapiArgumentsTextBox.Text.TrimEnd()} {preset}";
            ViewModel.SmapiArguments = SmapiArgumentsTextBox.Text;
            try
            {
                await ViewModel.SaveSettingsAsync();
            }
            catch (Exception error)
            {
                App.MainWindow.ShowMessage("无法保存启动参数", error.Message, InfoBarSeverity.Error);
            }
        }
        SmapiArgumentPresetComboBox.SelectedItem = null;
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingTheme || ViewModel is null || ThemeComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }
        ViewModel.Theme = item.Tag as string ?? "system";
    }

    private void OnAccentPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingAccentPreset
            || ViewModel is null
            || AccentPresetComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }
        ViewModel.AccentPreset = item.Tag as string
            ?? UiSettingsService.DefaultAccentPresetId;
    }

    private void ShowPathSuccess()
    {
        PathInfoBar.Title = "游戏路径已获取";
        PathInfoBar.Message = ViewModel?.GamePathText ?? string.Empty;
        PathInfoBar.Severity = InfoBarSeverity.Success;
        PathInfoBar.IsOpen = true;
    }

    private void ShowPathError(string message)
    {
        PathInfoBar.Title = "路径无效";
        PathInfoBar.Message = message;
        PathInfoBar.Severity = InfoBarSeverity.Error;
        PathInfoBar.IsOpen = true;
    }
}
