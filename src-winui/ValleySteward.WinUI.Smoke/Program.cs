using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ValleySteward.WinUI.Models;
using ValleySteward.WinUI.Services;
using ActivityKind = ValleySteward.WinUI.Models.ActivityKind;

var root = Path.Combine(
    Path.GetTempPath(),
    $"valley-steward-winui-smoke-{Environment.ProcessId}-{Guid.NewGuid():N}");

try
{
    Directory.CreateDirectory(root);
    SmokeSteamAvatarCache(root);
    SmokeRemoteModImageUrls();
    SmokeModShareLinks();
    await SmokeUiSettingsAsync(root);
    await SmokeActivityHistoryAsync(root);
    await SmokeModUpdateBatchQueueAsync();
    SmokeCredentialManager();
    SmokeAiRequestMetadata();
    await SmokeAiProviderPipelineAsync(root);
    SmokeNexusSafety(root);
    await SmokeGitHubDownloadSettingsAsync(root);
    await SmokeModInstallerBasicAsync(Path.Combine(root, "mod-installer-basic"));
#if ARCHIVE_SECURITY_SMOKE
    SmokeSmapiArchiveSafety(root);
    SmokeSmapiInstallerLaunchInfo();
#endif
#if MOD_INSTALLER_SECURITY_SMOKE
    await SmokeModInstallerAsync(Path.Combine(root, "mod-installer"));
#endif
    await File.WriteAllBytesAsync(Path.Combine(root, "Stardew Valley.exe"), []);
    await File.WriteAllBytesAsync(Path.Combine(root, "StardewModdingAPI.exe"), []);

    var extendedRoot = @"\\?\" + root;
    Assert(
        GameDiscoveryService.NormalizeWindowsPath(extendedRoot) == root,
        "Drive-letter extended path prefix was not removed.");
    Assert(
        GameDiscoveryService.NormalizeWindowsPath(@"\\?\UNC\server\share\Stardew Valley")
            == @"\\server\share\Stardew Valley",
        "Extended UNC path prefix was not converted to a normal UNC path.");
    Assert(
        GameDiscoveryService.NormalizeWindowsPath(@"\\server\share\Stardew Valley")
            == @"\\server\share\Stardew Valley",
        "Normal UNC path was changed.");
    Assert(
        GameDiscoveryService.NormalizeWindowsPath(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\")
            == @"\\?\Volume{00000000-0000-0000-0000-000000000000}\",
        "Non-drive device path was changed.");

    var migratedConfigPath = Path.Combine(root, "smoke-config", "game-path.json");
    Directory.CreateDirectory(Path.GetDirectoryName(migratedConfigPath)!);
    await File.WriteAllTextAsync(
        migratedConfigPath,
        JsonSerializer.Serialize(new { version = 1, path = extendedRoot }));
    var migratingDiscovery = new GameDiscoveryService(migratedConfigPath);
    var migratedInstallation = await migratingDiscovery.LoadSavedAsync()
        ?? throw new InvalidOperationException("Extended saved game path was not loaded.");
    Assert(migratedInstallation.Path == root, "Loaded installation leaked an extended path prefix.");
    using (var migratedConfig = JsonDocument.Parse(await File.ReadAllTextAsync(migratedConfigPath)))
    {
        Assert(
            migratedConfig.RootElement.GetProperty("path").GetString() == root,
            "Legacy extended game path config was not migrated.");
    }
    Assert(
        SmapiInstallerService.ValidateGamePath(extendedRoot) == root,
        "SMAPI installer game path leaked an extended path prefix.");

    var modsRoot = Path.Combine(root, "Mods");
    var exampleRoot = Path.Combine(modsRoot, "ExampleMod");
    Directory.CreateDirectory(exampleRoot);
    await WriteManifestAsync(
        Path.Combine(exampleRoot, "manifest.json"),
        new
        {
            Name = "Example Mod",
            Description = "Smoke test mod",
            Author = "Valley Steward",
            Version = "1.2.3",
            UniqueId = "Smoke.Example",
            Dependencies = new[]
            {
                new { UniqueID = "Pathoschild.ContentPatcher", IsRequired = true },
            },
            UpdateKeys = new[]
            {
                "Nexus:123",
                "GitHub:owner/repo",
                "ModDrop:42",
            },
        });

    foreach (var (directory, uniqueId) in new[]
    {
        ("ConsoleCommands", "SMAPI.ConsoleCommands"),
        ("SaveBackup", "SMAPI.SaveBackup"),
    })
    {
        var bundledRoot = Path.Combine(modsRoot, directory);
        Directory.CreateDirectory(bundledRoot);
        await WriteManifestAsync(
            Path.Combine(bundledRoot, "manifest.json"),
            new
            {
                Name = directory,
                Description = "Bundled SMAPI mod",
                Author = "SMAPI",
                Version = "4.5.2",
                UniqueID = uniqueId,
            });
    }

    var discovery = new GameDiscoveryService();
    var installation = await discovery.InspectAsync(root)
        ?? throw new InvalidOperationException("Game directory inspection failed.");
    Assert(installation.Executable == "Stardew Valley.exe", "Unexpected game executable.");

    var smapi = await new SmapiService().InspectAsync(root);
    Assert(smapi.Installed, "SMAPI executable was not detected.");
    Assert(smapi.Version == "4.5.2", $"Unexpected SMAPI version: {smapi.Version}");

    var mods = new ModService();
    var scanned = await mods.ScanAsync(root);
    var example = scanned.Single(mod => mod.Id == "Smoke.Example");
    Assert(example.Enabled, "New mod should be enabled.");
    Assert(example.Dependencies.SequenceEqual(new[] { "Pathoschild.ContentPatcher" }), "Dependencies were not read.");
    Assert(
        example.UpdateKeys.SequenceEqual(new[] { "Nexus:123", "GitHub:owner/repo", "ModDrop:42" }),
        "UpdateKeys were not preserved during manifest scanning.");

    await mods.SetEnabledAsync(root, example, false);
    Assert(Directory.Exists(Path.Combine(modsRoot, ".ExampleMod")), "Disable did not rename the mod directory.");
    example = (await mods.ScanAsync(root)).Single(mod => mod.Id == "Smoke.Example");
    Assert(!example.Enabled, "Disabled mod was reported as enabled.");

    await mods.SetEnabledAsync(root, example, true);
    example = (await mods.ScanAsync(root)).Single(mod => mod.Id == "Smoke.Example");
    Assert(example.Enabled, "Re-enabled mod was reported as disabled.");

    var manifestPath = Path.Combine(exampleRoot, "manifest.json");
    var manifestBeforeTranslation = await File.ReadAllBytesAsync(manifestPath);
    var translationSource = await mods.ReadTranslationSourceAsync(root, example);
    await mods.SaveTranslationAsync(
        root,
        example,
        translationSource,
        new ValleySteward.WinUI.Models.InstalledModTranslation("示例 Mod", "用于验证 sidecar 的简介。"));
    example = (await mods.ScanAsync(root)).Single(mod => mod.Id == "Smoke.Example");
    Assert(example.Translated, "Saved translation sidecar was not loaded.");
    Assert(example.Name == "示例 Mod", "Saved translated name was not applied during scan.");
    Assert(
        manifestBeforeTranslation.SequenceEqual(await File.ReadAllBytesAsync(manifestPath)),
        "Translation modified manifest.json instead of using a sidecar.");
    Assert(
        File.Exists(Path.Combine(exampleRoot, ".valley-steward-translation.json")),
        "Translation sidecar was not created.");

    await SmokeModUpdateChecksAsync(example, manifestPath);

    await mods.MoveToTrashAsync(root, example);
    var trash = Path.Combine(modsRoot, ".mod-manager-trash");
    Assert(Directory.Exists(trash) && Directory.EnumerateDirectories(trash).Any(), "Mod was not moved into the manager trash.");

    var trashItems = await mods.ListTrashAsync(root);
    var trashedExample = trashItems.Single(item => item.DisplayName == "Example Mod");
    Assert(trashedExample.OriginalDirectoryName == "ExampleMod", "Trash did not retain the original directory name.");
    Assert(trashedExample.OriginalRelativePath == "ExampleMod", "Trash did not retain the original relative path.");

    var restored = await mods.RestoreFromTrashAsync(root, trashedExample);
    Assert(!restored.Renamed, "Conflict-free restore unexpectedly renamed the Mod directory.");
    Assert(Directory.Exists(exampleRoot), "Mod was not restored to its original directory.");
    Assert(
        !File.Exists(Path.Combine(exampleRoot, ".valley-steward-trash.json")),
        "Manager trash metadata leaked into the restored Mod directory.");

    example = (await mods.ScanAsync(root)).Single(mod => mod.Id == "Smoke.Example");
    await mods.MoveToTrashAsync(root, example);
    Directory.CreateDirectory(exampleRoot);
    var conflictSentinel = Path.Combine(exampleRoot, "do-not-overwrite.txt");
    await File.WriteAllTextAsync(conflictSentinel, "existing user directory");
    trashedExample = (await mods.ListTrashAsync(root)).Single(item => item.DisplayName == "Example Mod");
    await AssertThrowsAsync<InvalidDataException>(() =>
        mods.RestoreFromTrashAsync(root, trashedExample with { EntryName = ".." }));

    var conflictRestore = await mods.RestoreFromTrashAsync(root, trashedExample);
    Assert(conflictRestore.Renamed, "Restore did not report an original-directory conflict.");
    Assert(
        conflictRestore.RestoredDirectoryName == "ExampleMod (已恢复 2)",
        $"Unexpected conflict restore name: {conflictRestore.RestoredDirectoryName}");
    Assert(
        await File.ReadAllTextAsync(conflictSentinel) == "existing user directory",
        "Restore overwrote an existing directory.");

    example = (await mods.ScanAsync(root)).Single(mod => mod.Id == "Smoke.Example");
    await mods.MoveToTrashAsync(root, example);

    var outsideTrashSentinel = Path.Combine(modsRoot, "keep-outside-trash.txt");
    await File.WriteAllTextAsync(outsideTrashSentinel, "keep");
    var reparseTarget = Path.Combine(root, "reparse-target");
    var reparseEntry = Path.Combine(trash, "reparse-entry");
    Directory.CreateDirectory(reparseTarget);
    var exercisedReparseGuard = false;
    try
    {
        if (TryCreateDirectoryReparsePoint(reparseEntry, reparseTarget))
        {
            exercisedReparseGuard = true;
            await AssertThrowsAsync<UnauthorizedAccessException>(() => mods.ListTrashAsync(root));
            Assert(Directory.Exists(reparseTarget), "Reparse-point guard touched the link target.");
        }
    }
    finally
    {
        if (Directory.Exists(reparseEntry)
            && (File.GetAttributes(reparseEntry) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(reparseEntry);
        }
    }

    var emptiedCount = await mods.EmptyTrashAsync(root);
    Assert(emptiedCount > 0, "Empty trash did not report deleted entries.");
    Assert(Directory.Exists(trash), "Empty trash deleted the manager trash directory itself.");
    Assert(!Directory.EnumerateFileSystemEntries(trash).Any(), "Empty trash left entries behind.");
    Assert(File.Exists(outsideTrashSentinel), "Empty trash deleted a file outside the manager trash.");
    Assert(File.Exists(conflictSentinel), "Empty trash deleted the conflicting original directory.");

    var arguments = UiSettingsService.ParseArguments("--no-terminal -m \"value with spaces\" --mods-path");
    Assert(
        arguments.SequenceEqual(new[] { "--no-terminal", "-m", "value with spaces", "--mods-path" }),
        "Launch arguments were parsed or rewritten incorrectly.");

    Console.WriteLine("PASS game-path");
    Console.WriteLine("PASS windows-extended-path-normalization-migration");
    Console.WriteLine("PASS steam-local-avatar-cache-selection-normalization-fallback");
    Console.WriteLine("PASS smapi-version=4.5.2");
    Console.WriteLine("PASS unique-id-alias");
    Console.WriteLine("PASS mod-disable-enable-trash-restore-conflict-empty-boundary");
    Console.WriteLine(exercisedReparseGuard
        ? "PASS mod-trash-reparse-guard"
        : "SKIP mod-trash-reparse-guard-runtime-link-creation-unavailable");
    Console.WriteLine("PASS installed-mod-translation-sidecar-no-manifest-write");
    Console.WriteLine("PASS mod-updatekeys-nexus-github-bounded-isolated-no-write");
    Console.WriteLine("PASS launch-arguments-hyphen-preserved");
    Console.WriteLine("PASS ui-theme-accent-settings-roundtrip-fallback");
    Console.WriteLine("PASS activity-history-atomic-cap-redaction-clear");
    Console.WriteLine("PASS mod-update-batch-all-visible-safe-default-serial-isolation-cancellation");
    Console.WriteLine("SKIP game-process-job-stop-restart-requires-dedicated-benign-fixture");
    Console.WriteLine("PASS credential-manager-guid-roundtrip-cleanup");
    Console.WriteLine("PASS ai-http-https-preview-redaction-prompt");
    Console.WriteLine("PASS ai-endpoint-parallel-events-formats-full-body-timeout");
    Console.WriteLine("PASS nexus-nxm-url-filename-sidecar-safety");
    Console.WriteLine("PASS github-download-settings-atomic-backup");
    Console.WriteLine("PASS mod-installer-basic-install-update-config-preserve");
#if ARCHIVE_SECURITY_SMOKE
    Console.WriteLine("PASS smapi-archive-safe-extraction");
    Console.WriteLine("PASS smapi-hidden-console-launch-contract");
#else
    Console.WriteLine("SKIP smapi-archive-and-launch-security-opt-in");
#endif
    Console.WriteLine("PASS smapi-version-semver-normalization");
#if MOD_INSTALLER_SECURITY_SMOKE
    Console.WriteLine("PASS mod-zip-safe-plan-install-update-translation-rollback");
#else
    Console.WriteLine("SKIP mod-installer-security-opt-in");
#endif
    if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
    {
        await RunLiveChecksAsync();
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

static void SmokeSteamAvatarCache(string root)
{
    const string steamId64 = "76561198000000000";
    const string otherSteamId64 = "76561198000000001";
    var steamRoot = Path.Combine(root, "steam-avatar-fixture");
    var avatarCache = Path.Combine(steamRoot, "config", "avatarcache");
    Directory.CreateDirectory(avatarCache);

    var expectedPng = Path.Combine(avatarCache, steamId64 + ".png");
    var lowerPriorityJpeg = Path.Combine(avatarCache, steamId64 + ".jpg");
    var unrelatedPng = Path.Combine(avatarCache, otherSteamId64 + ".png");
    var validPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    File.WriteAllBytes(expectedPng, validPng);
    File.WriteAllBytes(lowerPriorityJpeg, [0xFF, 0xD8, 0xFF, 0xD9]);
    File.WriteAllBytes(unrelatedPng, validPng);

    var selected = SteamService.ResolveAvatarPath(
        steamId64,
        [Path.Combine(root, "missing-steam-root"), steamRoot]);
    Assert(
        string.Equals(selected, Path.GetFullPath(expectedPng), StringComparison.OrdinalIgnoreCase),
        "Steam avatar resolver did not prefer the exact SteamID64 PNG in config/avatarcache.");

    var extendedSteamRoot = @"\\?\" + steamRoot;
    var selectedFromExtendedRoot = SteamService.ResolveAvatarPath(steamId64, [extendedSteamRoot]);
    Assert(
        selectedFromExtendedRoot is { } normalizedAvatarPath
        && string.Equals(normalizedAvatarPath, Path.GetFullPath(expectedPng), StringComparison.OrdinalIgnoreCase)
        && !normalizedAvatarPath.StartsWith(@"\\?\", StringComparison.Ordinal),
        "Steam avatar resolver leaked an extended path prefix.");

    File.Delete(expectedPng);
    File.WriteAllText(lowerPriorityJpeg, "not an image");
    Assert(
        SteamService.ResolveAvatarPath(steamId64, [steamRoot]) is null,
        "Steam avatar resolver accepted a corrupt cache image.");
    Assert(
        SteamService.ResolveAvatarPath("not-a-steam-id", [steamRoot]) is null,
        "Steam avatar resolver accepted an invalid SteamID64.");
    Assert(
        SteamService.ResolveAvatarPath(steamId64, [Path.Combine(root, "missing-steam-root")]) is null,
        "Steam avatar resolver did not fall back when the local cache was missing.");
}

static void SmokeRemoteModImageUrls()
{
    var https = "https://staticdelivery.nexusmods.com/mods/1303/images/123/cover.png";
    Assert(
        RemoteModService.NormalizeRemoteImageUrl(https) == https,
        "HTTPS remote mod cover was rejected.");
    Assert(
        RemoteModService.NormalizeRemoteImageUrl("//staticdelivery.nexusmods.com/mods/1303/images/123/cover.png")
            == https,
        "Protocol-relative remote mod cover was not normalized to HTTPS.");
    Assert(
        RemoteModService.NormalizeRemoteImageUrl("http://staticdelivery.nexusmods.com/mods/1303/images/123/cover.png") is null,
        "HTTP remote mod cover was accepted.");
    Assert(
        RemoteModService.NormalizeRemoteImageUrl("https://user@example.com/cover.png") is null,
        "Remote mod cover with user info was accepted.");

    var item = new RemoteModItem(
        "nexus:123",
        "Cover Fixture",
        "Smoke",
        "One line.",
        "Nexus Mods",
        "1.0.0",
        "1 下载",
        DateTimeOffset.UtcNow,
        "https://www.nexusmods.com/stardewvalley/mods/123",
        https);
    Assert(item.ImageUrl == https, "RemoteModItem did not retain a normalized cover URL.");

    using var nexusGraphQl = JsonDocument.Parse(
        """{"pictureUrl":"//staticdelivery.nexusmods.com/mods/1303/images/123/cover.png","thumbnailUrl":"https://staticdelivery.nexusmods.com/mods/1303/images/123/thumb.png"}""");
    Assert(
        RemoteModService.ReadImageUrl(nexusGraphQl.RootElement, "pictureUrl", "thumbnailUrl") == https,
        "Nexus GraphQL cover fields were not mapped to ImageUrl.");

    var trendingCover = "https://staticdelivery.nexusmods.com/mods/1303/images/456/trending.png";
    using var nexusTrending = JsonDocument.Parse(
        """{"picture_url":"https://staticdelivery.nexusmods.com/mods/1303/images/456/trending.png","thumbnail_url":"https://staticdelivery.nexusmods.com/mods/1303/images/456/thumb.png"}""");
    Assert(
        RemoteModService.ReadImageUrl(nexusTrending.RootElement, "picture_url", "thumbnail_url", "pictureUrl", "thumbnailUrl") == trendingCover,
        "Nexus trending cover fields were not mapped to ImageUrl.");

    var githubAvatar = "https://avatars.githubusercontent.com/u/123456?v=4";
    using var githubOwner = JsonDocument.Parse("""{"avatar_url":"https://avatars.githubusercontent.com/u/123456?v=4"}""");
    Assert(
        RemoteModService.ReadImageUrl(githubOwner.RootElement, "avatar_url") == githubAvatar,
        "GitHub owner avatar field was not mapped to ImageUrl.");

    Console.WriteLine("PASS remote-mod-cover-url-normalization-and-json-extraction");
}

static void SmokeModShareLinks()
{
    Assert(
        ModShareService.ApiBaseUrl == "http://x-svalley-api.summercn.cn",
        "Share API base URL changed.");
    Assert(
        ModShareService.BuildOriginalUrl(["Nexus:41846"]) == "https://www.nexusmods.com/stardewvalley/mods/41846",
        "Nexus UpdateKey was not converted to the original mod URL.");
    Assert(
        ModShareService.BuildGitHubReleaseUrl(["GitHub:Pathoschild/StardewMods"])
            == "https://github.com/Pathoschild/StardewMods/releases/latest",
        "GitHub UpdateKey was not converted to the release URL.");
    Assert(
        ModShareService.BuildCoverUrl(["GitHub:Pathoschild/StardewMods"], null)
            == "https://github.com/Pathoschild.png?size=96",
        "GitHub UpdateKey was not converted to a share cover avatar URL.");
    Assert(
        ModShareService.BuildGitHubReleaseUrl(["GitHub:bad/repo/extra"]) is null,
        "Invalid GitHub UpdateKey produced a release URL.");
    Assert(
        ModShareService.BuildCoverUrl(["GitHub:bad/repo/extra"], null) is null,
        "Invalid GitHub UpdateKey produced a share cover URL.");
    Assert(
        ModShareService.IsValidShareCode("A1b2C3d4E5"),
        "Valid 10-character alphanumeric share code was rejected.");
    Assert(
        !ModShareService.IsValidShareCode("A1b2C3d4E-"),
        "Invalid share code with punctuation was accepted.");
    Assert(
        !ModShareService.IsValidShareCode("A1b2C3d4E"),
        "Invalid share code length was accepted.");
    Console.WriteLine("PASS mod-share-fixed-api-and-provider-links");
}

static bool TryCreateDirectoryReparsePoint(string linkPath, string targetPath)
{
    try
    {
        Directory.CreateSymbolicLink(linkPath, targetPath);
        return (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
    }
    catch (Exception error) when (error is IOException
        or UnauthorizedAccessException
        or PlatformNotSupportedException)
    {
        return false;
    }
}

static async Task WriteManifestAsync(string path, object value)
{
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

#if MOD_INSTALLER_SECURITY_SMOKE
static async Task SmokeModInstallerAsync(string fixtureRoot)
{
    Directory.CreateDirectory(fixtureRoot);
    var installer = new ModInstallerService();
    var game = Path.Combine(fixtureRoot, "game");
    Directory.CreateDirectory(game);

    var firstArchive = Path.Combine(fixtureRoot, "example-v1.zip");
    WriteModInstallArchive(
        firstArchive,
        ("Example-v1/ExampleMod/manifest.json", CreateModInstallManifest(
            "Example Mod",
            "Smoke.Installer.Example",
            "1.0.0",
            new[] { new { UniqueID = "Pathoschild.ContentPatcher", IsRequired = true, MinimumVersion = "2.0.0" } })),
        ("Example-v1/ExampleMod/example.dll", "version one"),
        ("Example-v1/README.txt", "ignored outer wrapper file"));

    var firstPlan = await installer.InspectArchiveAsync(firstArchive, game);
    Assert(firstPlan.CanInstall, "A valid wrapped Mod ZIP was not installable.");
    Assert(firstPlan.Mods.Count == 1, "Wrapped Mod ZIP did not expose exactly one Mod.");
    Assert(
        firstPlan.Mods[0].ArchiveRoot == "Example-v1/ExampleMod",
        "Wrapped Mod root was not detected recursively.");
    Assert(
        firstPlan.Mods[0].Dependencies.Single().UniqueId == "Pathoschild.ContentPatcher",
        "Mod dependency metadata was not returned in the install plan.");
    Assert(firstPlan.IgnoredEntryCount == 1, "Outer wrapper files were not marked as ignored.");

    var firstResult = await installer.InstallAsync(game, firstPlan);
    var installedRoot = Path.Combine(game, "Mods", "ExampleMod");
    Assert(firstResult.Installed == 1 && firstResult.Replaced == 0, "New-install result counts are wrong.");
    Assert(File.Exists(Path.Combine(installedRoot, "manifest.json")), "New Mod manifest was not installed.");
    Assert(
        await File.ReadAllTextAsync(Path.Combine(installedRoot, "example.dll")) == "version one",
        "New Mod payload was not installed.");
    Assert(!File.Exists(Path.Combine(game, "Mods", "README.txt")), "Outer wrapper file leaked into Mods.");

    var oldConfig = "{\"userChoice\":\"keep-me\"}";
    var customConfig = "{\"custom\":true}";
    await File.WriteAllTextAsync(Path.Combine(installedRoot, "config.json"), oldConfig);
    Directory.CreateDirectory(Path.Combine(installedRoot, "settings"));
    await File.WriteAllTextAsync(Path.Combine(installedRoot, "settings", "user.json"), customConfig);
    var oldTranslation = JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        source = new
        {
            uniqueId = "Smoke.Installer.Example",
            version = "1.0.0",
            name = "Example Mod",
            description = "Installer smoke fixture",
        },
        translation = new { name = "示例 Mod 旧译名", description = "应在普通更新中原样保留。" },
    });
    var installedTranslationPath = Path.Combine(installedRoot, ".valley-steward-translation.json");
    await File.WriteAllTextAsync(installedTranslationPath, oldTranslation);

    var updateArchive = Path.Combine(fixtureRoot, "example-v2.zip");
    WriteModInstallArchive(
        updateArchive,
        ("Download/ExampleMod/manifest.json", CreateModInstallManifest(
            "Example Mod",
            "Smoke.Installer.Example",
            "2.0.0")),
        ("Download/ExampleMod/example.dll", "version two"),
        ("Download/ExampleMod/config.json", "package default must not replace user config"),
        ("Download/ExampleMod/settings/user.json", "package custom default"),
        ("Download/ExampleMod/.valley-steward-translation.json", "package metadata must not replace manager metadata"));

    var updatePlan = await installer.InspectAsync(updateArchive, game);
    Assert(updatePlan.CanInstall, "Existing same-ID Mod should be a nonblocking update.");
    Assert(
        updatePlan.Conflicts.Any(conflict => conflict.Kind == ModInstallConflictKind.ExistingMod && !conflict.Blocking),
        "Update conflict was not surfaced for UI confirmation.");
    var updateResult = await installer.InstallAsync(
        game,
        updatePlan,
        new ModInstallOptions { PreserveRelativePaths = new[] { "settings/user.json" } });
    Assert(updateResult.Replaced == 1, "Update was not reported as a replacement.");
    Assert(updateResult.BackupPath is not null && Directory.Exists(updateResult.BackupPath), "Update backup was not retained.");
    Assert(await File.ReadAllTextAsync(Path.Combine(installedRoot, "config.json")) == oldConfig, "config.json was not preserved.");
    Assert(
        await File.ReadAllTextAsync(Path.Combine(installedRoot, "settings", "user.json")) == customConfig,
        "Configured user file was not preserved.");
    Assert(
        await File.ReadAllTextAsync(installedTranslationPath) == oldTranslation,
        "Valley Steward translation sidecar was not preserved by default.");
    Assert(
        await File.ReadAllTextAsync(Path.Combine(installedRoot, "example.dll")) == "version two",
        "Updated Mod payload was not committed.");
    Assert(
        updateResult.Mods.Single().PreservedFiles.Contains("config.json", StringComparer.OrdinalIgnoreCase)
            && updateResult.Mods.Single().PreservedFiles.Contains(
                ".valley-steward-translation.json",
                StringComparer.OrdinalIgnoreCase),
        "Update result did not summarize default preserved files.");

    var translatedArchive = Path.Combine(fixtureRoot, "example-v3.zip");
    WriteModInstallArchive(
        translatedArchive,
        ("ExampleMod/manifest.json", CreateModInstallManifest(
            "Example Mod",
            "Smoke.Installer.Example",
            "3.0.0")),
        ("ExampleMod/example.dll", "version three"));
    var nexusSidecar = Path.Combine(fixtureRoot, "nexus-123-456.valley-steward.json");
    await File.WriteAllTextAsync(
        nexusSidecar,
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            provider = "Nexus Mods",
            modId = "123",
            fileId = "456",
            name = "示例安装器 Mod",
            description = "从明确传入的 Nexus 下载 sidecar 写入。",
        }));
    var translatedResult = await installer.InstallAsync(
        game,
        new NexusDownloadResult(
            translatedArchive,
            Path.GetFileName(translatedArchive),
            new FileInfo(translatedArchive).Length,
            nexusSidecar));
    Assert(translatedResult.Mods.Single().TranslationApplied, "Explicit Nexus translation sidecar was not applied.");
    var translatedScan = (await new ModService().ScanAsync(game))
        .Single(mod => mod.Id == "Smoke.Installer.Example");
    Assert(
        translatedScan.Translated && translatedScan.Name == "示例安装器 Mod",
        "Installed translation sidecar did not match ModService's format.");

    var bundleGame = Path.Combine(fixtureRoot, "bundle-game");
    Directory.CreateDirectory(bundleGame);
    var bundleArchive = Path.Combine(fixtureRoot, "bundle.zip");
    WriteModInstallArchive(
        bundleArchive,
        ("Bundle/One/manifest.json", CreateModInstallManifest("Bundle One", "Smoke.Bundle.One", "1.0.0")),
        ("Bundle/One/one.dll", "one"),
        ("Bundle/One/assets/legacy.xnb", "xnb"),
        ("Bundle/Two/manifest.json", CreateModInstallManifest("Bundle Two", "Smoke.Bundle.Two", "1.0.0")),
        ("Bundle/Two/two.dll", "two"));
    var bundlePlan = await installer.InspectAsync(bundleArchive, bundleGame);
    Assert(bundlePlan.Mods.Count == 2, "Multi-Mod ZIP was not split into two manifest roots.");
    Assert(bundlePlan.RequiresXnbConfirmation && bundlePlan.XnbFiles.Count == 1, "XNB risk was not returned in the plan.");
    await AssertThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(bundleGame, bundlePlan));
    Assert(!Directory.Exists(Path.Combine(bundleGame, "Mods", "One")), "XNB confirmation gate partially installed a Mod.");
    var bundleResult = await installer.InstallAsync(
        bundleGame,
        bundlePlan,
        new ModInstallOptions { AllowXnbFiles = true });
    Assert(bundleResult.Installed == 2, "Multi-Mod ZIP did not install both Mods.");
    Assert(File.Exists(Path.Combine(bundleGame, "Mods", "One", "one.dll")), "First bundled Mod is missing.");
    Assert(File.Exists(Path.Combine(bundleGame, "Mods", "Two", "two.dll")), "Second bundled Mod is missing.");

    var noManifestArchive = Path.Combine(fixtureRoot, "no-manifest.zip");
    WriteModInstallArchive(noManifestArchive, ("Content/Town.xnb", "legacy replacement"));
    var noManifestPlan = await installer.InspectAsync(noManifestArchive);
    Assert(!noManifestPlan.CanInstall, "Manifest-less archive was marked installable.");
    Assert(
        noManifestPlan.Conflicts.Any(conflict => conflict.Kind == ModInstallConflictKind.MissingManifest),
        "Manifest-less archive did not return a UI-readable conflict.");
    Assert(noManifestPlan.RequiresXnbConfirmation, "Manifest-less XNB archive did not report XNB risk.");

    await SmokeMaliciousModArchivesAsync(fixtureRoot);
    await SmokeModInstallRollbackAsync(fixtureRoot, installer);
}

static async Task SmokeMaliciousModArchivesAsync(string fixtureRoot)
{
    var maliciousRoot = Path.Combine(fixtureRoot, "malicious");
    Directory.CreateDirectory(maliciousRoot);
    var manifest = CreateModInstallManifest("Unsafe", "Smoke.Unsafe", "1.0.0");
    var unsafePaths = new[]
    {
        "../escaped.txt",
        "..\\escaped-backslash.txt",
        "C:/absolute.txt",
        "/absolute-unix.txt",
        "Unsafe/CON.txt",
        "Unsafe/LPT1.log",
        "Unsafe/CONIN$.txt",
        "Unsafe/COM¹.log",
        "Unsafe/name:stream.txt",
    };
    foreach (var unsafePath in unsafePaths)
    {
        var archivePath = Path.Combine(maliciousRoot, $"unsafe-{Guid.NewGuid():N}.zip");
        WriteModInstallArchive(
            archivePath,
            ("Unsafe/manifest.json", manifest),
            (unsafePath, "unsafe"));
        await AssertThrowsAsync<InvalidDataException>(() => new ModInstallerService().InspectAsync(archivePath));
    }
    Assert(!File.Exists(Path.Combine(maliciousRoot, "escaped.txt")), "ZIP slip wrote outside extraction root.");

    var symlinkArchive = Path.Combine(maliciousRoot, "symlink.zip");
    using (var stream = new FileStream(symlinkArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
    {
        WriteArchiveEntry(archive, "Unsafe/manifest.json", manifest);
        var link = archive.CreateEntry("Unsafe/link");
        link.ExternalAttributes = unchecked((int)0xA1FF0000);
        using var writer = new StreamWriter(link.Open());
        writer.Write("../../outside");
    }
    await AssertThrowsAsync<InvalidDataException>(() => new ModInstallerService().InspectAsync(symlinkArchive));

    var reparseArchive = Path.Combine(maliciousRoot, "reparse.zip");
    using (var stream = new FileStream(reparseArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
    {
        WriteArchiveEntry(archive, "Unsafe/manifest.json", manifest);
        var reparse = archive.CreateEntry("Unsafe/reparse");
        reparse.ExternalAttributes = (int)FileAttributes.ReparsePoint;
        using var writer = new StreamWriter(reparse.Open());
        writer.Write("unsafe");
    }
    await AssertThrowsAsync<InvalidDataException>(() => new ModInstallerService().InspectAsync(reparseArchive));

    var duplicateArchive = Path.Combine(maliciousRoot, "case-collision.zip");
    WriteModInstallArchive(
        duplicateArchive,
        ("Unsafe/manifest.json", manifest),
        ("Unsafe/File.txt", "one"),
        ("unsafe/file.TXT", "two"));
    await AssertThrowsAsync<InvalidDataException>(() => new ModInstallerService().InspectAsync(duplicateArchive));

    var wrongExtension = Path.Combine(maliciousRoot, "valid-zip-content.7z");
    WriteModInstallArchive(wrongExtension, ("Unsafe/manifest.json", manifest));
    await AssertThrowsAsync<InvalidDataException>(() => new ModInstallerService().InspectAsync(wrongExtension));

    var deepArchive = Path.Combine(maliciousRoot, "deep.zip");
    WriteModInstallArchive(deepArchive, ("Outer/Inner/Unsafe/manifest.json", manifest));
    await AssertThrowsAsync<InvalidDataException>(() => new ModInstallerService(new ModArchiveLimits
    {
        MaximumPathDepth = 3,
    }).InspectAsync(deepArchive));

    var manyEntriesArchive = Path.Combine(maliciousRoot, "many-entries.zip");
    WriteModInstallArchive(
        manyEntriesArchive,
        ("Unsafe/manifest.json", manifest),
        ("Unsafe/a.txt", "a"),
        ("Unsafe/b.txt", "b"));
    await AssertThrowsAsync<InvalidDataException>(() => new ModInstallerService(new ModArchiveLimits
    {
        MaximumEntries = 2,
    }).InspectAsync(manyEntriesArchive));

    var oversizedArchive = Path.Combine(maliciousRoot, "oversized-entry.zip");
    WriteModInstallArchive(
        oversizedArchive,
        ("Unsafe/manifest.json", manifest),
        ("Unsafe/payload.bin", new string('x', 2_048)));
    await AssertThrowsAsync<InvalidDataException>(() => new ModInstallerService(new ModArchiveLimits
    {
        MaximumEntryBytes = 1_024,
        MaximumExtractedBytes = 4_096,
    }).InspectAsync(oversizedArchive));
    await AssertThrowsAsync<InvalidDataException>(() => new ModInstallerService(new ModArchiveLimits
    {
        MaximumArchiveBytes = 32,
    }).InspectAsync(oversizedArchive));

    var oversizedTotalArchive = Path.Combine(maliciousRoot, "oversized-total.zip");
    WriteModInstallArchive(
        oversizedTotalArchive,
        ("Unsafe/manifest.json", manifest),
        ("Unsafe/first.bin", new string('a', 1_100)),
        ("Unsafe/second.bin", new string('b', 1_100)));
    await AssertThrowsAsync<InvalidDataException>(() => new ModInstallerService(new ModArchiveLimits
    {
        MaximumEntryBytes = 2_048,
        MaximumExtractedBytes = 2_048,
    }).InspectAsync(oversizedTotalArchive));
}

static async Task SmokeModInstallRollbackAsync(string fixtureRoot, ModInstallerService installer)
{
    var game = Path.Combine(fixtureRoot, "rollback-game");
    var existingRoot = Path.Combine(game, "Mods", "ExistingMod");
    Directory.CreateDirectory(existingRoot);
    await File.WriteAllTextAsync(
        Path.Combine(existingRoot, "manifest.json"),
        CreateModInstallManifest("Existing Mod", "Smoke.Rollback.Existing", "1.0.0"));
    await File.WriteAllTextAsync(Path.Combine(existingRoot, "old.dll"), "must survive rollback");
    Directory.CreateDirectory(Path.Combine(existingRoot, "config.json"));
    await File.WriteAllTextAsync(Path.Combine(existingRoot, "config.json", "sentinel.txt"), "directory conflict");

    var archivePath = Path.Combine(fixtureRoot, "rollback-bundle.zip");
    WriteModInstallArchive(
        archivePath,
        ("Bundle/NewMod/manifest.json", CreateModInstallManifest("New Mod", "Smoke.Rollback.New", "1.0.0")),
        ("Bundle/NewMod/new.dll", "must be rolled back"),
        ("Bundle/ExistingMod/manifest.json", CreateModInstallManifest(
            "Existing Mod",
            "Smoke.Rollback.Existing",
            "2.0.0")),
        ("Bundle/ExistingMod/new.dll", "must not survive rollback"));

    var plan = await installer.InspectAsync(archivePath, game);
    Assert(plan.CanInstall, "Rollback fixture did not produce an installable plan.");
    await AssertThrowsAsync<InvalidDataException>(() => installer.InstallAsync(game, plan));
    Assert(!Directory.Exists(Path.Combine(game, "Mods", "NewMod")), "Rollback left a newly installed Mod behind.");
    Assert(File.Exists(Path.Combine(existingRoot, "old.dll")), "Rollback did not restore the previous Mod directory.");
    Assert(!File.Exists(Path.Combine(existingRoot, "new.dll")), "Rollback left updated payload in the restored Mod.");
    Assert(Directory.Exists(Path.Combine(existingRoot, "config.json")), "Rollback did not restore the conflicting config directory.");
    using var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(existingRoot, "manifest.json")));
    Assert(
        manifestDocument.RootElement.GetProperty("Version").GetString() == "1.0.0",
        "Rollback did not restore the old manifest version.");
    var stagingParent = Path.Combine(game, ".valley-steward-staging");
    var backupParent = Path.Combine(game, ".valley-steward-backups");
    Assert(
        !Directory.Exists(stagingParent) || !Directory.EnumerateFileSystemEntries(stagingParent).Any(),
        "Failed install left staging transaction data behind.");
    Assert(
        !Directory.Exists(backupParent) || !Directory.EnumerateFileSystemEntries(backupParent).Any(),
        "Successful rollback left a redundant backup transaction behind.");
}

static string CreateModInstallManifest(
    string name,
    string uniqueId,
    string version,
    object[]? dependencies = null)
{
    return JsonSerializer.Serialize(new
    {
        Name = name,
        Description = "Installer smoke fixture",
        Author = "Valley Steward Smoke",
        Version = version,
        UniqueID = uniqueId,
        Dependencies = dependencies ?? Array.Empty<object>(),
    });
}

static void WriteModInstallArchive(string path, params (string Path, string Content)[] entries)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    foreach (var entry in entries)
    {
        WriteArchiveEntry(archive, entry.Path, entry.Content);
    }
}
#endif

static async Task SmokeModInstallerBasicAsync(string fixtureRoot)
{
    Directory.CreateDirectory(fixtureRoot);
    var game = Path.Combine(fixtureRoot, "game");
    Directory.CreateDirectory(Path.Combine(game, "Mods"));
    await File.WriteAllBytesAsync(Path.Combine(game, "Stardew Valley.exe"), []);
    var installer = new ModInstallerService();

    var firstArchive = Path.Combine(fixtureRoot, "example-v1.zip");
    WriteBenignModArchive(firstArchive, "1.0.0", "first");
    var firstPlan = await installer.InspectAsync(firstArchive, game);
    Assert(firstPlan.CanInstall && firstPlan.Mods.Count == 1, "A valid Mod ZIP was not installable.");
    var firstResult = await installer.InstallAsync(game, firstPlan);
    Assert(firstResult.Installed == 1 && firstResult.Replaced == 0, "Initial Mod install result was incorrect.");

    var installedRoot = Path.Combine(game, "Mods", firstPlan.Mods[0].TargetFolderName);
    var configPath = Path.Combine(installedRoot, "config.json");
    await File.WriteAllTextAsync(configPath, "{\"enabled\":true}");
    var secondArchive = Path.Combine(fixtureRoot, "example-v2.zip");
    WriteBenignModArchive(secondArchive, "2.0.0", "second");
    var secondPlan = await installer.InspectAsync(secondArchive, game);
    var secondResult = await installer.InstallAsync(game, secondPlan);
    Assert(
        secondResult.Installed == 0 && secondResult.Replaced == 1,
        $"Mod update result was incorrect: installed={secondResult.Installed}, replaced={secondResult.Replaced}, existing={secondPlan.Mods[0].ExistingVersion ?? "none"}.");
    Assert(await File.ReadAllTextAsync(configPath) == "{\"enabled\":true}", "Mod update did not preserve config.json.");
    Assert(await File.ReadAllTextAsync(Path.Combine(installedRoot, "payload.txt")) == "second", "Mod update did not replace payload files.");

    var translatedArchive = Path.Combine(fixtureRoot, "example-v3.zip");
    WriteBenignModArchive(translatedArchive, "3.0.0", "third");
    var archiveSha256 = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(translatedArchive)))
        .ToLowerInvariant();
    var translationSidecar = DownloadTranslationSidecarService.WriteForArchive(
        translatedArchive,
        "GitHub",
        "smoke/repository",
        new NexusDownloadTranslation("Smoke 示例", "由 GitHub 下载 sidecar 保存的简介。"),
        archiveSha256);
    var translatedPlan = await installer.InspectAsync(translatedArchive, game);
    var translatedResult = await installer.InstallAsync(
        game,
        translatedPlan,
        new ModInstallOptions
        {
            TranslationSidecarPath = translationSidecar,
            TranslationTargetUniqueId = "Smoke.BasicInstaller",
        });
    Assert(translatedResult.Mods.Single().TranslationApplied, "GitHub download translation was not applied.");
    var translatedMod = (await new ModService().ScanAsync(game))
        .Single(mod => mod.Id == "Smoke.BasicInstaller");
    Assert(
        translatedMod.Translated && translatedMod.Name == "Smoke 示例",
        "GitHub download sidecar did not match the installed translation format.");
    using var sidecarDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(translationSidecar));
    Assert(
        sidecarDocument.RootElement.GetProperty("provider").GetString() == "GitHub"
            && sidecarDocument.RootElement.GetProperty("archiveSha256").GetString() == archiveSha256,
        "GitHub download sidecar lost its provider or archive binding.");
    Assert(
        !Directory.EnumerateFiles(fixtureRoot, "*.tmp", SearchOption.AllDirectories).Any(),
        "GitHub download sidecar left a temporary file behind.");
}

static void WriteBenignModArchive(string path, string version, string payload)
{
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    var manifest = JsonSerializer.Serialize(new
    {
        Name = "Smoke Example",
        Author = "Valley Steward",
        Version = version,
        UniqueID = "Smoke.BasicInstaller",
        Description = "Benign installer smoke fixture",
    });
    AddEntry("SmokeExample/manifest.json", manifest);
    AddEntry("SmokeExample/payload.txt", payload);

    void AddEntry(string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}

static async Task SmokeModUpdateBatchQueueAsync()
{
    var service = new ModUpdateBatchService();
    var safeA = CreateBatchUpdateFixture("Smoke.Batch.A", "1.0.0", "1.1.0");
    var safeB = CreateBatchUpdateFixture("Smoke.Batch.B", "2.0.0", "2.1.0");
    var safeC = CreateBatchUpdateFixture("Smoke.Batch.C", "3.0.0", "3.1.0");
    var manualOnly = CreateBatchUpdateFixture(
        "Smoke.Batch.Manual",
        "1.0.0",
        "1.1.0",
        canAutoUpdate: false);
    var staleCheck = CreateBatchUpdateFixture(
        "Smoke.Batch.Stale",
        "1.0.0",
        "1.1.0",
        checkedInstalledVersion: "0.9.0");
    var downgrade = CreateBatchUpdateFixture("Smoke.Batch.Down", "2.0.0", "1.9.0");
    var duplicateA = CreateBatchUpdateFixture("Smoke.Batch.Duplicate", "1.0.0", "1.1.0");
    var duplicateB = CreateBatchUpdateFixture("smoke.batch.duplicate", "1.0.0", "1.1.0");

    InstalledMod[] checkedMods =
    [
        safeA,
        manualOnly,
        staleCheck,
        downgrade,
        duplicateA,
        duplicateB,
        safeB,
        safeC,
    ];
    var selections = service.CreateSelections(checkedMods);
    Assert(selections.Count == checkedMods.Length, "Available updates were omitted from batch selection UI data.");
    Assert(
        selections.Count(item => item.CanAutoUpdate) == 3,
        "Unsafe updates were enabled or safe updates were disabled in selection UI data.");
    Assert(
        selections.Single(item => item.ModId == manualOnly.Id) is
        {
            CanAutoUpdate: false,
            Provider: ModUpdateProvider.GitHub,
            CannotUpdateReason: "manual fixture",
        },
        "Manual-only update did not retain its source and disabled reason.");
    Assert(
        selections.Where(item => item.ModId.Equals(
                duplicateA.Id,
                StringComparison.OrdinalIgnoreCase))
            .All(item => !item.CanAutoUpdate
                && item.CannotUpdateReason?.Contains("重复", StringComparison.Ordinal) == true),
        "Duplicate IDs were not visible and disabled with a reason.");

    var candidates = service.CreateCandidates(checkedMods);
    Assert(
        candidates.Select(candidate => candidate.ModId).SequenceEqual(
            new[] { safeA.Id, safeB.Id, safeC.Id }),
        "Batch selection admitted an unchecked, manual-only, stale, downgrade, or duplicate Mod.");

    var executionOrder = new ConcurrentQueue<string>();
    var active = 0;
    var maximumActive = 0;
    var batch = await service.RunAsync(
        candidates,
        async (candidate, cancellationToken) =>
        {
            var nowActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, nowActive);
            executionOrder.Enqueue(candidate.ModId);
            try
            {
                await Task.Delay(5, cancellationToken);
                if (candidate.ModId == safeB.Id)
                {
                    throw new InvalidOperationException("synthetic isolated failure");
                }
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
    Assert(!batch.WasCancelled, "Completed batch was reported as cancelled.");
    Assert(batch.Succeeded == 2 && batch.Failed == 1, "Batch success/failure totals are incorrect.");
    Assert(maximumActive == 1, "Batch updates were not serialized.");
    Assert(
        executionOrder.SequenceEqual(candidates.Select(candidate => candidate.ModId)),
        "A failed batch item stopped or reordered later updates.");
    Assert(
        batch.Items.Single(item => item.Candidate.ModId == safeB.Id).ErrorMessage
            ?.Contains("synthetic isolated failure", StringComparison.Ordinal) == true,
        "Per-item batch failure details were not retained.");

    using var cancellation = new CancellationTokenSource();
    var cancellationCalls = 0;
    var cancelled = await service.RunAsync(
        candidates,
        (candidate, cancellationToken) =>
        {
            cancellationCalls++;
            cancellation.Cancel();
            return Task.CompletedTask;
        },
        cancellationToken: cancellation.Token);
    Assert(cancelled.WasCancelled, "Batch cancellation was not reported.");
    Assert(cancelled.Completed == 1 && cancellationCalls == 1, "Cancellation did not stop before the next item.");
    Assert(cancelled.Items[0].Succeeded, "An item completed before cancellation was lost.");

    static void UpdateMaximum(ref int target, int value)
    {
        var observed = Volatile.Read(ref target);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref target, value, observed);
            if (previous == observed)
            {
                return;
            }
            observed = previous;
        }
    }
}

static InstalledMod CreateBatchUpdateFixture(
    string id,
    string installedVersion,
    string expectedVersion,
    bool canAutoUpdate = true,
    string? checkedInstalledVersion = null)
{
    var pageUrl = $"https://github.com/smoke/{Uri.EscapeDataString(id)}";
    ModUpdateDownloadDescriptor? descriptor = canAutoUpdate
        ? new GitHubModUpdateDownloadDescriptor(
            new Uri($"{pageUrl}/releases/download/v{expectedVersion}/mod.zip"),
            "mod.zip",
            1_024,
            null,
            expectedVersion,
            pageUrl)
        : null;
    return new InstalledMod
    {
        Id = id,
        Name = id,
        Author = "Smoke",
        Version = installedVersion,
        Path = id,
        Health = "healthy",
        Enabled = true,
        UpdateKeys = ["GitHub:smoke/repository"],
        UpdateResult = new InstalledModUpdateResult(
            id,
            id,
            checkedInstalledVersion ?? installedVersion,
            ModUpdateCheckStatus.UpdateAvailable,
            expectedVersion,
            "synthetic batch fixture",
            [
                new ModUpdateSourceResult(
                    "GitHub:smoke/repository",
                    ModUpdateProvider.GitHub,
                    ModUpdateCheckStatus.UpdateAvailable,
                    checkedInstalledVersion ?? installedVersion,
                    expectedVersion,
                    pageUrl,
                    "synthetic source fixture",
                    descriptor,
                    descriptor is null ? "manual fixture" : null),
            ],
            descriptor,
            descriptor is null ? "manual fixture" : null),
    };
}

static async Task SmokeModUpdateChecksAsync(InstalledMod scannedMod, string manifestPath)
{
    Assert(
        ModUpdateService.TryParseUpdateKey("Nexus:123", out var nexusKey, out _)
            && nexusKey is { Provider: ModUpdateProvider.Nexus, Identifier: "123" },
        "Nexus UpdateKey was not parsed.");
    Assert(
        ModUpdateService.TryParseUpdateKey("GitHub:owner/repo", out var githubKey, out _)
            && githubKey is { Provider: ModUpdateProvider.GitHub, Identifier: "owner/repo" },
        "GitHub owner/repository UpdateKey was not parsed.");
    Assert(
        ModUpdateService.TryParseUpdateKey(
            "GitHub:https://github.com/owner/repo/releases/tag/v1.2.3",
            out var githubUrlKey,
            out _)
            && githubUrlKey?.Identifier == "owner/repo",
        "GitHub Release URL UpdateKey was not normalized to its repository.");
    Assert(
        !ModUpdateService.TryParseUpdateKey(
            "GitHub:https://github.com.example.invalid/owner/repo/releases/latest",
            out _,
            out _),
        "A suffix-confusion GitHub host was accepted.");
    Assert(
        !ModUpdateService.TryParseUpdateKey(
            "GitHub:http://github.com/owner/repo/releases/latest",
            out _,
            out _),
        "A non-HTTPS GitHub Release URL was accepted.");
    Assert(
        !ModUpdateService.TryParseUpdateKey(
            "GitHub:https://github.com/owner%2Frepo/releases/latest",
            out _,
            out _),
        "An encoded path separator was accepted in a GitHub Release URL.");

    Assert(ModUpdateService.CompareVersions("v1.2.3", "1.2.3") == 0, "Leading v was not normalized.");
    Assert(ModUpdateService.CompareVersions("1.2", "1.2.0") == 0, "Missing zero version parts differed.");
    Assert(ModUpdateService.CompareVersions("1.2.3", "1.2.3-beta.1") > 0, "Stable version did not beat prerelease.");
    Assert(ModUpdateService.CompareVersions("1.2.3-beta.2", "1.2.3-beta.10") < 0, "Numeric prerelease order is wrong.");
    Assert(ModUpdateService.CompareVersions("release 1.2.3", "1.2.3") is null, "Descriptive version was guessed.");

    const string syntheticApiKey = "smoke-nexus-personal-key-123456";
    var handler = new ModUpdateStubHandler();
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    var service = new ModUpdateService(client, () => syntheticApiKey, maximumConcurrentRequests: 2);
    var fixtureMods = new[]
    {
        scannedMod,
        CreateUpdateFixture("Smoke.Failed", "1.0.0", "GitHub:fail/repo"),
        CreateUpdateFixture("Smoke.Ambiguous", "1.0.0", "GitHub:multi/repo"),
        CreateUpdateFixture("Smoke.Concurrent1", "1.0.0", "GitHub:concurrency/r1"),
        CreateUpdateFixture("Smoke.Concurrent2", "1.0.0", "GitHub:concurrency/r2"),
        CreateUpdateFixture("Smoke.Concurrent3", "1.0.0", "GitHub:concurrency/r3"),
        CreateUpdateFixture("Smoke.Concurrent4", "1.0.0", "GitHub:concurrency/r4"),
    };
    var manifestBefore = await File.ReadAllBytesAsync(manifestPath);
    var results = await service.CheckAllAsync(fixtureMods);

    var nexusResult = results.Single(result => result.ModId == scannedMod.Id);
    Assert(nexusResult.UpdateAvailable, "Nexus newer version was not reported.");
    Assert(nexusResult.CanAutoUpdate, "Nexus active primary file did not produce a download descriptor.");
    Assert(
        nexusResult.Download is NexusModUpdateDownloadDescriptor
        {
            ModId: "123",
            FileId: "456",
            ExpectedVersion: "1.3.0",
        },
        "Nexus update descriptor did not bind the Mod ID, file ID, and expected version.");
    Assert(
        nexusResult.Sources.Any(source => source.Provider == ModUpdateProvider.GitHub
            && source.Status == ModUpdateCheckStatus.UpToDate),
        "A second valid update source was not checked independently.");
    Assert(
        nexusResult.Sources.Any(source => source.Provider == ModUpdateProvider.Unknown
            && source.Status == ModUpdateCheckStatus.Unavailable),
        "Unsupported UpdateKey did not explain why it could not be checked.");

    var failed = results.Single(result => result.ModId == "Smoke.Failed");
    Assert(failed.Status == ModUpdateCheckStatus.Failed, "One failed GitHub source was not isolated as a failure.");
    Assert(
        results.Where(result => result.ModId.StartsWith("Smoke.Concurrent", StringComparison.Ordinal))
            .All(result => result.UpdateAvailable && result.CanAutoUpdate),
        "A failed source prevented unrelated GitHub update checks.");

    var ambiguous = results.Single(result => result.ModId == "Smoke.Ambiguous");
    Assert(ambiguous.UpdateAvailable, "Ambiguous GitHub assets hid the available version.");
    Assert(!ambiguous.CanAutoUpdate, "Multiple GitHub ZIP assets were selected automatically.");
    Assert(
        ambiguous.CannotUpdateReason?.Contains("2 个", StringComparison.Ordinal) == true,
        "Ambiguous GitHub assets did not explain why one-click update is unavailable.");

    var directDownload = await service.ResolveGitHubReleaseDownloadAsync("owner/repo");
    Assert(
        directDownload.CanDownload
            && directDownload.Download is GitHubModUpdateDownloadDescriptor
            {
                AssetName: "repo-v1.2.3.zip",
                ExpectedVersion: "v1.2.3",
            },
        "Unique trusted GitHub ZIP did not produce a one-click download descriptor.");
    var ambiguousDownload = await service.ResolveGitHubReleaseDownloadAsync("multi/repo");
    Assert(
        !ambiguousDownload.CanDownload
            && ambiguousDownload.CannotDownloadReason?.Contains("2 个", StringComparison.Ordinal) == true,
        "Ambiguous GitHub ZIP assets were silently auto-selected.");

    Assert(handler.MaximumObservedConcurrency is >= 2 and <= 2, "HTTP concurrency limit was not enforced.");
    Assert(
        handler.NexusApiKeys.Count > 0
            && handler.NexusApiKeys.All(value => value == syntheticApiKey),
        "Nexus Personal API Key was not sent only through the request header.");
    Assert(
        handler.RequestUris.All(uri => !uri.AbsoluteUri.Contains(syntheticApiKey, StringComparison.Ordinal)),
        "Nexus Personal API Key leaked into a request URL.");
    Assert(
        manifestBefore.SequenceEqual(await File.ReadAllBytesAsync(manifestPath)),
        "Update checking modified the installed Mod manifest.");

    var requestsBeforeMissingKeyCheck = handler.RequestUris.Count;
    var missingKeyService = new ModUpdateService(client, () => null, maximumConcurrentRequests: 2);
    var missingKey = await missingKeyService.CheckAsync(
        CreateUpdateFixture("Smoke.NoKey", "1.0.0", "Nexus:999"));
    Assert(missingKey.Status == ModUpdateCheckStatus.Unavailable, "Missing Nexus key was not reported as unavailable.");
    Assert(
        missingKey.Sources.Single().Message.Contains("API Key", StringComparison.Ordinal),
        "Missing Nexus key did not provide an actionable reason.");
    Assert(handler.RequestUris.Count == requestsBeforeMissingKeyCheck, "Missing Nexus key still caused an API request.");
}

static InstalledMod CreateUpdateFixture(string id, string version, params string[] updateKeys)
{
    return new InstalledMod
    {
        Id = id,
        Name = id,
        Description = "Temporary update-check fixture",
        Author = "Smoke",
        Version = version,
        Path = Path.Combine("Mods", id),
        Health = "healthy",
        Enabled = true,
        UpdateKeys = updateKeys,
    };
}

static async Task SmokeUiSettingsAsync(string root)
{
    var configPath = Path.Combine(root, "ui-settings", "ui-settings-v1.json");
    var service = new UiSettingsService(configPath);
    var settings = new UiSettings
    {
        RememberLaunch = false,
        Theme = "dark",
        AccentPreset = "zune",
        SmapiArguments = "--no-terminal",
        FirstRunCompleted = true,
        AutoInstallStarterMod = false,
    };

    await service.SaveAsync(settings);
    var loaded = await service.LoadAsync();
    Assert(!loaded.RememberLaunch, "UI settings did not preserve the remember-launch choice.");
    Assert(loaded.Theme == "dark", "UI theme did not survive save/load.");
    Assert(loaded.AccentPreset == "zune", "Accent preset did not survive save/load.");
    Assert(loaded.SmapiArguments == "--no-terminal", "UI settings lost unrelated values.");
    Assert(loaded.FirstRunCompleted, "UI settings did not preserve first-run completion.");
    Assert(!loaded.AutoInstallStarterMod, "UI settings did not preserve the starter Mod opt-out.");
    Assert(
        !Directory.EnumerateFiles(Path.GetDirectoryName(configPath)!, "*.tmp").Any(),
        "UI settings left a temporary file behind.");

    await File.WriteAllTextAsync(
        configPath,
        """
        {
          "version": 1,
          "rememberLaunch": false,
          "theme": "unsupported-theme",
          "accentPreset": "unsupported-accent"
        }
        """);
    var normalized = await service.LoadAsync();
    Assert(!normalized.RememberLaunch, "UI settings fallback discarded valid fields.");
    Assert(normalized.Theme == "system", "Unsupported UI theme did not fall back to system.");
    Assert(!normalized.FirstRunCompleted, "Legacy UI settings should not be treated as first-run completed.");
    Assert(normalized.AutoInstallStarterMod, "Legacy UI settings should default to starter Mod installation.");
    Assert(
        normalized.AccentPreset == UiSettingsService.DefaultAccentPresetId,
        "Unsupported accent preset did not fall back to Windows 11 blue.");
    Assert(
        UiSettingsService.AccentColorPresets.Select(preset => preset.Id).Distinct().Count()
            == UiSettingsService.AccentColorPresets.Count,
        "Accent preset identifiers are not unique.");
}

static async Task SmokeActivityHistoryAsync(string root)
{
    var configPath = Path.Combine(root, "activity-history", "activity-history-v1.json");
    var service = new ActivityHistoryService(configPath);
    const string secret = "sensitive-token-value";
    await service.AddAsync(
        ActivityKind.Download,
        ActivityOutcome.Success,
        "下载完成",
        $"nxm://stardewvalley/mods/1/files/2?key={secret}&expires=999；key={secret}；C:\\Users\\Example\\download.zip",
        $"https://github.com/example/mod/releases/download/v1/mod.zip?token={secret}",
        "1.0.0");
    var savedText = await File.ReadAllTextAsync(configPath);
    Assert(!savedText.Contains(secret, StringComparison.Ordinal), "Activity history persisted a secret token.");
    Assert(!savedText.Contains("C:\\Users\\Example", StringComparison.OrdinalIgnoreCase), "Activity history persisted a local absolute path.");
    var first = (await service.ListAsync()).Single();
    Assert(first.SourceUrl == "https://github.com/example/mod/releases/download/v1/mod.zip", "Activity source URL retained query credentials.");
    Assert(first.Detail.Contains("[已隐藏]", StringComparison.Ordinal), "Activity detail did not mark redacted content.");

    Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
    var oversizedEntries = Enumerable.Range(0, 205).Select(index => new
    {
        id = Guid.NewGuid(),
        timestamp = DateTimeOffset.UtcNow.AddMinutes(-index),
        kind = (int)ActivityKind.Install,
        outcome = (int)ActivityOutcome.Success,
        title = $"Entry {index}",
        detail = "bounded",
        sourceUrl = (string?)null,
        version = "1.0",
    }).ToArray();
    await File.WriteAllTextAsync(
        configPath,
        JsonSerializer.Serialize(new { version = 1, entries = oversizedEntries }));
    var capped = await new ActivityHistoryService(configPath).ListAsync();
    Assert(capped.Count == 200, "Activity history did not enforce the 200-entry cap while loading.");

    await service.ClearAsync();
    Assert((await service.ListAsync()).Count == 0, "Activity history clear did not persist.");
    Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(configPath)!, "*.tmp").Any(), "Activity history left temporary files behind.");
}

static async Task RunLiveChecksAsync()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var cancellationToken = timeout.Token;

    var release = await new SmapiService().GetLatestReleaseAsync(
        forceRefresh: true,
        cancellationToken);
    Assert(!string.IsNullOrWhiteSpace(release.Version), "Live SMAPI release version was empty.");
    Assert(
        release.Asset.Name.Equals($"SMAPI-{release.TagName}-installer.zip", StringComparison.Ordinal)
        || release.Asset.Name.Equals($"SMAPI-{release.Version}-installer.zip", StringComparison.Ordinal),
        $"Live SMAPI release selected an unexpected asset: {release.Asset.Name}");
    Console.WriteLine($"PASS live-smapi-release={release.Version} asset={release.Asset.Name}");

    var remoteMods = new RemoteModService();
    var browse = await remoteMods.BrowseAsync(RemoteSearchSource.All, cancellationToken);
    Assert(browse.Errors.Count == 0, $"Live Mod browsing failed: {string.Join(" | ", browse.Errors)}");
    Assert(browse.Mods.Count > 1, "Live Mod browsing returned fewer than two results.");
    Assert(browse.Mods.Any(mod => mod.Source == "Nexus Mods"), "Live browsing returned no Nexus Mods entries.");
    Assert(browse.Mods.Any(mod => mod.Source == "GitHub"), "Live browsing returned no GitHub entries.");
    Console.WriteLine($"PASS live-mod-browse={browse.Mods.Count} nexus+github");

    var search = await remoteMods.SearchAsync("Content Patcher", RemoteSearchSource.All, cancellationToken);
    Assert(search.Errors.Count == 0, $"Live Mod search failed: {string.Join(" | ", search.Errors)}");
    Assert(search.Mods.Count > 0, "Live Mod search returned no results.");
    Console.WriteLine($"PASS live-mod-search={search.Mods.Count}");

    var credentials = new CredentialService();
    var nexusKey = credentials.Read(CredentialService.NexusApiKeyTarget);
    if (string.IsNullOrWhiteSpace(nexusKey))
    {
        Console.WriteLine("SKIP live-nexus-saved-key (not configured)");
    }
    else
    {
        await new NexusDownloadService(credentials).ValidateApiKeyAsync(nexusKey, cancellationToken);
        Console.WriteLine("PASS live-nexus-saved-key");
    }

    var ai = new AiTranslationService(credentials);
    var aiStatus = await ai.GetStatusAsync(cancellationToken);
    if (!aiStatus.Configured || aiStatus.BaseUrl is null || aiStatus.ModelId is null)
    {
        Console.WriteLine("SKIP live-ai-model-test-translation (not configured)");
        return;
    }

    var models = await ai.ListModelsAsync(aiStatus.BaseUrl, null, cancellationToken);
    Assert(models.Models.Count > 0, "Configured AI service returned no models.");
    var test = await ai.TestConnectionAsync(
        aiStatus.BaseUrl,
        aiStatus.ModelId,
        null,
        cancellationToken);
    Assert(!string.IsNullOrWhiteSpace(test.Message), "Configured AI service returned an empty test reply.");
    var translation = await ai.TranslateAsync(
        "Lookup Anything",
        "View useful information about villagers, crops, and objects.",
        cancellationToken);
    Assert(
        !string.IsNullOrWhiteSpace(translation.Name) && !string.IsNullOrWhiteSpace(translation.Summary),
        "Configured AI service returned an empty translation.");
    Console.WriteLine($"PASS live-ai-models={models.Models.Count} test+translation");
}

static void SmokeCredentialManager()
{
    var target = $"valley-steward-smoke-{Guid.NewGuid():N}";
    var secret = $"smoke-{Guid.NewGuid():N}";
    var credentials = new CredentialService();
    try
    {
        Assert(credentials.Read(target) is null, "Temporary credential target unexpectedly existed.");
        credentials.Write(target, secret, "valley-steward-smoke");
        Assert(
            string.Equals(credentials.Read(target), secret, StringComparison.Ordinal),
            "Credential Manager roundtrip did not preserve the secret.");
        Assert(credentials.Delete(target), "Temporary credential was not deleted.");
        Assert(credentials.Read(target) is null, "Temporary credential remained after deletion.");
    }
    finally
    {
        credentials.Delete(target);
    }
}

static void SmokeAiRequestMetadata()
{
    var key = $"smoke-{Guid.NewGuid():N}";
    var service = new AiTranslationService(new CredentialService());
    var http = service.CreateModelsRequestPreview("http://127.0.0.1:11434/v1", key);
    var https = service.CreateTestRequestPreview("https://example.invalid/v1", "smoke-model", key);

    Assert(http.Endpoint == "http://127.0.0.1:11434/v1/models", "HTTP model endpoint was normalized incorrectly.");
    Assert(https.Endpoint == "https://example.invalid/v1/chat/completions", "HTTPS chat endpoint was normalized incorrectly.");
    Assert(!http.MaskedRequest.Contains(key, StringComparison.Ordinal), "Model request preview exposed the API key.");
    Assert(!https.MaskedRequest.Contains(key, StringComparison.Ordinal), "Chat request preview exposed the API key.");
    Assert(
        AiTranslationService.TranslationSystemPrompt.Contains("适配游戏 星露谷物语", StringComparison.Ordinal),
        "Translation prompt lost the required Stardew Valley context.");
}

static async Task SmokeAiProviderPipelineAsync(string root)
{
    var configPath = Path.Combine(root, "ai-provider", "ai-translation.json");
    var credentialTarget = $"valley-steward-ai-smoke-{Guid.NewGuid():N}";
    var syntheticKey = $"smoke-{Guid.NewGuid():N}";
    var credentials = new CredentialService();
    var handler = new AiTranslationStubHandler();
    using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var service = new AiTranslationService(
        credentials,
        client,
        configPath,
        credentialTarget,
        TimeSpan.FromSeconds(3),
        maximumConcurrentTranslations: 3);
    var activities = new ConcurrentQueue<AiTranslationRequestActivityEventArgs>();
    service.RequestActivity += (_, activity) => activities.Enqueue(activity);

    try
    {
        await service.SaveSettingsAsync(
            "https://code.summercn.cn/v1/",
            "smoke-model",
            syntheticKey);
        var preview = service.CreateTranslationRequestPreview(
            "https://code.summercn.cn/v1/",
            "smoke-model",
            "Example Mod",
            "Example description",
            syntheticKey);
        Assert(
            preview.Endpoint == "https://code.summercn.cn/v1/chat/completions",
            $"Translation preview targeted an unexpected endpoint: {preview.Endpoint}");
        Assert(
            !preview.MaskedRequest.Contains(syntheticKey, StringComparison.Ordinal),
            "Translation preview exposed the API key.");

        var models = await service.ListModelsAsync(
            "https://code.summercn.cn/v1/",
            null);
        Assert(
            models.Models.Select(model => model.Id).SequenceEqual(new[] { "smoke-model" }),
            "Compatible models response was not parsed.");
        var connection = await service.TestConnectionAsync(
            "https://code.summercn.cn/v1/",
            "smoke-model",
            null);
        Assert(connection.Message == "连接成功", "Compatible connection-test response was not parsed.");

        var batch = Enumerable.Range(1, 6)
            .Select(index => new AiTranslationBatchItem(
                $"mod-{index}",
                $"Example {index}",
                $"Description {index}"))
            .ToArray();
        var translated = await service.TranslateManyAsync(batch, maximumConcurrency: 6);
        Assert(translated.Count == batch.Length, "Parallel AI translation lost results.");
        Assert(
            translated.All(result => !string.IsNullOrWhiteSpace(result.Translation.Name)
                && !string.IsNullOrWhiteSpace(result.Translation.Summary)),
            "A compatible AI response produced an empty translation.");
        Assert(
            handler.MaximumObservedTranslationConcurrency is >= 2 and <= 3,
            $"Unexpected translation concurrency: {handler.MaximumObservedTranslationConcurrency}");
        Assert(
            handler.ChatRequestUris.All(uri =>
                uri.AbsoluteUri == "https://code.summercn.cn/v1/chat/completions"),
            "A translation request bypassed the saved Base URL.");
        Assert(handler.SawRequiredTranslationPrompt, "Translation request lost the Stardew Valley prompt.");
        Assert(handler.AllTranslationRequestsDisabledStreaming, "Translation request did not explicitly disable streaming.");
        Assert(
            activities.Any(activity => activity.Stage == AiTranslationRequestStage.Sending)
                && activities.Any(activity => activity.Stage == AiTranslationRequestStage.ResponseReceived)
                && activities.Any(activity => activity.Stage == AiTranslationRequestStage.Completed),
            "AI request activity events did not cover the request lifecycle.");
        Assert(
            activities.All(activity =>
                !activity.Request.MaskedRequest.Contains(syntheticKey, StringComparison.Ordinal)
                && !(activity.Detail?.Contains(syntheticKey, StringComparison.Ordinal) ?? false)),
            "AI request diagnostics exposed the API key.");

        handler.BlockResponseBody = true;
        var timeoutActivities = new ConcurrentQueue<AiTranslationRequestActivityEventArgs>();
        var timeoutService = new AiTranslationService(
            credentials,
            client,
            configPath,
            credentialTarget,
            TimeSpan.FromMilliseconds(250),
            maximumConcurrentTranslations: 1);
        timeoutService.RequestActivity += (_, activity) => timeoutActivities.Enqueue(activity);
        var timeoutWatch = Stopwatch.StartNew();
        await AssertThrowsAsync<TimeoutException>(() => timeoutService.TranslateAsync(
            "Never Ending Mod",
            "The response body never finishes."));
        Assert(timeoutWatch.Elapsed < TimeSpan.FromSeconds(2), "Full-body AI timeout did not stop promptly.");
        Assert(
            timeoutActivities.Any(activity => activity.Stage == AiTranslationRequestStage.TimedOut),
            "AI timeout activity was not published.");
    }
    finally
    {
        try
        {
            await service.ClearSettingsAsync();
        }
        finally
        {
            credentials.Delete(credentialTarget);
        }
    }
}

static void SmokeNexusSafety(string root)
{
    const string syntheticKey = "smoke-download+key/1234567890";
    var expires = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
    var nxm = $"nxm://stardewvalley/mods/123/files/456?key={Uri.EscapeDataString(syntheticKey)}&expires={expires}&user_id=789";
    var parsed = NexusDownloadService.ParseNxmLink(nxm);
    Assert(parsed.GameDomain == "stardewvalley", "NXM game domain was not preserved.");
    Assert(parsed.ModId == "123" && parsed.FileId == "456", "NXM IDs were parsed incorrectly.");
    Assert(parsed.Authorization.Key == syntheticKey, "NXM key was parsed incorrectly.");

    var downloadLink = NexusDownloadService.BuildDownloadLinkUri(
        parsed.ModId,
        parsed.FileId,
        parsed.Authorization);
    Assert(
        downloadLink.AbsolutePath == "/v1/games/stardewvalley/mods/123/files/456/download_link.json",
        "Nexus download-link path did not bind the game-scoped IDs.");
    Assert(downloadLink.Query.Contains("expires=", StringComparison.Ordinal), "NXM expiry was not forwarded.");
    Assert(!downloadLink.AbsoluteUri.Contains("key/", StringComparison.Ordinal), "NXM key was not query-encoded.");

    AssertThrows<ArgumentException>(() => NexusDownloadService.ParseNxmLink(
        $"nxm://skyrim/mods/123/files/456?key={syntheticKey}&expires={expires}"));
    AssertThrows<ArgumentException>(() => NexusDownloadService.ParseNxmLink(
        $"nxm://stardewvalley/mods/123/files/456?key=one&key=two&expires={expires}"));
    AssertThrows<NexusNxmLinkExpiredException>(() => NexusDownloadService.BuildDownloadLinkUri(
        "123",
        "456",
        new NexusDownloadAuthorization(syntheticKey, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds())));

    Assert(
        NexusDownloadService.IsTrustedDownloadUri(new Uri("https://cf-files.nexusmods.com/file.zip")),
        "Official Nexus download host was rejected.");
    Assert(
        NexusDownloadService.IsTrustedDownloadUri(new Uri("https://premium-files.nexus-cdn.com/file.zip")),
        "Official Nexus CDN host was rejected.");
    Assert(
        !NexusDownloadService.IsTrustedDownloadUri(new Uri("https://nexusmods.com.example.invalid/file.zip")),
        "Suffix-confusion download host was trusted.");
    Assert(
        !NexusDownloadService.IsTrustedDownloadUri(new Uri("http://files.nexusmods.com/file.zip")),
        "Non-HTTPS Nexus download URL was trusted.");

    var disposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"CON.zip\"" };
    var reserved = NexusDownloadService.GetSafeFileName(disposition, "123", "456");
    Assert(reserved == "_CON.zip", "Reserved Windows filename was not neutralized.");
    disposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "..%2F..%2Fpayload.zip" };
    var traversal = NexusDownloadService.GetSafeFileName(disposition, "123", "456");
    Assert(traversal == "payload.zip", "Download filename traversal was not reduced to a leaf name.");
    disposition = new ContentDispositionHeaderValue("attachment") { FileName = $"\"{new string('a', 220)}.zip\"" };
    Assert(
        NexusDownloadService.GetSafeFileName(disposition, "123", "456").Length <= 160,
        "Downloaded filename exceeded the bounded length.");

    var sidecarDirectory = Path.Combine(root, "nexus-downloads");
    var sidecar = NexusDownloadService.WriteTranslationSidecar(
        sidecarDirectory,
        "123",
        "456",
        new NexusDownloadTranslation("自动照料农场", "适配星露谷物语的中文简介。"));
    using var document = JsonDocument.Parse(File.ReadAllBytes(sidecar));
    Assert(document.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "Nexus sidecar schema changed.");
    Assert(document.RootElement.GetProperty("provider").GetString() == "Nexus Mods", "Nexus sidecar provider changed.");
    Assert(document.RootElement.GetProperty("modId").GetString() == "123", "Nexus sidecar Mod ID is wrong.");
    Assert(document.RootElement.GetProperty("fileId").GetString() == "456", "Nexus sidecar file ID is wrong.");
    Assert(
        document.RootElement.GetProperty("description").GetString() == "适配星露谷物语的中文简介。",
        "Nexus translated description was not persisted.");
    Assert(
        !File.ReadAllText(sidecar).Contains(syntheticKey, StringComparison.Ordinal),
        "Nexus sidecar exposed NXM authorization data.");
    Assert(
        !Directory.EnumerateFiles(sidecarDirectory, "*.tmp").Any(),
        "Nexus sidecar left a temporary file behind.");
}

static async Task SmokeGitHubDownloadSettingsAsync(string root)
{
    var configDirectory = Path.Combine(root, "github-settings");
    var service = new GitHubDownloadSettingsService(configDirectory);
    Assert(
        await service.LoadAsync() == GitHubDownloadSettings.Direct,
        "Missing GitHub settings did not default to direct mode.");

    var saved = await service.SaveAsync(new GitHubDownloadSettings(
        GitHubDownloadMode.Custom,
        $"  {GitHubDownloadSettingsService.GhProxyPreset}  "));
    Assert(saved.Mode == GitHubDownloadMode.Custom, "Custom GitHub mode was not saved.");
    Assert(
        saved.CustomPrefix == GitHubDownloadSettingsService.GhProxyPreset,
        "gh-proxy preset was not normalized.");

    var primary = Path.Combine(configDirectory, "github-download.json");
    var backup = Path.Combine(configDirectory, ".github-download.json.bak");
    var stored = await File.ReadAllTextAsync(primary);
    Assert(stored.Contains("\"mode\": \"custom\"", StringComparison.Ordinal), "Tauri-compatible mode was not written.");
    Assert(stored.Contains("\"customPrefix\"", StringComparison.Ordinal), "Tauri-compatible customPrefix was not written.");
    Assert(File.Exists(backup), "GitHub settings backup was not written.");

    await File.WriteAllTextAsync(primary, "not json");
    var recovered = await service.LoadAsync();
    Assert(recovered == saved, "Valid GitHub settings backup was not recovered.");
    Assert(
        (await File.ReadAllTextAsync(primary)).Contains("\"version\": 1", StringComparison.Ordinal),
        "Recovered GitHub settings were not restored atomically.");

    await AssertThrowsAsync<InvalidDataException>(() => service.SaveAsync(
        new GitHubDownloadSettings(GitHubDownloadMode.Custom, "http://gh-proxy.invalid/")));
    Assert(!Directory.EnumerateFiles(configDirectory, "*.tmp").Any(), "GitHub settings left a temporary file behind.");

    var cleared = await service.ClearAsync();
    Assert(cleared == GitHubDownloadSettings.Direct, "Clearing GitHub settings did not restore direct mode.");
}

#if ARCHIVE_SECURITY_SMOKE
static void SmokeSmapiArchiveSafety(string root)
{
    const string version = "4.5.2";
    var archiveDirectory = Path.Combine(root, "smapi-archives");
    Directory.CreateDirectory(archiveDirectory);

    var validArchive = Path.Combine(archiveDirectory, "valid.zip");
    WriteSmapiArchive(validArchive, version);
    var validDestination = Path.Combine(archiveDirectory, "valid-extracted");
    Directory.CreateDirectory(validDestination);
    var extracted = SmapiInstallerService.ExtractArchive(validArchive, validDestination, version);
    Assert(File.Exists(extracted.Executable), "Valid Windows SMAPI installer archive was not extracted.");
    Assert(
        extracted.Executable.EndsWith(
            Path.Combine("internal", "windows", "SMAPI.Installer.exe"),
            StringComparison.OrdinalIgnoreCase),
        "Windows SMAPI installer entry was not selected.");

    foreach (var unsafeEntry in new[]
    {
        "../outside.txt",
        $"SMAPI {version} installer/internal/windows/CON.txt",
        $"SMAPI {version} installer/internal/windows/LPT1.log",
    })
    {
        var archivePath = Path.Combine(archiveDirectory, $"unsafe-{Guid.NewGuid():N}.zip");
        WriteSmapiArchive(archivePath, version, unsafeEntry);
        var destination = Path.Combine(archiveDirectory, $"unsafe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        AssertThrows<InvalidDataException>(
            () => SmapiInstallerService.ExtractArchive(archivePath, destination, version));
    }
    Assert(!File.Exists(Path.Combine(archiveDirectory, "outside.txt")), "SMAPI ZIP escaped its extraction directory.");

    Assert(SmapiService.NormalizeVersion("4.5.2+commit") == "4.5.2", "SMAPI build metadata was not normalized.");
    Assert(SmapiService.NormalizeVersion("v4.5.2-beta.1+commit") == "4.5.2-beta.1", "SMAPI prerelease was lost.");
    Assert(SmapiService.NormalizeVersion("runtime 4.5.2") is null, "Embedded version text was accepted.");
    Assert(SmapiService.CompareVersions("4.5.2-beta.1", "4.5.2") < 0, "SMAPI prerelease ordering is wrong.");
    Assert(SmapiService.CompareVersions("4.6.0-beta.1", "4.5.2") > 0, "SMAPI numeric version ordering is wrong.");
}

static void SmokeSmapiInstallerLaunchInfo()
{
    var startInfo = SmapiInstallerService.CreateOfficialInstallerStartInfo(
        @"C:\installer\SMAPI.Installer.exe",
        @"C:\installer",
        @"D:\Games\Stardew Valley",
        SmapiInstallerAction.Uninstall);
    Assert(startInfo.UseShellExecute, "SMAPI installer lost the real console screen buffer required by Console.Clear.");
    Assert(startInfo.WindowStyle == ProcessWindowStyle.Hidden, "SMAPI installer console is not hidden.");
    Assert(!startInfo.RedirectStandardInput, "SMAPI installer input was redirected.");
    Assert(!startInfo.RedirectStandardOutput, "SMAPI installer output was redirected.");
    Assert(!startInfo.RedirectStandardError, "SMAPI installer errors were redirected.");
    Assert(
        startInfo.ArgumentList.SequenceEqual(new[]
        {
            "--uninstall",
            "--game-path",
            @"D:\Games\Stardew Valley",
            "--no-prompt",
        }),
        "SMAPI uninstall arguments changed.");
}

static void WriteSmapiArchive(string path, string version, string? unsafeEntry = null)
{
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    if (unsafeEntry is not null)
    {
        WriteArchiveEntry(archive, unsafeEntry, "unsafe");
    }

    var root = $"SMAPI {version} installer/internal/windows";
    WriteArchiveEntry(archive, $"{root}/SMAPI.Installer.exe", "executable");
    WriteArchiveEntry(archive, $"{root}/SMAPI.Installer.dll", "library");
    WriteArchiveEntry(archive, $"{root}/install.dat", "payload");
}
#endif

#if MOD_INSTALLER_SECURITY_SMOKE || ARCHIVE_SECURITY_SMOKE
static void WriteArchiveEntry(ZipArchive archive, string name, string content)
{
    var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
    using var writer = new StreamWriter(entry.Open());
    writer.Write(content);
}
#endif

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
}

static async Task AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
}

sealed class ModUpdateStubHandler : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly List<Uri> _requestUris = [];
    private readonly List<string> _nexusApiKeys = [];
    private int _activeRequests;
    private int _maximumObservedConcurrency;

    public IReadOnlyList<Uri> RequestUris
    {
        get
        {
            lock (_sync)
            {
                return _requestUris.ToArray();
            }
        }
    }

    public IReadOnlyList<string> NexusApiKeys
    {
        get
        {
            lock (_sync)
            {
                return _nexusApiKeys.ToArray();
            }
        }
    }

    public int MaximumObservedConcurrency => Volatile.Read(ref _maximumObservedConcurrency);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Stub request URI was missing.");
        lock (_sync)
        {
            _requestUris.Add(uri);
            if (uri.Host.Equals("api.nexusmods.com", StringComparison.OrdinalIgnoreCase)
                && request.Headers.TryGetValues("apikey", out var values))
            {
                _nexusApiKeys.AddRange(values);
            }
        }

        var active = Interlocked.Increment(ref _activeRequests);
        while (true)
        {
            var observed = Volatile.Read(ref _maximumObservedConcurrency);
            if (active <= observed
                || Interlocked.CompareExchange(ref _maximumObservedConcurrency, active, observed) == observed)
            {
                break;
            }
        }

        try
        {
            await Task.Delay(35, cancellationToken);
            var response = uri.Host.Equals("api.nexusmods.com", StringComparison.OrdinalIgnoreCase)
                ? CreateNexusResponse(uri)
                : uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                    ? CreateGitHubResponse(uri)
                    : new HttpResponseMessage(HttpStatusCode.BadGateway);
            response.RequestMessage = request;
            return response;
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    private static HttpResponseMessage CreateNexusResponse(Uri uri)
    {
        if (uri.AbsolutePath.Equals(
            "/v1/games/stardewvalley/mods/123.json",
            StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse(new
            {
                mod_id = 123,
                name = "Example Mod",
                version = "1.3.0",
            });
        }
        if (uri.AbsolutePath.Equals(
            "/v1/games/stardewvalley/mods/123/files.json",
            StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse(new
            {
                files = new object[]
                {
                    new
                    {
                        file_id = 455,
                        name = "Example-1.2.3.zip",
                        version = "1.2.3",
                        category_id = 4,
                        category_name = "OLD_VERSION",
                        is_primary = false,
                        uploaded_timestamp = 1_700_000_000,
                    },
                    new
                    {
                        file_id = 456,
                        name = "Example-1.3.0.zip",
                        version = "1.3.0",
                        category_id = 1,
                        category_name = "MAIN",
                        is_primary = true,
                        uploaded_timestamp = 1_800_000_000,
                    },
                },
            });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage CreateGitHubResponse(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 5
            || !segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase)
            || !segments[3].Equals("releases", StringComparison.OrdinalIgnoreCase)
            || !segments[4].Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var owner = segments[1];
        var repository = segments[2];
        if (owner.Equals("fail", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        var tag = owner.Equals("multi", StringComparison.OrdinalIgnoreCase)
            ? "v2.0.0"
            : owner.Equals("concurrency", StringComparison.OrdinalIgnoreCase)
                ? "v1.0.1"
                : "v1.2.3";
        var assets = owner.Equals("multi", StringComparison.OrdinalIgnoreCase)
            ? new object[]
            {
                CreateGitHubAsset(owner, repository, tag, $"{repository}-windows.zip"),
                CreateGitHubAsset(owner, repository, tag, $"{repository}-portable.zip"),
            }
            : new object[]
            {
                CreateGitHubAsset(owner, repository, tag, $"{repository}-{tag}.zip"),
            };
        return JsonResponse(new
        {
            tag_name = tag,
            draft = false,
            prerelease = false,
            assets,
        });
    }

    private static object CreateGitHubAsset(
        string owner,
        string repository,
        string tag,
        string name)
    {
        return new
        {
            name,
            state = "uploaded",
            size = 12_345,
            digest = $"sha256:{new string('a', 64)}",
            browser_download_url = $"https://github.com/{owner}/{repository}/releases/download/{tag}/{name}",
        };
    }

    private static HttpResponseMessage JsonResponse(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json"),
        };
    }
}

sealed class AiTranslationStubHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Uri> _chatRequestUris = new();
    private int _activeTranslations;
    private int _maximumObservedTranslationConcurrency;
    private int _translationResponseIndex;
    private int _sawRequiredTranslationPrompt;
    private int _allTranslationRequestsDisabledStreaming = 1;

    public bool BlockResponseBody { get; set; }
    public IReadOnlyList<Uri> ChatRequestUris => _chatRequestUris.ToArray();
    public int MaximumObservedTranslationConcurrency =>
        Volatile.Read(ref _maximumObservedTranslationConcurrency);
    public bool SawRequiredTranslationPrompt => Volatile.Read(ref _sawRequiredTranslationPrompt) != 0;
    public bool AllTranslationRequestsDisabledStreaming =>
        Volatile.Read(ref _allTranslationRequestsDisabledStreaming) != 0;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("AI stub request URI was missing.");
        if (uri.AbsolutePath.Equals("/v1/models", StringComparison.Ordinal))
        {
            return JsonResponse(new
            {
                models = new[] { new { name = "smoke-model", owner = "smoke" } },
            }, request);
        }
        if (!uri.AbsolutePath.Equals("/v1/chat/completions", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        }

        _chatRequestUris.Enqueue(uri);
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var isTranslation = root.GetProperty("messages")
            .EnumerateArray()
            .Any(message => message.GetProperty("role").GetString() == "system");
        if (!isTranslation)
        {
            return JsonResponse(new
            {
                choices = new[] { new { message = new { content = "连接成功" } } },
            }, request);
        }

        if (root.TryGetProperty("stream", out var stream)
            && stream.ValueKind == JsonValueKind.False)
        {
            // Expected OpenAI-compatible non-streaming request.
        }
        else
        {
            Interlocked.Exchange(ref _allTranslationRequestsDisabledStreaming, 0);
        }
        var systemPrompt = root.GetProperty("messages")[0].GetProperty("content").GetString();
        if (systemPrompt?.Contains("适配游戏 星露谷物语", StringComparison.Ordinal) == true)
        {
            Interlocked.Exchange(ref _sawRequiredTranslationPrompt, 1);
        }

        if (BlockResponseBody)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new NeverEndingReadStream()),
            };
        }

        var active = Interlocked.Increment(ref _activeTranslations);
        UpdateMaximumConcurrency(active);
        try
        {
            await Task.Delay(50, cancellationToken);
            var index = Interlocked.Increment(ref _translationResponseIndex);
            var translation = JsonSerializer.Serialize(new
            {
                name = $"译名 {index}",
                summary = $"译文 {index}",
            });
            object payload = (index % 6) switch
            {
                0 => new
                {
                    choices = new[]
                    {
                        new { message = new { content = $"```json\n{translation}\n```" } },
                    },
                },
                1 => new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                content = new[] { new { type = "output_text", text = translation } },
                            },
                        },
                    },
                },
                2 => new
                {
                    choices = new[]
                    {
                        new
                        {
                            text = JsonSerializer.Serialize(new
                            {
                                name = $"译名 {index}",
                                description = $"译文 {index}",
                            }),
                        },
                    },
                },
                3 => new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                content = (string?)null,
                                tool_calls = new[]
                                {
                                    new { function = new { arguments = translation } },
                                },
                            },
                        },
                    },
                },
                4 => new { output_text = translation },
                _ => new
                {
                    choices = new[]
                    {
                        new { message = new { content = $"翻译如下：\n{translation}" } },
                    },
                },
            };
            return JsonResponse(payload, request);
        }
        finally
        {
            Interlocked.Decrement(ref _activeTranslations);
        }
    }

    private void UpdateMaximumConcurrency(int active)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _maximumObservedTranslationConcurrency);
            if (active <= observed
                || Interlocked.CompareExchange(
                    ref _maximumObservedTranslationConcurrency,
                    active,
                    observed) == observed)
            {
                return;
            }
        }
    }

    private static HttpResponseMessage JsonResponse(object value, HttpRequestMessage request)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json"),
        };
    }
}

sealed class NeverEndingReadStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) => WaitForCancellationAsync(cancellationToken);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        new(WaitForCancellationAsync(cancellationToken));

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
}
