using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed class GitHubDownloadSettingsService
{
    public const string GhProxyPreset = "https://gh-proxy.com/";

    private const int ConfigVersion = 1;
    private const int MaxConfigBytes = 8 * 1024;
    private const int MaxPrefixBytes = 512;
    private static readonly SemaphoreSlim ConfigGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _configDirectory;

    public GitHubDownloadSettingsService()
        : this(AppPaths.ConfigDirectory)
    {
    }

    internal GitHubDownloadSettingsService(string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            throw new ArgumentException("GitHub 下载设置目录不能为空。", nameof(configDirectory));
        }
        _configDirectory = Path.GetFullPath(configDirectory);
    }

    public async Task<GitHubDownloadSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await ConfigGate.WaitAsync(cancellationToken);
        try
        {
            var primaryResult = await TryReadAsync(ConfigPath, cancellationToken);
            if (primaryResult.Settings is not null)
            {
                return primaryResult.Settings;
            }

            var backupResult = await TryReadAsync(BackupConfigPath, cancellationToken);
            if (backupResult.Settings is not null)
            {
                Directory.CreateDirectory(_configDirectory);
                await WriteAtomicallyAsync(
                    ConfigPath,
                    Serialize(backupResult.Settings),
                    cancellationToken);
                return backupResult.Settings;
            }

            if (primaryResult.Error is not null || backupResult.Error is not null)
            {
                throw new InvalidDataException(
                    "GitHub 下载设置及其备份都不可用："
                    + $"{primaryResult.Error?.Message ?? "主设置不存在"}；"
                    + $"{backupResult.Error?.Message ?? "备份不存在"}",
                    backupResult.Error ?? primaryResult.Error);
            }
            return GitHubDownloadSettings.Direct;
        }
        finally
        {
            ConfigGate.Release();
        }
    }

    public async Task<GitHubDownloadSettings> SaveAsync(
        GitHubDownloadSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings.Mode, settings.CustomPrefix);
        var bytes = Serialize(normalized);

        await ConfigGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_configDirectory);
            await WriteAtomicallyAsync(BackupConfigPath, bytes, cancellationToken);
            await WriteAtomicallyAsync(ConfigPath, bytes, cancellationToken);
            return normalized;
        }
        finally
        {
            ConfigGate.Release();
        }
    }

    public Task<GitHubDownloadSettings> ClearAsync(
        CancellationToken cancellationToken = default)
    {
        return SaveAsync(GitHubDownloadSettings.Direct, cancellationToken);
    }

    internal static GitHubDownloadSettings Normalize(
        GitHubDownloadMode mode,
        string? customPrefix)
    {
        return mode switch
        {
            GitHubDownloadMode.Direct => GitHubDownloadSettings.Direct,
            GitHubDownloadMode.Custom => new GitHubDownloadSettings(
                GitHubDownloadMode.Custom,
                NormalizeCustomPrefix(customPrefix)),
            _ => throw new InvalidDataException("GitHub 下载模式无效。"),
        };
    }

    private async Task<ConfigReadResult> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new ConfigReadResult(null, null);
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaxConfigBytes)
            {
                throw new InvalidDataException("设置文件大小无效。");
            }
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.Length is <= 0 or > MaxConfigBytes)
            {
                throw new InvalidDataException("设置文件大小无效。");
            }

            var stored = JsonSerializer.Deserialize<StoredSettings>(bytes, JsonOptions)
                ?? throw new InvalidDataException("设置文件内容为空。");
            if (stored.Version != ConfigVersion)
            {
                throw new InvalidDataException($"不支持 GitHub 下载设置版本 {stored.Version}。");
            }

            var mode = stored.Mode?.Trim().ToLowerInvariant() switch
            {
                "direct" => GitHubDownloadMode.Direct,
                "custom" => GitHubDownloadMode.Custom,
                "gh-proxy" or "ghproxy" => GitHubDownloadMode.Custom,
                _ => throw new InvalidDataException("GitHub 下载模式无效。"),
            };
            var prefix = mode == GitHubDownloadMode.Custom
                && stored.Mode?.Trim().ToLowerInvariant() is "gh-proxy" or "ghproxy"
                && string.IsNullOrWhiteSpace(stored.ResolvedCustomPrefix)
                    ? GhProxyPreset
                    : stored.ResolvedCustomPrefix;
            return new ConfigReadResult(Normalize(mode, prefix), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new ConfigReadResult(null, error);
        }
    }

    private static string NormalizeCustomPrefix(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || Encoding.UTF8.GetByteCount(trimmed) > MaxPrefixBytes)
        {
            throw new InvalidDataException("GitHub 镜像前缀为空或过长。");
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException("GitHub 镜像前缀不是有效的绝对地址。");
        }
        ValidatePlainHttpsUri(uri);

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + "/",
        };
        var normalized = builder.Uri.AbsoluteUri;
        if (Encoding.UTF8.GetByteCount(normalized) > MaxPrefixBytes)
        {
            throw new InvalidDataException("规范化后的 GitHub 镜像前缀过长。");
        }
        return normalized;
    }

    private static void ValidatePlainHttpsUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(
                "GitHub 镜像前缀必须是普通 HTTPS 地址，不能包含凭据、自定义端口、查询参数或片段。");
        }
    }

    private static byte[] Serialize(GitHubDownloadSettings settings)
    {
        var stored = new StoredSettings(
            ConfigVersion,
            settings.Mode == GitHubDownloadMode.Direct ? "direct" : "custom",
            settings.CustomPrefix,
            null,
            null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stored, JsonOptions);
        if (bytes.Length > MaxConfigBytes)
        {
            throw new InvalidDataException("GitHub 下载设置过大。");
        }
        return bytes;
    }

    private static async Task WriteAtomicallyAsync(
        string targetPath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("GitHub 下载设置没有有效的父目录。");
        var temporaryPath = Path.Combine(
            directory,
            $".github-download-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A failed temporary-file cleanup must not replace the save result.
            }
        }
    }

    private string ConfigPath => Path.Combine(_configDirectory, "github-download.json");
    private string BackupConfigPath => Path.Combine(_configDirectory, ".github-download.json.bak");

    private sealed record StoredSettings(
        int Version,
        string Mode,
        string? CustomPrefix,
        [property: JsonPropertyName("custom_prefix")] string? LegacyCustomPrefix,
        string? Prefix)
    {
        [JsonIgnore]
        public string? ResolvedCustomPrefix => CustomPrefix ?? LegacyCustomPrefix ?? Prefix;
    }

    private sealed record ConfigReadResult(
        GitHubDownloadSettings? Settings,
        Exception? Error);
}
