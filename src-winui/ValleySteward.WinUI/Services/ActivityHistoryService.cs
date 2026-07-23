using System.Text.Json;
using System.Text.RegularExpressions;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed class ActivityHistoryService
{
    private const int CurrentVersion = 1;
    private const int MaximumEntries = 200;
    private const long MaximumFileBytes = 512 * 1024;
    private const int MaximumTitleCharacters = 160;
    private const int MaximumDetailCharacters = 1_200;
    private const int MaximumVersionCharacters = 128;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static readonly Regex UrlPattern = new(
        @"(?i)\b(?:https?|nxm)://[^\s<>]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex SecretAssignmentPattern = new(
        @"(?i)\b(api[_-]?key|access[_-]?token|token|key|expires|user_id)\s*[=:]\s*[^&\s,;]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex WindowsPathPattern = new(
        @"(?i)(?:[a-z]:\\|\\\\)[^\r\n\t]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly string _configPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ActivityHistoryService(string? configPath = null)
    {
        _configPath = string.IsNullOrWhiteSpace(configPath)
            ? AppPaths.ActivityHistoryConfig
            : Path.GetFullPath(configPath);
    }

    public async Task<IReadOnlyList<ActivityEntry>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActivityEntry> AddAsync(
        ActivityKind kind,
        ActivityOutcome outcome,
        string title,
        string detail,
        string? sourceUrl = null,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new ActivityEntry(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            kind,
            outcome,
            NormalizeText(title, MaximumTitleCharacters, "操作标题"),
            NormalizeText(RedactSensitiveText(detail), MaximumDetailCharacters, "操作详情"),
            NormalizeSourceUrl(sourceUrl),
            NormalizeOptionalText(version, MaximumVersionCharacters));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = (await ReadLockedAsync(cancellationToken)).ToList();
            entries.Insert(0, entry);
            if (entries.Count > MaximumEntries)
            {
                entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries);
            }
            await WriteLockedAsync(entries, cancellationToken);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteLockedAsync(Array.Empty<ActivityEntry>(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ActivityEntry>> ReadLockedAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_configPath)
                || new FileInfo(_configPath).Length is <= 0 or > MaximumFileBytes)
            {
                return Array.Empty<ActivityEntry>();
            }

            await using var stream = new FileStream(
                _configPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<ActivityHistoryDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            if (document is not { Version: CurrentVersion } || document.Entries is null)
            {
                return Array.Empty<ActivityEntry>();
            }

            return document.Entries
                .Where(IsValidStoredEntry)
                .OrderByDescending(entry => entry.Timestamp)
                .Take(MaximumEntries)
                .Select(NormalizeStoredEntry)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<ActivityEntry>();
        }
    }

    private async Task WriteLockedAsync(
        IReadOnlyList<ActivityEntry> entries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_configPath)
            ?? throw new InvalidOperationException("活动历史文件缺少父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_configPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        var backupPath = $"{_configPath}.bak";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new ActivityHistoryDocument(CurrentVersion, entries.Take(MaximumEntries).ToArray()),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (new FileInfo(temporaryPath).Length > MaximumFileBytes)
            {
                throw new InvalidDataException("活动历史超过允许的大小上限。");
            }
            if (File.Exists(_configPath))
            {
                File.Replace(temporaryPath, _configPath, backupPath, ignoreMetadataErrors: true);
                TryDelete(backupPath);
            }
            else
            {
                File.Move(temporaryPath, _configPath);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static ActivityEntry NormalizeStoredEntry(ActivityEntry entry)
    {
        return entry with
        {
            Title = NormalizeText(entry.Title, MaximumTitleCharacters, "操作标题"),
            Detail = NormalizeText(RedactSensitiveText(entry.Detail), MaximumDetailCharacters, "操作详情"),
            SourceUrl = NormalizeSourceUrl(entry.SourceUrl),
            Version = NormalizeOptionalText(entry.Version, MaximumVersionCharacters),
        };
    }

    private static bool IsValidStoredEntry(ActivityEntry entry)
    {
        return entry.Id != Guid.Empty
            && entry.Timestamp > DateTimeOffset.UnixEpoch
            && entry.Timestamp < DateTimeOffset.UtcNow.AddDays(1)
            && Enum.IsDefined(entry.Kind)
            && Enum.IsDefined(entry.Outcome)
            && !string.IsNullOrWhiteSpace(entry.Title);
    }

    private static string NormalizeText(string value, int maximumCharacters, string label)
    {
        value = new string(value
            .Select(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'
                ? ' '
                : character)
            .ToArray()).Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException($"{label}不能为空。", nameof(value));
        }
        return value.Length <= maximumCharacters ? value : value[..maximumCharacters];
    }

    private static string? NormalizeOptionalText(string? value, int maximumCharacters)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : NormalizeText(value, maximumCharacters, "可选字段");
    }

    private static string? NormalizeSourceUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }
        return new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri.AbsoluteUri;
    }

    private static string RedactSensitiveText(string value)
    {
        value = UrlPattern.Replace(value, match =>
        {
            if (!Uri.TryCreate(match.Value.TrimEnd('.', ',', ';', '。', '，'), UriKind.Absolute, out var uri))
            {
                return "[已隐藏链接参数]";
            }
            if (uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase))
            {
                return $"nxm://{uri.Host}{uri.AbsolutePath} [已隐藏]";
            }
            var hadSensitiveParts = !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment);
            var sanitized = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty,
            }.Uri.AbsoluteUri;
            return hadSensitiveParts ? $"{sanitized} [已隐藏]" : sanitized;
        });
        value = SecretAssignmentPattern.Replace(value, "$1=[已隐藏]");
        value = WindowsPathPattern.Replace(value, "[本地路径已隐藏]");
        return value;
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
            // A stale temporary or backup file must not hide the primary result.
        }
    }

    private sealed record ActivityHistoryDocument(int Version, IReadOnlyList<ActivityEntry>? Entries);
}
