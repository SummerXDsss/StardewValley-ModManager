using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed class ModUpdateBatchService
{
    private const int MaximumErrorCharacters = 1_200;

    public IReadOnlyList<ModUpdateBatchCandidate> CreateCandidates(
        IEnumerable<InstalledMod> mods)
    {
        return CreateSelections(mods)
            .Where(item => item.Candidate is not null)
            .Select(item => item.Candidate!)
            .ToArray();
    }

    public IReadOnlyList<ModUpdateBatchSelectionItem> CreateSelections(
        IEnumerable<InstalledMod> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        var snapshot = mods.ToArray();
        if (snapshot.Any(mod => mod is null))
        {
            throw new ArgumentException("Mod 列表不能包含 null。", nameof(mods));
        }

        var duplicateIds = snapshot
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selections = new List<ModUpdateBatchSelectionItem>();
        foreach (var mod in snapshot)
        {
            if (mod.UpdateResult is not { Status: ModUpdateCheckStatus.UpdateAvailable } result)
            {
                continue;
            }
            var descriptor = result.Download;
            var provider = descriptor?.Provider
                ?? result.Sources.FirstOrDefault(source =>
                    source.UpdateAvailable
                    && ModUpdateService.CompareVersions(source.LatestVersion, result.LatestVersion) == 0)
                    ?.Provider
                ?? ModUpdateProvider.Unknown;
            var reason = GetCannotUpdateReason(mod, result, descriptor, duplicateIds);
            ModUpdateBatchCandidate? candidate = null;
            if (reason is null && descriptor is not null)
            {
                candidate = new ModUpdateBatchCandidate(
                    mod.Id,
                    mod.Name,
                    mod.Version,
                    descriptor);
            }
            selections.Add(new ModUpdateBatchSelectionItem(
                mod.Id,
                mod.Name,
                mod.Version,
                result.LatestVersion ?? descriptor?.ExpectedVersion,
                provider,
                candidate,
                reason));
        }
        return selections;
    }

    public async Task<ModUpdateBatchResult> RunAsync(
        IEnumerable<ModUpdateBatchCandidate> candidates,
        Func<ModUpdateBatchCandidate, CancellationToken, Task> updateAsync,
        IProgress<ModUpdateBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(updateAsync);
        var queue = candidates.ToArray();
        if (queue.Any(candidate => candidate is null))
        {
            throw new ArgumentException("更新队列不能包含 null。", nameof(candidates));
        }
        if (queue.GroupBy(candidate => candidate.ModId, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException("更新队列不能包含重复的 Mod UniqueID。", nameof(candidates));
        }

        var results = new List<ModUpdateBatchItemResult>(queue.Length);
        var succeeded = 0;
        var failed = 0;
        foreach (var candidate in queue)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ModUpdateBatchResult(results, WasCancelled: true);
            }

            progress?.Report(new ModUpdateBatchProgress(
                results.Count,
                queue.Length,
                succeeded,
                failed,
                candidate,
                ItemCompleted: false));
            try
            {
                await updateAsync(candidate, cancellationToken);
                results.Add(new ModUpdateBatchItemResult(
                    candidate,
                    ModUpdateBatchItemOutcome.Success,
                    null));
                succeeded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ModUpdateBatchResult(results, WasCancelled: true);
            }
            catch (Exception error)
            {
                results.Add(new ModUpdateBatchItemResult(
                    candidate,
                    ModUpdateBatchItemOutcome.Failed,
                    NormalizeError(error.Message)));
                failed++;
            }

            progress?.Report(new ModUpdateBatchProgress(
                results.Count,
                queue.Length,
                succeeded,
                failed,
                candidate,
                ItemCompleted: true));
        }

        return new ModUpdateBatchResult(results, WasCancelled: false);
    }

    private static string NormalizeError(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "更新失败，但未返回错误详情。"
            : new string(value
                .Select(character => char.IsControl(character)
                    && character is not '\r' and not '\n' and not '\t'
                        ? ' '
                        : character)
                .ToArray()).Trim();
        return normalized.Length <= MaximumErrorCharacters
            ? normalized
            : normalized[..MaximumErrorCharacters];
    }

    private static string? GetCannotUpdateReason(
        InstalledMod mod,
        InstalledModUpdateResult result,
        ModUpdateDownloadDescriptor? descriptor,
        IReadOnlySet<string> duplicateIds)
    {
        if (mod.Health != "healthy")
        {
            return "本地 Mod 状态异常，不能自动更新。";
        }
        if (duplicateIds.Contains(mod.Id))
        {
            return "本机存在重复的 Mod UniqueID，无法确定唯一更新目标。";
        }
        if (!result.ModId.Equals(mod.Id, StringComparison.OrdinalIgnoreCase))
        {
            return "检查结果与当前 Mod 的 UniqueID 不一致。";
        }
        if (ModUpdateService.CompareVersions(result.InstalledVersion, mod.Version) != 0)
        {
            return "批量检查后本地版本已经变化，请重新检查。";
        }
        if (!result.CanAutoUpdate || descriptor is null)
        {
            return result.CannotUpdateReason ?? "没有可安全绑定到目标版本的下载文件。";
        }
        if (ModUpdateService.CompareVersions(result.LatestVersion, descriptor.ExpectedVersion) != 0)
        {
            return "下载文件版本与检查得到的最新版本不一致。";
        }
        if (ModUpdateService.CompareVersions(descriptor.ExpectedVersion, mod.Version) is not > 0)
        {
            return "目标版本不高于当前版本，不能自动更新。";
        }
        return null;
    }
}
