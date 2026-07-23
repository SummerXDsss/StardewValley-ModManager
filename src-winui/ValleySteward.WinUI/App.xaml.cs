using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ValleySteward.WinUI.Services;
using Windows.UI;
using WinRT.Interop;

namespace ValleySteward.WinUI;

public partial class App : Application
{
    private const int ShowWindowRestore = 9;
    private const int MaximumWindowRecoveryAttempts = 2;

    public static MainWindow MainWindow { get; private set; } = null!;
    private int _windowRecoveryAttempts;

    public App()
    {
        WriteStartupLog("App.ctor before InitializeComponent");
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        WriteStartupLog("App.ctor after InitializeComponent");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            WriteStartupLog("OnLaunched before MainWindow");
            MainWindow = new MainWindow();
            WriteStartupLog("OnLaunched before Activate");
            MainWindow.Activate();
            WriteStartupLog("OnLaunched after Activate");
            _ = VerifyMainWindowAsync();
        }
        catch (Exception error)
        {
            WriteCrashLog("OnLaunched", error);
            throw;
        }
    }

    public static void ApplyAccentPreset(string presetId, ElementTheme actualTheme)
    {
        var preset = UiSettingsService.GetAccentColorPreset(presetId);
        var dark = actualTheme == ElementTheme.Dark;
        var accent = ParseColor(dark ? preset.DarkColor : preset.LightColor);
        var onAccent = dark
            ? Color.FromArgb(255, 0, 0, 0)
            : Color.FromArgb(255, 255, 255, 255);
        var resources = Current.Resources;

        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = Mix(accent, 255, 255, 255, 0.12);
        resources["SystemAccentColorLight2"] = Mix(accent, 255, 255, 255, 0.24);
        resources["SystemAccentColorLight3"] = Mix(accent, 255, 255, 255, 0.36);
        resources["SystemAccentColorDark1"] = Mix(accent, 0, 0, 0, 0.12);
        resources["SystemAccentColorDark2"] = Mix(accent, 0, 0, 0, 0.24);
        resources["SystemAccentColorDark3"] = Mix(accent, 0, 0, 0, 0.36);

        SetBrush(resources, "AccentFillColorDefaultBrush", accent);
        SetBrush(resources, "AccentFillColorSecondaryBrush", WithAlpha(accent, 230));
        SetBrush(resources, "AccentFillColorTertiaryBrush", WithAlpha(accent, 204));
        SetBrush(resources, "AccentFillColorDisabledBrush", WithAlpha(accent, 54));
        SetBrush(resources, "AccentTextFillColorPrimaryBrush", accent);
        SetBrush(resources, "AccentTextFillColorSecondaryBrush", WithAlpha(accent, 219));
        SetBrush(resources, "AccentTextFillColorTertiaryBrush", WithAlpha(accent, 153));
        SetBrush(resources, "AccentTextFillColorDisabledBrush", WithAlpha(accent, 92));
        SetBrush(resources, "AccentAAFillColorDefaultBrush", accent);
        SetBrush(resources, "AccentAAFillColorSecondaryBrush", WithAlpha(accent, 230));
        SetBrush(resources, "AccentAAFillColorTertiaryBrush", WithAlpha(accent, 204));
        SetBrush(resources, "TextOnAccentFillColorPrimaryBrush", onAccent);
        SetBrush(resources, "TextOnAccentFillColorSecondaryBrush", WithAlpha(onAccent, 179));
        SetBrush(resources, "TextOnAccentFillColorDisabledBrush", WithAlpha(onAccent, 135));
        SetBrush(resources, "NavigationViewSelectionIndicatorForeground", accent);
        SetBrush(resources, "ProgressRingForegroundThemeBrush", accent);
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        resources[key] = new SolidColorBrush(color);
    }

    private static Color ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length != 6)
        {
            throw new FormatException($"无效的主题颜色：{value}");
        }
        return Color.FromArgb(
            255,
            byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static Color Mix(Color source, byte red, byte green, byte blue, double amount)
    {
        return Color.FromArgb(
            source.A,
            (byte)Math.Round(source.R + ((red - source.R) * amount)),
            (byte)Math.Round(source.G + ((green - source.G) * amount)),
            (byte)Math.Round(source.B + ((blue - source.B) * amount)));
    }

    private static Color WithAlpha(Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog("UnhandledException", e.Exception ?? new InvalidOperationException(e.Message));
        e.Handled = false;
    }

    public static void WriteStartupLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "ValleySteward.WinUI.startup.log"),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Startup diagnostics must never affect the app.
        }
    }

    private async Task VerifyMainWindowAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        MainWindow.DispatcherQueue.TryEnqueue(VerifyMainWindowOnUiThread);
    }

    private void VerifyMainWindowOnUiThread()
    {
        var handle = GetMainWindowHandle();
        var exists = handle != IntPtr.Zero && IsWindow(handle);
        var visible = exists && IsWindowVisible(handle);
        WriteStartupLog($"VerifyMainWindow handle=0x{handle.ToInt64():X} exists={exists} visible={visible}");

        if (exists && visible)
        {
            ShowWindow(handle, ShowWindowRestore);
            SetForegroundWindow(handle);
            return;
        }

        if (_windowRecoveryAttempts >= MaximumWindowRecoveryAttempts)
        {
            WriteStartupLog("VerifyMainWindow recovery limit reached");
            return;
        }

        _windowRecoveryAttempts++;
        WriteStartupLog($"VerifyMainWindow recreating window attempt={_windowRecoveryAttempts}");
        MainWindow = new MainWindow();
        MainWindow.Activate();
        _ = VerifyMainWindowAsync();
    }

    private static IntPtr GetMainWindowHandle()
    {
        try
        {
            return WindowNative.GetWindowHandle(MainWindow);
        }
        catch (Exception error)
        {
            WriteStartupLog($"GetMainWindowHandle failed: {error.GetType().Name}: {error.Message}");
            return IntPtr.Zero;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void WriteCrashLog(string stage, Exception error)
    {
        try
        {
            var text = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] {stage}")
                .AppendLine(error.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ValleySteward.WinUI.log"), text);
        }
        catch
        {
            // Logging must never hide the original startup failure.
        }
    }
}
