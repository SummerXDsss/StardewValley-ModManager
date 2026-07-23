using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed partial class SteamService
{
    private const ulong SteamId64AccountBase = 76_561_197_960_265_728;
    private const long MaximumAvatarBytes = 4 * 1024 * 1024;
    private static readonly string[] AvatarExtensions = [".png", ".jpg", ".jpeg"];

    public Task<SteamStatus> ReadStatusAsync()
    {
        return Task.Run(() =>
        {
            var running = Process.GetProcessesByName("steam").Length > 0;
            var activeAccount = running ? ReadActiveAccountId() : null;
            var users = ReadLoginUsers();
            var selected = activeAccount.HasValue
                ? users.FirstOrDefault(user => user.AccountId == activeAccount.Value)
                : users.OrderByDescending(user => user.MostRecent).ThenByDescending(user => user.Timestamp).FirstOrDefault();

            if (selected is not null)
            {
                var steamId64 = selected.SteamId64.ToString();
                return new SteamStatus(
                    running,
                    new SteamIdentity(
                        steamId64,
                        selected.AccountId.ToString(),
                        selected.AccountName,
                        selected.PersonaName,
                        activeAccount.HasValue ? "注册表活动账号" : "loginusers.vdf",
                        running && (activeAccount is null || selected.AccountId == activeAccount.Value),
                        ResolveAvatarPath(steamId64, GameDiscoveryService.SteamRoots())));
            }

            if (activeAccount.HasValue)
            {
                var steamId64 = (SteamId64AccountBase + activeAccount.Value).ToString();
                return new SteamStatus(
                    running,
                    new SteamIdentity(
                        steamId64,
                        activeAccount.Value.ToString(),
                        null,
                        null,
                        "注册表活动账号",
                        true,
                        ResolveAvatarPath(steamId64, GameDiscoveryService.SteamRoots())));
            }

            return new SteamStatus(running, null);
        });
    }

    internal static string? ResolveAvatarPath(
        string? steamId64,
        IEnumerable<string> steamRoots)
    {
        if (!ulong.TryParse(steamId64, out var parsedSteamId)
            || parsedSteamId < SteamId64AccountBase
            || parsedSteamId - SteamId64AccountBase > uint.MaxValue)
        {
            return null;
        }

        foreach (var untrustedRoot in steamRoots)
        {
            if (!TryNormalizeSteamRoot(untrustedRoot, out var root))
            {
                continue;
            }

            var cacheDirectory = Path.Combine(root, "config", "avatarcache");
            foreach (var extension in AvatarExtensions)
            {
                var candidate = Path.Combine(cacheDirectory, steamId64 + extension);
                if (IsTrustedAvatarFile(root, candidate, extension))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static bool TryNormalizeSteamRoot(string? value, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            root = Path.GetFullPath(GameDiscoveryService.NormalizeWindowsPath(value.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.IsPathFullyQualified(root);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsTrustedAvatarFile(string root, string candidate, string extension)
    {
        try
        {
            var fullPath = Path.GetFullPath(candidate);
            var relative = Path.GetRelativePath(root, fullPath);
            if (relative.Length == 0
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                return false;
            }

            var current = root;
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }

            var info = new FileInfo(fullPath);
            if (!info.Exists
                || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || info.Length is <= 0 or > MaximumAvatarBytes)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[12];
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                header.Length,
                FileOptions.SequentialScan);
            var bytesRead = stream.Read(header);
            return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? bytesRead >= 8
                    && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
                : bytesRead >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        }
        catch (Exception error) when (error is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException
                                      or PathTooLongException)
        {
            return false;
        }
    }

    private static uint? ReadActiveAccountId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            var value = key?.GetValue("ActiveUser");
            return value switch
            {
                int id when id > 0 => unchecked((uint)id),
                uint id when id > 0 => id,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<SteamLoginUser> ReadLoginUsers()
    {
        var users = new Dictionary<uint, SteamLoginUser>();
        foreach (var root in GameDiscoveryService.SteamRoots())
        {
            var path = Path.Combine(root, "config", "loginusers.vdf");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(path);
                foreach (Match match in UserBlockRegex().Matches(content))
                {
                    if (!ulong.TryParse(match.Groups["steamId"].Value, out var steamId64)
                        || steamId64 < SteamId64AccountBase
                        || steamId64 - SteamId64AccountBase > uint.MaxValue)
                    {
                        continue;
                    }

                    var body = match.Groups["body"].Value;
                    var accountId = (uint)(steamId64 - SteamId64AccountBase);
                    var user = new SteamLoginUser(
                        accountId,
                        steamId64,
                        ReadField(body, "AccountName"),
                        ReadField(body, "PersonaName"),
                        ReadField(body, "MostRecent") == "1",
                        ulong.TryParse(ReadField(body, "Timestamp"), out var timestamp) ? timestamp : 0);
                    if (!users.TryGetValue(accountId, out var current) || user.Timestamp > current.Timestamp)
                    {
                        users[accountId] = user;
                    }
                }
            }
            catch
            {
                // Ignore a locked or malformed Steam account file.
            }
        }

        return users.Values.ToArray();
    }

    private static string? ReadField(string body, string field)
    {
        var match = Regex.Match(
            body,
            $"\\\"{Regex.Escape(field)}\\\"\\s+\\\"(?<value>[^\\\"]*)\\\"",
            RegexOptions.IgnoreCase);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups["value"].Value)
            ? match.Groups["value"].Value
            : null;
    }

    [GeneratedRegex("\\\"(?<steamId>\\d{17})\\\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline)]
    private static partial Regex UserBlockRegex();

    private sealed record SteamLoginUser(
        uint AccountId,
        ulong SteamId64,
        string? AccountName,
        string? PersonaName,
        bool MostRecent,
        ulong Timestamp);
}
