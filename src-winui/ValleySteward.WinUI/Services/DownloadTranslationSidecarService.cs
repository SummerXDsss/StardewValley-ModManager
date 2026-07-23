using System.Text.Json;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public static class DownloadTranslationSidecarService
{
    private const int MaximumNameCharacters = 512;
    private const int MaximumDescriptionCharacters = 16_000;
    private static readonly object WriteLock = new();

    public static string WriteForArchive(
        string archivePath,
        string provider,
        string sourceId,
        NexusDownloadTranslation translation,
        string archiveSha256)
    {
        ArgumentNullException.ThrowIfNull(translation);
        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath)
            || (File.GetAttributes(fullArchivePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("下载完成的 ZIP 不存在或不是普通文件。", fullArchivePath);
        }
        if (provider is not ("GitHub" or "Nexus Mods"))
        {
            throw new ArgumentException("下载翻译 sidecar 的来源不受支持。", nameof(provider));
        }
        sourceId = NormalizeText(sourceId, "下载来源 ID", 512);
        var name = NormalizeText(translation.Name, "下载译名", MaximumNameCharacters);
        var description = NormalizeText(
            translation.Description,
            "下载译文",
            MaximumDescriptionCharacters,
            allowLineBreaks: true);
        archiveSha256 = archiveSha256.Trim().ToLowerInvariant();
        if (archiveSha256.Length != 64 || archiveSha256.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("下载 ZIP 的 SHA-256 无效。", nameof(archiveSha256));
        }

        var target = fullArchivePath + ".valley-steward.json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                provider,
                sourceId,
                name,
                description,
                archiveFileName = Path.GetFileName(fullArchivePath),
                archiveSha256,
            },
            new JsonSerializerOptions { WriteIndented = true });
        var temporary = target + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        lock (WriteLock)
        {
            try
            {
                if (Directory.Exists(target)
                    || File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("拒绝覆盖不是普通文件的下载翻译 sidecar。");
                }
                using (var stream = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                Array.Clear(bytes);
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    // Keep the original write error when cleanup also fails.
                }
            }
        }
        return target;
    }

    private static string NormalizeText(
        string value,
        string label,
        int maximumCharacters,
        bool allowLineBreaks = false)
    {
        value = value.Trim();
        if (value.Length == 0 || value.EnumerateRunes().Count() > maximumCharacters)
        {
            throw new ArgumentException($"{label}为空或过长。", nameof(value));
        }
        if (value.Any(character =>
                char.IsControl(character)
                && (!allowLineBreaks || character is not ('\r' or '\n' or '\t'))))
        {
            throw new ArgumentException($"{label}包含无效控制字符。", nameof(value));
        }
        return value;
    }
}
