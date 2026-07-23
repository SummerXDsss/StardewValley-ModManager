using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed class ModInstallerService
{
    private const string ManifestFileName = "manifest.json";
    private const string InstalledTranslationSidecarName = ".valley-steward-translation.json";
    private const int MaximumTextCharacters = 16_000;
    private const int MaximumDependencies = 512;
    private const int CopyBufferBytes = 128 * 1024;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixDirectoryType = 0x4000;
    private const int UnixRegularFileType = 0x8000;
    private const int UnixSymbolicLinkType = 0xA000;
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };
    private static readonly JsonSerializerOptions SidecarJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ModArchiveLimits _limits;

    public ModInstallerService(ModArchiveLimits? limits = null)
    {
        _limits = limits ?? new ModArchiveLimits();
        ValidateLimits(_limits);
    }

    public Task<ModInstallPlan> InspectAsync(
        string archivePath,
        string? gamePath = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => InspectCore(archivePath, gamePath, cancellationToken),
            cancellationToken);
    }

    public Task<ModInstallPlan> InspectArchiveAsync(
        string archivePath,
        string? gamePath = null,
        CancellationToken cancellationToken = default)
    {
        return InspectAsync(archivePath, gamePath, cancellationToken);
    }

    public async Task<ModInstallResult> InstallAsync(
        string gamePath,
        string archivePath,
        ModInstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await InspectAsync(archivePath, gamePath, cancellationToken);
        return await InstallAsync(gamePath, plan, options, cancellationToken);
    }

    public Task<ModInstallResult> InstallAsync(
        string gamePath,
        NexusDownloadResult download,
        ModInstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(download);
        var effectiveOptions = options ?? new ModInstallOptions();
        if (string.IsNullOrWhiteSpace(effectiveOptions.TranslationSidecarPath)
            && !string.IsNullOrWhiteSpace(download.MetadataPath))
        {
            effectiveOptions = effectiveOptions with
            {
                TranslationSidecarPath = download.MetadataPath,
            };
        }
        return InstallAsync(gamePath, download.Path, effectiveOptions, cancellationToken);
    }

    public async Task<ModInstallResult> InstallAsync(
        string gamePath,
        ModInstallPlan plan,
        ModInstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(
                () => InstallCore(gamePath, plan, options ?? new ModInstallOptions(), cancellationToken),
                cancellationToken);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private ModInstallPlan InspectCore(
        string archivePath,
        string? gamePath,
        CancellationToken cancellationToken)
    {
        var fullArchivePath = ValidateArchiveFile(archivePath);
        var catalog = ReadCatalog(fullArchivePath, cancellationToken);
        var normalizedGamePath = string.IsNullOrWhiteSpace(gamePath)
            ? null
            : GameDiscoveryService.NormalizeWindowsPath(Path.GetFullPath(gamePath.Trim()));
        return CreatePlan(catalog, normalizedGamePath, cancellationToken);
    }

    private ModInstallResult InstallCore(
        string gamePath,
        ModInstallPlan requestedPlan,
        ModInstallOptions options,
        CancellationToken cancellationToken)
    {
        var normalizedGamePath = ValidateGameDirectory(gamePath);
        var refreshedPlan = InspectCore(requestedPlan.ArchivePath, normalizedGamePath, cancellationToken);
        if (!string.Equals(
                requestedPlan.ArchiveSha256,
                refreshedPlan.ArchiveSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Mod ZIP 在预检后发生了变化，请重新检查后再安装。");
        }
        if (!refreshedPlan.CanInstall)
        {
            var details = string.Join(
                "；",
                refreshedPlan.Conflicts.Where(conflict => conflict.Blocking).Select(conflict => conflict.Message));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(details) ? "Mod ZIP 当前不可安装。" : details);
        }
        if (refreshedPlan.RequiresXnbConfirmation && !options.AllowXnbFiles)
        {
            throw new InvalidOperationException(
                "压缩包包含 XNB 文件。此类文件可能属于旧式内容替换，确认来源可信后显式允许才能安装。");
        }

        var preservePaths = NormalizePreservePaths(options.PreserveRelativePaths);
        var translation = ResolveTranslation(
            refreshedPlan,
            options,
            requestedPlan.ArchiveSha256);
        var catalog = ReadCatalog(refreshedPlan.ArchivePath, cancellationToken);
        if (!string.Equals(catalog.Sha256, refreshedPlan.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Mod ZIP 在安装开始前发生了变化，请重新检查后再安装。");
        }

        var modsRoot = Path.Combine(normalizedGamePath, "Mods");
        Directory.CreateDirectory(modsRoot);
        EnsureOrdinaryDirectory(modsRoot, "Mods 目录");
        var stagingParent = EnsurePrivateDirectory(normalizedGamePath, ".valley-steward-staging");
        var backupParent = EnsurePrivateDirectory(normalizedGamePath, ".valley-steward-backups");
        var transactionId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var stagingRoot = Path.Combine(stagingParent, transactionId);
        var backupRoot = Path.Combine(backupParent, transactionId);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);
        EnsureOrdinaryDirectory(stagingRoot, "Mod 安装暂存目录");
        EnsureOrdinaryDirectory(backupRoot, "Mod 安装备份目录");

        var prepared = new List<PreparedMod>();
        var keepBackup = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var planById = refreshedPlan.Mods.ToDictionary(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < catalog.Mods.Count; index++)
            {
                var source = catalog.Mods[index];
                var planned = planById[source.Manifest.UniqueId];
                var target = planned.TargetPath
                    ?? throw new InvalidOperationException("安装计划缺少目标路径。");
                EnsurePathInside(modsRoot, target);
                var stage = Path.Combine(
                    stagingRoot,
                    $"{index:D4}-{MakeSafeTargetFolderName(planned.TargetFolderName, source.Manifest.UniqueId)}");
                Directory.CreateDirectory(stage);
                prepared.Add(new PreparedMod(source, planned, stage, target));
            }

            ExtractToStaging(catalog, prepared, cancellationToken);
            ValidateStagedManifests(prepared, cancellationToken);

            foreach (var item in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureDirectoryChainHasNoReparsePoint(
                    modsRoot,
                    Path.GetDirectoryName(item.TargetPath)
                        ?? throw new InvalidDataException("Mod 安装目标缺少父目录。"));
                var targetState = GetPathState(item.TargetPath);
                if (item.Plan.ExistingVersion is null)
                {
                    if (targetState != PathState.Missing)
                    {
                        throw new IOException($"安装目标在预检后被占用：{item.TargetPath}");
                    }
                }
                else
                {
                    if (targetState != PathState.Directory)
                    {
                        throw new IOException($"待更新的 Mod 目录在预检后发生了变化：{item.TargetPath}");
                    }
                    EnsureOrdinaryDirectory(item.TargetPath, "待更新的 Mod 目录");
                    var current = ReadManifestFromDirectory(item.TargetPath);
                    if (!string.Equals(current.UniqueId, item.Source.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(current.Version, item.Plan.ExistingVersion, StringComparison.Ordinal))
                    {
                        throw new IOException($"待更新目录中的 Mod ID 或版本已发生变化：{item.TargetPath}");
                    }

                    item.BackupPath = Path.Combine(
                        backupRoot,
                        $"{prepared.IndexOf(item):D4}-{Path.GetFileName(item.TargetPath)}");
                    Directory.Move(item.TargetPath, item.BackupPath);
                    item.BackupTaken = true;
                    keepBackup = true;
                    CopyPreservedFiles(item.BackupPath, item.StagePath, preservePaths, item.PreservedFiles);
                }

                if (translation is not null
                    && string.Equals(
                        translation.TargetUniqueId,
                        item.Source.Manifest.UniqueId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    WriteInstalledTranslation(item.StagePath, item.Source.Manifest, translation);
                    item.TranslationApplied = true;
                }

                Directory.Move(item.StagePath, item.TargetPath);
                item.Committed = true;
            }

            var installed = prepared.Select(item => new ModInstalledItem(
                item.Source.Manifest.Name,
                item.Source.Manifest.UniqueId,
                item.Source.Manifest.Version,
                item.TargetPath,
                item.BackupTaken,
                item.BackupPath,
                item.PreservedFiles.ToArray(),
                item.TranslationApplied)).ToArray();
            var replaced = installed.Count(mod => mod.Replaced);
            var newlyInstalled = installed.Length - replaced;
            var messages = new List<string>
            {
                $"安装完成：新增 {newlyInstalled} 个，覆盖更新 {replaced} 个。",
            };
            var preservedCount = installed.Sum(mod => mod.PreservedFiles.Count);
            if (preservedCount > 0)
            {
                messages.Add($"已保留 {preservedCount} 个用户配置文件。");
            }
            if (translation is not null)
            {
                messages.Add(installed.Any(mod => mod.TranslationApplied)
                    ? "已安全写入下载时保存的中文名称与简介。"
                    : "翻译 sidecar 未匹配到目标 Mod，因此没有写入。");
            }
            if (replaced > 0)
            {
                messages.Add($"旧版本备份保存在 {backupRoot}");
            }

            return new ModInstallResult(
                installed,
                newlyInstalled,
                replaced,
                replaced > 0 ? backupRoot : null,
                messages.ToArray());
        }
        catch (Exception installError)
        {
            var rollbackErrors = RollBack(prepared, stagingRoot);
            if (rollbackErrors.Count > 0)
            {
                keepBackup = true;
                throw new IOException(
                    $"Mod 安装失败，且自动恢复没有完全完成。旧目录保留在 {backupRoot}。"
                    + $" 安装错误：{installError.Message}；恢复错误：{string.Join("；", rollbackErrors.Select(error => error.Message))}",
                    new AggregateException(new[] { installError }.Concat(rollbackErrors)));
            }
            keepBackup = false;
            throw;
        }
        finally
        {
            TryDeleteOwnedDirectory(stagingRoot);
            TryDeleteEmptyDirectory(stagingParent);
            if (!keepBackup)
            {
                TryDeleteOwnedDirectory(backupRoot);
                TryDeleteEmptyDirectory(backupParent);
            }
        }
    }

    private Catalog ReadCatalog(string archivePath, CancellationToken cancellationToken)
    {
        archivePath = ValidateArchiveFile(archivePath);
        using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.SequentialScan);
        var sha256 = ComputeSha256(stream, cancellationToken);
        stream.Position = 0;

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > _limits.MaximumEntries)
            {
                throw new InvalidDataException(
                    $"ZIP 条目数超过 {_limits.MaximumEntries} 个的安全上限。");
            }

            var entries = new List<CatalogEntry>(archive.Entries.Count);
            var paths = new Dictionary<string, CatalogEntry>(PathComparer);
            long totalBytes = 0;
            for (var index = 0; index < archive.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.Entries[index];
                var isDirectory = IsDirectoryEntry(entry);
                ValidateArchiveEntryType(entry, isDirectory);
                var normalizedPath = NormalizeArchivePath(entry.FullName, isDirectory);
                var length = entry.Length;
                if (length < 0 || length > _limits.MaximumEntryBytes)
                {
                    throw new InvalidDataException(
                        $"ZIP 条目 {normalizedPath} 超过 {_limits.MaximumEntryBytes / 1024 / 1024} MB 的单文件上限。");
                }
                if (isDirectory && length != 0)
                {
                    throw new InvalidDataException($"ZIP 目录条目包含数据：{normalizedPath}");
                }
                try
                {
                    totalBytes = checked(totalBytes + length);
                }
                catch (OverflowException error)
                {
                    throw new InvalidDataException("ZIP 声明的解压总量无效。", error);
                }
                if (totalBytes > _limits.MaximumExtractedBytes)
                {
                    throw new InvalidDataException(
                        $"ZIP 解压总量超过 {_limits.MaximumExtractedBytes / 1024 / 1024} MB 的安全上限。");
                }

                var catalogEntry = new CatalogEntry(index, normalizedPath, isDirectory, length);
                if (!paths.TryAdd(normalizedPath, catalogEntry))
                {
                    throw new InvalidDataException($"ZIP 包含重复或大小写冲突的路径：{normalizedPath}");
                }
                entries.Add(catalogEntry);
            }
            ValidateNoFileDirectoryCollisions(entries);

            var mods = new List<CatalogMod>();
            foreach (var entry in entries.Where(entry => !entry.IsDirectory && IsManifestPath(entry.Path)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Length > _limits.MaximumManifestBytes)
                {
                    throw new InvalidDataException(
                        $"{entry.Path} 超过 {_limits.MaximumManifestBytes / 1024} KiB 的上限。");
                }
                var manifest = ReadManifest(archive.Entries[entry.Index], entry.Path, cancellationToken);
                var root = GetParentPath(entry.Path);
                mods.Add(new CatalogMod(root, manifest));
            }
            if (mods.Count > _limits.MaximumMods)
            {
                throw new InvalidDataException($"ZIP 中的 Mod 数量超过 {_limits.MaximumMods} 个的安全上限。");
            }
            var duplicateId = mods
                .GroupBy(mod => mod.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateId is not null)
            {
                throw new InvalidDataException($"ZIP 内存在重复 Mod ID：{duplicateId.Key}");
            }
            ValidateDisjointModRoots(mods);

            var xnbFiles = entries
                .Where(entry => !entry.IsDirectory && entry.Path.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Path)
                .ToArray();
            return new Catalog(
                archivePath,
                sha256,
                stream.Length,
                totalBytes,
                entries,
                mods,
                xnbFiles);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or NotSupportedException)
        {
            throw new InvalidDataException("无法安全读取该 ZIP 压缩包。", error);
        }
    }

    private ModInstallPlan CreatePlan(
        Catalog catalog,
        string? gamePath,
        CancellationToken cancellationToken)
    {
        var conflicts = new List<ModInstallConflict>();
        var warnings = new List<string>();
        var installed = gamePath is not null
            ? ScanExistingMods(Path.Combine(gamePath, "Mods"), cancellationToken)
            : Array.Empty<ExistingMod>();
        var modsRoot = gamePath is null ? null : Path.GetFullPath(Path.Combine(gamePath, "Mods"));

        var plannedMods = new List<ModArchiveMod>();
        foreach (var source in catalog.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderName = MakeSafeTargetFolderName(
                GetLeafName(source.Root),
                source.Manifest.UniqueId);
            string? targetPath = null;
            string? existingVersion = null;
            if (modsRoot is not null)
            {
                var matches = installed
                    .Where(mod => string.Equals(
                        mod.Manifest.UniqueId,
                        source.Manifest.UniqueId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length > 1)
                {
                    conflicts.Add(new ModInstallConflict(
                        ModInstallConflictKind.DuplicateInstalledId,
                        source.Manifest.UniqueId,
                        null,
                        true,
                        $"本机存在多个 ID 为 {source.Manifest.UniqueId} 的 Mod，无法确定更新目标。"));
                    targetPath = Path.Combine(modsRoot, folderName);
                }
                else if (matches.Length == 1)
                {
                    targetPath = matches[0].Directory;
                    existingVersion = matches[0].Manifest.Version;
                    conflicts.Add(new ModInstallConflict(
                        ModInstallConflictKind.ExistingMod,
                        source.Manifest.UniqueId,
                        targetPath,
                        false,
                        $"将把 {source.Manifest.UniqueId} 从 {existingVersion} 更新为 {source.Manifest.Version}。"));
                }
                else
                {
                    targetPath = Path.Combine(modsRoot, folderName);
                    var state = GetPathState(targetPath);
                    if (state != PathState.Missing)
                    {
                        conflicts.Add(new ModInstallConflict(
                            ModInstallConflictKind.TargetOccupied,
                            source.Manifest.UniqueId,
                            targetPath,
                            true,
                            $"目标名称 {folderName} 已被其他文件或 Mod 占用。"));
                    }
                }
                EnsurePathInside(modsRoot, targetPath);
            }

            plannedMods.Add(new ModArchiveMod(
                source.Manifest.Name,
                source.Manifest.UniqueId,
                source.Manifest.Version,
                source.Manifest.Author,
                source.Manifest.Description,
                source.Manifest.Dependencies,
                source.Root,
                folderName,
                targetPath,
                existingVersion));
        }

        if (plannedMods.Count == 0)
        {
            conflicts.Add(new ModInstallConflict(
                ModInstallConflictKind.MissingManifest,
                null,
                null,
                true,
                "ZIP 中没有找到 manifest.json，不能作为 SMAPI Mod 安装。"));
        }
        AddTargetConflicts(plannedMods, conflicts);

        var ignoredEntryCount = catalog.Entries.Count(entry =>
            catalog.Mods.All(mod => !IsPathInsideArchiveRoot(entry.Path, mod.Root)));
        if (ignoredEntryCount > 0)
        {
            warnings.Add($"压缩包中有 {ignoredEntryCount} 个位于 Mod 根目录外的条目，安装时会忽略。");
        }
        if (catalog.XnbFiles.Count > 0)
        {
            warnings.Add(
                $"检测到 {catalog.XnbFiles.Count} 个 XNB 文件。安装前需要确认它们不是覆盖游戏原文件的旧式 Mod。");
        }

        return new ModInstallPlan(
            catalog.ArchivePath,
            catalog.Sha256,
            catalog.ArchiveBytes,
            catalog.Entries.Count,
            catalog.UncompressedBytes,
            plannedMods.ToArray(),
            conflicts.ToArray(),
            catalog.XnbFiles,
            ignoredEntryCount,
            warnings.ToArray());
    }

    private void ExtractToStaging(
        Catalog catalog,
        IReadOnlyList<PreparedMod> prepared,
        CancellationToken cancellationToken)
    {
        ValidateArchiveFile(catalog.ArchivePath);
        using var stream = new FileStream(
            catalog.ArchivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.SequentialScan);
        var sha256 = ComputeSha256(stream, cancellationToken);
        if (!string.Equals(sha256, catalog.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Mod ZIP 在解压前发生了变化，请重新检查后再安装。");
        }
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        long extractedBytes = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            foreach (var entry in catalog.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetMod = prepared.FirstOrDefault(item =>
                    IsPathInsideArchiveRoot(entry.Path, item.Source.Root));
                if (targetMod is null)
                {
                    continue;
                }
                var relative = GetRelativeArchivePath(entry.Path, targetMod.Source.Root);
                if (relative.Length == 0)
                {
                    continue;
                }
                var destination = ResolveSafeRelativePath(targetMod.StagePath, relative);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                    EnsureDirectoryChainHasNoReparsePoint(targetMod.StagePath, destination);
                    continue;
                }

                var parent = Path.GetDirectoryName(destination)
                    ?? throw new InvalidDataException($"ZIP 条目缺少父目录：{entry.Path}");
                Directory.CreateDirectory(parent);
                EnsureDirectoryChainHasNoReparsePoint(targetMod.StagePath, parent);
                using var input = archive.Entries[entry.Index].Open();
                using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferBytes,
                    FileOptions.SequentialScan);
                var written = CopyBounded(
                    input,
                    output,
                    entry.Length,
                    ref extractedBytes,
                    buffer,
                    cancellationToken);
                if (written != entry.Length)
                {
                    throw new InvalidDataException(
                        $"ZIP 条目长度不一致：{entry.Path}（声明 {entry.Length}，实际 {written}）。");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private long CopyBounded(
        Stream input,
        Stream output,
        long expectedBytes,
        ref long extractedBytes,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        long written = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            try
            {
                written = checked(written + read);
                extractedBytes = checked(extractedBytes + read);
            }
            catch (OverflowException error)
            {
                throw new InvalidDataException("ZIP 解压后的大小无效。", error);
            }
            if (written > expectedBytes || written > _limits.MaximumEntryBytes)
            {
                throw new InvalidDataException("ZIP 条目实际解压大小超过声明值或安全上限。");
            }
            if (extractedBytes > _limits.MaximumExtractedBytes)
            {
                throw new InvalidDataException("ZIP 实际解压总量超过安全上限。");
            }
            output.Write(buffer, 0, read);
        }
        output.Flush();
        return written;
    }

    private static void ValidateStagedManifests(
        IEnumerable<PreparedMod> prepared,
        CancellationToken cancellationToken)
    {
        foreach (var item in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var staged = ReadManifestFromDirectory(item.StagePath);
            if (!string.Equals(staged.UniqueId, item.Source.Manifest.UniqueId, StringComparison.Ordinal)
                || !string.Equals(staged.Version, item.Source.Manifest.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"解压后的 manifest.json 与预检结果不一致：{item.Source.Manifest.UniqueId}");
            }
        }
    }

    private static IReadOnlyList<Exception> RollBack(
        IReadOnlyList<PreparedMod> prepared,
        string stagingRoot)
    {
        var errors = new List<Exception>();
        for (var index = prepared.Count - 1; index >= 0; index--)
        {
            var item = prepared[index];
            try
            {
                if (item.Committed && Directory.Exists(item.TargetPath))
                {
                    var discard = Path.Combine(stagingRoot, $"rollback-discard-{index:D4}-{Guid.NewGuid():N}");
                    Directory.Move(item.TargetPath, discard);
                    item.Committed = false;
                }
                if (item.BackupTaken
                    && item.BackupPath is not null
                    && Directory.Exists(item.BackupPath))
                {
                    if (GetPathState(item.TargetPath) != PathState.Missing)
                    {
                        throw new IOException($"无法恢复已被占用的目标路径：{item.TargetPath}");
                    }
                    Directory.Move(item.BackupPath, item.TargetPath);
                    item.BackupTaken = false;
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        return errors;
    }

    private static void CopyPreservedFiles(
        string backupDirectory,
        string stageDirectory,
        IReadOnlyList<string> preservePaths,
        ICollection<string> copied)
    {
        foreach (var relative in preservePaths)
        {
            var source = ResolveSafeRelativePath(backupDirectory, relative);
            var state = GetPathState(source);
            if (state == PathState.Missing)
            {
                continue;
            }
            EnsureDirectoryChainHasNoReparsePoint(backupDirectory, Path.GetDirectoryName(source)!);
            if (state != PathState.File)
            {
                throw new InvalidDataException($"要保留的配置必须是普通文件：{relative}");
            }
            var attributes = File.GetAttributes(source);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"拒绝保留符号链接或重解析点配置：{relative}");
            }
            var destination = ResolveSafeRelativePath(stageDirectory, relative);
            var parent = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException($"配置路径缺少父目录：{relative}");
            Directory.CreateDirectory(parent);
            EnsureDirectoryChainHasNoReparsePoint(stageDirectory, parent);
            if (Directory.Exists(destination))
            {
                throw new InvalidDataException($"新版本中的目录与要保留的配置冲突：{relative}");
            }
            File.Copy(source, destination, overwrite: true);
            copied.Add(relative);
        }
    }

    private static IReadOnlyList<string> NormalizePreservePaths(IReadOnlyList<string>? configured)
    {
        var results = new HashSet<string>(PathComparer)
        {
            "config.json",
            InstalledTranslationSidecarName,
        };
        if (configured is not null)
        {
            if (configured.Count > 128)
            {
                throw new ArgumentException("最多可配置 128 个需要保留的文件。", nameof(configured));
            }
            foreach (var path in configured)
            {
                results.Add(NormalizeUserRelativePath(path));
            }
        }
        return results.OrderBy(path => path, PathComparer).ToArray();
    }

    private static DownloadTranslation? ResolveTranslation(
        ModInstallPlan plan,
        ModInstallOptions options,
        string archiveSha256)
    {
        string? sidecarPath = null;
        var explicitlyBound = !string.IsNullOrWhiteSpace(options.TranslationSidecarPath);
        if (explicitlyBound)
        {
            sidecarPath = Path.GetFullPath(options.TranslationSidecarPath!.Trim());
        }
        else
        {
            foreach (var candidate in GetBoundSidecarCandidates(plan.ArchivePath))
            {
                if (File.Exists(candidate) && SidecarIsBoundToArchive(candidate, plan.ArchivePath, archiveSha256))
                {
                    sidecarPath = candidate;
                    break;
                }
            }
        }
        if (sidecarPath is null)
        {
            return null;
        }

        var sidecar = ReadDownloadTranslationSidecar(sidecarPath);
        string targetUniqueId;
        if (!string.IsNullOrWhiteSpace(options.TranslationTargetUniqueId))
        {
            targetUniqueId = options.TranslationTargetUniqueId.Trim();
            if (!plan.Mods.Any(mod => string.Equals(
                    mod.UniqueId,
                    targetUniqueId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("翻译 sidecar 指定的目标 Mod ID 不在该 ZIP 中。");
            }
        }
        else if (!string.IsNullOrWhiteSpace(sidecar.ModUniqueId))
        {
            targetUniqueId = sidecar.ModUniqueId.Trim();
            if (!plan.Mods.Any(mod => string.Equals(
                    mod.UniqueId,
                    targetUniqueId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("翻译 sidecar 内的 Mod ID 与该 ZIP 不匹配。");
            }
        }
        else if (plan.Mods.Count == 1)
        {
            targetUniqueId = plan.Mods[0].UniqueId;
        }
        else
        {
            throw new InvalidDataException("多 Mod 压缩包必须明确指定翻译要应用到的 Mod ID。");
        }

        if (!explicitlyBound && !SidecarIsBoundToArchive(sidecarPath, plan.ArchivePath, archiveSha256))
        {
            throw new InvalidDataException("自动发现的翻译 sidecar 没有与当前 ZIP 安全绑定。");
        }
        return new DownloadTranslation(targetUniqueId, sidecar.Name, sidecar.Description);
    }

    private static string[] GetBoundSidecarCandidates(string archivePath)
    {
        return new[]
        {
            archivePath + ".valley-steward.json",
            Path.ChangeExtension(archivePath, ".valley-steward.json"),
        }.Distinct(PathComparer).ToArray();
    }

    private static bool SidecarIsBoundToArchive(
        string sidecarPath,
        string archivePath,
        string archiveSha256)
    {
        try
        {
            using var document = ReadBoundedJson(sidecarPath, 256 * 1024);
            var root = document.RootElement;
            var hash = OptionalString(root, "archiveSha256");
            var fileName = OptionalString(root, "archiveFileName");
            return string.Equals(hash, archiveSha256, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(fileName)
                    || string.Equals(fileName, Path.GetFileName(archivePath), StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static DownloadSidecar ReadDownloadTranslationSidecar(string path)
    {
        using var document = ReadBoundedJson(path, 256 * 1024);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetProperty(root, "schemaVersion", out var schema)
            || !schema.TryGetInt32(out var schemaVersion)
            || schemaVersion != 1
            || OptionalString(root, "provider") is not ("Nexus Mods" or "GitHub"))
        {
            throw new InvalidDataException("下载翻译 sidecar 的格式或版本不受支持。");
        }
        var name = RequiredString(root, "name", 512);
        var description = RequiredString(root, "description", MaximumTextCharacters);
        var modUniqueId = OptionalString(root, "modUniqueId");
        if (modUniqueId is not null)
        {
            ValidateText(modUniqueId, "sidecar Mod ID", 512, allowEmpty: false);
        }
        return new DownloadSidecar(name, description, modUniqueId);
    }

    private static void WriteInstalledTranslation(
        string stageDirectory,
        Manifest manifest,
        DownloadTranslation translation)
    {
        var target = Path.Combine(stageDirectory, InstalledTranslationSidecarName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new InstalledTranslationSidecar(
                1,
                new InstalledTranslationSource(
                    manifest.UniqueId,
                    manifest.Version,
                    manifest.Name,
                    manifest.Description ?? string.Empty),
                new InstalledTranslationContent(translation.Name, translation.Description)),
            SidecarJsonOptions);
        try
        {
            using var output = new FileStream(
                target,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private Manifest ReadManifest(
        ZipArchiveEntry entry,
        string displayPath,
        CancellationToken cancellationToken)
    {
        using var input = entry.Open();
        using var memory = new MemoryStream((int)Math.Min(entry.Length, _limits.MaximumManifestBytes));
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                if (memory.Length + read > _limits.MaximumManifestBytes)
                {
                    throw new InvalidDataException($"{displayPath} 解压后超过安全上限。");
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
            using var document = JsonDocument.Parse(memory.ToArray());
            return ParseManifest(document.RootElement, displayPath);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{displayPath} 不是有效的 JSON：{error.Message}", error);
        }
    }

    private static Manifest ReadManifestFromDirectory(string directory)
    {
        var path = Path.Combine(directory, ManifestFileName);
        var bytes = ReadOrdinaryFileBounded(
            path,
            256 * 1024,
            $"Mod manifest.json 不是安全的普通文件：{directory}");
        using var document = JsonDocument.Parse(bytes);
        return ParseManifest(document.RootElement, path);
    }

    private static Manifest ParseManifest(JsonElement root, string displayPath)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{displayPath} 的根节点必须是对象。");
        }
        var name = RequiredString(root, "Name", 512);
        var uniqueId = OptionalString(root, "UniqueID") ?? OptionalString(root, "UniqueId")
            ?? throw new InvalidDataException($"{displayPath} 缺少 UniqueID。");
        ValidateText(uniqueId, "Mod UniqueID", 512, allowEmpty: false);
        var version = RequiredString(root, "Version", 128);
        var author = RequiredString(root, "Author", 512);
        var description = OptionalString(root, "Description");
        if (description is not null)
        {
            ValidateText(description, "Mod Description", MaximumTextCharacters, allowEmpty: true);
        }

        var dependencies = new List<ModInstallDependency>();
        if (TryGetProperty(root, "Dependencies", out var dependencyArray))
        {
            if (dependencyArray.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"{displayPath} 的 Dependencies 必须是数组。");
            }
            foreach (var dependency in dependencyArray.EnumerateArray())
            {
                if (dependencies.Count >= MaximumDependencies)
                {
                    throw new InvalidDataException($"{displayPath} 的依赖数量超过安全上限。");
                }
                if (dependency.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"{displayPath} 包含无效依赖项。");
                }
                var dependencyId = OptionalString(dependency, "UniqueID")
                    ?? OptionalString(dependency, "UniqueId")
                    ?? throw new InvalidDataException($"{displayPath} 的依赖项缺少 UniqueID。");
                ValidateText(dependencyId, "依赖 Mod ID", 512, allowEmpty: false);
                var isRequired = true;
                if (TryGetProperty(dependency, "IsRequired", out var required))
                {
                    if (required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new InvalidDataException(
                            $"{displayPath} 的依赖 IsRequired 必须是布尔值。");
                    }
                    isRequired = required.GetBoolean();
                }
                var minimumVersion = OptionalString(dependency, "MinimumVersion");
                if (minimumVersion is not null)
                {
                    ValidateText(minimumVersion, "依赖最低版本", 128, allowEmpty: true);
                }
                dependencies.Add(new ModInstallDependency(dependencyId, isRequired, minimumVersion));
            }
        }
        return new Manifest(name, uniqueId, version, author, description, dependencies.ToArray());
    }

    private static JsonDocument ReadBoundedJson(string path, int maximumBytes)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            var bytes = ReadOrdinaryFileBounded(
                fullPath,
                maximumBytes,
                "翻译 sidecar 不是安全的普通文件或超过大小上限。");
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("翻译 sidecar 不是有效 JSON。", error);
        }
    }

    private static byte[] ReadOrdinaryFileBounded(string path, int maximumBytes, string errorMessage)
    {
        if (GetPathState(path) != PathState.File)
        {
            throw new InvalidDataException(errorMessage);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(errorMessage);
        }
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || stream.Length < 0
            || stream.Length > maximumBytes)
        {
            throw new InvalidDataException(errorMessage);
        }
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static ExistingMod[] ScanExistingMods(string modsRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modsRoot))
        {
            return Array.Empty<ExistingMod>();
        }
        EnsureOrdinaryDirectory(modsRoot, "Mods 目录");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 16,
        };
        var results = new List<ExistingMod>();
        foreach (var manifestPath in Directory.EnumerateFiles(modsRoot, ManifestFileName, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInsideManagerTrash(modsRoot, manifestPath))
            {
                continue;
            }
            try
            {
                var directory = Path.GetDirectoryName(manifestPath)!;
                var manifest = ReadManifestFromDirectory(directory);
                results.Add(new ExistingMod(directory, manifest));
            }
            catch (InvalidDataException)
            {
                // Invalid installed manifests cannot be matched by ID; a direct target collision is still blocked.
            }
            catch (JsonException)
            {
                // Same as above.
            }
        }
        return results.ToArray();
    }

    private static void AddTargetConflicts(
        IReadOnlyList<ModArchiveMod> mods,
        ICollection<ModInstallConflict> conflicts)
    {
        var withTargets = mods.Where(mod => mod.TargetPath is not null).ToArray();
        foreach (var group in withTargets.GroupBy(mod => mod.TargetPath!, PathComparer).Where(group => group.Count() > 1))
        {
            foreach (var mod in group)
            {
                conflicts.Add(new ModInstallConflict(
                    ModInstallConflictKind.PackageTargetCollision,
                    mod.UniqueId,
                    mod.TargetPath,
                    true,
                    $"压缩包中的多个 Mod 会写入同一目标：{mod.TargetPath}"));
            }
        }

        for (var left = 0; left < withTargets.Length; left++)
        {
            for (var right = left + 1; right < withTargets.Length; right++)
            {
                var leftPath = withTargets[left].TargetPath!;
                var rightPath = withTargets[right].TargetPath!;
                if (PathComparer.Equals(leftPath, rightPath)
                    || (!IsPathInside(leftPath, rightPath) && !IsPathInside(rightPath, leftPath)))
                {
                    continue;
                }
                conflicts.Add(new ModInstallConflict(
                    ModInstallConflictKind.OverlappingTarget,
                    withTargets[right].UniqueId,
                    rightPath,
                    true,
                    $"Mod 安装目标互相嵌套，无法安全执行事务：{leftPath} / {rightPath}"));
            }
        }
    }

    private static void ValidateDisjointModRoots(IReadOnlyList<CatalogMod> mods)
    {
        for (var left = 0; left < mods.Count; left++)
        {
            for (var right = left + 1; right < mods.Count; right++)
            {
                if (IsPathInsideArchiveRoot(mods[left].Root, mods[right].Root)
                    || IsPathInsideArchiveRoot(mods[right].Root, mods[left].Root))
                {
                    throw new InvalidDataException(
                        $"ZIP 包含互相嵌套的 manifest.json，无法无歧义地拆分 Mod："
                        + $"{DisplayRoot(mods[left].Root)} / {DisplayRoot(mods[right].Root)}");
                }
            }
        }
    }

    private static void ValidateNoFileDirectoryCollisions(IReadOnlyList<CatalogEntry> entries)
    {
        foreach (var file in entries.Where(entry => !entry.IsDirectory))
        {
            var prefix = file.Path + "/";
            if (entries.Any(other => other.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"ZIP 同时把路径作为文件和目录使用：{file.Path}");
            }
        }
    }

    private string NormalizeArchivePath(string rawPath, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("ZIP 包含空白或 NUL 路径。");
        }
        if (rawPath.Length > _limits.MaximumPathCharacters)
        {
            throw new InvalidDataException("ZIP 条目路径过长。");
        }
        var replaced = rawPath.Replace('\\', '/');
        if (replaced.StartsWith('/')
            || replaced.StartsWith("//", StringComparison.Ordinal)
            || Path.IsPathRooted(rawPath)
            || (replaced.Length >= 2 && char.IsLetter(replaced[0]) && replaced[1] == ':'))
        {
            throw new InvalidDataException($"ZIP 包含绝对路径：{rawPath}");
        }
        if (isDirectory)
        {
            replaced = replaced.TrimEnd('/');
        }
        if (replaced.Length == 0)
        {
            throw new InvalidDataException("ZIP 包含无效根目录条目。");
        }

        var segments = replaced.Split('/', StringSplitOptions.None);
        if (segments.Length > _limits.MaximumPathDepth)
        {
            throw new InvalidDataException(
                $"ZIP 条目层级超过 {_limits.MaximumPathDepth} 层：{rawPath}");
        }
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = ValidatePathSegment(segments[index], rawPath);
        }
        var normalized = string.Join('/', segments);
        if (normalized.Length > _limits.MaximumPathCharacters)
        {
            throw new InvalidDataException("ZIP 条目规范化后的路径过长。");
        }
        return normalized;
    }

    private static string NormalizeUserRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("要保留的配置路径不能为空。", nameof(path));
        }
        var replaced = path.Trim().Replace('\\', '/');
        if (replaced.StartsWith('/')
            || Path.IsPathRooted(path)
            || (replaced.Length >= 2 && char.IsLetter(replaced[0]) && replaced[1] == ':'))
        {
            throw new ArgumentException("要保留的配置路径必须是相对路径。", nameof(path));
        }
        var segments = replaced.Split('/', StringSplitOptions.None);
        if (segments.Length > 16)
        {
            throw new ArgumentException("要保留的配置路径层级过深。", nameof(path));
        }
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = ValidatePathSegment(segments[index], path);
        }
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static string ValidatePathSegment(string segment, string displayPath)
    {
        segment = segment.Normalize(NormalizationForm.FormC);
        if (segment.Length == 0 || segment is "." or "..")
        {
            throw new InvalidDataException($"ZIP 路径包含空白、. 或 .. 段：{displayPath}");
        }
        if (segment.Length > 240
            || segment.EndsWith('.')
            || char.IsWhiteSpace(segment[^1])
            || segment.Any(character => character < 0x20 || character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
        {
            throw new InvalidDataException($"ZIP 路径包含 Windows 不安全名称：{displayPath}");
        }
        var deviceStem = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        if (ReservedWindowsNames.Contains(deviceStem))
        {
            throw new InvalidDataException($"ZIP 路径使用 Windows 设备名：{displayPath}");
        }
        return segment;
    }

    private static void ValidateArchiveEntryType(ZipArchiveEntry entry, bool isDirectory)
    {
        var attributes = entry.ExternalAttributes;
        if ((attributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"ZIP 包含重解析点：{entry.FullName}");
        }
        if ((attributes & (int)FileAttributes.Device) != 0)
        {
            throw new InvalidDataException($"ZIP 包含 Windows 设备条目：{entry.FullName}");
        }
        var unixType = (attributes >> 16) & UnixFileTypeMask;
        if (unixType == UnixSymbolicLinkType)
        {
            throw new InvalidDataException($"ZIP 包含符号链接：{entry.FullName}");
        }
        if (unixType != 0
            && unixType != UnixRegularFileType
            && unixType != UnixDirectoryType)
        {
            throw new InvalidDataException($"ZIP 包含不支持的特殊文件：{entry.FullName}");
        }
        if ((unixType == UnixDirectoryType) != isDirectory && unixType != 0)
        {
            throw new InvalidDataException($"ZIP 条目类型与路径不一致：{entry.FullName}");
        }
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return entry.FullName.EndsWith('/')
            || entry.FullName.EndsWith('\\')
            || entry.Name.Length == 0
            || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0
            || unixType == UnixDirectoryType;
    }

    private static string ComputeSha256(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private string ValidateArchiveFile(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("请选择 Mod ZIP 文件。", nameof(archivePath));
        }
        var fullPath = Path.GetFullPath(archivePath.Trim());
        if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("只支持安装 .zip 格式的 Mod 压缩包。");
        }
        var state = GetPathState(fullPath);
        if (state != PathState.File)
        {
            throw new FileNotFoundException("Mod ZIP 不存在或不是普通文件。", fullPath);
        }
        var info = new FileInfo(fullPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("拒绝从符号链接或重解析点读取 Mod ZIP。");
        }
        if (info.Length <= 0 || info.Length > _limits.MaximumArchiveBytes)
        {
            throw new InvalidDataException(
                $"Mod ZIP 为空或超过 {_limits.MaximumArchiveBytes / 1024 / 1024} MB 的大小上限。");
        }
        return fullPath;
    }

    private static string ValidateGameDirectory(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            throw new ArgumentException("游戏路径不能为空。", nameof(gamePath));
        }
        var fullPath = GameDiscoveryService.NormalizeWindowsPath(Path.GetFullPath(gamePath.Trim()));
        EnsureOrdinaryDirectory(fullPath, "游戏目录");
        return fullPath;
    }

    private static string EnsurePrivateDirectory(string gamePath, string name)
    {
        var path = Path.GetFullPath(Path.Combine(gamePath, name));
        EnsurePathInside(gamePath, path);
        Directory.CreateDirectory(path);
        EnsureOrdinaryDirectory(path, name);
        return path;
    }

    private static void EnsureOrdinaryDirectory(string path, string label)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{label}不存在：{path}");
        }
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{label}不能是符号链接或重解析点：{path}");
        }
    }

    private static void EnsureDirectoryChainHasNoReparsePoint(string root, string directory)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullDirectory = Path.GetFullPath(directory);
        if (!string.Equals(fullRoot, fullDirectory, StringComparison.OrdinalIgnoreCase))
        {
            EnsurePathInside(fullRoot, fullDirectory);
        }
        EnsureOrdinaryDirectory(fullRoot, "受控目录根");
        var relative = Path.GetRelativePath(fullRoot, fullDirectory);
        if (relative == ".")
        {
            return;
        }
        var current = fullRoot;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureOrdinaryDirectory(current, "受控目录");
        }
    }

    private static string ResolveSafeRelativePath(string root, string relative)
    {
        var platformRelative = relative.Replace('/', Path.DirectorySeparatorChar);
        var result = Path.GetFullPath(Path.Combine(root, platformRelative));
        EnsurePathInside(root, result);
        return result;
    }

    private static void EnsurePathInside(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"路径越过允许目录：{candidate}");
        }
    }

    private static bool IsPathInside(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathInsideArchiveRoot(string path, string root)
    {
        if (root.Length == 0)
        {
            return true;
        }
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRelativeArchivePath(string path, string root)
    {
        if (root.Length == 0)
        {
            return path;
        }
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        return path[(root.Length + 1)..];
    }

    private static string GetParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string GetLeafName(string path)
    {
        if (path.Length == 0)
        {
            return string.Empty;
        }
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static string MakeSafeTargetFolderName(string candidate, string uniqueId)
    {
        candidate = string.IsNullOrWhiteSpace(candidate) ? uniqueId : candidate;
        var builder = new StringBuilder(Math.Min(candidate.Length, 120));
        foreach (var character in candidate.Trim().Trim('.'))
        {
            if (builder.Length >= 120)
            {
                break;
            }
            builder.Append(character < 0x20 || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '_'
                : character);
        }
        var result = builder.ToString().Trim().TrimEnd('.');
        if (result.Length == 0)
        {
            result = $"Mod-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uniqueId)))[..10]}";
        }
        if (ReservedWindowsNames.Contains(result.Split('.', 2)[0].TrimEnd(' ', '.')))
        {
            result = "_" + result;
        }
        return result;
    }

    private static bool IsManifestPath(string path)
    {
        return string.Equals(GetLeafName(path), ManifestFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayRoot(string root) => root.Length == 0 ? "<ZIP 根目录>" : root;

    private static bool IsInsideManagerTrash(string modsRoot, string path)
    {
        var trash = Path.GetFullPath(Path.Combine(modsRoot, ".mod-manager-trash"));
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(trash + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static PathState GetPathState(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 ? PathState.Directory : PathState.File;
        }
        catch (FileNotFoundException)
        {
            return PathState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return PathState.Missing;
        }
    }

    private static void TryDeleteOwnedDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup; rollback errors are reported separately.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // A concurrent installer or cleanup task may still be using the parent.
        }
    }

    private static string RequiredString(JsonElement root, string name, int maximumCharacters)
    {
        var value = OptionalString(root, name)
            ?? throw new InvalidDataException($"manifest.json 缺少 {name}。");
        ValidateText(value, name, maximumCharacters, allowEmpty: false);
        return value;
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"manifest.json 的 {name} 必须是字符串。");
        }
        return value.GetString();
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

    private static void ValidateText(
        string value,
        string label,
        int maximumCharacters,
        bool allowEmpty)
    {
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{label} 不能为空。");
        }
        if (value.EnumerateRunes().Count() > maximumCharacters)
        {
            throw new InvalidDataException($"{label} 不能超过 {maximumCharacters} 个字符。");
        }
        if (value.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t'))
        {
            throw new InvalidDataException($"{label} 包含无效控制字符。");
        }
    }

    private static void ValidateLimits(ModArchiveLimits limits)
    {
        if (limits.MaximumArchiveBytes <= 0
            || limits.MaximumEntries <= 0
            || limits.MaximumEntryBytes <= 0
            || limits.MaximumExtractedBytes <= 0
            || limits.MaximumPathDepth <= 0
            || limits.MaximumPathCharacters < 32
            || limits.MaximumMods <= 0
            || limits.MaximumManifestBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Mod ZIP 安全限制必须为正数且可用。");
        }
    }

    private sealed record Catalog(
        string ArchivePath,
        string Sha256,
        long ArchiveBytes,
        long UncompressedBytes,
        IReadOnlyList<CatalogEntry> Entries,
        IReadOnlyList<CatalogMod> Mods,
        IReadOnlyList<string> XnbFiles);

    private sealed record CatalogEntry(int Index, string Path, bool IsDirectory, long Length);

    private sealed record CatalogMod(string Root, Manifest Manifest);

    private sealed record Manifest(
        string Name,
        string UniqueId,
        string Version,
        string Author,
        string? Description,
        IReadOnlyList<ModInstallDependency> Dependencies);

    private sealed record ExistingMod(string Directory, Manifest Manifest);

    private sealed class PreparedMod
    {
        public PreparedMod(CatalogMod source, ModArchiveMod plan, string stagePath, string targetPath)
        {
            Source = source;
            Plan = plan;
            StagePath = stagePath;
            TargetPath = targetPath;
        }

        public CatalogMod Source { get; }
        public ModArchiveMod Plan { get; }
        public string StagePath { get; }
        public string TargetPath { get; }
        public string? BackupPath { get; set; }
        public bool BackupTaken { get; set; }
        public bool Committed { get; set; }
        public bool TranslationApplied { get; set; }
        public List<string> PreservedFiles { get; } = new();
    }

    private sealed record DownloadSidecar(string Name, string Description, string? ModUniqueId);

    private sealed record DownloadTranslation(string TargetUniqueId, string Name, string Description);

    private sealed record InstalledTranslationSidecar(
        int SchemaVersion,
        InstalledTranslationSource Source,
        InstalledTranslationContent Translation);

    private sealed record InstalledTranslationSource(
        string UniqueId,
        string Version,
        string Name,
        string Description);

    private sealed record InstalledTranslationContent(string Name, string Description);

    private enum PathState
    {
        Missing,
        File,
        Directory,
    }
}
