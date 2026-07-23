using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed partial class GameDiscoveryService
{
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPathPrefix = @"\\?\UNC\";

    private static readonly string[] GameExecutables =
    [
        "Stardew Valley.exe",
        "StardewValley.exe",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _gamePathConfig;

    public GameDiscoveryService()
        : this(AppPaths.GamePathConfig)
    {
    }

    internal GameDiscoveryService(string gamePathConfig)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePathConfig);
        _gamePathConfig = gamePathConfig;
    }

    public async Task<GameInstallation?> LoadSavedAsync()
    {
        if (!File.Exists(_gamePathConfig))
        {
            return null;
        }

        var info = new FileInfo(_gamePathConfig);
        if (info.Length > 16 * 1024)
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<StoredGamePath>(
            await File.ReadAllTextAsync(_gamePathConfig),
            JsonOptions);
        if (stored is not { Version: 1 })
        {
            return null;
        }

        var installation = await Task.Run(() => Inspect(stored.Path));
        if (installation is not null
            && !string.Equals(stored.Path, installation.Path, StringComparison.Ordinal))
        {
            await SaveAsync(installation);
        }
        return installation;
    }

    public Task<GameInstallation?> DetectAsync()
    {
        return Task.Run(() => CandidatePaths()
            .Select(Inspect)
            .FirstOrDefault(candidate => candidate is not null));
    }

    public Task<GameInstallation?> InspectAsync(string path)
    {
        return Task.Run(() => Inspect(path));
    }

    public async Task SaveAsync(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var configDirectory = Path.GetDirectoryName(_gamePathConfig);
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            Directory.CreateDirectory(configDirectory);
        }

        var path = NormalizeWindowsPath(installation.Path.Trim().Trim('"'));
        var json = JsonSerializer.Serialize(new StoredGamePath(1, path), JsonOptions);
        await File.WriteAllTextAsync(_gamePathConfig, json);
    }

    internal static string NormalizeWindowsPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.StartsWith(ExtendedUncPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[ExtendedUncPathPrefix.Length..];
        }

        if (!path.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var unprefixed = path[ExtendedPathPrefix.Length..];
        return unprefixed.Length >= 3
            && char.IsAsciiLetter(unprefixed[0])
            && unprefixed[1] == ':'
            && (unprefixed[2] == Path.DirectorySeparatorChar
                || unprefixed[2] == Path.AltDirectorySeparatorChar)
                ? unprefixed
                : path;
    }

    private static GameInstallation? Inspect(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            var inputPath = NormalizeWindowsPath(rawPath.Trim().Trim('"'));
            var path = NormalizeWindowsPath(Path.GetFullPath(inputPath));
            if (!Directory.Exists(path))
            {
                return null;
            }

            var executablePath = GameExecutables
                .Select(name => Path.Combine(path, name))
                .FirstOrDefault(File.Exists);
            if (executablePath is null)
            {
                return null;
            }

            var fileVersion = FileVersionInfo.GetVersionInfo(executablePath);
            var version = NormalizeGameVersion(fileVersion.ProductVersion)
                ?? NormalizeGameVersion(fileVersion.FileVersion);
            return new GameInstallation(
                path,
                Path.GetFileName(executablePath),
                InferStore(path),
                string.IsNullOrWhiteSpace(version) ? null : version);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var candidates = new List<string>();
        foreach (var root in SteamRoots())
        {
            candidates.AddRange(SteamGamePaths(root));
        }

        candidates.AddRange(GogGamePaths());
        candidates.AddRange(
        [
            @"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley",
            @"C:\Program Files\Steam\steamapps\common\Stardew Valley",
            @"C:\GOG Games\Stardew Valley",
            @"C:\Program Files (x86)\GOG Galaxy\Games\Stardew Valley",
            @"C:\Program Files\ModifiableWindowsApps\Stardew Valley",
        ]);

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> SteamRoots()
    {
        var roots = new List<string>();
        AddRegistryString(roots, RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath");
        AddRegistryString(roots, RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "InstallPath");

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            AddRegistryString(roots, RegistryHive.LocalMachine, view, @"SOFTWARE\Valve\Steam", "InstallPath");
        }

        roots.Add(@"C:\Program Files (x86)\Steam");
        roots.Add(@"C:\Program Files\Steam");
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> SteamGamePaths(string steamRoot)
    {
        var libraries = new List<string> { steamRoot };
        var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile) && new FileInfo(libraryFile).Length <= 1024 * 1024)
        {
            try
            {
                var content = File.ReadAllText(libraryFile);
                foreach (Match match in VdfPathRegex().Matches(content))
                {
                    var path = match.Groups["path"].Value.Replace(@"\\", @"\");
                    if (Path.IsPathFullyQualified(path))
                    {
                        libraries.Add(path);
                    }
                }
            }
            catch
            {
                // A malformed Steam library file should not block the default locations.
            }
        }

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var steamApps = Path.Combine(library, "steamapps");
            var installDirectory = "Stardew Valley";
            var manifest = Path.Combine(steamApps, "appmanifest_413150.acf");
            if (File.Exists(manifest))
            {
                try
                {
                    var match = InstallDirRegex().Match(File.ReadAllText(manifest));
                    if (match.Success && IsSafeInstallDirectory(match.Groups["dir"].Value))
                    {
                        installDirectory = match.Groups["dir"].Value;
                    }
                }
                catch
                {
                    // Fall back to Steam's standard install directory name.
                }
            }

            yield return Path.Combine(steamApps, "common", installDirectory);
        }
    }

    private static IEnumerable<string> GogGamePaths()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey? root = null;
                try
                {
                    root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"SOFTWARE\GOG.com\Games");
                    if (root is null)
                    {
                        continue;
                    }

                    foreach (var keyName in root.GetSubKeyNames())
                    {
                        using var game = root.OpenSubKey(keyName);
                        var gameName = game?.GetValue("gameName") as string;
                        if (keyName != "1453375253" && !string.Equals(gameName, "Stardew Valley", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        foreach (var valueName in new[] { "path", "workingDir" })
                        {
                            if (game?.GetValue(valueName) is string path)
                            {
                                yield return path.Trim().Trim('"');
                            }
                        }
                    }
                }
                finally
                {
                    root?.Dispose();
                }
            }
        }
    }

    private static void AddRegistryString(
        ICollection<string> values,
        RegistryHive hive,
        RegistryView view,
        string keyPath,
        string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath);
            if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.Trim().Trim('"'));
            }
        }
        catch
        {
            // Registry detection is best-effort.
        }
    }

    private static bool IsSafeInstallDirectory(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !Path.IsPathFullyQualified(value)
            && !value.Contains(':')
            && value.Split('/', '\\').All(part => part is not "" and not "." and not "..");
    }

    private static string InferStore(string path)
    {
        if (path.Contains("steamapps", StringComparison.OrdinalIgnoreCase))
        {
            return "Steam";
        }

        if (path.Contains("gog", StringComparison.OrdinalIgnoreCase))
        {
            return "GOG";
        }

        if (path.Contains("ModifiableWindowsApps", StringComparison.OrdinalIgnoreCase)
            || path.Contains("XboxGames", StringComparison.OrdinalIgnoreCase))
        {
            return "Xbox App";
        }

        return "手动安装";
    }

    private static string? NormalizeGameVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var match = GameVersionRegex().Match(value);
        return match.Success ? match.Value : value.Trim().Trim(',', ' ');
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPathRegex();

    [GeneratedRegex("\\\"installdir\\\"\\s+\\\"(?<dir>[^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    [GeneratedRegex("\\d+\\.\\d+\\.\\d+(?:\\.\\d+)?")]
    private static partial Regex GameVersionRegex();

    private sealed record StoredGamePath(int Version, string Path);
}
