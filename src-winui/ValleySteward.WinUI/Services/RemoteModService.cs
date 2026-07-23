using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ValleySteward.WinUI.Common;

namespace ValleySteward.WinUI.Services;

public sealed class RemoteModService
{
    private const string NexusGraphQlUrl = "https://api.nexusmods.com/v2/graphql";
    private const string NexusTrendingUrl = "https://api.nexusmods.com/v3/games/stardewvalley/trending-mods";
    private const long MaximumResponseBytes = 4L * 1024 * 1024;
    private const int MaximumErrorBytes = 16 * 1024;
    private const string NexusSearchQuery = """
        query SearchMods($filter: ModsFilter, $offset: Int, $count: Int) {
          mods(filter: $filter, offset: $offset, count: $count) {
            nodes {
              modId name summary pictureUrl thumbnailUrl endorsements downloads
              version author updatedAt createdAt uploader { name }
            }
          }
        }
        """;

    private static readonly HttpClient Client = CreateClient();

    public Task<RemoteSearchResult> SearchAsync(
        string query,
        RemoteSearchSource source,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return BrowseAsync(source, cancellationToken);
        }
        if (query.Length is < 2 or > 120)
        {
            throw new ArgumentException("搜索词需要包含 2 到 120 个字符。", nameof(query));
        }

        return SearchCoreAsync(query, source, cancellationToken);
    }

    public async Task<RemoteSearchResult> BrowseAsync(
        RemoteSearchSource source,
        CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task<ProviderSearchResult>>();
        if (source is RemoteSearchSource.All or RemoteSearchSource.Nexus)
        {
            tasks.Add(SearchProviderAsync(
                "Nexus Mods",
                () => BrowseNexusAsync(cancellationToken)));
        }
        if (source is RemoteSearchSource.All or RemoteSearchSource.GitHub)
        {
            tasks.Add(SearchProviderAsync(
                "GitHub",
                () => SearchGitHubAsync(null, cancellationToken)));
        }
        return MergeResults(await Task.WhenAll(tasks));
    }

    private static async Task<RemoteSearchResult> SearchCoreAsync(
        string query,
        RemoteSearchSource source,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task<ProviderSearchResult>>();
        if (source is RemoteSearchSource.All or RemoteSearchSource.Nexus)
        {
            tasks.Add(SearchProviderAsync(
                "Nexus Mods",
                () => SearchNexusAsync(query, cancellationToken)));
        }
        if (source is RemoteSearchSource.All or RemoteSearchSource.GitHub)
        {
            tasks.Add(SearchProviderAsync(
                "GitHub",
                () => SearchGitHubAsync(query, cancellationToken)));
        }

        return MergeResults(await Task.WhenAll(tasks));
    }

    private static RemoteSearchResult MergeResults(IReadOnlyList<ProviderSearchResult> results)
    {
        var mods = results
            .SelectMany(result => result.Mods)
            .GroupBy(mod => mod.PageUrl, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(mod => mod.UpdatedAt)
            .ToArray();
        var errors = results
            .Where(result => result.Error is not null)
            .Select(result => $"{result.Provider}：{result.Error}")
            .ToArray();
        return new RemoteSearchResult(mods, errors);
    }

    private static async Task<ProviderSearchResult> SearchProviderAsync(
        string provider,
        Func<Task<IReadOnlyList<RemoteModItem>>> search)
    {
        try
        {
            return new ProviderSearchResult(provider, await search(), null);
        }
        catch (Exception error)
        {
            return new ProviderSearchResult(provider, Array.Empty<RemoteModItem>(), error.Message);
        }
    }

    private static async Task<IReadOnlyList<RemoteModItem>> SearchNexusAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            query = NexusSearchQuery,
            variables = new
            {
                filter = new
                {
                    op = "AND",
                    filter = new object[]
                    {
                        new { gameDomainName = new[] { new { value = "stardewvalley", op = "EQUALS" } } },
                        new
                        {
                            op = "OR",
                            filter = new object[]
                            {
                                new { nameStemmed = new[] { new { value = query, op = "MATCHES" } } },
                                new { description = new[] { new { value = query, op = "MATCHES" } } },
                                new { author = new[] { new { value = query, op = "MATCHES" } } },
                                new { uploader = new[] { new { value = query, op = "MATCHES" } } },
                            },
                        },
                    },
                },
                offset = 0,
                count = 24,
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, NexusGraphQlUrl)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("Application-Name", "Valley-Steward");
        request.Headers.Add("Application-Version", "0.2-winui");
        using var response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "Nexus Mods", cancellationToken);

        using var document = await ReadJsonAsync(response, "Nexus Mods 搜索", cancellationToken);
        if (!TryProperty(document.RootElement, "data", out var data)
            || !TryProperty(data, "mods", out var mods)
            || mods.ValueKind == JsonValueKind.Null
            || !TryProperty(mods, "nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(ReadGraphQlError(document.RootElement) ?? "搜索没有返回结果数据。");
        }

        var results = new List<RemoteModItem>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var id = ReadNumberOrString(node, "modId");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            var uploader = TryProperty(node, "uploader", out var uploaderObject)
                ? ReadString(uploaderObject, "name")
                : null;
            var downloads = ReadLong(node, "downloads");
            var endorsements = ReadLong(node, "endorsements");
            results.Add(new RemoteModItem(
                $"nexus:{id}",
                ReadString(node, "name") ?? $"Nexus Mod #{id}",
                ReadString(node, "author") ?? uploader ?? "Nexus Mods 作者",
                ReadString(node, "summary") ?? "暂无简介",
                "Nexus Mods",
                ReadString(node, "version") ?? "未知",
                downloads > 0 ? $"{downloads:N0} 下载" : $"{endorsements:N0} 认可",
                ReadDate(node, "updatedAt") ?? ReadDate(node, "createdAt"),
                $"https://www.nexusmods.com/stardewvalley/mods/{id}",
                ReadImageUrl(node, "pictureUrl", "thumbnailUrl")));
        }
        return results;
    }

    private static async Task<IReadOnlyList<RemoteModItem>> BrowseNexusAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, NexusTrendingUrl);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "Nexus Mods", cancellationToken);
        using var document = await ReadJsonAsync(response, "Nexus Mods 趋势榜", cancellationToken);
        if (!TryProperty(document.RootElement, "data", out var data)
            || !TryProperty(data, "mods", out var mods)
            || mods.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Nexus Mods 趋势榜没有返回 Mod 列表。");
        }
        if (mods.GetArrayLength() == 0)
        {
            return await SearchNexusAsync("mod", cancellationToken);
        }

        var results = new List<RemoteModItem>();
        foreach (var item in mods.EnumerateArray())
        {
            var pageUrl = ReadString(item, "mod_page_url");
            if (!TryReadNexusModId(pageUrl, out var id))
            {
                continue;
            }
            results.Add(new RemoteModItem(
                $"nexus:{id}",
                ReadString(item, "name") ?? $"Nexus Mod #{id}",
                ReadString(item, "author") ?? "Nexus Mods 作者",
                ReadString(item, "summary") ?? "Nexus Mods 当前趋势 Mod",
                "Nexus Mods",
                "趋势",
                "公开趋势榜",
                null,
                $"https://www.nexusmods.com/stardewvalley/mods/{id}",
                ReadImageUrl(item, "picture_url", "thumbnail_url", "pictureUrl", "thumbnailUrl")));
        }
        return results.Count > 0
            ? results
            : await SearchNexusAsync("mod", cancellationToken);
    }

    private static async Task<IReadOnlyList<RemoteModItem>> SearchGitHubAsync(
        string? query,
        CancellationToken cancellationToken)
    {
        var expression = query is null
            ? "\"stardew valley\" mod in:name,description,readme archived:false fork:false"
            : $"{query} stardew valley mod in:name,description,readme archived:false fork:false";
        var search = Uri.EscapeDataString(expression);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/search/repositories?q={search}&sort=updated&order=desc&per_page=20");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "GitHub", cancellationToken);

        using var document = await ReadJsonAsync(response, "GitHub 搜索", cancellationToken);
        if (!TryProperty(document.RootElement, "items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("搜索没有返回仓库列表。");
        }

        var results = new List<RemoteModItem>();
        foreach (var item in items.EnumerateArray())
        {
            var id = ReadNumberOrString(item, "id");
            var name = ReadString(item, "name");
            var pageUrl = ReadString(item, "html_url");
            if (id is null || name is null || pageUrl is null)
            {
                continue;
            }
            var owner = TryProperty(item, "owner", out var ownerObject)
                ? ReadString(ownerObject, "login")
                : null;
            var ownerAvatar = TryProperty(item, "owner", out ownerObject)
                ? ReadImageUrl(ownerObject, "avatar_url")
                : null;
            results.Add(new RemoteModItem(
                $"github:{id}",
                name,
                owner ?? "GitHub 作者",
                ReadString(item, "description") ?? "暂无简介",
                "GitHub",
                "仓库",
                $"{ReadLong(item, "stargazers_count"):N0} Stars",
                ReadDate(item, "updated_at"),
                pageUrl,
                ownerAvatar));
        }
        return results;
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
            Timeout = TimeSpan.FromSeconds(18),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Valley-Steward-WinUI/0.2");
        return client;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string provider,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new HttpRequestException(
                $"{provider} 返回了未受支持的重定向。",
                null,
                response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var body = await ReadBoundedAsync(
            stream,
            MaximumErrorBytes,
            cancellationToken,
            rejectOverflow: false);
        var detail = System.Text.Encoding.UTF8.GetString(body).Trim();
        if (detail.Length > 280)
        {
            detail = detail[..280];
        }
        throw new HttpRequestException($"{provider} 返回 {(int)response.StatusCode} {response.ReasonPhrase}{(detail.Length > 0 ? $"：{detail}" : string.Empty)}");
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        string label,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException($"{label}响应超过 {MaximumResponseBytes / 1024} KB 的安全上限。");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var body = await ReadBoundedAsync(stream, MaximumResponseBytes, cancellationToken);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{label}返回的数据格式无效。", error);
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
                throw new InvalidDataException($"远程响应超过 {limit / 1024} KB 的安全上限。");
            }
            return memory.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static bool TryReadNexusModId(string? pageUrl, out string id)
    {
        id = string.Empty;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3
            || !segments[0].Equals("stardewvalley", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("mods", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        id = segments[2];
        return id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit);
    }

    private static string? ReadGraphQlError(JsonElement root)
    {
        if (!TryProperty(root, "errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return errors.EnumerateArray()
            .Select(error => ReadString(error, "message"))
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    }

    internal static string? ReadImageUrl(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = NormalizeRemoteImageUrl(ReadString(element, name));
            if (value is not null)
            {
                return value;
            }
        }
        return null;
    }

    internal static string? NormalizeRemoteImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        value = value.Trim();
        if (value.Length > 2048 || value.Any(char.IsControl))
        {
            return null;
        }
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort
            && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.AbsoluteUri
            : null;
    }

    private static string? ReadNumberOrString(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static long ReadLong(JsonElement element, string name)
    {
        return TryProperty(element, name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        return DateTimeOffset.TryParse(ReadString(element, name), out var date) ? date : null;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
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

    private sealed record ProviderSearchResult(
        string Provider,
        IReadOnlyList<RemoteModItem> Mods,
        string? Error);
}

public enum RemoteSearchSource
{
    All,
    Nexus,
    GitHub,
}

public sealed class RemoteModItem : ObservableObject
{
    private string _name;
    private string _summary;
    private bool _translated;
    private bool _isTranslating;
    private bool _isQuickInstalling;

    public RemoteModItem(
        string id,
        string name,
        string author,
        string summary,
        string source,
        string version,
        string popularity,
        DateTimeOffset? updatedAt,
        string pageUrl,
        string? imageUrl = null)
    {
        Id = id;
        _name = name;
        OriginalName = name;
        Author = author;
        _summary = summary;
        OriginalSummary = summary;
        Source = source;
        Version = version;
        Popularity = popularity;
        UpdatedAt = updatedAt;
        PageUrl = pageUrl;
        ImageUrl = RemoteModService.NormalizeRemoteImageUrl(imageUrl);
    }

    public string Id { get; }
    public string Name => _name;
    public string OriginalName { get; }
    public string Author { get; }
    public string Summary => _summary;
    public string OriginalSummary { get; }
    public string Source { get; }
    public string Version { get; }
    public string Popularity { get; }
    public DateTimeOffset? UpdatedAt { get; }
    public string PageUrl { get; }
    public string? ImageUrl { get; }
    public bool Translated => _translated;
    public bool IsTranslating
    {
        get => _isTranslating;
        set
        {
            if (SetProperty(ref _isTranslating, value))
            {
                OnPropertyChanged(nameof(CanTranslate));
                OnPropertyChanged(nameof(TranslationActionText));
            }
        }
    }

    public bool CanTranslate => !IsTranslating;
    public string TranslationActionText => IsTranslating
        ? "翻译中"
        : Translated
            ? "重新翻译"
            : "一键翻译";
    public string UpdatedText => UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "更新时间未知";

    public bool IsQuickInstalling
    {
        get => _isQuickInstalling;
        set
        {
            if (SetProperty(ref _isQuickInstalling, value))
            {
                OnPropertyChanged(nameof(CanQuickInstall));
                OnPropertyChanged(nameof(QuickInstallActionText));
            }
        }
    }

    public bool CanQuickInstall => !IsQuickInstalling;
    public string QuickInstallActionText => IsQuickInstalling ? "处理中…" : "下载并安装";

    public void ApplyTranslation(string name, string summary)
    {
        SetProperty(ref _name, name, nameof(Name));
        SetProperty(ref _summary, summary, nameof(Summary));
        if (SetProperty(ref _translated, true, nameof(Translated)))
        {
            OnPropertyChanged(nameof(TranslationActionText));
        }
    }
}

public sealed record RemoteSearchResult(
    IReadOnlyList<RemoteModItem> Mods,
    IReadOnlyList<string> Errors);
