using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed partial class SmapiService
{
    private static readonly string[] RuntimeFiles =
    [
        "StardewModdingAPI.dll",
        "StardewModdingAPI.exe",
        "StardewModdingAPI",
        "StardewModdingAPI.bin.osx",
    ];
    private readonly SmapiReleaseService _releaseService = new();
    private readonly SmapiInstallerService _installerService = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public Task<SmapiStatus> InspectAsync(string gamePath)
    {
        return Task.Run(() => Inspect(gamePath));
    }

    public Task<SmapiReleaseInfo> GetLatestReleaseAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        return _releaseService.GetLatestAsync(forceRefresh, cancellationToken);
    }

    public async Task<SmapiOperationResult> InstallLatestAsync(
        string gamePath,
        IProgress<SmapiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var canonicalGamePath = SmapiInstallerService.ValidateGamePath(gamePath);
            SmapiInstallerService.EnsureGameProcessesStopped(canonicalGamePath);
            var previous = Inspect(canonicalGamePath);

            progress?.Report(new SmapiOperationProgress(
                SmapiOperationStage.Checking,
                "正在读取 SMAPI 稳定版 Release…"));
            var release = await _releaseService.GetLatestAsync(
                forceRefresh: true,
                cancellationToken);
            var downloaded = await _releaseService.DownloadAsync(
                release,
                progress,
                cancellationToken);

            SmapiInstallerService.EnsureGameProcessesStopped(canonicalGamePath);
            var execution = await _installerService.ExecuteAsync(
                downloaded,
                canonicalGamePath,
                SmapiInstallerAction.Install,
                progress,
                cancellationToken);

            progress?.Report(new SmapiOperationProgress(
                SmapiOperationStage.Verifying,
                "正在复检 SMAPI 安装结果与版本…"));
            var current = Inspect(canonicalGamePath);
            var expectedVersion = NormalizeVersion(release.Version);
            if (!current.Installed)
            {
                throw new InvalidOperationException(
                    $"SMAPI 安装器退出代码为 {execution.ExitCode}，但没有检测到 SMAPI 可执行文件，不能判定安装成功。");
            }
            if (current.Version is null)
            {
                throw new InvalidOperationException(
                    $"SMAPI 安装器退出代码为 {execution.ExitCode}，但无法核验安装版本，不能判定安装成功。");
            }
            if (expectedVersion is null
                || !current.Version.Equals(expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SMAPI 安装器退出代码为 {execution.ExitCode}，但检测到版本 {current.Version}，预期为 {release.Version}。");
            }

            progress?.Report(new SmapiOperationProgress(
                SmapiOperationStage.Completed,
                $"SMAPI {current.Version} 已安装并通过复检。",
                100));
            return new SmapiOperationResult(
                true,
                previous.Version,
                current.Version,
                release.Version,
                execution.ExitCode,
                downloaded.DigestVerified,
                downloaded.UsedMirror,
                $"SMAPI {current.Version} 已安装并通过版本复检。");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<SmapiOperationResult> UninstallAsync(
        string gamePath,
        IProgress<SmapiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var canonicalGamePath = SmapiInstallerService.ValidateGamePath(gamePath);
            var previous = Inspect(canonicalGamePath);
            if (!previous.Installed)
            {
                throw new InvalidOperationException("所选游戏目录中没有安装 SMAPI。");
            }
            SmapiInstallerService.EnsureGameProcessesStopped(canonicalGamePath);

            progress?.Report(new SmapiOperationProgress(
                SmapiOperationStage.Checking,
                "正在读取 SMAPI 官方卸载器…"));
            var release = await _releaseService.GetLatestAsync(
                forceRefresh: true,
                cancellationToken);
            var downloaded = await _releaseService.DownloadAsync(
                release,
                progress,
                cancellationToken);

            SmapiInstallerService.EnsureGameProcessesStopped(canonicalGamePath);
            var execution = await _installerService.ExecuteAsync(
                downloaded,
                canonicalGamePath,
                SmapiInstallerAction.Uninstall,
                progress,
                cancellationToken);

            progress?.Report(new SmapiOperationProgress(
                SmapiOperationStage.Verifying,
                "正在复检 SMAPI 卸载结果…"));
            var current = Inspect(canonicalGamePath);
            if (current.Installed)
            {
                throw new InvalidOperationException(
                    $"SMAPI 卸载器退出代码为 {execution.ExitCode}，但 SMAPI 仍然存在，不能判定卸载成功。");
            }

            var remaining = RuntimeFiles
                .Where(fileName => File.Exists(Path.Combine(canonicalGamePath, fileName)))
                .ToArray();
            if (remaining.Length > 0)
            {
                throw new InvalidOperationException(
                    $"SMAPI 卸载器退出代码为 {execution.ExitCode}，但仍残留运行时文件：{string.Join("、", remaining)}。");
            }

            progress?.Report(new SmapiOperationProgress(
                SmapiOperationStage.Completed,
                "SMAPI 已卸载并通过复检。",
                100));
            return new SmapiOperationResult(
                false,
                previous.Version,
                null,
                release.Version,
                execution.ExitCode,
                downloaded.DigestVerified,
                downloaded.UsedMirror,
                "SMAPI 已卸载，运行时文件复检通过。");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = VersionRegex().Match(value.Trim());
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups["major"].Value, out var major)
            || !int.TryParse(match.Groups["minor"].Value, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, out var patch))
        {
            return null;
        }

        var fourthText = match.Groups["fourth"].Value;
        var numeric = $"{major}.{minor}.{patch}";
        if (fourthText.Length > 0)
        {
            if (!int.TryParse(fourthText, out var fourth))
            {
                return null;
            }
            if (fourth != 0)
            {
                numeric += $".{fourth}";
            }
        }
        return numeric + match.Groups["prerelease"].Value;
    }

    internal static int? CompareVersions(string? leftValue, string? rightValue)
    {
        var left = NormalizeVersion(leftValue);
        var right = NormalizeVersion(rightValue);
        if (left is null || right is null)
        {
            return null;
        }

        var leftParts = SplitNormalizedVersion(left);
        var rightParts = SplitNormalizedVersion(right);
        var numericComparison = leftParts.Numeric.CompareTo(rightParts.Numeric);
        if (numericComparison != 0)
        {
            return numericComparison;
        }
        if (leftParts.Prerelease is null)
        {
            return rightParts.Prerelease is null ? 0 : 1;
        }
        if (rightParts.Prerelease is null)
        {
            return -1;
        }
        return ComparePrerelease(leftParts.Prerelease, rightParts.Prerelease);
    }

    private static SmapiStatus Inspect(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return SmapiStatus.Missing;
        }
        var executable = Path.Combine(gamePath, "StardewModdingAPI.exe");
        if (!File.Exists(executable))
        {
            return SmapiStatus.Missing;
        }

        var version = ReadFileVersion(Path.Combine(gamePath, "StardewModdingAPI.dll"))
            ?? ReadBundledModVersion(Path.Combine(gamePath, "Mods"));
        return new SmapiStatus(true, version, Path.GetFileName(executable));
    }

    private static string? ReadFileVersion(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return NormalizeVersion(info.ProductVersion) ?? NormalizeVersion(info.FileVersion);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadBundledModVersion(string modsPath)
    {
        if (!Directory.Exists(modsPath))
        {
            return null;
        }

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 5,
        };
        foreach (var manifestPath in Directory.EnumerateFiles(modsPath, "manifest.json", options))
        {
            if (manifestPath.Contains(
                    $"{Path.DirectorySeparatorChar}.mod-manager-trash{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                || new FileInfo(manifestPath).Length > 256 * 1024)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = document.RootElement;
                var uniqueId = GetString(root, "UniqueID") ?? GetString(root, "UniqueId");
                if (uniqueId is not "SMAPI.SaveBackup" and not "SMAPI.ConsoleCommands")
                {
                    continue;
                }

                var version = NormalizeVersion(GetString(root, "Version"));
                if (version is not null)
                {
                    versions.Add(version);
                }
            }
            catch
            {
                // A third-party invalid manifest does not invalidate SMAPI detection.
            }
        }

        return versions.Count == 1 ? versions.Single() : null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name)
                || property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }
        return null;
    }

    private static (Version Numeric, string? Prerelease) SplitNormalizedVersion(string value)
    {
        var separator = value.IndexOf('-');
        var numeric = separator < 0 ? value : value[..separator];
        var prerelease = separator < 0 ? null : value[(separator + 1)..];
        return (Version.Parse(numeric), prerelease);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var leftIdentifiers = left.Split('.');
        var rightIdentifiers = right.Split('.');
        for (var index = 0; index < Math.Min(leftIdentifiers.Length, rightIdentifiers.Length); index++)
        {
            var leftIdentifier = leftIdentifiers[index];
            var rightIdentifier = rightIdentifiers[index];
            var leftNumeric = leftIdentifier.All(char.IsAsciiDigit);
            var rightNumeric = rightIdentifier.All(char.IsAsciiDigit);
            int comparison;
            if (leftNumeric && rightNumeric)
            {
                var normalizedLeft = leftIdentifier.TrimStart('0');
                var normalizedRight = rightIdentifier.TrimStart('0');
                normalizedLeft = normalizedLeft.Length == 0 ? "0" : normalizedLeft;
                normalizedRight = normalizedRight.Length == 0 ? "0" : normalizedRight;
                comparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
                if (comparison == 0)
                {
                    comparison = string.CompareOrdinal(normalizedLeft, normalizedRight);
                }
            }
            else if (leftNumeric != rightNumeric)
            {
                comparison = leftNumeric ? -1 : 1;
            }
            else
            {
                comparison = string.CompareOrdinal(leftIdentifier, rightIdentifier);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }
        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    [GeneratedRegex(
        "^[vV]?(?<major>\\d+)\\.(?<minor>\\d+)\\.(?<patch>\\d+)" +
        "(?:\\.(?<fourth>\\d+))?" +
        "(?<prerelease>-(?:[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?" +
        "(?:\\+(?:[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$")]
    private static partial Regex VersionRegex();
}
