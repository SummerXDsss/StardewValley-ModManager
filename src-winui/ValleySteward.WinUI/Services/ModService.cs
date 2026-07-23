using System.Diagnostics;
using System.Text.Json;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed class ModService
{
    private const long MaxManifestBytes = 256 * 1024;
    private const string TranslationSidecarName = ".valley-steward-translation.json";
    private const string TrashDirectoryName = ".mod-manager-trash";
    private const string TrashMetadataFileName = ".valley-steward-trash.json";
    private const int TrashMetadataSchemaVersion = 1;
    private const int MaximumNameCharacters = 512;
    private const int MaximumVersionCharacters = 128;
    private const int MaximumSourceDescriptionCharacters = 12_000;
    private const int MaximumTranslatedDescriptionCharacters = 16_000;
    private static readonly object TranslationWriteLock = new();
    private static readonly JsonSerializerOptions SidecarJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public Task<IReadOnlyList<InstalledMod>> ScanAsync(string gamePath)
    {
        return Task.Run<IReadOnlyList<InstalledMod>>(() =>
        {
            var modsRoot = Path.Combine(gamePath, "Mods");
            if (!Directory.Exists(modsRoot))
            {
                return Array.Empty<InstalledMod>();
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MaxRecursionDepth = 8,
            };

            var mods = new List<InstalledMod>();
            foreach (var manifestPath in Directory.EnumerateFiles(modsRoot, "manifest.json", options))
            {
                if (IsInsideTrash(manifestPath))
                {
                    continue;
                }

                mods.Add(ReadManifest(gamePath, modsRoot, manifestPath));
            }

            return mods
                .OrderByDescending(mod => mod.Enabled)
                .ThenBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        });
    }

    public Task SetEnabledAsync(string gamePath, InstalledMod mod, bool enabled)
    {
        return Task.Run(() =>
        {
            var (modsRoot, current) = ValidateModPath(gamePath, mod.Path);
            var name = Path.GetFileName(current);
            var cleanName = name.Trim('.');
            if (string.IsNullOrWhiteSpace(cleanName))
            {
                throw new InvalidOperationException("Mod 目录名称无效。");
            }

            var nextName = enabled ? cleanName : $".{cleanName}";
            var target = Path.Combine(Path.GetDirectoryName(current)!, nextName);
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            EnsureInsideRoot(modsRoot, target);
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new IOException("目标目录已经存在，无法切换 Mod 状态。");
            }

            Directory.Move(current, target);
        });
    }

    public Task MoveToTrashAsync(string gamePath, InstalledMod mod)
    {
        return Task.Run(() =>
        {
            var (modsRoot, current) = ValidateModPath(gamePath, mod.Path);
            EnsureNotReparsePoint(modsRoot, "Mods 目录");
            EnsureExistingPathSegmentsContainNoReparsePoints(modsRoot, current);
            EnsureNotInTrash(modsRoot, current);
            EnsureTreeContainsNoReparsePoints(current);

            var trash = GetTrashRoot(modsRoot, createIfMissing: true)!;
            var trashedAt = DateTimeOffset.UtcNow;
            var target = CreateUniqueTrashTarget(trash, Path.GetFileName(current), trashedAt);
            var originalRelativePath = Path.GetRelativePath(modsRoot, current);

            Directory.Move(current, target);
            try
            {
                WriteTrashMetadata(
                    target,
                    new TrashMetadata(
                        TrashMetadataSchemaVersion,
                        originalRelativePath,
                        trashedAt));
            }
            catch
            {
                if (!Directory.Exists(current) && Directory.Exists(target))
                {
                    Directory.Move(target, current);
                }
                throw;
            }
        });
    }

    public Task<IReadOnlyList<ModTrashItem>> ListTrashAsync(string gamePath)
    {
        return Task.Run<IReadOnlyList<ModTrashItem>>(() =>
        {
            var modsRoot = GetExistingModsRoot(gamePath);
            var trash = GetTrashRoot(modsRoot, createIfMissing: false);
            if (trash is null)
            {
                return Array.Empty<ModTrashItem>();
            }

            var items = new List<ModTrashItem>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(trash))
            {
                EnsureDirectChild(trash, entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException("回收区包含重解析点，已拒绝读取。");
                }
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    continue;
                }

                EnsureTreeContainsNoReparsePoints(entry);
                items.Add(ReadTrashItem(modsRoot, trash, entry));
            }

            return items
                .OrderByDescending(item => item.TrashedAt)
                .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        });
    }

    public Task<ModRestoreResult> RestoreFromTrashAsync(string gamePath, ModTrashItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Task.Run(() =>
        {
            var modsRoot = GetExistingModsRoot(gamePath);
            var trash = GetTrashRoot(modsRoot, createIfMissing: false)
                ?? throw new DirectoryNotFoundException("Mod 回收区不存在。");
            var entry = ResolveTrashEntry(trash, item.EntryName);
            EnsureTreeContainsNoReparsePoints(entry);

            // Re-read the on-disk metadata instead of trusting a stale UI object.
            var currentItem = ReadTrashItem(modsRoot, trash, entry);
            var desiredTarget = ResolveOriginalTarget(modsRoot, currentItem.OriginalRelativePath);
            var parent = Path.GetDirectoryName(desiredTarget)
                ?? throw new InvalidDataException("回收区记录的原始路径无效。");
            EnsureExistingPathSegmentsContainNoReparsePoints(modsRoot, parent);
            Directory.CreateDirectory(parent);
            EnsureExistingPathSegmentsContainNoReparsePoints(modsRoot, parent);

            var target = ResolveNonConflictingRestoreTarget(modsRoot, desiredTarget);
            Directory.Move(entry, target);
            try
            {
                DeleteTrashMetadata(target);
            }
            catch
            {
                if (!Directory.Exists(entry) && Directory.Exists(target))
                {
                    Directory.Move(target, entry);
                }
                throw;
            }

            var restoredRelativePath = Path.GetRelativePath(modsRoot, target);
            return new ModRestoreResult(
                currentItem.OriginalRelativePath,
                restoredRelativePath,
                !string.Equals(target, desiredTarget, StringComparison.OrdinalIgnoreCase));
        });
    }

    public Task<int> EmptyTrashAsync(string gamePath)
    {
        return Task.Run(() =>
        {
            var modsRoot = GetExistingModsRoot(gamePath);
            var trash = GetTrashRoot(modsRoot, createIfMissing: false);
            if (trash is null)
            {
                return 0;
            }

            var entries = Directory.EnumerateFileSystemEntries(trash).ToArray();
            foreach (var entry in entries)
            {
                EnsureDirectChild(trash, entry);
                EnsureTreeContainsNoReparsePoints(entry);
            }
            foreach (var entry in entries)
            {
                DeleteFileSystemEntryWithoutFollowingReparsePoints(entry);
            }

            return entries.Length;
        });
    }

    public void OpenFolder(string gamePath, InstalledMod mod)
    {
        var (_, current) = ValidateModPath(gamePath, mod.Path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
            ArgumentList = { current },
        });
    }

    public Task<InstalledModTranslationSource> ReadTranslationSourceAsync(
        string gamePath,
        InstalledMod mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return Task.Run(() =>
        {
            var (_, directory) = ValidateModPath(gamePath, mod.Path);
            return ReadTranslationSource(Path.Combine(directory, "manifest.json"));
        });
    }

    public Task SaveTranslationAsync(
        string gamePath,
        InstalledMod mod,
        InstalledModTranslationSource expectedSource,
        InstalledModTranslation translation)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(expectedSource);
        ArgumentNullException.ThrowIfNull(translation);
        return Task.Run(() =>
        {
            var (_, directory) = ValidateModPath(gamePath, mod.Path);
            var manifestPath = Path.Combine(directory, "manifest.json");
            var currentSource = ReadTranslationSource(manifestPath);
            if (currentSource != expectedSource)
            {
                throw new InvalidOperationException("AI 翻译期间 Mod 清单已发生变化，请重新翻译。");
            }
            ValidateText(translation.Name, "翻译后的 Mod 名称", MaximumNameCharacters, allowEmpty: false);
            ValidateText(
                translation.Description,
                "翻译后的 Mod 简介",
                MaximumTranslatedDescriptionCharacters,
                allowEmpty: false);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new TranslationSidecar(
                    1,
                    new TranslationSidecarSource(
                        expectedSource.UniqueId,
                        expectedSource.Version,
                        expectedSource.Name,
                        expectedSource.Description),
                    new TranslationSidecarContent(translation.Name, translation.Description)),
                SidecarJsonOptions);
            if (bytes.LongLength > MaxManifestBytes)
            {
                throw new InvalidDataException("Mod 翻译文件超过 256 KiB。");
            }

            try
            {
                WriteTranslationSidecar(directory, bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }
        });
    }

    private static InstalledMod ReadManifest(string gamePath, string modsRoot, string manifestPath)
    {
        var directory = Path.GetDirectoryName(manifestPath) ?? modsRoot;
        var relative = Path.GetRelativePath(gamePath, directory);
        var enabled = Path.GetRelativePath(modsRoot, directory)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .All(part => !part.StartsWith('.'));

        try
        {
            var info = new FileInfo(manifestPath);
            if (info.Length > MaxManifestBytes)
            {
                throw new InvalidDataException("manifest.json 超过 256 KiB。");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            var sourceName = RequiredString(root, "Name");
            var sourceDescription = OptionalString(root, "Description");
            var id = OptionalString(root, "UniqueID") ?? OptionalString(root, "UniqueId")
                ?? throw new InvalidDataException("缺少 UniqueID。");
            var version = RequiredString(root, "Version");
            var author = RequiredString(root, "Author");
            var translation = ReadTranslation(directory, id, version, sourceName, sourceDescription ?? string.Empty);
            var dependencies = ReadDependencies(root);
            var (updateKeys, updateKeysError) = ReadUpdateKeys(root);

            return new InstalledMod
            {
                Id = id,
                Name = translation?.Name ?? sourceName,
                Description = translation?.Description ?? sourceDescription,
                Translated = translation is not null,
                Author = author,
                Version = version,
                Path = relative,
                Enabled = enabled,
                Health = "healthy",
                Dependencies = dependencies,
                UpdateKeys = updateKeys,
                UpdateKeysError = updateKeysError,
            };
        }
        catch (Exception error)
        {
            return new InstalledMod
            {
                Id = $"invalid:{relative}",
                Name = Path.GetFileName(directory),
                Description = null,
                Translated = false,
                Author = "未知",
                Version = "未知",
                Path = relative,
                Enabled = enabled,
                Health = "error",
                Error = $"manifest.json 无法解析：{error.Message}",
            };
        }
    }

    private static InstalledModTranslationSource ReadTranslationSource(string manifestPath)
    {
        var info = new FileInfo(manifestPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Mod 目录中缺少 manifest.json。", manifestPath);
        }
        if (info.Length > MaxManifestBytes)
        {
            throw new InvalidDataException("manifest.json 超过 256 KiB。");
        }
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException("manifest.json 必须是普通文件，不能是目录或重解析点。");
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;
        var source = new InstalledModTranslationSource(
            OptionalString(root, "UniqueID") ?? OptionalString(root, "UniqueId")
                ?? throw new InvalidDataException("缺少 UniqueID。"),
            RequiredString(root, "Version"),
            RequiredString(root, "Name"),
            OptionalString(root, "Description") ?? string.Empty);
        ValidateText(source.UniqueId, "Mod UniqueID", MaximumNameCharacters, allowEmpty: false);
        ValidateText(source.Version, "Mod 版本", MaximumVersionCharacters, allowEmpty: false);
        ValidateText(source.Name, "Mod 名称", MaximumNameCharacters, allowEmpty: false);
        ValidateText(
            source.Description,
            "Mod 简介",
            MaximumSourceDescriptionCharacters,
            allowEmpty: true);
        return source;
    }

    private static void WriteTranslationSidecar(string directory, byte[] bytes)
    {
        var target = Path.Combine(directory, TranslationSidecarName);
        var temporary = Path.Combine(
            directory,
            $".valley-steward-translation-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        lock (TranslationWriteLock)
        {
            try
            {
                if (File.Exists(target))
                {
                    var attributes = File.GetAttributes(target);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    {
                        throw new InvalidDataException("拒绝覆盖不是普通文件的 Mod 翻译 sidecar。");
                    }
                }
                else if (Directory.Exists(target))
                {
                    throw new InvalidDataException("拒绝覆盖不是普通文件的 Mod 翻译 sidecar。");
                }

                using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4_096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(target))
                {
                    File.Replace(temporary, target, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, target);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch
                    {
                        // The write error is more useful than a temporary-file cleanup error.
                    }
                }
            }
        }
    }

    private static void ValidateText(
        string value,
        string label,
        int maximumCharacters,
        bool allowEmpty)
    {
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{label}不能为空。");
        }
        if (value.Length > maximumCharacters)
        {
            throw new InvalidDataException($"{label}不能超过 {maximumCharacters} 个字符。");
        }
        if (value.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
        {
            throw new InvalidDataException($"{label}包含无效控制字符。");
        }
    }

    private static IReadOnlyList<string> ReadDependencies(JsonElement root)
    {
        if (!TryGetProperty(root, "Dependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        foreach (var dependency in dependencies.EnumerateArray())
        {
            var required = !TryGetProperty(dependency, "IsRequired", out var requiredElement)
                || requiredElement.ValueKind != JsonValueKind.False;
            var id = OptionalString(dependency, "UniqueID") ?? OptionalString(dependency, "UniqueId");
            if (required && !string.IsNullOrWhiteSpace(id))
            {
                results.Add(id);
            }
        }

        return results;
    }

    private static (IReadOnlyList<string> Keys, string? Error) ReadUpdateKeys(JsonElement root)
    {
        if (!TryGetProperty(root, "UpdateKeys", out var updateKeys))
        {
            return (Array.Empty<string>(), null);
        }
        if (updateKeys.ValueKind != JsonValueKind.Array)
        {
            return (Array.Empty<string>(), "manifest.json 的 UpdateKeys 必须是字符串数组。");
        }

        var results = new List<string>();
        var ignoredValues = 0;
        foreach (var value in updateKeys.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { } key)
            {
                results.Add(key);
            }
            else
            {
                ignoredValues++;
            }
        }

        return ignoredValues == 0
            ? (results, null)
            : (results, $"UpdateKeys 中有 {ignoredValues} 个非字符串值，已忽略。");
    }

    private static Translation? ReadTranslation(
        string directory,
        string id,
        string version,
        string name,
        string description)
    {
        var path = Path.Combine(directory, TranslationSidecarName);
        if (!File.Exists(path) || new FileInfo(path).Length > MaxManifestBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!TryGetProperty(root, "schemaVersion", out var schemaVersion)
                || schemaVersion.GetInt32() != 1
                || !TryGetProperty(root, "source", out var source)
                || OptionalString(source, "uniqueId") != id
                || OptionalString(source, "version") != version
                || OptionalString(source, "name") != name
                || OptionalString(source, "description") != description
                || !TryGetProperty(root, "translation", out var translated))
            {
                return null;
            }

            var translatedName = OptionalString(translated, "name");
            var translatedDescription = OptionalString(translated, "description");
            return string.IsNullOrWhiteSpace(translatedName) || string.IsNullOrWhiteSpace(translatedDescription)
                ? null
                : new Translation(translatedName, translatedDescription);
        }
        catch
        {
            return null;
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        return OptionalString(root, name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"缺少 {name}。");
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string GetExistingModsRoot(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            throw new ArgumentException("游戏目录不能为空。", nameof(gamePath));
        }

        var modsRoot = Path.GetFullPath(Path.Combine(Path.GetFullPath(gamePath), "Mods"));
        if (!Directory.Exists(modsRoot))
        {
            throw new DirectoryNotFoundException("Mods 目录不存在。");
        }

        EnsureNotReparsePoint(modsRoot, "Mods 目录");
        return modsRoot;
    }

    private static string? GetTrashRoot(string modsRoot, bool createIfMissing)
    {
        EnsureNotReparsePoint(modsRoot, "Mods 目录");
        var trash = Path.GetFullPath(Path.Combine(modsRoot, TrashDirectoryName));
        EnsureInsideRoot(modsRoot, trash);
        if (Directory.Exists(trash))
        {
            EnsureNotReparsePoint(trash, "Mod 回收区");
            return trash;
        }
        if (File.Exists(trash))
        {
            throw new InvalidDataException("Mod 回收区路径被普通文件占用。");
        }
        if (!createIfMissing)
        {
            return null;
        }

        Directory.CreateDirectory(trash);
        EnsureNotReparsePoint(trash, "Mod 回收区");
        return trash;
    }

    private static string CreateUniqueTrashTarget(
        string trashRoot,
        string originalDirectoryName,
        DateTimeOffset trashedAt)
    {
        ValidateSimpleDirectoryName(originalDirectoryName, "Mod 目录名称");
        var timestamp = trashedAt.ToUnixTimeMilliseconds();
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var target = Path.Combine(trashRoot, $"{timestamp + attempt}-{originalDirectoryName}");
            EnsureDirectChild(trashRoot, target);
            if (!Directory.Exists(target) && !File.Exists(target))
            {
                return target;
            }
        }

        throw new IOException("无法在 Mod 回收区生成不冲突的目录名称。");
    }

    private static void WriteTrashMetadata(string entryDirectory, TrashMetadata metadata)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, SidecarJsonOptions);
        if (bytes.LongLength > MaxManifestBytes)
        {
            throw new InvalidDataException("Mod 回收区元数据超过 256 KiB。");
        }

        var target = Path.Combine(entryDirectory, TrashMetadataFileName);
        var temporary = Path.Combine(
            entryDirectory,
            $".valley-steward-trash-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4_096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target);
        }
        finally
        {
            Array.Clear(bytes);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static ModTrashItem ReadTrashItem(string modsRoot, string trashRoot, string entry)
    {
        EnsureDirectChild(trashRoot, entry);
        EnsureNotReparsePoint(entry, "回收区项目");

        var entryName = Path.GetFileName(entry);
        var metadata = ReadTrashMetadata(entry);
        var (legacyName, legacyTime) = ParseLegacyTrashEntryName(entryName);
        var requestedRelativePath = metadata?.OriginalRelativePath ?? legacyName;
        var originalTarget = ResolveOriginalTarget(modsRoot, requestedRelativePath);
        var originalRelativePath = Path.GetRelativePath(modsRoot, originalTarget);
        var originalDirectoryName = Path.GetFileName(originalTarget);
        var (displayName, version) = ReadTrashManifest(entry, originalDirectoryName);

        return new ModTrashItem(
            entryName,
            originalDirectoryName,
            originalRelativePath,
            displayName,
            version,
            metadata?.TrashedAt ?? legacyTime);
    }

    private static TrashMetadata? ReadTrashMetadata(string entryDirectory)
    {
        var path = Path.Combine(entryDirectory, TrashMetadataFileName);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path))
            {
                throw new InvalidDataException("Mod 回收区元数据不是普通文件。");
            }
            return null;
        }

        var info = new FileInfo(path);
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new UnauthorizedAccessException("Mod 回收区元数据不能是目录或重解析点。");
        }
        if (info.Length > MaxManifestBytes)
        {
            throw new InvalidDataException("Mod 回收区元数据超过 256 KiB。");
        }

        var metadata = JsonSerializer.Deserialize<TrashMetadata>(File.ReadAllBytes(path), SidecarJsonOptions)
            ?? throw new InvalidDataException("Mod 回收区元数据为空。");
        if (metadata.SchemaVersion != TrashMetadataSchemaVersion
            || string.IsNullOrWhiteSpace(metadata.OriginalRelativePath))
        {
            throw new InvalidDataException("Mod 回收区元数据版本或原始路径无效。");
        }
        return metadata;
    }

    private static (string Name, string? Version) ReadTrashManifest(
        string entryDirectory,
        string fallbackName)
    {
        var manifestPath = Path.Combine(entryDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return (fallbackName, null);
        }

        var info = new FileInfo(manifestPath);
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new UnauthorizedAccessException("回收区 Mod 的 manifest.json 不能是重解析点。");
        }
        if (info.Length > MaxManifestBytes)
        {
            return (fallbackName, null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            return (
                OptionalString(document.RootElement, "Name") ?? fallbackName,
                OptionalString(document.RootElement, "Version"));
        }
        catch (JsonException)
        {
            return (fallbackName, null);
        }
    }

    private static (string OriginalName, DateTimeOffset? TrashedAt) ParseLegacyTrashEntryName(
        string entryName)
    {
        var separatorIndex = entryName.IndexOf('-');
        if (separatorIndex <= 0
            || separatorIndex == entryName.Length - 1
            || !long.TryParse(entryName.AsSpan(0, separatorIndex), out var timestamp))
        {
            ValidateSimpleDirectoryName(entryName, "回收区项目名称");
            return (entryName, null);
        }

        var originalName = entryName[(separatorIndex + 1)..];
        ValidateSimpleDirectoryName(originalName, "回收区记录的原始目录名称");
        try
        {
            var trashedAt = timestamp >= 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            return (originalName, trashedAt);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (originalName, null);
        }
    }

    private static string ResolveTrashEntry(string trashRoot, string entryName)
    {
        ValidateSimpleDirectoryName(entryName, "回收区项目名称");
        var entry = Path.GetFullPath(Path.Combine(trashRoot, entryName));
        EnsureDirectChild(trashRoot, entry);
        if (!Directory.Exists(entry))
        {
            throw new DirectoryNotFoundException("所选回收区项目不存在。");
        }
        EnsureNotReparsePoint(entry, "回收区项目");
        return entry;
    }

    private static string ResolveOriginalTarget(string modsRoot, string originalRelativePath)
    {
        if (string.IsNullOrWhiteSpace(originalRelativePath)
            || Path.IsPathFullyQualified(originalRelativePath))
        {
            throw new InvalidDataException("回收区记录的原始路径无效。");
        }

        var parts = originalRelativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
        {
            throw new InvalidDataException("回收区记录的原始路径包含不安全的路径段。");
        }

        var target = Path.GetFullPath(Path.Combine(modsRoot, originalRelativePath));
        EnsureInsideRoot(modsRoot, target);
        EnsureNotInTrash(modsRoot, target);
        ValidateSimpleDirectoryName(Path.GetFileName(target), "回收区记录的原始目录名称");
        return target;
    }

    private static string ResolveNonConflictingRestoreTarget(string modsRoot, string desiredTarget)
    {
        EnsureInsideRoot(modsRoot, desiredTarget);
        EnsureNotInTrash(modsRoot, desiredTarget);
        if (!Directory.Exists(desiredTarget) && !File.Exists(desiredTarget))
        {
            return desiredTarget;
        }

        var parent = Path.GetDirectoryName(desiredTarget)
            ?? throw new InvalidDataException("恢复目标路径无效。");
        var originalName = Path.GetFileName(desiredTarget);
        for (var suffix = 2; suffix <= 10_000; suffix++)
        {
            var candidate = Path.Combine(parent, $"{originalName} (已恢复 {suffix})");
            EnsureInsideRoot(modsRoot, candidate);
            EnsureNotInTrash(modsRoot, candidate);
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("恢复目标存在过多同名目录，无法生成不冲突的名称。");
    }

    private static void DeleteTrashMetadata(string restoredDirectory)
    {
        var path = Path.Combine(restoredDirectory, TrashMetadataFileName);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path))
            {
                throw new InvalidDataException("回收区元数据不是普通文件。");
            }
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new UnauthorizedAccessException("拒绝删除目录或重解析点形式的回收区元数据。");
        }
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        File.Delete(path);
    }

    private static void EnsureExistingPathSegmentsContainNoReparsePoints(
        string root,
        string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (!string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            EnsureInsideRoot(fullRoot, fullCandidate);
        }

        EnsureNotReparsePoint(fullRoot, "Mods 目录");
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == ".")
        {
            return;
        }

        var current = fullRoot;
        foreach (var part in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current) || File.Exists(current))
            {
                EnsureNotReparsePoint(current, "恢复目标路径");
            }
        }
    }

    private static void EnsureTreeContainsNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Mod 文件树包含重解析点，已拒绝操作。");
            }
            if ((attributes & FileAttributes.Directory) == 0)
            {
                continue;
            }

            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                pending.Push(child);
            }
        }
    }

    private static void DeleteFileSystemEntryWithoutFollowingReparsePoints(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("回收区包含重解析点，已拒绝清空。");
        }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            File.Delete(path);
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            DeleteFileSystemEntryWithoutFollowingReparsePoints(child);
        }
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: false);
    }

    private static void EnsureNotReparsePoint(string path, string label)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException($"{label}不能是重解析点。");
        }
    }

    private static void EnsureDirectChild(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        EnsureInsideRoot(fullRoot, fullCandidate);
        if (!string.Equals(
                Path.GetDirectoryName(fullCandidate),
                fullRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("拒绝操作回收区直属项目以外的路径。");
        }
    }

    private static void EnsureNotInTrash(string modsRoot, string candidate)
    {
        var trash = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(modsRoot, TrashDirectoryName)));
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (string.Equals(trash, fullCandidate, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(
                trash + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("拒绝把 Mod 恢复到管理器回收区内。");
        }
    }

    private static void ValidateSimpleDirectoryName(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || Path.IsPathFullyQualified(value)
            || !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
            || value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidDataException($"{label}无效。");
        }
    }

    private static (string ModsRoot, string Current) ValidateModPath(string gamePath, string modPath)
    {
        var canonicalGame = Path.GetFullPath(gamePath);
        var modsRoot = Path.GetFullPath(Path.Combine(canonicalGame, "Mods"));
        if (!Directory.Exists(modsRoot))
        {
            throw new DirectoryNotFoundException("Mods 目录不存在。");
        }

        var requested = Path.IsPathFullyQualified(modPath)
            ? modPath
            : Path.Combine(canonicalGame, modPath);
        var current = Path.GetFullPath(requested);
        EnsureInsideRoot(modsRoot, current);
        if (!Directory.Exists(current))
        {
            throw new DirectoryNotFoundException("Mod 目录不存在。");
        }

        return (modsRoot, current);
    }

    private static void EnsureInsideRoot(string root, string candidate)
    {
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("拒绝操作 Mods 目录以外的路径。");
        }
    }

    private static bool IsInsideTrash(string path)
    {
        return path.Contains(
            $"{Path.DirectorySeparatorChar}.mod-manager-trash{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Translation(string Name, string Description);
    private sealed record TranslationSidecar(
        int SchemaVersion,
        TranslationSidecarSource Source,
        TranslationSidecarContent Translation);
    private sealed record TranslationSidecarSource(
        string UniqueId,
        string Version,
        string Name,
        string Description);
    private sealed record TranslationSidecarContent(string Name, string Description);
    private sealed record TrashMetadata(
        int SchemaVersion,
        string OriginalRelativePath,
        DateTimeOffset TrashedAt);
}
