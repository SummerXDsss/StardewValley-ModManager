using System.Text;
using System.Text.Json;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed class UiSettingsService
{
    public const string DefaultAccentPresetId = "windows-11-blue";

    public static IReadOnlyList<AccentColorPreset> AccentColorPresets { get; } =
    [
        new("windows-11-blue", "Windows 11 蓝", "#005FB8", "#60CDFF"),
        new("windows-classic-blue", "Windows 经典蓝", "#003399", "#8AB4F8"),
        new("xp-olive", "Windows XP 橄榄", "#5F6F22", "#C3D36B"),
        new("zune", "Zune 棕橙", "#A33D11", "#FF9F70"),
        new("windows-purple", "Windows 紫红", "#8A157E", "#F08CDA"),
        new("graphite", "石墨中性", "#525E64", "#B8C4C9"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _configPath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public UiSettingsService(string? configPath = null)
    {
        _configPath = string.IsNullOrWhiteSpace(configPath)
            ? AppPaths.UiSettingsConfig
            : Path.GetFullPath(configPath);
    }

    public async Task<UiSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(_configPath)
                || new FileInfo(_configPath).Length > 64 * 1024)
            {
                return new UiSettings();
            }

            var json = await File.ReadAllTextAsync(_configPath);
            return JsonSerializer.Deserialize<UiSettings>(json, JsonOptions) is { Version: 1 } settings
                ? Normalize(settings)
                : new UiSettings();
        }
        catch
        {
            return new UiSettings();
        }
    }

    public async Task SaveAsync(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);

        await _saveGate.WaitAsync();
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_configPath)
                ?? throw new InvalidOperationException("界面设置文件缺少父目录。");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_configPath)}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _configPath, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // A failed cleanup must not hide the original save error.
                }
            }
            _saveGate.Release();
        }
    }

    public static string NormalizeTheme(string? value)
    {
        return value is "system" or "light" or "dark" ? value : "system";
    }

    public static string NormalizeAccentPreset(string? value)
    {
        return AccentColorPresets.Any(
            preset => string.Equals(preset.Id, value, StringComparison.Ordinal))
            ? value!
            : DefaultAccentPresetId;
    }

    public static AccentColorPreset GetAccentColorPreset(string? value)
    {
        var normalized = NormalizeAccentPreset(value);
        return AccentColorPresets.First(preset => preset.Id == normalized);
    }

    private static UiSettings Normalize(UiSettings settings)
    {
        settings.Theme = NormalizeTheme(settings.Theme);
        settings.AccentPreset = NormalizeAccentPreset(settings.AccentPreset);
        settings.SmapiArguments ??= string.Empty;
        settings.VanillaArguments ??= string.Empty;
        if (!Enum.IsDefined(settings.LaunchPreference))
        {
            settings.LaunchPreference = LaunchTarget.Smapi;
        }
        return settings;
    }

    public static IReadOnlyList<string> ParseArguments(string text)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var escaping = false;
        foreach (var character in text)
        {
            if (escaping)
            {
                current.Append(character);
                escaping = false;
                continue;
            }

            if (character == '\\' && quoted)
            {
                escaping = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(character);
        }

        if (escaping)
        {
            current.Append('\\');
        }
        if (quoted)
        {
            throw new FormatException("启动参数中存在未闭合的引号。");
        }
        if (current.Length > 0)
        {
            arguments.Add(current.ToString());
        }
        if (arguments.Count > 32 || arguments.Any(argument => argument.Length > 512))
        {
            throw new FormatException("启动参数过多或单个参数过长。");
        }

        return arguments;
    }
}

public sealed class UiSettings
{
    public int Version { get; set; } = 1;
    public bool RememberLaunch { get; set; } = true;
    public LaunchTarget LaunchPreference { get; set; } = LaunchTarget.Smapi;
    public string SmapiArguments { get; set; } = string.Empty;
    public string VanillaArguments { get; set; } = string.Empty;
    public string Theme { get; set; } = "system";
    public string AccentPreset { get; set; } = UiSettingsService.DefaultAccentPresetId;
    public bool FirstRunCompleted { get; set; }
    public bool AutoInstallStarterMod { get; set; } = true;
}

public sealed record AccentColorPreset(
    string Id,
    string DisplayName,
    string LightColor,
    string DarkColor);
