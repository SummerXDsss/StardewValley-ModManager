using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed class ModUpdateDownloadService
{
    private const long MaximumDownloadBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumRedirects = 10;
    private const int MaximumAssetNameCharacters = 180;
    private const string UserAgent =
        "Valley-Steward/0.2 (+https://github.com/SummerXDsss/StardewValley-ModManager)";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);
    private static readonly HttpClient GitHubClient = CreateClient();
    private static readonly object TargetWriteLock = new();
    private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly NexusDownloadService _nexusDownloadService;
    private readonly HttpClient _githubClient;
    private readonly Func<CancellationToken, Task<GitHubDownloadSettings>> _settingsProvider;
    private readonly string _downloadDirectory;

    public ModUpdateDownloadService(CredentialService? credentials = null)
        : this(
            new NexusDownloadService(credentials),
            GitHubClient,
            cancellationToken => new GitHubDownloadSettingsService().LoadAsync(cancellationToken),
            GetDefaultDownloadDirectory())
    {
    }

    internal ModUpdateDownloadService(
        NexusDownloadService nexusDownloadService,
        HttpClient githubClient,
        Func<CancellationToken, Task<GitHubDownloadSettings>> settingsProvider,
        string downloadDirectory)
    {
        _nexusDownloadService = nexusDownloadService
            ?? throw new ArgumentNullException(nameof(nexusDownloadService));
        _githubClient = githubClient ?? throw new ArgumentNullException(nameof(githubClient));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        if (string.IsNullOrWhiteSpace(downloadDirectory))
        {
            throw new ArgumentException("Mod 更新下载目录不能为空。", nameof(downloadDirectory));
        }
        _downloadDirectory = Path.GetFullPath(downloadDirectory);
    }

    public async Task<ModUpdateDownloadResult> DownloadAsync(
        ModUpdateDownloadDescriptor descriptor,
        IProgress<NexusDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor switch
        {
            NexusModUpdateDownloadDescriptor nexus when descriptor.Provider == ModUpdateProvider.Nexus
                => await DownloadNexusAsync(nexus, progress, cancellationToken),
            GitHubModUpdateDownloadDescriptor github when descriptor.Provider == ModUpdateProvider.GitHub
                => await DownloadGitHubAsync(github, progress, cancellationToken),
            _ => throw new InvalidDataException("Mod 更新下载描述与提供方不匹配，已拒绝下载。"),
        };
    }

    private async Task<ModUpdateDownloadResult> DownloadNexusAsync(
        NexusModUpdateDownloadDescriptor descriptor,
        IProgress<NexusDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var downloaded = await _nexusDownloadService.DownloadAsync(
            descriptor.ModId,
            descriptor.FileId,
            progress,
            cancellationToken);
        return new ModUpdateDownloadResult(
            downloaded.Path,
            downloaded.FileName,
            downloaded.BytesWritten,
            ModUpdateProvider.Nexus,
            null,
            false,
            false);
    }

    private async Task<ModUpdateDownloadResult> DownloadGitHubAsync(
        GitHubModUpdateDownloadDescriptor descriptor,
        IProgress<NexusDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateGitHubDescriptor(descriptor);
        var expectedSha256 = NormalizeSha256(descriptor.Sha256);
        var loadedSettings = await _settingsProvider(cancellationToken)
            ?? throw new InvalidDataException("GitHub 下载设置为空。");
        var settings = GitHubDownloadSettingsService.Normalize(
            loadedSettings.Mode,
            loadedSettings.CustomPrefix);
        var (downloadUri, mirrorOrigin, usedMirror) = ResolveDownloadUri(
            descriptor.AssetUri,
            settings,
            expectedSha256);

        Directory.CreateDirectory(_downloadDirectory);
        var partPath = GetContainedPath(
            _downloadDirectory,
            $".mod-update-{Environment.ProcessId}-{Guid.NewGuid():N}.part");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);
            using var response = await SendWithRedirectsAsync(
                downloadUri,
                uri =>
                {
                    if (usedMirror)
                    {
                        ValidateMirrorResponseUri(uri, mirrorOrigin!);
                    }
                    else
                    {
                        ValidateDirectDownloadResponseUri(uri, descriptor);
                    }
                },
                timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitHub 更新包下载服务器返回 HTTP {(int)response.StatusCode}。",
                    null,
                    response.StatusCode);
            }

            var contentLength = response.Content.Headers.ContentLength;
            ValidateSize(contentLength, "GitHub 更新包响应");
            if (descriptor.AssetSize is { } expectedSize
                && contentLength is { } responseSize
                && expectedSize != responseSize)
            {
                throw new InvalidDataException(
                    $"GitHub 更新包大小发生变化（预期 {expectedSize} 字节，收到 {responseSize} 字节）。");
            }

            var expectedDownloadSize = descriptor.AssetSize ?? contentLength;
            var written = await WriteDownloadAsync(
                response.Content,
                partPath,
                expectedDownloadSize,
                progress,
                timeout.Token);
            if (expectedDownloadSize is { } size && written.BytesWritten != size)
            {
                throw new EndOfStreamException(
                    $"GitHub 更新包下载不完整（预期 {size} 字节，收到 {written.BytesWritten} 字节）。");
            }

            var digestVerified = expectedSha256 is not null;
            if (digestVerified
                && !written.Sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"GitHub 更新包 SHA-256 不匹配（预期 {expectedSha256}，收到 {written.Sha256}）。");
            }

            string targetPath;
            lock (TargetWriteLock)
            {
                targetPath = NexusDownloadService.GetAvailableTarget(
                    _downloadDirectory,
                    descriptor.AssetName);
                File.Move(partPath, targetPath, overwrite: false);
            }
            progress?.Report(new NexusDownloadProgress(
                written.BytesWritten,
                expectedDownloadSize ?? written.BytesWritten));
            return new ModUpdateDownloadResult(
                targetPath,
                Path.GetFileName(targetPath),
                written.BytesWritten,
                ModUpdateProvider.GitHub,
                written.Sha256,
                digestVerified,
                usedMirror);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub 更新包下载超时，临时文件已清理。");
        }
        finally
        {
            TryDelete(partPath);
        }
    }

    private async Task<DownloadWriteResult> WriteDownloadAsync(
        HttpContent content,
        string partPath,
        long? expectedSize,
        IProgress<NexusDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            partPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var signature = new byte[4];
        var signatureLength = 0;
        long total = 0;
        var lastReport = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                total = checked(total + read);
                if (total > MaximumDownloadBytes)
                {
                    throw new InvalidDataException(
                        $"GitHub 更新包超过 {MaximumDownloadBytes / 1024 / 1024} MB 的安全上限。");
                }
                if (signatureLength < signature.Length)
                {
                    var copyLength = Math.Min(signature.Length - signatureLength, read);
                    buffer.AsSpan(0, copyLength).CopyTo(signature.AsSpan(signatureLength));
                    signatureLength += copyLength;
                }

                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (lastReport.ElapsedMilliseconds >= 200)
                {
                    progress?.Report(new NexusDownloadProgress(total, expectedSize));
                    lastReport.Restart();
                }
            }

            if (total == 0)
            {
                throw new EndOfStreamException("GitHub 更新包下载结果为空。");
            }
            if (signatureLength != signature.Length
                || !signature.AsSpan().SequenceEqual("PK\u0003\u0004"u8))
            {
                throw new InvalidDataException("GitHub 更新包不是有效的 ZIP 文件。");
            }
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
            return new DownloadWriteResult(
                total,
                Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(
        Uri initialUri,
        Action<Uri> validateUri,
        CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            validateUri(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/zip, application/octet-stream");

            HttpResponseMessage response;
            try
            {
                response = await _githubClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException error)
            {
                throw new HttpRequestException(
                    "无法连接 GitHub 更新包下载服务，请检查网络或代理后重试。",
                    null,
                    error.StatusCode);
            }

            if (response.RequestMessage?.RequestUri is { } responseUri
                && !SameSchemeHostPortAndPath(responseUri, current))
            {
                try
                {
                    validateUri(responseUri);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            var statusCode = response.StatusCode;
            response.Dispose();
            if (redirect == MaximumRedirects)
            {
                throw new HttpRequestException("GitHub 更新包下载重定向次数过多。", null, statusCode);
            }
            if (location is null)
            {
                throw new InvalidDataException("GitHub 更新包下载重定向缺少 Location 地址。");
            }
            try
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
            }
            catch (UriFormatException)
            {
                throw new InvalidDataException("GitHub 更新包下载重定向地址无效。");
            }
        }

        throw new HttpRequestException("GitHub 更新包下载重定向次数过多。");
    }

    private static (Uri DownloadUri, Uri? MirrorOrigin, bool UsedMirror) ResolveDownloadUri(
        Uri officialUri,
        GitHubDownloadSettings settings,
        string? expectedSha256)
    {
        if (settings.Mode == GitHubDownloadMode.Direct)
        {
            return (officialUri, null, false);
        }
        if (settings.Mode != GitHubDownloadMode.Custom
            || string.IsNullOrWhiteSpace(settings.CustomPrefix))
        {
            throw new InvalidDataException("GitHub 下载设置中的镜像前缀无效。");
        }
        if (expectedSha256 is null)
        {
            throw new InvalidDataException(
                "该 GitHub Mod 更新包没有官方 SHA-256；为防止第三方镜像篡改，已拒绝镜像下载。");
        }

        var mirrored = new Uri(settings.CustomPrefix + officialUri.AbsoluteUri, UriKind.Absolute);
        ValidatePlainHttpsUri(mirrored, allowQuery: false);
        if (mirrored.AbsoluteUri.Length > 8 * 1024)
        {
            throw new InvalidDataException("拼接后的 GitHub 镜像地址过长。");
        }
        return (mirrored, new Uri(settings.CustomPrefix), true);
    }

    private static void ValidateGitHubDescriptor(GitHubModUpdateDownloadDescriptor descriptor)
    {
        if (!descriptor.Provider.Equals(ModUpdateProvider.GitHub))
        {
            throw new InvalidDataException("GitHub 更新描述的提供方无效。");
        }
        ValidateAssetName(descriptor.AssetName);
        ValidateSize(descriptor.AssetSize, "GitHub Release 资产");
        ValidateOfficialAssetUri(descriptor.AssetUri, descriptor.AssetName);
        _ = NormalizeSha256(descriptor.Sha256);
    }

    private static void ValidateOfficialAssetUri(Uri uri, string assetName)
    {
        ValidatePlainHttpsUri(uri, allowQuery: false);
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub 更新包地址不属于 github.com。");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 6
            || !segments[2].Equals("releases", StringComparison.Ordinal)
            || !segments[3].Equals("download", StringComparison.Ordinal)
            || segments[0].Length is < 1 or > 100
            || segments[1].Length is < 1 or > 100
            || segments[4].Length is < 1 or > 200)
        {
            throw new InvalidDataException("GitHub 更新包不是标准 Release 资产地址。");
        }
        string decodedAssetName;
        try
        {
            decodedAssetName = Uri.UnescapeDataString(segments[5]);
        }
        catch (UriFormatException error)
        {
            throw new InvalidDataException("GitHub 更新包文件名编码无效。", error);
        }
        if (!decodedAssetName.Equals(assetName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("GitHub 更新包文件名与 Release 描述不一致。");
        }
    }

    private static void ValidateDirectDownloadResponseUri(
        Uri uri,
        GitHubModUpdateDownloadDescriptor descriptor)
    {
        ValidatePlainHttpsUri(uri, allowQuery: true);
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            ValidateOfficialAssetUri(uri, descriptor.AssetName);
            return;
        }
        if (uri.Host is not ("release-assets.githubusercontent.com"
            or "objects.githubusercontent.com"
            or "github-releases.githubusercontent.com"))
        {
            throw new InvalidDataException("GitHub 更新包被重定向到不受信任的主机。");
        }
    }

    private static void ValidateMirrorResponseUri(Uri uri, Uri mirrorOrigin)
    {
        ValidatePlainHttpsUri(uri, allowQuery: false);
        if (!uri.Host.Equals(mirrorOrigin.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != mirrorOrigin.Port
            || !uri.Scheme.Equals(mirrorOrigin.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub 镜像重定向到了不受信任的地址。");
        }
    }

    private static void ValidatePlainHttpsUri(Uri uri, bool allowQuery)
    {
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Fragment)
            || !allowQuery && !string.IsNullOrEmpty(uri.Query))
        {
            throw new InvalidDataException(
                "Mod 更新下载地址必须是普通 HTTPS 地址，不能包含凭据、自定义端口或意外参数。");
        }
    }

    private static void ValidateAssetName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumAssetNameCharacters
            || !value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
            || value.Any(character => char.IsControl(character)
                || Path.GetInvalidFileNameChars().Contains(character))
            || ReservedWindowsFileNames.Contains(Path.GetFileNameWithoutExtension(value)))
        {
            throw new InvalidDataException("GitHub Release 的 ZIP 文件名无效。");
        }
    }

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var normalized = value.Trim();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("GitHub 更新包 SHA-256 格式无效。");
        }
        return normalized.ToLowerInvariant();
    }

    private static void ValidateSize(long? size, string label)
    {
        if (size is null)
        {
            return;
        }
        if (size is <= 0 or > MaximumDownloadBytes)
        {
            throw new InvalidDataException(
                $"{label}大小必须介于 1 字节与 {MaximumDownloadBytes / 1024 / 1024} MB 之间。");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static bool SameSchemeHostPortAndPath(Uri left, Uri right)
    {
        return left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
            && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port
            && left.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped).Equals(
                right.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped),
                StringComparison.Ordinal);
    }

    private static string GetContainedPath(string directory, string fileName)
    {
        var fullDirectory = Path.GetFullPath(directory);
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Mod 更新缓存文件名无效。");
        }
        var path = Path.GetFullPath(Path.Combine(fullDirectory, fileName));
        if (!string.Equals(Path.GetDirectoryName(path), fullDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Mod 更新缓存路径越界。");
        }
        return path;
    }

    private static string GetDefaultDownloadDirectory()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new DirectoryNotFoundException("无法确定当前用户的应用缓存目录。");
        }
        return Path.Combine(
            localData,
            "com.summerxdsss.valleysteward",
            "cache",
            "downloads",
            "mod-updates");
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Cleanup must not replace the original download error.
        }
    }

    private sealed record DownloadWriteResult(long BytesWritten, string Sha256);
}
