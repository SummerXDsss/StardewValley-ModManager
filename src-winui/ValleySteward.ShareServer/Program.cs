using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<ShareStore>();
builder.Services.AddSingleton<ShareRateLimiter>();

var app = builder.Build();

app.MapGet("/", () => Results.Json(new
{
    name = "Valley Steward Share API",
    status = "ok",
    endpoints = new[]
    {
        "GET /health",
        "GET /api/shares",
        "GET /api/shares/{code}",
        "GET /api/my-shares",
        "POST /api/my-shares/claim",
        "POST /api/shares",
        "PUT /api/shares/{code}/visibility",
        "DELETE /api/shares/{code}",
    },
}));

app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow }));

app.MapGet("/api/shares", async (
    string? q,
    bool? includeSearchOnly,
    ShareStore store,
    CancellationToken cancellationToken) =>
{
    var shares = await store.ListAsync(q, includeSearchOnly == true, cancellationToken);
    return Results.Ok(shares.Select(ShareEntryDto.FromEntry));
});

app.MapGet("/api/my-shares", async (HttpContext context, ShareStore store, CancellationToken cancellationToken) =>
{
    var uploadIp = IpReader.GetUploadIp(context);
    var shares = await store.ListOwnedAsync(uploadIp, cancellationToken);
    return Results.Ok(shares.Select(ShareEntryDto.FromEntry));
});

app.MapPost("/api/my-shares/claim", async (HttpContext context, ShareStore store, CancellationToken cancellationToken) =>
{
    var uploadIp = IpReader.GetUploadIp(context);
    var shares = await store.ListOwnedAsync(uploadIp, cancellationToken);
    var dtos = shares.Select(ShareEntryDto.FromEntry).ToArray();
    return Results.Ok(new ShareClaimDto(IpReader.Mask(uploadIp), dtos.Length, dtos));
});

app.MapGet("/api/shares/{code}", async (string code, ShareStore store, CancellationToken cancellationToken) =>
{
    if (!ShareCode.IsValid(code))
    {
        return Results.BadRequest(new { error = "分享码必须是 10 位字母数字组合。" });
    }

    var entry = await store.GetAsync(code, cancellationToken);
    return entry is null ? Results.NotFound(new { error = "分享不存在或已被清理。" }) : Results.Ok(ShareEntryDto.FromEntry(entry));
});

app.MapPost("/api/shares", async (
    ShareRequest request,
    HttpContext context,
    ShareStore store,
    ShareRateLimiter rateLimiter,
    CancellationToken cancellationToken) =>
{
    var now = DateTimeOffset.UtcNow;
    var uploadIp = IpReader.GetUploadIp(context);
    if (!rateLimiter.Allow(uploadIp, now, out var retryAfter))
    {
        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return Results.Json(
            new { error = "分享发布过于频繁，请稍后再试。" },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var validation = ShareValidator.Validate(request);
    if (validation is not null)
    {
        return Results.BadRequest(new { error = validation });
    }

    var entry = new ShareEntry(
        ShareCode.Create(),
        ShareValidator.NormalizeComputerName(request.ComputerName),
        uploadIp,
        now,
        request.Mods
            .Take(ShareValidator.MaximumMods)
            .Select(ShareValidator.NormalizeMod)
            .ToArray(),
        ShareVisibility.Public);

    entry = await store.AddAsync(entry, cancellationToken);
    return Results.Created($"/api/shares/{entry.Code}", ShareEntryDto.FromEntry(entry));
});

app.MapPut("/api/shares/{code}/visibility", async (
    string code,
    ShareVisibilityRequest request,
    HttpContext context,
    ShareStore store,
    CancellationToken cancellationToken) =>
{
    if (!ShareCode.IsValid(code))
    {
        return Results.BadRequest(new { error = "分享码必须是 10 位字母数字组合。" });
    }

    if (!Enum.IsDefined(request.Visibility))
    {
        return Results.BadRequest(new { error = "分享可见性无效。" });
    }

    var uploadIp = IpReader.GetUploadIp(context);
    var update = await store.UpdateVisibilityOwnedAsync(code, uploadIp, request.Visibility, cancellationToken);
    return update.Result switch
    {
        ShareStoreMutationResult.Success when update.Entry is not null => Results.Ok(ShareEntryDto.FromEntry(update.Entry)),
        ShareStoreMutationResult.NotFound => Results.NotFound(new { error = "分享不存在或已被清理。" }),
        ShareStoreMutationResult.Forbidden => Results.Json(new { error = "只能管理当前 IP 发布或认领的分享。" }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Json(new { error = "分享可见性更新失败。" }, statusCode: StatusCodes.Status500InternalServerError),
    };
});

app.MapDelete("/api/shares/{code}", async (
    string code,
    HttpContext context,
    ShareStore store,
    CancellationToken cancellationToken) =>
{
    if (!ShareCode.IsValid(code))
    {
        return Results.BadRequest(new { error = "分享码必须是 10 位字母数字组合。" });
    }

    var uploadIp = IpReader.GetUploadIp(context);
    var result = await store.DeleteOwnedAsync(code, uploadIp, cancellationToken);
    return result switch
    {
        ShareStoreMutationResult.Success => Results.NoContent(),
        ShareStoreMutationResult.NotFound => Results.NotFound(new { error = "分享不存在或已被清理。" }),
        ShareStoreMutationResult.Forbidden => Results.Json(new { error = "只能删除当前 IP 发布或认领的分享。" }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Json(new { error = "分享删除失败。" }, statusCode: StatusCodes.Status500InternalServerError),
    };
});

app.Run();

internal sealed class ShareStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ShareStore(IConfiguration configuration)
    {
        _path = configuration["VALLEY_STEWARD_SHARE_STORE"]
            ?? Environment.GetEnvironmentVariable("VALLEY_STEWARD_SHARE_STORE")
            ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "shares.json");
    }

    public async Task<IReadOnlyList<ShareEntry>> ListAsync(
        string? query,
        bool includeSearchOnly,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var normalizedQuery = NormalizeQuery(query);
            return (await LoadAsync(cancellationToken))
                .Where(entry => IsVisibleForHall(entry, includeSearchOnly))
                .Where(entry => normalizedQuery is null || MatchesQuery(entry, normalizedQuery))
                .OrderByDescending(entry => entry.UploadedAt)
                .Take(100)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShareEntry>> ListOwnedAsync(string uploadIp, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .Where(entry => IsOwner(entry, uploadIp))
                .OrderByDescending(entry => entry.UploadedAt)
                .Take(300)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShareEntry?> GetAsync(string code, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .FirstOrDefault(entry => string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShareStoreUpdateResult> UpdateVisibilityOwnedAsync(
        string code,
        string uploadIp,
        ShareVisibility visibility,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = (await LoadAsync(cancellationToken)).ToList();
            var index = entries.FindIndex(entry => string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return new ShareStoreUpdateResult(ShareStoreMutationResult.NotFound, null);
            }
            if (!IsOwner(entries[index], uploadIp))
            {
                return new ShareStoreUpdateResult(ShareStoreMutationResult.Forbidden, null);
            }

            var updated = entries[index] with { Visibility = visibility };
            entries[index] = updated;
            await SaveAsync(entries, cancellationToken);
            return new ShareStoreUpdateResult(ShareStoreMutationResult.Success, updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShareStoreMutationResult> DeleteOwnedAsync(
        string code,
        string uploadIp,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = (await LoadAsync(cancellationToken)).ToList();
            var index = entries.FindIndex(entry => string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return ShareStoreMutationResult.NotFound;
            }
            if (!IsOwner(entries[index], uploadIp))
            {
                return ShareStoreMutationResult.Forbidden;
            }

            entries.RemoveAt(index);
            await SaveAsync(entries, cancellationToken);
            return ShareStoreMutationResult.Success;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShareEntry> AddAsync(ShareEntry entry, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = (await LoadAsync(cancellationToken)).ToList();
            while (entries.Any(existing => string.Equals(existing.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
            {
                entry = entry with { Code = ShareCode.Create() };
            }
            entries.Add(entry);
            entries = entries
                .OrderByDescending(item => item.UploadedAt)
                .Take(1000)
                .ToList();
            await SaveAsync(entries, cancellationToken);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ShareEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<ShareEntry>();
        }

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > 32L * 1024 * 1024)
        {
            throw new InvalidDataException("分享存储文件超过安全上限。");
        }

        return await JsonSerializer.DeserializeAsync<List<ShareEntry>>(stream, _jsonOptions, cancellationToken)
            ?? new List<ShareEntry>();
    }

    private async Task SaveAsync(IReadOnlyList<ShareEntry> entries, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, entries, _jsonOptions, cancellationToken);
        }
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static bool IsOwner(ShareEntry entry, string uploadIp)
    {
        return string.Equals(entry.UploadIp, uploadIp, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisibleForHall(ShareEntry entry, bool includeSearchOnly)
    {
        return entry.Visibility == ShareVisibility.Public
            || includeSearchOnly && entry.Visibility == ShareVisibility.SearchOnly;
    }

    private static string? NormalizeQuery(string? query)
    {
        query = query?.Trim();
        return string.IsNullOrWhiteSpace(query)
            ? null
            : query.Length > 128 ? query[..128] : query;
    }

    private static bool MatchesQuery(ShareEntry entry, string query)
    {
        return Contains(entry.Code, query)
            || Contains(entry.ComputerName, query)
            || Contains(IpReader.Mask(entry.UploadIp), query)
            || entry.Mods.Any(mod =>
                Contains(mod.Name, query)
                || Contains(mod.UniqueId, query)
                || Contains(mod.Author, query)
                || Contains(mod.Provider, query)
                || Contains(mod.Version, query)
                || mod.UpdateKeys.Any(key => Contains(key, query)));
    }

    private static bool Contains(string? text, string query)
    {
        return text?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

internal sealed class ShareRateLimiter
{
    private const int MaximumPostsPerWindow = 12;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<string, ClientWindow> _clients = new(StringComparer.OrdinalIgnoreCase);

    public bool Allow(string ip, DateTimeOffset now, out TimeSpan retryAfter)
    {
        var key = string.IsNullOrWhiteSpace(ip) ? "0.0.0.0" : ip.Trim();
        lock (_gate)
        {
            PruneExpired(now);
            if (!_clients.TryGetValue(key, out var window)
                || now - window.StartedAt >= Window)
            {
                _clients[key] = new ClientWindow(now, 1);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            if (window.Count >= MaximumPostsPerWindow)
            {
                retryAfter = (window.StartedAt + Window) - now;
                if (retryAfter < TimeSpan.FromSeconds(1))
                {
                    retryAfter = TimeSpan.FromSeconds(1);
                }
                return false;
            }

            _clients[key] = window with { Count = window.Count + 1 };
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        if (_clients.Count < 512)
        {
            return;
        }

        foreach (var key in _clients
            .Where(item => now - item.Value.StartedAt >= Window)
            .Select(item => item.Key)
            .ToArray())
        {
            _clients.Remove(key);
        }
    }

    private sealed record ClientWindow(DateTimeOffset StartedAt, int Count);
}

internal static class ShareValidator
{
    public const int MaximumMods = 300;
    private const int MaximumComputerName = 64;
    private const int MaximumText = 512;
    private const int MaximumDescription = 2000;
    private const int MaximumUrl = 2048;

    public static string? Validate(ShareRequest request)
    {
        if (request.Mods.Count is < 1 or > MaximumMods)
        {
            return $"每次分享需要包含 1 到 {MaximumMods} 个 Mod。";
        }
        if (request.Mods.Any(mod => string.IsNullOrWhiteSpace(mod.Name) || string.IsNullOrWhiteSpace(mod.UniqueId)))
        {
            return "每个 Mod 都需要名称和 UniqueID。";
        }
        if (request.Mods.Any(mod => TextLength(mod.Name) > MaximumText
            || TextLength(mod.UniqueId) > MaximumText
            || TextLength(mod.Author) > MaximumText
            || TextLength(mod.Version) > MaximumText
            || TextLength(mod.Description) > MaximumDescription))
        {
            return "Mod 文本字段过长。";
        }
        if (request.Mods.Any(mod => TextLength(mod.SourceUrl) > MaximumUrl
            || TextLength(mod.GitHubReleaseUrl) > MaximumUrl
            || TextLength(mod.CoverUrl) > MaximumUrl))
        {
            return "Mod URL 字段过长。";
        }
        return null;
    }

    public static string NormalizeComputerName(string? value)
    {
        value = NormalizeText(value, MaximumComputerName);
        return string.IsNullOrWhiteSpace(value) ? "未知电脑" : value;
    }

    public static SharedMod NormalizeMod(ShareModRequest request)
    {
        var sourceUrl = NormalizeUrl(request.SourceUrl);
        var githubReleaseUrl = NormalizeUrl(request.GitHubReleaseUrl);
        var provider = KnownProvider.Detect(request.UpdateKeys, sourceUrl, githubReleaseUrl);
        return new SharedMod(
            NormalizeText(request.Name, MaximumText) ?? "Unnamed Mod",
            NormalizeText(request.UniqueId, MaximumText) ?? "Unknown.UniqueId",
            NormalizeText(request.Author, MaximumText),
            NormalizeText(request.Version, MaximumText),
            NormalizeText(request.Description, MaximumDescription),
            request.Enabled,
            request.UpdateKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => NormalizeText(key, MaximumText))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(16)
                .ToArray(),
            provider.Name,
            provider.CanOpenOriginal ? provider.OriginalUrl : null,
            provider.CanDirectDownload ? provider.GitHubReleaseUrl ?? provider.OriginalUrl : null,
            provider.GitHubReleaseUrl,
            NormalizeTrustedImageUrl(request.CoverUrl));
    }

    private static int TextLength(string? value) => value?.Trim().Length ?? 0;

    private static string? NormalizeText(string? value, int limit)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        value = string.Concat(value.Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t'));
        return value.Length > limit ? value[..limit] : value;
    }

    private static string? NormalizeUrl(string? value)
    {
        value = NormalizeText(value, MaximumUrl);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.UserInfo.Length == 0
            && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null;
    }

    private static string? NormalizeTrustedImageUrl(string? value)
    {
        value = NormalizeText(value, MaximumUrl);
        if (value?.StartsWith("//", StringComparison.Ordinal) == true)
        {
            value = "https:" + value;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.UserInfo.Length == 0
            && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;
    }
}

internal static class KnownProvider
{
    public static ProviderInfo Detect(IReadOnlyList<string> updateKeys, string? sourceUrl, string? githubReleaseUrl)
    {
        var github = githubReleaseUrl ?? DetectGitHubRelease(updateKeys) ?? DetectGitHubUrl(sourceUrl);
        var nexus = DetectNexus(updateKeys, sourceUrl);
        if (nexus is not null)
        {
            return new ProviderInfo("Nexus Mods", nexus, github, true, github is not null);
        }

        if (github is not null)
        {
            return new ProviderInfo("GitHub", github, github, true, true);
        }

        if (sourceUrl is not null && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return new ProviderInfo(uri.Host, sourceUrl, null, true, false);
        }

        return new ProviderInfo("用户分享", null, null, false, false);
    }

    private static string? DetectNexus(IReadOnlyList<string> updateKeys, string? sourceUrl)
    {
        var key = updateKeys.FirstOrDefault(value => value.StartsWith("Nexus:", StringComparison.OrdinalIgnoreCase));
        if (key is not null)
        {
            var id = key["Nexus:".Length..].Trim();
            if (id.All(char.IsDigit))
            {
                return $"https://www.nexusmods.com/stardewvalley/mods/{id}";
            }
        }
        return IsHost(sourceUrl, "nexusmods.com") ? sourceUrl : null;
    }

    private static string? DetectGitHubRelease(IReadOnlyList<string> updateKeys)
    {
        var key = updateKeys.FirstOrDefault(value => value.StartsWith("GitHub:", StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            return null;
        }

        var repo = key["GitHub:".Length..].Trim().Trim('/');
        return repo.Count(character => character == '/') == 1
            && repo.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/')
            ? $"https://github.com/{repo}/releases/latest"
            : null;
    }

    private static string? DetectGitHubUrl(string? sourceUrl)
    {
        if (!IsHost(sourceUrl, "github.com") || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            ? $"https://github.com/{segments[0]}/{segments[1]}/releases/latest"
            : sourceUrl;
    }

    private static bool IsHost(string? value, string hostSuffix)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Host.Equals(hostSuffix, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + hostSuffix, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class ShareCode
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string Create()
    {
        Span<char> chars = stackalloc char[10];
        do
        {
            for (var index = 0; index < chars.Length; index++)
            {
                chars[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        }
        while (!chars.ContainsAnyInRange('0', '9')
            || !chars.ContainsAnyInRange('A', 'Z') && !chars.ContainsAnyInRange('a', 'z'));
        return new string(chars);
    }

    public static bool IsValid(string value)
    {
        return value.Length == 10 && value.All(char.IsLetterOrDigit);
    }
}

internal static class IpReader
{
    public static string GetUploadIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var realIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
        var raw = forwarded?.Split(',').Select(value => value.Trim()).FirstOrDefault(value => value.Length > 0)
            ?? realIp
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "0.0.0.0";
        return IPAddress.TryParse(raw, out var address) ? address.ToString() : "0.0.0.0";
    }

    public static string Mask(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return "?.*.*.?";
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return $"{bytes[0]}.*.*.{bytes[3]}";
        }
        var text = address.ToString();
        var parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}:*:*:{parts[^1]}" : "*:*";
    }
}

internal sealed record ShareRequest(
    string? ComputerName,
    IReadOnlyList<ShareModRequest> Mods);

internal sealed record ShareModRequest(
    string Name,
    string UniqueId,
    string? Author,
    string? Version,
    string? Description,
    bool Enabled,
    IReadOnlyList<string> UpdateKeys,
    string? SourceUrl,
    string? GitHubReleaseUrl,
    string? CoverUrl);

internal sealed record ShareEntry(
    string Code,
    string ComputerName,
    string UploadIp,
    DateTimeOffset UploadedAt,
    IReadOnlyList<SharedMod> Mods,
    ShareVisibility Visibility = ShareVisibility.Public);

internal sealed record SharedMod(
    string Name,
    string UniqueId,
    string? Author,
    string? Version,
    string? Description,
    bool Enabled,
    IReadOnlyList<string> UpdateKeys,
    string Provider,
    string? OriginalUrl,
    string? DirectDownloadUrl,
    string? GitHubReleaseUrl,
    string? CoverUrl);

internal sealed record ProviderInfo(
    string Name,
    string? OriginalUrl,
    string? GitHubReleaseUrl,
    bool CanOpenOriginal,
    bool CanDirectDownload);

internal enum ShareVisibility
{
    Public,
    SearchOnly,
}

internal enum ShareStoreMutationResult
{
    Success,
    NotFound,
    Forbidden,
}

internal sealed record ShareStoreUpdateResult(
    ShareStoreMutationResult Result,
    ShareEntry? Entry);

internal sealed record ShareVisibilityRequest(ShareVisibility Visibility);

internal sealed record ShareClaimDto(
    string MaskedIp,
    int Count,
    IReadOnlyList<ShareEntryDto> Shares);

internal sealed record ShareEntryDto(
    string Code,
    string ComputerName,
    string MaskedIp,
    DateTimeOffset UploadedAt,
    int ModCount,
    ShareVisibility Visibility,
    string VisibilityText,
    IReadOnlyList<string> CoverUrls,
    IReadOnlyList<SharedMod> Mods)
{
    public static ShareEntryDto FromEntry(ShareEntry entry)
    {
        return new ShareEntryDto(
            entry.Code,
            entry.ComputerName,
            IpReader.Mask(entry.UploadIp),
            entry.UploadedAt,
            entry.Mods.Count,
            entry.Visibility,
            entry.Visibility == ShareVisibility.Public ? "全部公开" : "仅搜索可见",
            entry.Mods
                .Select(mod => mod.CoverUrl)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.Ordinal)
                .Take(10)
                .OfType<string>()
                .ToArray(),
            entry.Mods);
    }
}
