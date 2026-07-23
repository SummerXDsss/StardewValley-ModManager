using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed class NexusDownloadService
{
    private const string ApiHost = "api.nexusmods.com";
    private const string GameDomain = "stardewvalley";
    private const long MaximumJsonBytes = 4L * 1024 * 1024;
    private const long MaximumDownloadBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumFilesPerMod = 256;
    private const int MaximumVersionsPerFile = 512;
    private const int MaximumErrorBytes = 16 * 1024;
    private const int MaximumFileNameLength = 160;
    private const int MaximumRedirects = 5;
    private const int MinimumApiKeyLength = 20;
    private const int MaximumApiKeyLength = 512;
    private const int MaximumNxmKeyLength = 512;
    private const int MaximumTranslatedNameCharacters = 512;
    private const int MaximumTranslatedDescriptionCharacters = 16_000;
    private const string DownloadSidecarProvider = "Nexus Mods";
    private const int DownloadSidecarSchemaVersion = 1;

    private static readonly Uri ApiBase = new("https://api.nexusmods.com/v3/");
    private static readonly Uri LegacyApiBase = new("https://api.nexusmods.com/v1/");
    private static readonly HttpClient ApiClient = CreateClient(TimeSpan.FromSeconds(30));
    private static readonly HttpClient DownloadClient = CreateClient(TimeSpan.FromMinutes(20));
    private static readonly object SidecarWriteLock = new();
    private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    private readonly CredentialService _credentials;

    public NexusDownloadService(CredentialService? credentials = null)
    {
        _credentials = credentials ?? new CredentialService();
    }

    public bool HasApiKey()
    {
        return !string.IsNullOrWhiteSpace(_credentials.Read(CredentialService.NexusApiKeyTarget));
    }

    public async Task ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        apiKey = NormalizeApiKey(apiKey, "填写的 Nexus Personal API Key 格式无效。");
        using var document = await GetApiJsonAsync(
            new Uri(LegacyApiBase, "users/validate.json"),
            apiKey,
            NexusApiOperation.ValidateKey,
            cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Nexus API Key 验证响应格式无效。");
        }
    }

    public static NexusNxmLink ParseNxmLink(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(GameDomain, StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0
            || uri.Port != -1
            || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("NXM 链接无效，必须是星露谷物语 Nexus 官网生成的 nxm://stardewvalley/... 链接。", nameof(value));
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4
            || !segments[0].Equals("mods", StringComparison.OrdinalIgnoreCase)
            || !segments[2].Equals("files", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("NXM 链接路径无效。", nameof(value));
        }
        var modId = segments[1];
        var fileId = segments[3];
        ValidateNumericId(modId, "NXM Mod ID");
        ValidateNumericId(fileId, "NXM 文件 ID");

        var query = ParseUniqueQuery(uri.Query);
        if (!query.TryGetValue("key", out var key)
            || !query.TryGetValue("expires", out var expiresText))
        {
            throw new ArgumentException("NXM 链接缺少普通账户下载所需的 key 或 expires。", nameof(value));
        }
        ValidateNxmKey(key);
        if (!long.TryParse(expiresText, NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || expires <= 0)
        {
            throw new ArgumentException("NXM 链接的 expires 参数无效。", nameof(value));
        }

        return new NexusNxmLink(
            GameDomain,
            modId,
            fileId,
            new NexusDownloadAuthorization(key, expires));
    }

    public async Task<NexusModDetails> GetModDetailsAsync(
        string gameScopedModId,
        CancellationToken cancellationToken = default)
    {
        ValidateNumericId(gameScopedModId, "Mod ID");
        var apiKey = ReadApiKey();

        using var modDocument = await GetApiJsonAsync(
            new Uri(ApiBase, $"games/{GameDomain}/mods/{gameScopedModId}"),
            apiKey,
            NexusApiOperation.ReadData,
            cancellationToken);
        var modData = ReadEnvelopeData(modDocument.RootElement, "Mod 详情");
        var internalModId = ReadRequiredId(modData, "id", "Nexus 内部 Mod ID");
        var returnedGameId = ReadOptionalId(modData, "game_scoped_id") ?? gameScopedModId;
        ValidateNumericId(returnedGameId, "Nexus Mod ID");
        var modName = ReadOptionalString(modData, "name") ?? $"Nexus Mod #{gameScopedModId}";

        using var filesDocument = await GetApiJsonAsync(
            new Uri(ApiBase, $"mods/{internalModId}/files"),
            apiKey,
            NexusApiOperation.ReadData,
            cancellationToken);
        var filesData = ReadEnvelopeData(filesDocument.RootElement, "文件列表");
        if (!TryGetProperty(filesData, "mod_files", out var filesElement)
            || filesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Nexus 文件列表缺少 mod_files 数组。");
        }

        var rawFiles = filesElement.EnumerateArray().ToArray();
        if (rawFiles.Length > MaximumFilesPerMod)
        {
            throw new InvalidDataException($"Nexus 返回了超过 {MaximumFilesPerMod} 个文件，已拒绝加载异常响应。");
        }

        using var versionGate = new SemaphoreSlim(4);
        var fileTasks = rawFiles.Select(file => ReadFileAsync(file, apiKey, versionGate, cancellationToken));
        var files = await Task.WhenAll(fileTasks);
        return new NexusModDetails(
            internalModId,
            returnedGameId,
            modName,
            $"https://www.nexusmods.com/{GameDomain}/mods/{gameScopedModId}?tab=files",
            files);
    }

    public async Task<NexusDownloadResult> DownloadAsync(
        string gameScopedModId,
        string gameScopedFileId,
        IProgress<NexusDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        NexusDownloadTranslation? translation = null,
        NexusDownloadAuthorization? authorization = null)
    {
        ValidateNumericId(gameScopedModId, "Mod ID");
        ValidateNumericId(gameScopedFileId, "文件 ID");
        var normalizedTranslation = NormalizeDownloadTranslation(translation);
        if (authorization is not null)
        {
            ValidateDownloadAuthorization(authorization);
        }
        var apiKey = ReadApiKey();
        var linksUri = BuildDownloadLinkUri(gameScopedModId, gameScopedFileId, authorization);

        using var linksDocument = await GetApiJsonAsync(
            linksUri,
            apiKey,
            NexusApiOperation.DownloadLink,
            cancellationToken,
            authorization?.Key);
        if (linksDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Nexus 下载接口没有返回镜像列表。");
        }

        var mirrorUris = linksDocument.RootElement
            .EnumerateArray()
            .Select(item => ReadOptionalString(item, "URI"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && IsTrustedDownloadUri(uri))
            .Cast<Uri>()
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.Ordinal)
            .ToArray();
        if (mirrorUris.Length == 0)
        {
            throw new InvalidDataException("Nexus 未返回受信任的 HTTPS 下载镜像，请打开官方文件页继续下载。");
        }

        Exception? lastError = null;
        foreach (var mirrorUri in mirrorUris.Take(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            NexusDownloadResult result;
            try
            {
                result = await DownloadMirrorAsync(
                    mirrorUri,
                    gameScopedModId,
                    gameScopedFileId,
                    progress,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                lastError = error;
                continue;
            }

            if (normalizedTranslation is null)
            {
                return result;
            }

            try
            {
                var metadataPath = WriteTranslationSidecar(
                    Path.GetDirectoryName(result.Path)
                        ?? throw new InvalidDataException("下载文件路径缺少父目录。"),
                    gameScopedModId,
                    gameScopedFileId,
                    normalizedTranslation);
                return result with { MetadataPath = metadataPath };
            }
            catch (Exception error)
            {
                throw new IOException($"Mod 已下载，但无法保存翻译元数据：{error.Message}", error);
            }
        }

        throw new IOException(
            "Nexus 提供的下载镜像均不可用，请稍后重试或打开官方文件页下载。",
            lastError);
    }

    public Task<NexusDownloadResult> DownloadFromNxmAsync(
        string nxmLink,
        IProgress<NexusDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        NexusDownloadTranslation? translation = null)
    {
        var parsed = ParseNxmLink(nxmLink);
        return DownloadAsync(
            parsed.ModId,
            parsed.FileId,
            progress,
            cancellationToken,
            translation,
            parsed.Authorization);
    }

    public void OpenDownloadedFile(NexusDownloadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!File.Exists(result.Path))
        {
            throw new FileNotFoundException("下载文件已被移动或删除。", result.Path);
        }
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add($"/select,{result.Path}");
        Process.Start(startInfo);
    }

    private static async Task<NexusModFile> ReadFileAsync(
        JsonElement element,
        string apiKey,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        var fileId = ReadRequiredId(element, "id", "Nexus 文件 ID");
        var name = ReadOptionalString(element, "name") ?? $"文件 #{fileId}";
        var active = ReadOptionalBoolean(element, "is_active") ?? false;
        var versionsCount = ReadOptionalInt64(element, "versions_count") ?? 0;
        var uploadedAt = ReadOptionalDate(element, "last_file_uploaded_at");
        if (versionsCount < 0 || versionsCount > MaximumVersionsPerFile)
        {
            throw new InvalidDataException($"文件 {name} 的版本数量异常，已拒绝加载。");
        }

        IReadOnlyList<NexusFileVersion> versions = Array.Empty<NexusFileVersion>();
        if (versionsCount > 0)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                using var document = await GetApiJsonAsync(
                    new Uri(ApiBase, $"mod-files/{fileId}/versions"),
                    apiKey,
                    NexusApiOperation.ReadData,
                    cancellationToken);
                var data = ReadEnvelopeData(document.RootElement, "文件版本");
                if (!TryGetProperty(data, "versions", out var versionsElement)
                    || versionsElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"文件 {name} 的版本响应缺少 versions 数组。");
                }
                var rawVersions = versionsElement.EnumerateArray().ToArray();
                if (rawVersions.Length > MaximumVersionsPerFile)
                {
                    throw new InvalidDataException($"文件 {name} 返回了过多版本，已拒绝加载异常响应。");
                }
                versions = rawVersions.Select(ReadVersion).ToArray();
            }
            finally
            {
                gate.Release();
            }
        }

        return new NexusModFile(fileId, name, active, uploadedAt, versionsCount, versions);
    }

    private static NexusFileVersion ReadVersion(JsonElement element)
    {
        var id = ReadRequiredId(element, "id", "Nexus 版本 ID");
        var gameScopedId = ReadRequiredId(element, "game_scoped_id", "Nexus 文件 ID");
        ValidateNumericId(gameScopedId, "Nexus 文件 ID");
        return new NexusFileVersion(
            id,
            gameScopedId,
            ReadOptionalString(element, "name") ?? $"文件 #{gameScopedId}",
            ReadOptionalString(element, "version") ?? string.Empty,
            ReadOptionalString(element, "category") ?? string.Empty,
            ReadOptionalDate(element, "uploaded_at"),
            ReadOptionalBoolean(element, "is_primary") ?? false);
    }

    private static async Task<JsonDocument> GetApiJsonAsync(
        Uri uri,
        string apiKey,
        NexusApiOperation operation,
        CancellationToken cancellationToken,
        string? additionalSecret = null)
    {
        if (!IsTrustedApiUri(uri))
        {
            throw new InvalidOperationException("拒绝向非 Nexus API 地址发送凭据。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("apikey", apiKey);
        HttpResponseMessage response;
        try
        {
            response = await ApiClient.SendAsync(
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
                "无法连接 Nexus API，请检查网络或代理后重试。",
                null,
                error.StatusCode);
        }

        using (response)
        {
            await EnsureApiSuccessAsync(
                response,
                operation,
                cancellationToken,
                apiKey,
                additionalSecret);
            if (response.Content.Headers.ContentLength is > MaximumJsonBytes)
            {
                throw new InvalidDataException($"Nexus 响应超过 {MaximumJsonBytes / 1024} KB 的安全上限。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await ReadBoundedAsync(stream, MaximumJsonBytes, cancellationToken);
            try
            {
                return JsonDocument.Parse(bytes);
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Nexus 返回的数据格式无效。", error);
            }
        }
    }

    private static async Task<NexusDownloadResult> DownloadMirrorAsync(
        Uri mirrorUri,
        string modId,
        string fileId,
        IProgress<NexusDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsTrustedDownloadUri(mirrorUri))
        {
            throw new InvalidOperationException("拒绝从非 Nexus 域名下载文件。");
        }

        using var response = await SendDownloadRequestAsync(mirrorUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Nexus 下载服务器返回 HTTP {(int)response.StatusCode}。",
                null,
                response.StatusCode);
        }
        if (response.RequestMessage?.RequestUri is not { } finalUri || !IsTrustedDownloadUri(finalUri))
        {
            throw new InvalidOperationException("下载响应来自不受信任的地址。");
        }

        var totalBytes = response.Content.Headers.ContentLength;
        if (totalBytes is < 0 or > MaximumDownloadBytes)
        {
            throw new InvalidDataException($"下载文件超过 {MaximumDownloadBytes / 1024 / 1024} MB 的安全上限。");
        }

        var fileName = GetSafeFileName(response.Content.Headers.ContentDisposition, modId, fileId);
        var directory = GetDownloadDirectory();
        Directory.CreateDirectory(directory);
        var target = GetAvailableTarget(directory, fileName);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.part");
        long written = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var lastReport = Stopwatch.StartNew();
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                written = checked(written + read);
                if (written > MaximumDownloadBytes)
                {
                    throw new InvalidDataException($"下载文件超过 {MaximumDownloadBytes / 1024 / 1024} MB 的安全上限。");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (lastReport.ElapsedMilliseconds >= 200)
                {
                    progress?.Report(new NexusDownloadProgress(written, totalBytes));
                    lastReport.Restart();
                }
            }
            await output.FlushAsync(cancellationToken);
            if (written == 0)
            {
                throw new EndOfStreamException("Nexus 下载服务器返回了空文件。");
            }
            if (totalBytes.HasValue && written != totalBytes.Value)
            {
                throw new EndOfStreamException(
                    $"下载未完整结束：预期 {totalBytes.Value} 字节，实际收到 {written} 字节。");
            }
            File.Move(temporary, target, false);
            progress?.Report(new NexusDownloadProgress(written, totalBytes ?? written));
            return new NexusDownloadResult(target, Path.GetFileName(target), written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            TryDelete(temporary);
        }
    }

    private static async Task<HttpResponseMessage> SendDownloadRequestAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (!IsTrustedDownloadUri(currentUri))
            {
                throw new InvalidOperationException("拒绝从非 Nexus 域名下载文件。");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            HttpResponseMessage response;
            try
            {
                response = await DownloadClient.SendAsync(
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
                    "无法连接 Nexus 文件服务器，请检查网络或代理后重试。",
                    null,
                    error.StatusCode);
            }

            if (!IsRedirectStatus(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            var statusCode = response.StatusCode;
            response.Dispose();
            if (redirect == MaximumRedirects)
            {
                throw new HttpRequestException("Nexus 下载重定向次数过多，已停止下载。", null, statusCode);
            }
            if (location is null)
            {
                throw new HttpRequestException("Nexus 下载服务器返回了缺少 Location 的重定向。", null, statusCode);
            }

            var redirectedUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (!IsTrustedDownloadUri(redirectedUri))
            {
                throw new HttpRequestException("Nexus 下载重定向到了非受信任域名，已停止下载。", null, statusCode);
            }
            currentUri = redirectedUri;
        }

        throw new InvalidOperationException("无法完成 Nexus 下载重定向。");
    }

    private string ReadApiKey()
    {
        var key = _credentials.Read(CredentialService.NexusApiKeyTarget);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new NexusApiKeyMissingException(
                "请先前往“设置 > Nexus Mods”填写并保存 Personal API Key，再查看文件或下载。");
        }
        return NormalizeApiKey(
            key,
            "Windows 凭据管理器中的 Nexus API Key 格式无效，请在设置中重新保存。");
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Valley-Steward-WinUI/0.3 (Windows; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        return client;
    }

    private static async Task EnsureApiSuccessAsync(
        HttpResponseMessage response,
        NexusApiOperation operation,
        CancellationToken cancellationToken,
        params string?[] secrets)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new HttpRequestException("Nexus API 返回了重定向，已按照安全策略拒绝。", null, response.StatusCode);
        }
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await ReadErrorAsync(response, cancellationToken, secrets);
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"：{detail}";
        var message = operation switch
        {
            NexusApiOperation.DownloadLink => BuildDownloadLinkErrorMessage(
                response.StatusCode,
                suffix,
                secrets.Length > 1 && !string.IsNullOrEmpty(secrets[1])),
            NexusApiOperation.ValidateKey => response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    $"Nexus 拒绝了该 Personal API Key（HTTP {(int)response.StatusCode}）{suffix}。请确认复制的是个人 API Key。",
                HttpStatusCode.TooManyRequests => $"Nexus Key 验证请求过于频繁（HTTP 429）{suffix}，请稍后重试。",
                _ => $"Nexus Key 验证服务返回 HTTP {(int)response.StatusCode}{suffix}。",
            },
            _ => response.StatusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    $"Nexus 拒绝了 Personal API Key（HTTP 401）{suffix}。请在设置中重新保存有效 Key。",
                HttpStatusCode.Forbidden =>
                    $"Nexus 不允许读取该 Mod 或文件（HTTP 403）{suffix}。它可能已隐藏、受限或禁止管理器访问。",
                HttpStatusCode.NotFound => $"Nexus 未找到该 Mod 或文件（HTTP 404）{suffix}。",
                HttpStatusCode.TooManyRequests => $"Nexus 请求过于频繁（HTTP 429）{suffix}，请稍后重试。",
                _ => $"Nexus API 返回 HTTP {(int)response.StatusCode}{suffix}。",
            },
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static async Task<string?> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        params string?[] secrets)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var body = await ReadBoundedAsync(stream, MaximumErrorBytes, cancellationToken, rejectOverflow: false);
            var text = System.Text.Encoding.UTF8.GetString(body);
            try
            {
                using var json = JsonDocument.Parse(text);
                text = ReadOptionalString(json.RootElement, "message")
                    ?? ReadOptionalString(json.RootElement, "error")
                    ?? ReadOptionalString(json.RootElement, "detail")
                    ?? text;
            }
            catch (JsonException)
            {
                // Provider error bodies are not guaranteed to be JSON.
            }
            var redacted = text;
            foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)))
            {
                redacted = redacted.Replace(secret!, "[已隐藏]", StringComparison.Ordinal);
                redacted = redacted.Replace(Uri.EscapeDataString(secret!), "[已隐藏]", StringComparison.OrdinalIgnoreCase);
            }
            var compact = string.Join(' ', redacted.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return compact.Length > 300 ? compact[..300] : compact;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long limit,
        CancellationToken cancellationToken,
        bool rejectOverflow = true)
    {
        await using var memory = new MemoryStream((int)Math.Min(limit, 64 * 1024));
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (memory.Length <= limit)
            {
                var remaining = (int)Math.Min(buffer.Length, limit + 1 - memory.Length);
                if (remaining <= 0)
                {
                    break;
                }
                var read = await stream.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (memory.Length > limit && rejectOverflow)
            {
                throw new InvalidDataException($"Nexus 响应超过 {limit / 1024} KB 的安全上限。");
            }
            return memory.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string NormalizeApiKey(string value, string errorMessage)
    {
        value = value.Trim();
        if (value.Length is < MinimumApiKeyLength or > MaximumApiKeyLength
            || value.Any(character => character is < (char)0x21 or > (char)0x7e))
        {
            throw new InvalidDataException(errorMessage);
        }
        return value;
    }

    internal static Uri BuildDownloadLinkUri(
        string modId,
        string fileId,
        NexusDownloadAuthorization? authorization)
    {
        ValidateNumericId(modId, "Mod ID");
        ValidateNumericId(fileId, "文件 ID");
        var uri = new Uri(
            LegacyApiBase,
            $"games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json");
        if (authorization is null)
        {
            return uri;
        }

        ValidateDownloadAuthorization(authorization);
        var builder = new UriBuilder(uri)
        {
            Query = $"key={Uri.EscapeDataString(authorization.Key)}&expires={authorization.Expires.ToString(CultureInfo.InvariantCulture)}",
        };
        return builder.Uri;
    }

    private static Dictionary<string, string> ParseUniqueQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                throw new ArgumentException("NXM 链接查询参数格式无效。", nameof(query));
            }
            string name;
            string value;
            try
            {
                name = Uri.UnescapeDataString(part[..separator]);
                value = Uri.UnescapeDataString(part[(separator + 1)..]);
            }
            catch (UriFormatException error)
            {
                throw new ArgumentException("NXM 链接查询参数编码无效。", nameof(query), error);
            }
            if (string.IsNullOrWhiteSpace(name) || !values.TryAdd(name, value))
            {
                throw new ArgumentException("NXM 链接包含空白或重复查询参数。", nameof(query));
            }
        }
        return values;
    }

    private static void ValidateNxmKey(string key)
    {
        if (string.IsNullOrEmpty(key)
            || key.Length > MaximumNxmKeyLength
            || key.Any(character => character is < (char)0x21 or > (char)0x7e))
        {
            throw new ArgumentException("NXM 链接的 key 参数无效。", nameof(key));
        }
    }

    private static void ValidateDownloadAuthorization(NexusDownloadAuthorization authorization)
    {
        ValidateNxmKey(authorization.Key);
        if (authorization.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            throw new NexusNxmLinkExpiredException("NXM 下载授权已经过期，请重新从 Nexus 官网点击“Mod Manager Download”。");
        }
    }

    private static string BuildDownloadLinkErrorMessage(
        HttpStatusCode statusCode,
        string detailSuffix,
        bool hasNxmAuthorization)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest when hasNxmAuthorization =>
                $"Nexus 拒绝了 NXM 下载授权（HTTP 400）{detailSuffix}。链接可能与当前账号、Mod 或文件不匹配，请从官方文件页重新获取。",
            HttpStatusCode.Unauthorized =>
                $"Nexus 拒绝了 Personal API Key（HTTP 401）{detailSuffix}。请在设置中重新保存有效 Key。",
            HttpStatusCode.Forbidden when hasNxmAuthorization =>
                $"Nexus 不允许使用该 NXM 授权下载（HTTP 403）{detailSuffix}。请确认链接来自当前账号且作者允许管理器下载。",
            HttpStatusCode.Forbidden =>
                $"Nexus 普通账户不能直接生成 API 下载链接（HTTP 403）{detailSuffix}。Premium 账户可直接下载；普通账户请先在官方文件页点击“Mod Manager Download”取得 NXM 链接。",
            HttpStatusCode.NotFound =>
                $"Nexus 未找到对应的 Mod 或文件（HTTP 404）{detailSuffix}。它可能已归档或删除。",
            HttpStatusCode.Gone =>
                $"NXM 下载授权已过期（HTTP 410）{detailSuffix}。请从官方文件页重新获取。",
            HttpStatusCode.TooManyRequests =>
                $"Nexus 下载请求过于频繁（HTTP 429）{detailSuffix}，请稍后重试。",
            _ when (int)statusCode >= 500 =>
                $"Nexus 下载服务暂时不可用（HTTP {(int)statusCode}）{detailSuffix}，请稍后重试。",
            _ => $"Nexus 无法生成下载地址（HTTP {(int)statusCode}）{detailSuffix}，请打开官方文件页继续。",
        };
    }

    private static bool IsRedirectStatus(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static NexusDownloadTranslation? NormalizeDownloadTranslation(
        NexusDownloadTranslation? translation)
    {
        if (translation is null)
        {
            return null;
        }
        var name = translation.Name.Trim();
        var description = translation.Description.Trim();
        if (name.Length == 0 || name.EnumerateRunes().Count() > MaximumTranslatedNameCharacters)
        {
            throw new ArgumentException("下载译名为空或过长。", nameof(translation));
        }
        if (description.Length == 0
            || description.EnumerateRunes().Count() > MaximumTranslatedDescriptionCharacters)
        {
            throw new ArgumentException("下载译文为空或过长。", nameof(translation));
        }
        if (name.Any(char.IsControl)
            || description.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t'))
        {
            throw new ArgumentException("下载翻译元数据包含无效控制字符。", nameof(translation));
        }
        return new NexusDownloadTranslation(name, description);
    }

    internal static string WriteTranslationSidecar(
        string directory,
        string modId,
        string fileId,
        NexusDownloadTranslation translation)
    {
        ValidateNumericId(modId, "Mod ID");
        ValidateNumericId(fileId, "文件 ID");
        var normalized = NormalizeDownloadTranslation(translation)
            ?? throw new ArgumentNullException(nameof(translation));
        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var target = Path.GetFullPath(Path.Combine(
            fullDirectory,
            $"nexus-{modId}-{fileId}.valley-steward.json"));
        if (!string.Equals(Path.GetDirectoryName(target), fullDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("翻译元数据路径无效。");
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(
            new DownloadTranslationSidecar(
                DownloadSidecarSchemaVersion,
                DownloadSidecarProvider,
                modId,
                fileId,
                normalized.Name,
                normalized.Description),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            });

        lock (SidecarWriteLock)
        {
            try
            {
                var attributes = File.GetAttributes(target);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new InvalidDataException("拒绝覆盖不是普通文件的下载翻译元数据。");
                }
            }
            catch (FileNotFoundException)
            {
                // A missing sidecar is the expected first-download state.
            }
            catch (DirectoryNotFoundException)
            {
                // The directory was created above; a missing target is safe to create.
            }

            var temporary = Path.Combine(
                fullDirectory,
                $".valley-steward-nexus-translation-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(content);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        return target;
    }

    private static bool IsTrustedApiUri(Uri uri)
    {
        return uri.IsAbsoluteUri
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase)
            && uri.UserInfo.Length == 0
            && uri.IsDefaultPort;
    }

    internal static bool IsTrustedDownloadUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length > 0
            || !uri.IsDefaultPort)
        {
            return false;
        }
        var host = uri.IdnHost.TrimEnd('.');
        return IsDomainOrSubdomain(host, "nexusmods.com")
            || IsDomainOrSubdomain(host, "nexus-cdn.com");
    }

    private static bool IsDomainOrSubdomain(string host, string domain)
    {
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDownloadDirectory()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new DirectoryNotFoundException("无法确定当前用户的应用缓存目录。");
        }
        return Path.GetFullPath(Path.Combine(
            localData,
            "com.summerxdsss.valleysteward",
            "cache",
            "downloads"));
    }

    internal static string GetSafeFileName(
        ContentDispositionHeaderValue? contentDisposition,
        string modId,
        string fileId)
    {
        var proposed = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        proposed = proposed?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(proposed))
        {
            try
            {
                proposed = Uri.UnescapeDataString(proposed);
            }
            catch (UriFormatException)
            {
                proposed = null;
            }
        }
        proposed = Path.GetFileName(proposed ?? string.Empty);
        if (string.IsNullOrWhiteSpace(proposed))
        {
            proposed = $"nexus-{modId}-{fileId}.zip";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(proposed
            .Where(character => !invalid.Contains(character) && !char.IsControl(character))
            .ToArray())
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe))
        {
            return $"nexus-{modId}-{fileId}.zip";
        }
        if (ReservedWindowsFileNames.Contains(Path.GetFileNameWithoutExtension(safe)))
        {
            safe = $"_{safe}";
        }
        if (safe.Length > MaximumFileNameLength)
        {
            var extension = Path.GetExtension(safe);
            if (extension.Length > 20)
            {
                extension = string.Empty;
            }
            var stem = Path.GetFileNameWithoutExtension(safe);
            var stemLimit = MaximumFileNameLength - extension.Length;
            safe = stem[..Math.Min(stemLimit, stem.Length)].TrimEnd('.', ' ') + extension;
        }
        return string.IsNullOrWhiteSpace(safe) ? $"nexus-{modId}-{fileId}.zip" : safe;
    }

    internal static string GetAvailableTarget(string directory, string fileName)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var target = GetContainedTarget(fullDirectory, fileName);
        if (!Path.Exists(target))
        {
            return target;
        }
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; suffix <= 999; suffix++)
        {
            target = GetContainedTarget(fullDirectory, $"{stem} ({suffix}){extension}");
            if (!Path.Exists(target))
            {
                return target;
            }
        }
        throw new IOException("下载目录中存在过多同名文件，请先整理后重试。");
    }

    private static string GetContainedTarget(string directory, string fileName)
    {
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidDataException("下载文件名无效。");
        }
        var target = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!string.Equals(Path.GetDirectoryName(target), directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("下载文件路径越出了应用缓存目录。");
        }
        return target;
    }

    private static JsonElement ReadEnvelopeData(JsonElement root, string label)
    {
        if (!TryGetProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Nexus {label}响应缺少 data 对象。");
        }
        return data;
    }

    private static string ReadRequiredId(JsonElement element, string name, string label)
    {
        var value = ReadOptionalId(element, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Nexus 响应缺少{label}。");
        }
        if (value.Length > 64 || !value.All(char.IsAsciiLetterOrDigit))
        {
            throw new InvalidDataException($"Nexus 返回的{label}格式无效。");
        }
        return value;
    }

    private static string? ReadOptionalId(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    }

    private static bool? ReadOptionalBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static long? ReadOptionalInt64(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) && value.TryGetInt64(out var result)
            ? result
            : null;
    }

    private static DateTimeOffset? ReadOptionalDate(JsonElement element, string name)
    {
        return DateTimeOffset.TryParse(ReadOptionalString(element, name), out var date) ? date : null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static void ValidateNumericId(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 20
            || !value.All(char.IsAsciiDigit))
        {
            throw new ArgumentException($"{label} 无效。", nameof(value));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The original download error is more useful than cleanup failure.
        }
    }

    private enum NexusApiOperation
    {
        ReadData,
        ValidateKey,
        DownloadLink,
    }

    private sealed record DownloadTranslationSidecar(
        int SchemaVersion,
        string Provider,
        string ModId,
        string FileId,
        string Name,
        string Description);
}

public sealed class NexusApiKeyMissingException : InvalidOperationException
{
    public NexusApiKeyMissingException(string message) : base(message)
    {
    }
}

public sealed class NexusNxmLinkExpiredException : InvalidOperationException
{
    public NexusNxmLinkExpiredException(string message) : base(message)
    {
    }
}
