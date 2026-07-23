using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

internal enum SmapiInstallerAction
{
    Install,
    Uninstall,
}

internal sealed record SmapiInstallerExecution(int ExitCode, string InstallerPath);

internal sealed class SmapiInstallerService
{
    private const long MaxInstallerSize = 256L * 1024 * 1024;
    private const int MaxArchiveEntries = 2_048;
    private const long MaxExtractedFileSize = 64L * 1024 * 1024;
    private const long MaxExtractedTotalSize = 512L * 1024 * 1024;
    private const int MaxArchivePathBytes = 1_024;
    private const int MaxArchiveComponents = 32;
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] GameExecutableNames =
    [
        "Stardew Valley.exe",
        "StardewValley.exe",
    ];
    private static readonly string[] ProcessNames =
    [
        "Stardew Valley",
        "StardewValley",
        "StardewModdingAPI",
    ];

    public async Task<SmapiInstallerExecution> ExecuteAsync(
        DownloadedSmapiInstaller downloaded,
        string gamePath,
        SmapiInstallerAction action,
        IProgress<SmapiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var canonicalGamePath = ValidateGamePath(gamePath);
        EnsureGameProcessesStopped(canonicalGamePath);
        var archivePath = ValidateDownloadedArchivePath(downloaded);
        await VerifyArchiveAsync(archivePath, downloaded, cancellationToken);

        var extractionParent = Path.Combine(
            CacheDirectory,
            "installers",
            "smapi",
            SafePathSegment(downloaded.Version));
        Directory.CreateDirectory(extractionParent);
        var extractionDirectory = Path.Combine(
            extractionParent,
            $".{ActionArgument(action).TrimStart('-')}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        if (Directory.Exists(extractionDirectory))
        {
            throw new IOException("无法创建私有 SMAPI 解压目录。");
        }
        Directory.CreateDirectory(extractionDirectory);

        try
        {
            progress?.Report(new SmapiOperationProgress(
                SmapiOperationStage.Extracting,
                "正在安全解压 SMAPI 官方安装器…"));
            var extracted = await Task.Run(
                () => ExtractArchive(archivePath, extractionDirectory, downloaded.Version),
                cancellationToken);

            EnsureGameProcessesStopped(canonicalGamePath);
            progress?.Report(new SmapiOperationProgress(
                action == SmapiInstallerAction.Install
                    ? SmapiOperationStage.Installing
                    : SmapiOperationStage.Uninstalling,
                action == SmapiInstallerAction.Install
                    ? "正在静默运行 SMAPI 官方安装器…"
                    : "正在静默运行 SMAPI 官方卸载器…"));

            var exitCode = await RunOfficialInstallerAsync(
                extracted.Executable,
                extracted.Root,
                canonicalGamePath,
                action,
                cancellationToken);
            return new SmapiInstallerExecution(exitCode, archivePath);
        }
        finally
        {
            TryDeleteExtractionDirectory(extractionDirectory);
        }
    }

    public static string ValidateGamePath(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            throw new DirectoryNotFoundException("请先设置星露谷物语游戏目录。");
        }
        var inputPath = GameDiscoveryService.NormalizeWindowsPath(gamePath.Trim().Trim('"'));
        var fullPath = GameDiscoveryService.NormalizeWindowsPath(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputPath)));
        if (!Path.IsPathFullyQualified(fullPath) || !Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("SMAPI 游戏路径必须是现有的绝对目录。");
        }
        if (!GameExecutableNames.Any(name => File.Exists(Path.Combine(fullPath, name))))
        {
            throw new FileNotFoundException("所选目录中没有找到 Stardew Valley 可执行文件。", fullPath);
        }
        return fullPath;
    }

    public static void EnsureGameProcessesStopped(string gamePath)
    {
        var canonicalGamePath = ValidateGamePath(gamePath);
        var candidates = GameExecutableNames
            .Append("StardewModdingAPI.exe")
            .Select(name => Path.Combine(canonicalGamePath, name))
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .ToArray();

        foreach (var processName in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    string? executablePath;
                    try
                    {
                        if (process.HasExited)
                        {
                            continue;
                        }
                        executablePath = process.MainModule?.FileName;
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception
                        or NotSupportedException)
                    {
                        throw new InvalidOperationException(
                            $"检测到同名游戏进程（PID {process.Id}），但无法确认其路径。为保护游戏文件，请先关闭该进程。",
                            error);
                    }

                    if (string.IsNullOrWhiteSpace(executablePath))
                    {
                        throw new InvalidOperationException(
                            $"检测到同名游戏进程（PID {process.Id}），但无法确认其路径。请先关闭游戏。");
                    }
                    var fullExecutablePath = GameDiscoveryService.NormalizeWindowsPath(
                        Path.GetFullPath(executablePath));
                    if (candidates.Any(candidate => candidate.Equals(
                        fullExecutablePath,
                        StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException("检测到所选目录中的游戏正在运行，请先关闭游戏再安装或卸载 SMAPI。");
                    }
                }
            }
        }
    }

    private static async Task VerifyArchiveAsync(
        string archivePath,
        DownloadedSmapiInstaller downloaded,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(archivePath);
        if (downloaded.Size is <= 0 or > MaxInstallerSize || info.Length != downloaded.Size)
        {
            throw new InvalidDataException(
                $"缓存的 SMAPI 安装包大小发生变化（预期 {downloaded.Size} 字节，实际 {info.Length} 字节）。");
        }
        if (downloaded.Sha256.Length != 64 || !downloaded.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("缓存的 SMAPI 安装包 SHA-256 格式无效。");
        }

        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (count == 0)
                {
                    break;
                }
                hasher.AppendData(buffer, 0, count);
            }
            var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!actual.Equals(downloaded.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"缓存的 SMAPI 安装包 SHA-256 发生变化（预期 {downloaded.Sha256}，实际 {actual}）。");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static ExtractedInstaller ExtractArchive(
        string archivePath,
        string destination,
        string version)
    {
        using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count == 0)
        {
            throw new InvalidDataException("SMAPI 安装包 ZIP 为空。");
        }
        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException(
                $"SMAPI 安装包条目过多（{archive.Entries.Count} > {MaxArchiveEntries}）。");
        }

        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var expectedRootName = $"SMAPI {version} installer";
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;

        foreach (var entry in archive.Entries)
        {
            var relativePath = ValidateArchiveEntry(entry, expectedRootName);
            if (!extractedPaths.Add(relativePath))
            {
                throw new InvalidDataException($"SMAPI 安装包包含重复路径：{relativePath}");
            }

            var outputPath = Path.GetFullPath(Path.Combine(
                destinationRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!outputPath.StartsWith(
                destinationRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SMAPI 安装包路径越过了解压目录：{relativePath}");
            }

            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            if (isDirectory)
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }
            if (entry.Length is < 0 or > MaxExtractedFileSize)
            {
                throw new InvalidDataException(
                    $"SMAPI 安装包中的文件超过 {MaxExtractedFileSize} 字节上限：{relativePath}");
            }
            totalSize = checked(totalSize + entry.Length);
            if (totalSize > MaxExtractedTotalSize)
            {
                throw new InvalidDataException(
                    $"SMAPI 安装包解压后超过 {MaxExtractedTotalSize} 字节上限。");
            }

            var parent = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidDataException("SMAPI 安装包文件没有有效的父目录。");
            Directory.CreateDirectory(parent);
            using var input = entry.Open();
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var copied = CopyWithLimit(input, output, MaxExtractedFileSize);
            output.Flush(flushToDisk: true);
            if (copied != entry.Length)
            {
                throw new InvalidDataException($"SMAPI 安装包文件长度发生变化：{relativePath}");
            }
        }

        var root = Path.Combine(destinationRoot, expectedRootName);
        var windowsDirectory = Path.Combine(root, "internal", "windows");
        var executable = Path.Combine(windowsDirectory, "SMAPI.Installer.exe");
        var requiredFiles = new[]
        {
            executable,
            Path.Combine(windowsDirectory, "SMAPI.Installer.dll"),
            Path.Combine(windowsDirectory, "install.dat"),
        };
        foreach (var required in requiredFiles)
        {
            if (!File.Exists(required)
                || File.GetAttributes(required).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    $"SMAPI 安装包缺少必要文件：{Path.GetRelativePath(destinationRoot, required)}");
            }
        }
        return new ExtractedInstaller(root, executable);
    }

    private static string ValidateArchiveEntry(ZipArchiveEntry entry, string expectedRootName)
    {
        var name = entry.FullName;
        if (string.IsNullOrEmpty(name)
            || Encoding.UTF8.GetByteCount(name) > MaxArchivePathBytes
            || name.Contains('\\')
            || name.Contains('\0')
            || Path.IsPathFullyQualified(name))
        {
            throw new InvalidDataException("SMAPI 安装包包含不安全路径。");
        }

        var isDirectory = name.EndsWith("/", StringComparison.Ordinal);
        ValidateArchiveEntryType(entry, isDirectory);
        if (isDirectory && entry.Length != 0)
        {
            throw new InvalidDataException("SMAPI 安装包目录条目包含了意外数据。");
        }
        var trimmed = isDirectory ? name.TrimEnd('/') : name;
        var components = trimmed.Split('/', StringSplitOptions.None);
        if (components.Length is <= 0 or > MaxArchiveComponents
            || !components[0].Equals(expectedRootName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"SMAPI 安装包路径不属于预期安装器目录：{name}");
        }

        foreach (var component in components)
        {
            if (string.IsNullOrEmpty(component)
                || component is "." or ".."
                || component.EndsWith(' ')
                || component.EndsWith('.')
                || component.Contains(':')
                || component.Any(char.IsControl)
                || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsWindowsDeviceName(component))
            {
                throw new InvalidDataException($"SMAPI 安装包包含不安全路径组件：{name}");
            }
        }
        if (components.Length == 1 && !isDirectory)
        {
            throw new InvalidDataException("SMAPI 安装包根条目必须是目录。");
        }
        return string.Join('/', components) + (isDirectory ? "/" : string.Empty);
    }

    private static bool IsWindowsDeviceName(string component)
    {
        var baseName = component.Split('.', 2)[0];
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (baseName.Length == 4
            && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            return baseName[3] is >= '1' and <= '9' or '¹' or '²' or '³';
        }
        return false;
    }

    private static void ValidateArchiveEntryType(ZipArchiveEntry entry, bool isDirectory)
    {
        var windowsAttributes = entry.ExternalAttributes & 0xFFFF;
        if ((windowsAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("SMAPI 安装包包含重解析点或符号链接。");
        }

        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        var fileType = unixMode & 0xF000;
        var expectedType = isDirectory ? 0x4000 : 0x8000;
        if (fileType != 0 && fileType != expectedType)
        {
            throw new InvalidDataException("SMAPI 安装包包含不支持的特殊文件类型。");
        }
    }

    private static long CopyWithLimit(Stream input, Stream output, long limit)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var count = input.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    return total;
                }
                total = checked(total + count);
                if (total > limit)
                {
                    throw new InvalidDataException("SMAPI 安装包文件在解压时超过安全上限。");
                }
                output.Write(buffer, 0, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<int> RunOfficialInstallerAsync(
        string executable,
        string installerRoot,
        string gamePath,
        SmapiInstallerAction action,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateOfficialInstallerStartInfo(
            executable,
            installerRoot,
            gamePath,
            action);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("SMAPI 官方安装器没有成功启动。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallerTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("SMAPI 官方安装器运行超时，已终止安装器进程。");
            }
            throw;
        }

        return process.ExitCode;
    }

    internal static ProcessStartInfo CreateOfficialInstallerStartInfo(
        string executable,
        string installerRoot,
        string gamePath,
        SmapiInstallerAction action)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = installerRoot,
            // SMAPI 4.5.2 calls Console.Clear even in --no-prompt mode. A hidden
            // shell console keeps that screen buffer available without flashing a window.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(ActionArgument(action));
        startInfo.ArgumentList.Add("--game-path");
        startInfo.ArgumentList.Add(gamePath);
        startInfo.ArgumentList.Add("--no-prompt");
        return startInfo;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch
        {
            // The installer may have exited between the timeout and the kill call.
        }
    }

    private static string ValidateDownloadedArchivePath(DownloadedSmapiInstaller downloaded)
    {
        if (downloaded.FileName != Path.GetFileName(downloaded.FileName)
            || !downloaded.FileName.EndsWith("-installer.zip", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SMAPI 安装包文件名无效。");
        }
        var cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(CacheDirectory));
        var archivePath = Path.GetFullPath(downloaded.Path);
        if (!archivePath.StartsWith(
            cacheRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(archivePath).Equals(
                downloaded.FileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("下载的 SMAPI 安装包位于应用缓存目录之外。");
        }
        if (!File.Exists(archivePath)
            || File.GetAttributes(archivePath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new FileNotFoundException("下载的 SMAPI 安装包不存在或是重解析点。", archivePath);
        }
        return archivePath;
    }

    private static string SafePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 64
            || value.StartsWith('.')
            || value.Contains("..", StringComparison.Ordinal)
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '-' or '_' or '+')))
        {
            throw new InvalidDataException("SMAPI 版本号不能安全地用于缓存路径。");
        }
        return value;
    }

    private static string ActionArgument(SmapiInstallerAction action)
    {
        return action == SmapiInstallerAction.Install ? "--install" : "--uninstall";
    }

    private static void TryDeleteExtractionDirectory(string path)
    {
        try
        {
            var extractionRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Path.Combine(CacheDirectory, "installers", "smapi")));
            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(
                extractionRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
            // A failed best-effort cleanup must not hide installer verification results.
        }
    }

    private static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "com.summerxdsss.valleysteward");

    internal sealed record ExtractedInstaller(string Root, string Executable);
}
