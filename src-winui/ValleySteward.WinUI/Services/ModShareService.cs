using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed class ModShareService
{
    public const string ApiBaseUrl = "http://x-svalley-api.summercn.cn";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly HttpClient Client = CreateClient();

    public async Task<ModSharePublishResult> PublishAsync(
        IReadOnlyList<InstalledMod> mods,
        CancellationToken cancellationToken = default)
    {
        var request = new ModShareRequest(
            Environment.MachineName,
            mods
                .Where(mod => mod.Health == "healthy")
                .Take(300)
                .Select(CreateModRequest)
                .ToArray());
        if (request.Mods.Count == 0)
        {
            throw new InvalidOperationException("没有可分享的健康 Mod。");
        }

        using var response = await Client.PostAsJsonAsync(
            BuildUri("/api/shares"),
            request,
            JsonOptions,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var entry = await response.Content.ReadFromJsonAsync<ModShareEntry>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("分享服务返回空响应。");
        return new ModSharePublishResult(
            entry.Code,
            $"{ApiBaseUrl.TrimEnd('/')}/api/shares/{entry.Code}",
            entry);
    }

    public async Task<IReadOnlyList<ModShareEntry>> ListAsync(
        string? query = null,
        bool includeSearchOnly = false,
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(BuildListUri(query, includeSearchOnly), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ModShareEntry>>(JsonOptions, cancellationToken)
            ?? Array.Empty<ModShareEntry>();
    }

    public async Task<IReadOnlyList<ModShareEntry>> GetMineAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(BuildUri("/api/my-shares"), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ModShareEntry>>(JsonOptions, cancellationToken)
            ?? Array.Empty<ModShareEntry>();
    }

    public async Task<ModShareClaimResult> ClaimMineAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.PostAsync(BuildUri("/api/my-shares/claim"), content: null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ModShareClaimResult>(JsonOptions, cancellationToken)
            ?? new ModShareClaimResult("?.*.*.?", 0, Array.Empty<ModShareEntry>());
    }

    public async Task<ModShareEntry> GetAsync(string code, CancellationToken cancellationToken = default)
    {
        code = code.Trim();
        if (!IsValidShareCode(code))
        {
            throw new ArgumentException("分享码必须是 10 位数字或字母。", nameof(code));
        }

        using var response = await Client.GetAsync(
            BuildUri($"/api/shares/{Uri.EscapeDataString(code)}"),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ModShareEntry>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("分享服务返回空响应。");
    }

    public async Task<ModShareEntry> UpdateVisibilityAsync(
        string code,
        ShareVisibility visibility,
        CancellationToken cancellationToken = default)
    {
        code = code.Trim();
        if (!IsValidShareCode(code))
        {
            throw new ArgumentException("分享码必须是 10 位数字或字母。", nameof(code));
        }

        using var response = await Client.PutAsJsonAsync(
            BuildUri($"/api/shares/{Uri.EscapeDataString(code)}/visibility"),
            new ModShareVisibilityRequest(visibility),
            JsonOptions,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ModShareEntry>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("分享服务返回空响应。");
    }

    public async Task DeleteAsync(string code, CancellationToken cancellationToken = default)
    {
        code = code.Trim();
        if (!IsValidShareCode(code))
        {
            throw new ArgumentException("分享码必须是 10 位数字或字母。", nameof(code));
        }

        using var response = await Client.DeleteAsync(
            BuildUri($"/api/shares/{Uri.EscapeDataString(code)}"),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    internal static bool IsValidShareCode(string value)
    {
        return value.Length == 10 && value.All(char.IsLetterOrDigit);
    }

    private static ModShareModRequest CreateModRequest(InstalledMod mod)
    {
        var sourceUrl = BuildOriginalUrl(mod.UpdateKeys);
        var githubReleaseUrl = BuildGitHubReleaseUrl(mod.UpdateKeys) ?? DetectGitHubReleaseUrl(sourceUrl);
        return new ModShareModRequest(
            mod.Name,
            mod.Id,
            mod.Author,
            mod.Version,
            mod.Description,
            mod.Enabled,
            mod.UpdateKeys,
            sourceUrl,
            githubReleaseUrl,
            BuildCoverUrl(mod.UpdateKeys, sourceUrl));
    }

    internal static string? BuildOriginalUrl(IReadOnlyList<string> updateKeys)
    {
        foreach (var key in updateKeys)
        {
            if (key.StartsWith("Nexus:", StringComparison.OrdinalIgnoreCase))
            {
                var id = key["Nexus:".Length..].Trim();
                if (id.All(char.IsDigit))
                {
                    return $"https://www.nexusmods.com/stardewvalley/mods/{id}";
                }
            }
            if (key.StartsWith("GitHub:", StringComparison.OrdinalIgnoreCase)
                && BuildGitHubReleaseUrl([key]) is { } githubRelease)
            {
                return githubRelease;
            }
        }
        return null;
    }

    internal static string? BuildGitHubReleaseUrl(IReadOnlyList<string> updateKeys)
    {
        var key = updateKeys.FirstOrDefault(value => value.StartsWith("GitHub:", StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            return null;
        }

        var repository = key["GitHub:".Length..].Trim().Trim('/');
        return repository.Count(character => character == '/') == 1
            && repository.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/')
            ? $"https://github.com/{repository}/releases/latest"
            : null;
    }

    internal static string? BuildCoverUrl(IReadOnlyList<string> updateKeys, string? sourceUrl)
    {
        return TryGetGitHubOwner(updateKeys, sourceUrl, out var owner)
            ? $"https://github.com/{Uri.EscapeDataString(owner)}.png?size=96"
            : null;
    }

    private static string? DetectGitHubReleaseUrl(string? sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? $"https://github.com/{segments[0]}/{segments[1]}/releases/latest" : sourceUrl;
    }

    private static bool TryGetGitHubOwner(
        IReadOnlyList<string> updateKeys,
        string? sourceUrl,
        out string owner)
    {
        foreach (var key in updateKeys)
        {
            if (!key.StartsWith("GitHub:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var repository = key["GitHub:".Length..].Trim().Trim('/');
            var separator = repository.IndexOf('/');
            if (separator > 0
                && repository.Count(character => character == '/') == 1
                && IsGitHubPathSegment(repository[..separator]))
            {
                owner = repository[..separator];
                return true;
            }
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && IsGitHubPathSegment(segments[0]))
            {
                owner = segments[0];
                return true;
            }
        }

        owner = string.Empty;
        return false;
    }

    private static bool IsGitHubPathSegment(string value)
    {
        return value.Length is > 0 and <= 100
            && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static Uri BuildUri(string path)
    {
        return new Uri(new Uri(ApiBaseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
    }

    private static Uri BuildListUri(string? query, bool includeSearchOnly)
    {
        var parameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            parameters.Add($"q={Uri.EscapeDataString(query.Trim())}");
        }
        if (includeSearchOnly)
        {
            parameters.Add("includeSearchOnly=true");
        }

        var suffix = parameters.Count == 0 ? string.Empty : "?" + string.Join("&", parameters);
        return BuildUri("/api/shares" + suffix);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Valley-Steward-WinUI/0.4");
        return client;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new HttpRequestException("分享服务返回重定向，已拒绝。", null, response.StatusCode);
        }
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        if (detail.Length > 800)
        {
            detail = detail[..800];
        }
        throw new HttpRequestException(
            $"分享服务返回 HTTP {(int)response.StatusCode}：{detail}",
            null,
            response.StatusCode);
    }
}
