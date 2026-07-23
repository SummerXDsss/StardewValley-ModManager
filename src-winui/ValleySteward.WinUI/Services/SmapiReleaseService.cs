using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

internal sealed class SmapiReleaseService
{
    private const string GitHubApiUrl = "https://api.github.com/repos/Pathoschild/SMAPI/releases/latest";
    private const string GitHubLatestUrl = "https://github.com/Pathoschild/SMAPI/releases/latest";
    private const string UserAgent = "Valley-Steward/0.1 (+https://github.com/SummerXDsss/StardewValley-ModManager)";
    private const long MaxInstallerSize = 256L * 1024 * 1024;
    private const int MaxMetadataSize = 2 * 1024 * 1024;
    private const int MaxRedirects = 10;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Client = CreateClient();

    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private readonly GitHubDownloadSettingsService _downloadSettings = new();
    private SmapiReleaseInfo? _cachedRelease;
    private DateTimeOffset _cachedAt;

    public async Task<SmapiReleaseInfo> GetLatestAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh
            && _cachedRelease is not null
            && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
        {
            return _cachedRelease;
        }

        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh
                && _cachedRelease is not null
                && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
            {
                return _cachedRelease;
            }

            var release = await FetchFromApiAsync(cancellationToken);
            _cachedRelease = release;
            _cachedAt = DateTimeOffset.UtcNow;
            return release;
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public async Task<DownloadedSmapiInstaller> DownloadAsync(
        SmapiReleaseInfo release,
        IProgress<SmapiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRelease(release);
        var settings = await _downloadSettings.LoadAsync(cancellationToken);
        var officialUri = new Uri(release.Asset.DownloadUrl, UriKind.Absolute);
        var expectedDigest = GetSha256Digest(release.Asset.Digest);
        var (downloadUri, mirrorOrigin, usedMirror) = ResolveDownloadUri(
            officialUri,
            settings,
            expectedDigest);

        var releaseDirectory = Path.Combine(CacheDirectory, "downloads", "smapi", release.TagName);
        Directory.CreateDirectory(releaseDirectory);
        var targetPath = SafeChildPath(releaseDirectory, release.Asset.Name);
        var partPath = SafeChildPath(
            releaseDirectory,
            $".{release.Asset.Name}.{Environment.ProcessId}.{Guid.NewGuid():N}.part");

        progress?.Report(new SmapiOperationProgress(
            SmapiOperationStage.Downloading,
            usedMirror ? "正在通过 GitHub 镜像下载 SMAPI…" : "正在从 GitHub 下载 SMAPI…",
            0));

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
                        ValidateDirectDownloadResponseUri(uri, release.TagName, release.Asset.Name);
                    }
                },
                timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"SMAPI 下载服务器返回了 {(int)response.StatusCode} {response.ReasonPhrase}。",
                    null,
                    response.StatusCode);
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is not null)
            {
                ValidateInstallerSize(contentLength.Value);
                if (release.Asset.Size is not null && contentLength != release.Asset.Size)
                {
                    throw new InvalidDataException(
                        $"SMAPI 安装包大小发生变化（预期 {release.Asset.Size} 字节，收到 {contentLength} 字节）。");
                }
            }

            var expectedSize = release.Asset.Size ?? contentLength;
            var download = await WriteDownloadAsync(
                response,
                partPath,
                expectedSize,
                progress,
                timeout.Token);

            if (expectedSize is not null && download.Size != expectedSize)
            {
                throw new InvalidDataException(
                    $"SMAPI 安装包下载不完整（预期 {expectedSize} 字节，收到 {download.Size} 字节）。");
            }

            var digestVerified = false;
            if (expectedDigest is not null)
            {
                if (!download.Sha256.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"SMAPI 安装包 SHA-256 不匹配（预期 {expectedDigest}，收到 {download.Sha256}）。");
                }
                digestVerified = true;
            }

            File.Move(partPath, targetPath, overwrite: true);
            return new DownloadedSmapiInstaller(
                targetPath,
                release.Asset.Name,
                release.Version,
                download.Size,
                download.Sha256,
                digestVerified,
                usedMirror);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("SMAPI 安装包下载超时，临时文件已清理。");
        }
        finally
        {
            TryDeleteFile(partPath);
        }
    }

    private async Task<SmapiReleaseInfo> FetchFromApiAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataTimeout);
        try
        {
            using var request = CreateRequest(new Uri(GitHubApiUrl));
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return await FetchFromLatestRedirectAsync(cancellationToken);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitHub Releases API 返回了 {(int)response.StatusCode} {response.ReasonPhrase}。",
                    null,
                    response.StatusCode);
            }

            var bytes = await ReadLimitedAsync(response.Content, MaxMetadataSize, timeout.Token);
            using var document = JsonDocument.Parse(bytes);
            return ParseApiRelease(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("获取 SMAPI 最新版本超时。");
        }
    }

    private async Task<SmapiReleaseInfo> FetchFromLatestRedirectAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataTimeout);
        try
        {
            using var response = await SendWithRedirectsAsync(
                new Uri(GitHubLatestUrl),
                ValidateLatestReleaseRedirectUri,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitHub 最新 Release 页面返回了 {(int)response.StatusCode} {response.ReasonPhrase}。",
                    null,
                    response.StatusCode);
            }

            var finalUri = response.RequestMessage?.RequestUri
                ?? throw new InvalidDataException("GitHub 最新 Release 没有返回最终地址。");
            var tag = ParseReleaseTagUri(finalUri);
            var assetName = ExpectedAssetName(tag);
            var downloadUrl = BuildOfficialDownloadUri(tag, assetName).AbsoluteUri;
            return new SmapiReleaseInfo(
                DisplayVersion(tag),
                tag,
                finalUri.AbsoluteUri,
                null,
                "install on Windows.bat",
                SmapiReleaseSource.GitHubLatestRedirect,
                new SmapiReleaseAsset(null, assetName, null, downloadUrl, null));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("解析 SMAPI 最新 Release 超时。");
        }
    }

    private static SmapiReleaseInfo ParseApiRelease(JsonElement root)
    {
        if (GetBoolean(root, "draft") || GetBoolean(root, "prerelease"))
        {
            throw new InvalidDataException("GitHub 返回的 SMAPI Release 不是稳定版本。");
        }

        var tag = GetRequiredString(root, "tag_name");
        ValidateTag(tag);
        var pageUrl = GetRequiredString(root, "html_url");
        ValidateReleasePageUri(new Uri(pageUrl, UriKind.Absolute), tag);
        var expectedName = ExpectedAssetName(tag);

        var assets = root.TryGetProperty("assets", out var assetsElement)
            && assetsElement.ValueKind == JsonValueKind.Array
                ? assetsElement.EnumerateArray()
                    .Where(asset => string.Equals(
                        GetOptionalString(asset, "name"),
                        expectedName,
                        StringComparison.Ordinal))
                    .ToArray()
                : [];
        if (assets.Length != 1)
        {
            throw new InvalidDataException(
                assets.Length == 0
                    ? $"SMAPI Release 中没有标准安装包 {expectedName}。"
                    : $"SMAPI Release 中存在多个同名标准安装包 {expectedName}。");
        }

        var asset = assets[0];
        var size = GetRequiredInt64(asset, "size");
        ValidateInstallerSize(size);
        var downloadUrl = GetRequiredString(asset, "browser_download_url");
        ValidateOfficialDownloadUri(new Uri(downloadUrl, UriKind.Absolute), tag, expectedName);
        var digest = NormalizeDigest(GetOptionalString(asset, "digest"));
        var id = asset.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var idValue)
            ? idValue
            : (long?)null;
        var publishedAt = DateTimeOffset.TryParse(
            GetOptionalString(root, "published_at"),
            out var parsedPublishedAt)
                ? parsedPublishedAt
                : (DateTimeOffset?)null;

        return new SmapiReleaseInfo(
            DisplayVersion(tag),
            tag,
            pageUrl,
            publishedAt,
            "install on Windows.bat",
            SmapiReleaseSource.GitHubApi,
            new SmapiReleaseAsset(id, expectedName, size, downloadUrl, digest));
    }

    private static async Task<DownloadWriteResult> WriteDownloadAsync(
        HttpResponseMessage response,
        string partPath,
        long? expectedSize,
        IProgress<SmapiOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            partPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var signature = new byte[4];
        var signatureLength = 0;
        long total = 0;
        var lastPercent = -1;
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (count == 0)
                {
                    break;
                }

                total = checked(total + count);
                if (total > MaxInstallerSize)
                {
                    throw new InvalidDataException(
                        $"SMAPI 安装包超过 {MaxInstallerSize} 字节安全上限。");
                }
                if (signatureLength < signature.Length)
                {
                    var copied = Math.Min(signature.Length - signatureLength, count);
                    buffer.AsSpan(0, copied).CopyTo(signature.AsSpan(signatureLength));
                    signatureLength += copied;
                }

                hasher.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                if (expectedSize is > 0)
                {
                    var percent = (int)Math.Min(100, total * 100 / expectedSize.Value);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(new SmapiOperationProgress(
                            SmapiOperationStage.Downloading,
                            $"正在下载 SMAPI… {percent}%",
                            percent));
                    }
                }
            }

            if (total == 0)
            {
                throw new InvalidDataException("SMAPI 安装包下载结果为空。");
            }
            if (signatureLength != 4 || !signature.AsSpan().SequenceEqual("PK\u0003\u0004"u8))
            {
                throw new InvalidDataException("SMAPI 下载结果不是有效的 ZIP 安装包。");
            }

            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            return new DownloadWriteResult(total, hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<HttpResponseMessage> SendWithRedirectsAsync(
        Uri initialUri,
        Action<Uri> validateUri,
        CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            validateUri(current);
            using var request = CreateRequest(current);
            var response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            if (redirect == MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("SMAPI 下载重定向次数过多。");
            }
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new InvalidDataException("SMAPI 下载重定向缺少 Location 地址。");
            }
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new HttpRequestException("SMAPI 下载重定向次数过多。");
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
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

    private static (Uri DownloadUri, Uri? MirrorOrigin, bool UsedMirror) ResolveDownloadUri(
        Uri officialUri,
        GitHubDownloadSettings settings,
        string? officialSha256)
    {
        if (settings.Mode == GitHubDownloadMode.Direct)
        {
            return (officialUri, null, false);
        }
        if (settings.Mode != GitHubDownloadMode.Custom
            || string.IsNullOrEmpty(settings.CustomPrefix))
        {
            throw new InvalidDataException("GitHub 下载设置中的镜像前缀无效。");
        }
        if (officialSha256 is null)
        {
            throw new InvalidDataException(
                "GitHub 没有为这个 SMAPI 安装包提供 SHA-256；为避免第三方镜像篡改，已拒绝镜像下载。");
        }

        var applied = new Uri(settings.CustomPrefix + officialUri.AbsoluteUri, UriKind.Absolute);
        ValidatePlainHttpsUri(applied, "拼接后的 GitHub 镜像地址", allowPath: true);
        if (applied.AbsoluteUri.Length > 8 * 1024)
        {
            throw new InvalidDataException("拼接后的 GitHub 镜像地址过长。");
        }
        return (applied, new Uri(settings.CustomPrefix), true);
    }

    private static void ValidateRelease(SmapiReleaseInfo release)
    {
        ValidateTag(release.TagName);
        var expectedName = ExpectedAssetName(release.TagName);
        if (!release.Asset.Name.Equals(expectedName, StringComparison.Ordinal)
            || release.Asset.Name.Contains("double-zipped", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("没有选中 SMAPI 标准安装包。");
        }
        if (release.Asset.Size is not null)
        {
            ValidateInstallerSize(release.Asset.Size.Value);
        }
        ValidateOfficialDownloadUri(
            new Uri(release.Asset.DownloadUrl, UriKind.Absolute),
            release.TagName,
            expectedName);
    }

    private static void ValidateLatestReleaseRedirectUri(Uri uri)
    {
        ValidateGitHubAuthority(uri);
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("GitHub 最新 Release 重定向包含意外参数。");
        }
        if (uri.AbsolutePath.Equals("/Pathoschild/SMAPI/releases/latest", StringComparison.Ordinal))
        {
            return;
        }
        _ = ParseReleaseTagUri(uri);
    }

    private static string ParseReleaseTagUri(Uri uri)
    {
        ValidateGitHubAuthority(uri);
        const string prefix = "/Pathoschild/SMAPI/releases/tag/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
            || uri.AbsolutePath.Length == prefix.Length)
        {
            throw new InvalidDataException("GitHub 最新 Release 没有指向 SMAPI 版本标签。");
        }
        var escapedTag = uri.AbsolutePath[prefix.Length..];
        if (escapedTag.Contains('/'))
        {
            throw new InvalidDataException("SMAPI Release 标签路径无效。");
        }
        var tag = Uri.UnescapeDataString(escapedTag);
        ValidateTag(tag);
        ValidateReleasePageUri(uri, tag);
        return tag;
    }

    private static void ValidateReleasePageUri(Uri uri, string tag)
    {
        ValidateGitHubAuthority(uri);
        var expected = new Uri(
            $"https://github.com/Pathoschild/SMAPI/releases/tag/{Uri.EscapeDataString(tag)}");
        if (!SameSchemeHostAndPath(uri, expected)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("SMAPI Release 页面地址不是预期的 GitHub 官方地址。");
        }
    }

    private static void ValidateOfficialDownloadUri(Uri uri, string tag, string assetName)
    {
        ValidateGitHubAuthority(uri);
        var expected = BuildOfficialDownloadUri(tag, assetName);
        if (!SameSchemeHostAndPath(uri, expected)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("SMAPI 安装包地址不是预期的 GitHub 官方 Release 资源。");
        }
    }

    private static void ValidateDirectDownloadResponseUri(Uri uri, string tag, string assetName)
    {
        ValidatePlainHttpsUri(uri, "SMAPI 下载重定向", allowPath: true, allowQuery: true);
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            ValidateOfficialDownloadUri(uri, tag, assetName);
            return;
        }
        if (uri.Host is not ("release-assets.githubusercontent.com"
            or "objects.githubusercontent.com"
            or "github-releases.githubusercontent.com"))
        {
            throw new InvalidDataException("SMAPI 下载被重定向到不受信任的主机。");
        }
    }

    private static void ValidateMirrorResponseUri(Uri uri, Uri mirrorOrigin)
    {
        ValidatePlainHttpsUri(uri, "GitHub 镜像响应地址", allowPath: true);
        if (!uri.Host.Equals(mirrorOrigin.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != mirrorOrigin.Port
            || !uri.Scheme.Equals(mirrorOrigin.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub 镜像重定向到了不受信任的地址。");
        }
    }

    private static void ValidateGitHubAuthority(Uri uri)
    {
        ValidatePlainHttpsUri(uri, "GitHub 地址", allowPath: true);
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SMAPI 地址不属于 github.com。");
        }
    }

    private static void ValidatePlainHttpsUri(
        Uri uri,
        string label,
        bool allowPath,
        bool allowQuery = false)
    {
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Fragment)
            || (!allowQuery && !string.IsNullOrEmpty(uri.Query))
            || (!allowPath && uri.AbsolutePath != "/"))
        {
            throw new InvalidDataException($"{label}必须是普通 HTTPS 地址，不能包含凭据、自定义端口或意外参数。");
        }
    }

    private static bool SameSchemeHostAndPath(Uri left, Uri right)
    {
        return left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
            && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port
            && left.GetComponents(UriComponents.Path, UriFormat.UriEscaped)
                .Equals(
                    right.GetComponents(UriComponents.Path, UriFormat.UriEscaped),
                    StringComparison.Ordinal);
    }

    private static Uri BuildOfficialDownloadUri(string tag, string assetName)
    {
        return new Uri(
            $"https://github.com/Pathoschild/SMAPI/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}");
    }

    private static string ExpectedAssetName(string tag) => $"SMAPI-{tag}-installer.zip";

    private static string DisplayVersion(string tag)
    {
        return tag.Length > 1
            && tag[0] is 'v' or 'V'
            && char.IsAsciiDigit(tag[1])
                ? tag[1..]
                : tag;
    }

    private static void ValidateTag(string tag)
    {
        var valid = tag.Length is > 0 and <= 64
            && tag[0] != '.'
            && !tag.Contains("..", StringComparison.Ordinal)
            && tag.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_' or '+');
        if (!valid)
        {
            throw new InvalidDataException("SMAPI Release 标签不安全。");
        }
    }

    private static void ValidateInstallerSize(long size)
    {
        if (size is <= 0 or > MaxInstallerSize)
        {
            throw new InvalidDataException(
                $"SMAPI 安装包大小无效或超过 {MaxInstallerSize} 字节安全上限。");
        }
    }

    private static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }
        var value = digest.Trim();
        if (!value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }
        var hash = value[7..];
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("GitHub 返回的 SMAPI SHA-256 无效。");
        }
        return $"sha256:{hash.ToLowerInvariant()}";
    }

    private static string? GetSha256Digest(string? digest)
    {
        if (digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }
        var hash = digest[7..];
        return hash.Length == 64 && hash.All(Uri.IsHexDigit)
            ? hash.ToLowerInvariant()
            : throw new InvalidDataException("SMAPI SHA-256 摘要无效。");
    }

    private static string SafeChildPath(string directory, string fileName)
    {
        if (string.IsNullOrEmpty(fileName)
            || fileName != Path.GetFileName(fileName)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("SMAPI 缓存文件名不安全。");
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SMAPI 缓存路径越过了应用缓存目录。");
        }
        return candidate;
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxMetadataSize)
        {
            throw new InvalidDataException("GitHub Release 元数据过大。");
        }
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (count == 0)
                {
                    return output.ToArray();
                }
                if (output.Length + count > maximumBytes)
                {
                    throw new InvalidDataException("GitHub Release 元数据过大。");
                }
                output.Write(buffer, 0, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        return GetOptionalString(element, propertyName) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"GitHub Release 缺少 {propertyName}。");
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static long GetRequiredInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result)
            ? result
            : throw new InvalidDataException($"GitHub Release 缺少有效的 {propertyName}。");
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A failed cleanup must not replace the original download error.
        }
    }

    private static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "com.summerxdsss.valleysteward");

    private sealed record DownloadWriteResult(long Size, string Sha256);
}
