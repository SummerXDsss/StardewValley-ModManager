using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text.Json;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed class ModUpdateService
{
    private const string NexusApiHost = "api.nexusmods.com";
    private const string GitHubApiHost = "api.github.com";
    private const string GitHubWebHost = "github.com";
    private const string GameDomain = "stardewvalley";
    private const int MaximumUpdateKeysPerMod = 64;
    private const int MaximumVersionCharacters = 128;
    private const int MaximumGitHubAssets = 128;
    private const int MaximumNexusFiles = 512;
    private const int MaximumAssetNameCharacters = 180;
    private const int MinimumNexusApiKeyCharacters = 20;
    private const int MaximumNexusApiKeyCharacters = 512;
    private const long MaximumJsonBytes = 2L * 1024 * 1024;
    private const long MaximumAutomaticAssetBytes = 2L * 1024 * 1024 * 1024;

    private static readonly Uri NexusApiBase = new("https://api.nexusmods.com/v1/");
    private static readonly Uri GitHubApiBase = new("https://api.github.com/");
    private static readonly HttpClient SharedClient = CreateClient();
    private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _nexusApiKeyProvider;
    private readonly SemaphoreSlim _requestGate;

    public ModUpdateService(CredentialService? credentials = null)
    {
        var credentialStore = credentials ?? new CredentialService();
        _httpClient = SharedClient;
        _nexusApiKeyProvider = () => credentialStore.Read(CredentialService.NexusApiKeyTarget);
        _requestGate = new SemaphoreSlim(4, 4);
    }

    internal ModUpdateService(
        HttpClient httpClient,
        Func<string?> nexusApiKeyProvider,
        int maximumConcurrentRequests = 4)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _nexusApiKeyProvider = nexusApiKeyProvider
            ?? throw new ArgumentNullException(nameof(nexusApiKeyProvider));
        if (maximumConcurrentRequests is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentRequests),
                "并发请求数必须介于 1 与 16 之间。");
        }
        _requestGate = new SemaphoreSlim(maximumConcurrentRequests, maximumConcurrentRequests);
    }

    public Task<InstalledModUpdateResult> CheckAsync(
        InstalledMod mod,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return CheckSingleAsync(mod, cancellationToken);
    }

    public async Task<IReadOnlyList<InstalledModUpdateResult>> CheckAllAsync(
        IEnumerable<InstalledMod> mods,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mods);
        var snapshot = mods.ToArray();
        if (snapshot.Any(mod => mod is null))
        {
            throw new ArgumentException("Mod 列表不能包含 null。", nameof(mods));
        }

        var versionCache = new ConcurrentDictionary<string, Lazy<Task<RemoteVersionInfo>>>(
            StringComparer.OrdinalIgnoreCase);
        var nexusDownloadCache = new ConcurrentDictionary<string, Lazy<Task<DownloadResolution>>>(
            StringComparer.OrdinalIgnoreCase);
        var tasks = snapshot.Select(mod => CheckCoreAsync(
            mod,
            _requestGate,
            versionCache,
            nexusDownloadCache,
            cancellationToken));
        return await Task.WhenAll(tasks);
    }

    public async Task<RemoteModDownloadResolution> ResolveGitHubReleaseDownloadAsync(
        string repositoryIdentifier,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseUpdateKey(
                $"GitHub:{repositoryIdentifier}",
                out var updateKey,
                out var error)
            || updateKey is not { Provider: ModUpdateProvider.GitHub })
        {
            throw new ArgumentException(error ?? "GitHub 仓库地址无效。", nameof(repositoryIdentifier));
        }

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var remote = await FetchGitHubReleaseAsync(updateKey, cancellationToken);
            return new RemoteModDownloadResolution(
                ModUpdateProvider.GitHub,
                remote.LatestVersion,
                remote.PageUrl,
                remote.Download,
                remote.CannotUpdateReason);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public static bool TryParseUpdateKey(
        string? value,
        out ParsedModUpdateKey? updateKey,
        out string? error)
    {
        updateKey = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "UpdateKey 为空。";
            return false;
        }

        var raw = value.Trim();
        if (raw.Length > 512 || raw.Any(char.IsControl))
        {
            error = "UpdateKey 过长或包含控制字符。";
            return false;
        }

        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseGitHubReleaseUrl(raw, raw, out updateKey, out error);
        }

        var separator = raw.IndexOf(':');
        if (separator <= 0 || separator == raw.Length - 1)
        {
            error = "UpdateKey 必须使用 Nexus:<id>、GitHub:<owner/repo> 或 GitHub Release 地址。";
            return false;
        }

        var provider = raw[..separator].Trim();
        var identifier = raw[(separator + 1)..].Trim();
        if (provider.Equals("Nexus", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsPositiveNumericId(identifier))
            {
                error = "Nexus UpdateKey 必须包含有效的正整数 Mod ID。";
                return false;
            }

            updateKey = new ParsedModUpdateKey(
                raw,
                ModUpdateProvider.Nexus,
                identifier,
                $"https://www.nexusmods.com/{GameDomain}/mods/{identifier}");
            return true;
        }

        if (!provider.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
        {
            error = $"暂不支持 UpdateKey 提供方“{provider}”；当前支持 Nexus 与 GitHub Releases。";
            return false;
        }

        if (identifier.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || identifier.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseGitHubReleaseUrl(raw, identifier, out updateKey, out error);
        }

        var repositoryParts = identifier.Split('/');
        if (repositoryParts.Length != 2
            || !IsValidGitHubOwner(repositoryParts[0])
            || !TryNormalizeGitHubRepository(repositoryParts[1], out var repository))
        {
            error = "GitHub UpdateKey 必须是有效的 owner/repository。";
            return false;
        }

        var owner = repositoryParts[0];
        updateKey = CreateGitHubKey(raw, owner, repository);
        return true;
    }

    public static int? CompareVersions(string? leftValue, string? rightValue)
    {
        if (string.IsNullOrWhiteSpace(leftValue) || string.IsNullOrWhiteSpace(rightValue))
        {
            return null;
        }
        if (leftValue.Trim().Equals(rightValue.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (!TryParseComparableVersion(leftValue, out var left)
            || !TryParseComparableVersion(rightValue, out var right))
        {
            return null;
        }

        var coreLength = Math.Max(left.Core.Count, right.Core.Count);
        for (var index = 0; index < coreLength; index++)
        {
            var leftPart = index < left.Core.Count ? left.Core[index] : BigInteger.Zero;
            var rightPart = index < right.Core.Count ? right.Core[index] : BigInteger.Zero;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (left.Prerelease.Count == 0 || right.Prerelease.Count == 0)
        {
            return left.Prerelease.Count == right.Prerelease.Count
                ? 0
                : left.Prerelease.Count == 0 ? 1 : -1;
        }

        var prereleaseLength = Math.Max(left.Prerelease.Count, right.Prerelease.Count);
        for (var index = 0; index < prereleaseLength; index++)
        {
            if (index >= left.Prerelease.Count)
            {
                return -1;
            }
            if (index >= right.Prerelease.Count)
            {
                return 1;
            }

            var leftIdentifier = left.Prerelease[index];
            var rightIdentifier = right.Prerelease[index];
            var leftNumeric = BigInteger.TryParse(
                leftIdentifier,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var leftNumber);
            var rightNumeric = BigInteger.TryParse(
                rightIdentifier,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var rightNumber);
            if (leftNumeric && rightNumeric)
            {
                var comparison = leftNumber.CompareTo(rightNumber);
                if (comparison != 0)
                {
                    return comparison;
                }
                continue;
            }
            if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }

            var textComparison = string.Compare(
                leftIdentifier,
                rightIdentifier,
                StringComparison.OrdinalIgnoreCase);
            if (textComparison != 0)
            {
                return Math.Sign(textComparison);
            }
        }

        return 0;
    }

    private async Task<InstalledModUpdateResult> CheckSingleAsync(
        InstalledMod mod,
        CancellationToken cancellationToken)
    {
        var results = await CheckAllAsync([mod], cancellationToken);
        return results[0];
    }

    private async Task<InstalledModUpdateResult> CheckCoreAsync(
        InstalledMod mod,
        SemaphoreSlim gate,
        ConcurrentDictionary<string, Lazy<Task<RemoteVersionInfo>>> versionCache,
        ConcurrentDictionary<string, Lazy<Task<DownloadResolution>>> nexusDownloadCache,
        CancellationToken cancellationToken)
    {
        if (!IsValidVersionValue(mod.Version))
        {
            return new InstalledModUpdateResult(
                mod.Id,
                mod.Name,
                mod.Version,
                ModUpdateCheckStatus.Unknown,
                null,
                "本地 manifest.json 的 Version 为空、过长或包含控制字符，无法安全检查更新。",
                Array.Empty<ModUpdateSourceResult>(),
                null,
                null);
        }

        var rawKeys = mod.UpdateKeys.Take(MaximumUpdateKeysPerMod).ToArray();
        var sourceTasks = rawKeys.Select(key => CheckSourceAsync(
            mod.Version,
            key,
            gate,
            versionCache,
            nexusDownloadCache,
            cancellationToken));
        var sources = (await Task.WhenAll(sourceTasks)).ToList();

        if (!string.IsNullOrWhiteSpace(mod.UpdateKeysError))
        {
            sources.Add(UnavailableSource(
                "manifest.json/UpdateKeys",
                mod.Version,
                mod.UpdateKeysError));
        }
        if (mod.UpdateKeys.Count > MaximumUpdateKeysPerMod)
        {
            sources.Add(UnavailableSource(
                "manifest.json/UpdateKeys",
                mod.Version,
                $"为限制上游请求量，仅检查前 {MaximumUpdateKeysPerMod} 个 UpdateKey。"));
        }
        if (sources.Count == 0)
        {
            return new InstalledModUpdateResult(
                mod.Id,
                mod.Name,
                mod.Version,
                ModUpdateCheckStatus.Unavailable,
                null,
                "manifest.json 未声明 UpdateKeys，无法确定上游版本。",
                Array.Empty<ModUpdateSourceResult>(),
                null,
                null);
        }

        var available = sources.Where(source => source.UpdateAvailable).ToArray();
        if (available.Length > 0)
        {
            var highest = available[0];
            foreach (var candidate in available.Skip(1))
            {
                if (CompareVersions(candidate.LatestVersion, highest.LatestVersion) > 0)
                {
                    highest = candidate;
                }
            }

            var highestSources = available
                .Where(source => CompareVersions(source.LatestVersion, highest.LatestVersion) == 0)
                .ToArray();
            var downloadable = highestSources.FirstOrDefault(source => source.CanAutoUpdate);
            var latest = highest.LatestVersion;
            var reason = downloadable is null
                ? JoinReasons(highestSources.Select(source => source.CannotUpdateReason))
                    ?? "发现上游新版本，但没有可安全绑定到该版本的下载文件。"
                : null;
            return new InstalledModUpdateResult(
                mod.Id,
                mod.Name,
                mod.Version,
                ModUpdateCheckStatus.UpdateAvailable,
                latest,
                downloadable is null
                    ? $"发现新版本 {latest ?? "未知"}，但暂时不能一键更新。"
                    : $"发现新版本 {latest}，已确认可用的更新包。",
                sources,
                downloadable?.Download,
                reason);
        }

        if (sources.Any(source => source.Status == ModUpdateCheckStatus.Failed))
        {
            return new InstalledModUpdateResult(
                mod.Id,
                mod.Name,
                mod.Version,
                ModUpdateCheckStatus.Failed,
                sources.FirstOrDefault(source => source.LatestVersion is not null)?.LatestVersion,
                "至少一个上游来源检查失败；已完成的其他来源结果仍保留。",
                sources,
                null,
                null);
        }
        if (sources.Any(source => source.Status == ModUpdateCheckStatus.Unknown))
        {
            return new InstalledModUpdateResult(
                mod.Id,
                mod.Name,
                mod.Version,
                ModUpdateCheckStatus.Unknown,
                sources.FirstOrDefault(source => source.Status == ModUpdateCheckStatus.Unknown)?.LatestVersion,
                "至少一个上游版本无法可靠比较；已完成的其他来源结果仍保留。",
                sources,
                null,
                null);
        }

        var successful = sources.FirstOrDefault(source => source.Status == ModUpdateCheckStatus.UpToDate);
        return successful is not null
            ? new InstalledModUpdateResult(
                mod.Id,
                mod.Name,
                mod.Version,
                ModUpdateCheckStatus.UpToDate,
                successful.LatestVersion,
                "已安装版本不低于所有成功检查的受支持上游版本。",
                sources,
                null,
                null)
            : new InstalledModUpdateResult(
                mod.Id,
                mod.Name,
                mod.Version,
                ModUpdateCheckStatus.Unavailable,
                null,
                "没有可用的更新来源。",
                sources,
                null,
                null);
    }

    private async Task<ModUpdateSourceResult> CheckSourceAsync(
        string installedVersion,
        string rawKey,
        SemaphoreSlim gate,
        ConcurrentDictionary<string, Lazy<Task<RemoteVersionInfo>>> versionCache,
        ConcurrentDictionary<string, Lazy<Task<DownloadResolution>>> nexusDownloadCache,
        CancellationToken cancellationToken)
    {
        if (!TryParseUpdateKey(rawKey, out var parsed, out var parseError) || parsed is null)
        {
            return UnavailableSource(rawKey, installedVersion, parseError ?? "UpdateKey 无效。");
        }

        string? nexusApiKey = null;
        if (parsed.Provider == ModUpdateProvider.Nexus
            && !TryReadNexusApiKey(out nexusApiKey, out var keyError))
        {
            return new ModUpdateSourceResult(
                parsed.RawValue,
                parsed.Provider,
                ModUpdateCheckStatus.Unavailable,
                installedVersion,
                null,
                parsed.PageUrl,
                keyError!,
                null,
                null);
        }

        try
        {
            var cacheKey = $"{parsed.Provider}:{parsed.Identifier}";
            var lazy = versionCache.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<RemoteVersionInfo>>(
                    () => FetchRemoteWithGateAsync(parsed, nexusApiKey, gate, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            var remote = await lazy.Value;
            var comparison = CompareVersions(remote.LatestVersion, installedVersion);
            if (comparison is null)
            {
                return new ModUpdateSourceResult(
                    parsed.RawValue,
                    parsed.Provider,
                    ModUpdateCheckStatus.Unknown,
                    installedVersion,
                    remote.LatestVersion,
                    remote.PageUrl,
                    $"无法可靠比较本地版本“{installedVersion}”与上游版本“{remote.LatestVersion}”。",
                    null,
                    null);
            }
            if (comparison <= 0)
            {
                return new ModUpdateSourceResult(
                    parsed.RawValue,
                    parsed.Provider,
                    ModUpdateCheckStatus.UpToDate,
                    installedVersion,
                    remote.LatestVersion,
                    remote.PageUrl,
                    comparison == 0 ? "已是最新版本。" : "本地版本高于上游稳定版本。",
                    null,
                    null);
            }

            var resolution = new DownloadResolution(remote.Download, remote.CannotUpdateReason);
            if (parsed.Provider == ModUpdateProvider.Nexus)
            {
                var downloadLazy = nexusDownloadCache.GetOrAdd(
                    cacheKey,
                    _ => new Lazy<Task<DownloadResolution>>(
                        () => ResolveNexusDownloadWithGateAsync(
                            parsed,
                            remote.LatestVersion,
                            nexusApiKey!,
                            gate,
                            cancellationToken),
                        LazyThreadSafetyMode.ExecutionAndPublication));
                try
                {
                    resolution = await downloadLazy.Value;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    resolution = new DownloadResolution(
                        null,
                        $"发现新版，但无法确认 Nexus 主文件：{SafeErrorMessage(error)}");
                }
            }

            return new ModUpdateSourceResult(
                parsed.RawValue,
                parsed.Provider,
                ModUpdateCheckStatus.UpdateAvailable,
                installedVersion,
                remote.LatestVersion,
                remote.PageUrl,
                resolution.Download is null
                    ? "发现新版本，但没有可安全自动安装的唯一更新包。"
                    : "发现新版本，并已确认更新包。",
                resolution.Download,
                resolution.Download is null
                    ? resolution.CannotUpdateReason
                        ?? "无法把上游版本安全映射到唯一的下载文件。"
                    : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new ModUpdateSourceResult(
                parsed.RawValue,
                parsed.Provider,
                ModUpdateCheckStatus.Failed,
                installedVersion,
                null,
                parsed.PageUrl,
                SafeErrorMessage(error),
                null,
                null);
        }
    }

    private async Task<RemoteVersionInfo> FetchRemoteWithGateAsync(
        ParsedModUpdateKey updateKey,
        string? nexusApiKey,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return updateKey.Provider switch
            {
                ModUpdateProvider.Nexus => await FetchNexusVersionAsync(
                    updateKey,
                    nexusApiKey!,
                    cancellationToken),
                ModUpdateProvider.GitHub => await FetchGitHubReleaseAsync(updateKey, cancellationToken),
                _ => throw new InvalidOperationException("不支持的更新提供方。"),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DownloadResolution> ResolveNexusDownloadWithGateAsync(
        ParsedModUpdateKey updateKey,
        string latestVersion,
        string apiKey,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ResolveNexusDownloadAsync(updateKey, latestVersion, apiKey, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RemoteVersionInfo> FetchNexusVersionAsync(
        ParsedModUpdateKey updateKey,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            NexusApiBase,
            $"games/{GameDomain}/mods/{updateKey.Identifier}.json");
        using var request = CreateNexusRequest(endpoint, apiKey);
        using var document = await SendJsonAsync(
            request,
            ModUpdateProvider.Nexus,
            NexusApiHost,
            cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Nexus Mod 详情不是 JSON 对象。");
        }
        var returnedModId = ReadNumericId(document.RootElement, "mod_id");
        if (returnedModId is not null
            && ParseNumericId(returnedModId) != ParseNumericId(updateKey.Identifier))
        {
            throw new InvalidDataException("Nexus Mod 详情返回了与 UpdateKey 不一致的 Mod ID。");
        }
        var version = ReadRequiredVersion(document.RootElement, "version", "Nexus Mod 详情");
        return new RemoteVersionInfo(version, updateKey.PageUrl, null, null);
    }

    private async Task<DownloadResolution> ResolveNexusDownloadAsync(
        ParsedModUpdateKey updateKey,
        string latestVersion,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            NexusApiBase,
            $"games/{GameDomain}/mods/{updateKey.Identifier}/files.json");
        using var request = CreateNexusRequest(endpoint, apiKey);
        using var document = await SendJsonAsync(
            request,
            ModUpdateProvider.Nexus,
            NexusApiHost,
            cancellationToken);
        var root = document.RootElement;
        JsonElement files;
        if (root.ValueKind == JsonValueKind.Object
            && TryGetProperty(root, "files", out var filesProperty))
        {
            files = filesProperty;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            files = root;
        }
        else
        {
            throw new InvalidDataException("Nexus 文件列表缺少 files 数组。");
        }
        if (files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Nexus 文件列表的 files 不是数组。");
        }

        var elements = files.EnumerateArray().ToArray();
        if (elements.Length > MaximumNexusFiles)
        {
            throw new InvalidDataException($"Nexus 返回超过 {MaximumNexusFiles} 个文件，已拒绝异常响应。");
        }

        var candidates = new List<NexusFileCandidate>();
        foreach (var file in elements)
        {
            var fileId = ReadNumericId(file, "file_id");
            if (fileId is null)
            {
                continue;
            }
            var fileName = ReadOptionalString(file, "name");
            var fileVersion = ReadOptionalString(file, "version")?.Trim();
            var categoryName = ReadOptionalString(file, "category_name")?.Trim();
            var categoryId = ReadOptionalInt64(file, "category_id");
            var isPrimary = ReadOptionalBoolean(file, "is_primary") == true;
            var isDeleted = ReadOptionalBoolean(file, "is_deleted") == true;
            var isArchived = categoryId is 6 or 7
                || categoryName is not null
                    && (categoryName.Contains("ARCHIVED", StringComparison.OrdinalIgnoreCase)
                        || categoryName.Contains("DELETED", StringComparison.OrdinalIgnoreCase)
                        || categoryName.Contains("OLD", StringComparison.OrdinalIgnoreCase));
            var isMain = isPrimary
                || categoryId == 1
                || categoryName?.Equals("MAIN", StringComparison.OrdinalIgnoreCase) == true;
            if (isDeleted || isArchived || !isMain || string.IsNullOrWhiteSpace(fileVersion))
            {
                continue;
            }

            var versionComparison = CompareVersions(fileVersion, latestVersion);
            if (versionComparison != 0)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(fileName)
                || !IsSafeAssetName(fileName)
                || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            candidates.Add(new NexusFileCandidate(
                fileId,
                fileName,
                isPrimary,
                ReadOptionalInt64(file, "uploaded_timestamp") ?? 0));
        }

        var selected = candidates
            .OrderByDescending(candidate => candidate.IsPrimary)
            .ThenByDescending(candidate => candidate.UploadedTimestamp)
            .ThenByDescending(candidate => ParseNumericId(candidate.FileId))
            .FirstOrDefault();
        if (selected is null)
        {
            return new DownloadResolution(
                null,
                $"Nexus 没有返回与版本 {latestVersion} 匹配的 active primary/main ZIP 文件，请打开文件页手动选择。");
        }

        return new DownloadResolution(
            new NexusModUpdateDownloadDescriptor(
                updateKey.Identifier,
                selected.FileId,
                selected.FileName,
                latestVersion,
                $"{updateKey.PageUrl}?tab=files"),
            null);
    }

    private async Task<RemoteVersionInfo> FetchGitHubReleaseAsync(
        ParsedModUpdateKey updateKey,
        CancellationToken cancellationToken)
    {
        var (owner, repository) = SplitGitHubIdentifier(updateKey.Identifier);
        var endpoint = new Uri(
            GitHubApiBase,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd("Valley-Steward/0.2");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var document = await SendJsonAsync(
            request,
            ModUpdateProvider.GitHub,
            GitHubApiHost,
            cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("GitHub Release 详情不是 JSON 对象。");
        }
        if (ReadOptionalBoolean(root, "draft") == true
            || ReadOptionalBoolean(root, "prerelease") == true)
        {
            throw new InvalidDataException("GitHub latest Release 意外指向草稿或预发布版本。");
        }

        var version = ReadRequiredVersion(root, "tag_name", "GitHub Release");
        var pageUrl = $"https://github.com/{owner}/{repository}/releases/latest";
        if (!TryGetProperty(root, "assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return new RemoteVersionInfo(
                version,
                pageUrl,
                null,
                "GitHub Release 没有返回资产列表，不能确定可安装的 ZIP。");
        }

        var assetElements = assets.EnumerateArray().ToArray();
        if (assetElements.Length > MaximumGitHubAssets)
        {
            throw new InvalidDataException($"GitHub Release 返回超过 {MaximumGitHubAssets} 个资产，已拒绝异常响应。");
        }

        var zipAssets = new List<GitHubAssetCandidate>();
        foreach (var asset in assetElements)
        {
            var name = ReadOptionalString(asset, "name");
            var url = ReadOptionalString(asset, "browser_download_url")?.Trim();
            var state = ReadOptionalString(asset, "state")?.Trim();
            var size = ReadOptionalInt64(asset, "size");
            if (string.IsNullOrWhiteSpace(name)
                || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || !IsSafeAssetName(name)
                || string.IsNullOrWhiteSpace(url)
                || state is not null && !state.Equals("uploaded", StringComparison.OrdinalIgnoreCase)
                || size is <= 0 or > MaximumAutomaticAssetBytes
                || !TryValidateGitHubAssetUri(
                    url,
                    owner,
                    repository,
                    version,
                    name,
                    out var assetUri))
            {
                continue;
            }

            zipAssets.Add(new GitHubAssetCandidate(
                assetUri,
                name,
                size,
                ReadSha256(asset)));
        }

        if (zipAssets.Count != 1)
        {
            var reason = zipAssets.Count == 0
                ? "GitHub Release 没有唯一、已上传且大小有效的官方 ZIP 资产。"
                : $"GitHub Release 有 {zipAssets.Count} 个可用 ZIP，无法在没有文件规则时安全地自动选择。";
            return new RemoteVersionInfo(version, pageUrl, null, reason);
        }

        var selected = zipAssets[0];
        return new RemoteVersionInfo(
            version,
            pageUrl,
            new GitHubModUpdateDownloadDescriptor(
                selected.AssetUri,
                selected.AssetName,
                selected.AssetSize,
                selected.Sha256,
                version,
                pageUrl),
            null);
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpRequestMessage request,
        ModUpdateProvider provider,
        string trustedHost,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        ValidateApiResponseUri(response.RequestMessage?.RequestUri, trustedHost);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(provider, response.StatusCode);
        }
        if (response.Content.Headers.ContentLength is > MaximumJsonBytes)
        {
            throw new InvalidDataException("上游版本响应超过 2 MiB，已拒绝加载。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                total += read;
                if (total > MaximumJsonBytes)
                {
                    throw new InvalidDataException("上游版本响应超过 2 MiB，已拒绝加载。");
                }
                memory.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        try
        {
            return JsonDocument.Parse(memory.ToArray());
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("上游版本响应不是有效 JSON。", error);
        }
    }

    private bool TryReadNexusApiKey(out string? apiKey, out string? error)
    {
        apiKey = null;
        error = null;
        try
        {
            apiKey = _nexusApiKeyProvider()?.Trim();
        }
        catch
        {
            error = "无法从 Windows 凭据管理器读取 Nexus Personal API Key。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            error = "未配置 Nexus Personal API Key；请先在设置中填写并保存。";
            return false;
        }
        if (apiKey.Length is < MinimumNexusApiKeyCharacters or > MaximumNexusApiKeyCharacters
            || apiKey.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            apiKey = null;
            error = "已保存的 Nexus Personal API Key 格式无效，请在设置中重新填写。";
            return false;
        }
        return true;
    }

    private static HttpRequestMessage CreateNexusRequest(Uri endpoint, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("apikey", apiKey);
        request.Headers.UserAgent.ParseAdd("Valley-Steward/0.2");
        request.Headers.TryAddWithoutValidation("Application-Name", "Valley Steward");
        request.Headers.TryAddWithoutValidation("Application-Version", "0.2-winui");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static Exception CreateApiException(ModUpdateProvider provider, HttpStatusCode statusCode)
    {
        var name = provider == ModUpdateProvider.Nexus ? "Nexus" : "GitHub";
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => new InvalidOperationException(
                $"{name} 拒绝了身份验证；请检查 API Key。"),
            HttpStatusCode.Forbidden => new InvalidOperationException(
                provider == ModUpdateProvider.Nexus
                    ? "Nexus 拒绝了请求；API Key 可能无效、权限不足或额度已用尽。"
                    : "GitHub 拒绝了请求；匿名 API 请求额度可能已用尽。"),
            HttpStatusCode.NotFound => new InvalidOperationException(
                $"{name} 找不到 UpdateKey 指向的 Mod 或 Release。"),
            HttpStatusCode.TooManyRequests => new InvalidOperationException(
                $"{name} 请求过于频繁，请稍后再试。"),
            _ when (int)statusCode is >= 300 and < 400 => new InvalidOperationException(
                $"{name} API 返回了未经允许的重定向。"),
            _ => new HttpRequestException(
                $"{name} API 返回 HTTP {(int)statusCode}。",
                null,
                statusCode),
        };
    }

    private static void ValidateApiResponseUri(Uri? uri, string trustedHost)
    {
        if (uri is null
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(trustedHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0)
        {
            throw new InvalidDataException("上游 API 响应来自非预期地址，已停止处理。");
        }
    }

    private static bool TryParseGitHubReleaseUrl(
        string rawValue,
        string url,
        out ParsedModUpdateKey? updateKey,
        out string? error)
    {
        updateKey = null;
        error = null;
        if (url.Contains('\\')
            || url.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || url.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(GitHubWebHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
        {
            error = "GitHub Release 地址必须是不含凭据、参数或片段的 github.com HTTPS 地址。";
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3
            || !segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase)
            || !IsValidGitHubOwner(segments[0])
            || !TryNormalizeGitHubRepository(segments[1], out var repository)
            || !IsAllowedGitHubReleasePath(segments))
        {
            error = "GitHub 地址必须指向 owner/repository 的 Releases 页面或 Release 资源。";
            return false;
        }

        updateKey = CreateGitHubKey(rawValue, segments[0], repository);
        return true;
    }

    private static bool IsAllowedGitHubReleasePath(IReadOnlyList<string> segments)
    {
        if (segments.Count == 3)
        {
            return true;
        }
        if (segments.Count == 4)
        {
            return segments[3].Equals("latest", StringComparison.OrdinalIgnoreCase);
        }
        if (segments.Count == 5)
        {
            return segments[3].Equals("tag", StringComparison.OrdinalIgnoreCase)
                && IsSafeUrlSegment(segments[4]);
        }
        return segments.Count == 6
            && segments[3].Equals("download", StringComparison.OrdinalIgnoreCase)
            && IsSafeUrlSegment(segments[4])
            && IsSafeUrlSegment(segments[5]);
    }

    private static ParsedModUpdateKey CreateGitHubKey(string rawValue, string owner, string repository)
    {
        return new ParsedModUpdateKey(
            rawValue,
            ModUpdateProvider.GitHub,
            $"{owner}/{repository}",
            $"https://github.com/{owner}/{repository}/releases/latest");
    }

    private static bool TryValidateGitHubAssetUri(
        string value,
        string owner,
        string repository,
        string expectedTag,
        string expectedName,
        out Uri assetUri)
    {
        assetUri = null!;
        if (value.Contains('\\')
            || value.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || value.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(GitHubWebHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 6
            || !segments[0].Equals(owner, StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals(repository, StringComparison.OrdinalIgnoreCase)
            || !segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase)
            || !segments[3].Equals("download", StringComparison.OrdinalIgnoreCase)
            || !IsSafeUrlSegment(segments[4])
            || !Uri.UnescapeDataString(segments[4]).Equals(expectedTag, StringComparison.Ordinal)
            || !Uri.UnescapeDataString(segments[5]).Equals(expectedName, StringComparison.Ordinal))
        {
            return false;
        }

        assetUri = uri;
        return true;
    }

    private static bool IsValidGitHubOwner(string value)
    {
        return value.Length is >= 1 and <= 39
            && value[0] != '-'
            && value[^1] != '-'
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }

    private static bool TryNormalizeGitHubRepository(string value, out string repository)
    {
        repository = value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
        return repository.Length is >= 1 and <= 100
            && repository is not ("." or "..")
            && repository.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.');
    }

    private static bool IsSafeUrlSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 180)
        {
            return false;
        }
        try
        {
            var decoded = Uri.UnescapeDataString(value);
            return decoded is not ("." or "..")
                && !decoded.Contains('/')
                && !decoded.Contains('\\')
                && !decoded.Any(char.IsControl);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool IsSafeAssetName(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value);
        return value.Length is >= 1 and <= MaximumAssetNameCharacters
            && value is not ("." or "..")
            && value.Equals(value.Trim(), StringComparison.Ordinal)
            && value[^1] is not ('.' or ' ')
            && !value.Contains('/')
            && !value.Contains('\\')
            && !value.Any(character => char.IsControl(character)
                || Path.GetInvalidFileNameChars().Contains(character))
            && !string.IsNullOrWhiteSpace(stem)
            && !ReservedWindowsFileNames.Contains(stem);
    }

    private static bool IsPositiveNumericId(string value)
    {
        return value.Length is >= 1 and <= 19
            && value.All(char.IsAsciiDigit)
            && BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > BigInteger.Zero;
    }

    private static BigInteger ParseNumericId(string value)
    {
        return BigInteger.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static string? ReadNumericId(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
        {
            return null;
        }
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return text is not null && IsPositiveNumericId(text) ? text : null;
    }

    private static string ReadRequiredVersion(JsonElement root, string name, string label)
    {
        var version = ReadOptionalString(root, name)?.Trim();
        if (!IsValidVersionValue(version))
        {
            throw new InvalidDataException($"{label}缺少有效的版本号。");
        }
        return version!;
    }

    private static bool IsValidVersionValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaximumVersionCharacters
            && !value.Any(char.IsControl);
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        return TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long? ReadOptionalInt64(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }
        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static bool? ReadOptionalBoolean(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            _ => null,
        };
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
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

    private static string? ReadSha256(JsonElement asset)
    {
        var digest = ReadOptionalString(asset, "digest")?.Trim();
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var hash = digest[prefix.Length..];
        return hash.Length == 64 && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null;
    }

    private static (string Owner, string Repository) SplitGitHubIdentifier(string identifier)
    {
        var separator = identifier.IndexOf('/');
        return (identifier[..separator], identifier[(separator + 1)..]);
    }

    private static bool TryParseComparableVersion(string value, out ComparableVersion version)
    {
        version = null!;
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > MaximumVersionCharacters)
        {
            return false;
        }
        if (normalized[0] is 'v' or 'V')
        {
            normalized = normalized[1..];
        }
        var buildIndex = normalized.IndexOf('+');
        if (buildIndex >= 0)
        {
            if (buildIndex == normalized.Length - 1 || normalized[(buildIndex + 1)..].Contains('+'))
            {
                return false;
            }
            normalized = normalized[..buildIndex];
        }

        string coreText;
        string? prereleaseText = null;
        var prereleaseIndex = normalized.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            if (prereleaseIndex == normalized.Length - 1)
            {
                return false;
            }
            coreText = normalized[..prereleaseIndex];
            prereleaseText = normalized[(prereleaseIndex + 1)..];
        }
        else
        {
            coreText = normalized;
        }

        var coreParts = coreText.Split('.');
        if (coreParts.Length is < 1 or > 16)
        {
            return false;
        }
        var core = new List<BigInteger>(coreParts.Length);
        foreach (var part in coreParts)
        {
            if (part.Length is < 1 or > 32
                || !part.All(char.IsAsciiDigit)
                || !BigInteger.TryParse(
                    part,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number))
            {
                return false;
            }
            core.Add(number);
        }

        var prerelease = Array.Empty<string>();
        if (prereleaseText is not null)
        {
            prerelease = prereleaseText.Split('.');
            if (prerelease.Length > 32
                || prerelease.Any(part => part.Length is < 1 or > 64
                    || !part.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')))
            {
                return false;
            }
        }

        version = new ComparableVersion(core, prerelease);
        return true;
    }

    private static ModUpdateSourceResult UnavailableSource(
        string updateKey,
        string installedVersion,
        string message)
    {
        return new ModUpdateSourceResult(
            updateKey,
            ModUpdateProvider.Unknown,
            ModUpdateCheckStatus.Unavailable,
            installedVersion,
            null,
            null,
            message,
            null,
            null);
    }

    private static string? JoinReasons(IEnumerable<string?> reasons)
    {
        var values = reasons
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 0 ? null : string.Join("；", values);
    }

    private static string SafeErrorMessage(Exception error)
    {
        return error switch
        {
            HttpRequestException => $"网络请求失败：{error.Message}",
            TaskCanceledException => "上游请求超时。",
            InvalidDataException or InvalidOperationException => error.Message,
            _ => "更新检查遇到意外错误。",
        };
    }

    private sealed record ComparableVersion(
        IReadOnlyList<BigInteger> Core,
        IReadOnlyList<string> Prerelease);

    private sealed record RemoteVersionInfo(
        string LatestVersion,
        string PageUrl,
        ModUpdateDownloadDescriptor? Download,
        string? CannotUpdateReason);

    private sealed record DownloadResolution(
        ModUpdateDownloadDescriptor? Download,
        string? CannotUpdateReason);

    private sealed record NexusFileCandidate(
        string FileId,
        string FileName,
        bool IsPrimary,
        long UploadedTimestamp);

    private sealed record GitHubAssetCandidate(
        Uri AssetUri,
        string AssetName,
        long? AssetSize,
        string? Sha256);
}
