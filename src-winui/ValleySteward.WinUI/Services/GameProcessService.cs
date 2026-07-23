using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ValleySteward.WinUI.Models;

namespace ValleySteward.WinUI.Services;

public sealed class GameProcessService
{
    private readonly object _gate = new();
    private Process? _activeProcess;
    private SafeFileHandle? _activeJob;
    private bool _stopping;
    private LaunchRequest? _lastRequest;
    private GameProcessStatus _status = GameProcessStatus.Stopped;

    public GameProcessStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public Task<GameProcessStatus> LaunchAsync(
        GameInstallation installation,
        LaunchTarget target,
        IReadOnlyList<string> arguments,
        string? modsPath = null)
    {
        return Task.Run(() =>
        {
            lock (_gate)
            {
                RefreshTrackedProcess();
                if (_status.Running)
                {
                    throw new InvalidOperationException("游戏已经在运行。");
                }

                var executable = target == LaunchTarget.Smapi
                    ? Path.Combine(installation.Path, "StardewModdingAPI.exe")
                    : Path.Combine(installation.Path, installation.Executable);
                if (!File.Exists(executable))
                {
                    throw new FileNotFoundException(target == LaunchTarget.Smapi ? "未检测到 SMAPI。" : "未检测到游戏可执行文件。", executable);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = installation.Path,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                if (!string.IsNullOrWhiteSpace(modsPath))
                {
                    if (target != LaunchTarget.Smapi)
                    {
                        throw new InvalidOperationException("只有 SMAPI 启动方式支持指定 Mods 目录。");
                    }

                    var requestedMods = Path.GetFullPath(Path.Combine(installation.Path, modsPath));
                    var gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.Path)) + Path.DirectorySeparatorChar;
                    if (!Directory.Exists(requestedMods)
                        || !requestedMods.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("指定的 Mods 目录不存在或不在游戏目录内。");
                    }
                    startInfo.ArgumentList.Add("--mods-path");
                    startInfo.ArgumentList.Add(requestedMods);
                }

                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    throw new InvalidOperationException("游戏进程没有成功启动。");
                }

                SafeFileHandle? job = null;
                try
                {
                    job = CreatePrivateJob();
                    if (!AssignProcessToJobObject(job, process.SafeHandle))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将游戏进程加入私有 Job Object。");
                    }
                }
                catch
                {
                    job?.Dispose();
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(3000);
                    }
                    catch
                    {
                        // Preserve the original job-assignment error.
                    }
                    process.Dispose();
                    throw;
                }

                process.Exited += (_, _) => MarkExited(process);
                _activeProcess = process;
                _activeJob = job;
                _lastRequest = new LaunchRequest(installation, target, arguments.ToArray(), modsPath);
                _status = new GameProcessStatus(
                    GameProcessState.Running,
                    true,
                    process.Id,
                    target,
                    DateTimeOffset.Now,
                    null);
                return _status;
            }
        });
    }

    public Task<GameProcessStatus> RefreshAsync(GameInstallation? installation)
    {
        return Task.Run(() =>
        {
            lock (_gate)
            {
                RefreshTrackedProcess();
                if (!_status.Running && installation is not null)
                {
                    DetectExternalProcess(installation);
                }
                return _status;
            }
        });
    }

    public async Task<GameProcessStatus> StopAsync()
    {
        Process? process;
        SafeFileHandle? job;
        lock (_gate)
        {
            RefreshTrackedProcess();
            process = _activeProcess;
            job = _activeJob;
            if (process is null || !_status.Running)
            {
                return _status;
            }
            _stopping = true;
        }

        try
        {
            if (job is not null && !job.IsInvalid && !job.IsClosed)
            {
                if (!TerminateJobObject(job, 1) && JobHasActiveProcesses(job))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法关闭游戏进程树。");
                }
                await WaitForJobToStopAsync(job);
            }
            else
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch
        {
            lock (_gate)
            {
                _stopping = false;
            }
            throw;
        }

        var exitCode = TryGetExitCode(process);
        lock (_gate)
        {
            _status = _status with
            {
                State = GameProcessState.Stopped,
                Running = false,
                ExitCode = exitCode,
            };
            CleanupTrackedProcess();
            _stopping = false;
            return _status;
        }
    }

    public async Task<GameProcessStatus> RestartAsync()
    {
        LaunchRequest request;
        lock (_gate)
        {
            request = _lastRequest ?? throw new InvalidOperationException("没有可用于重启的上次启动配置。");
        }

        await StopAsync();
        return await LaunchAsync(request.Installation, request.Target, request.Arguments, request.ModsPath);
    }

    private void DetectExternalProcess(GameInstallation installation)
    {
        foreach (var name in new[] { "Stardew Valley", "StardewValley", "StardewModdingAPI" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (executable is null
                        || !Path.GetDirectoryName(executable)!.Equals(installation.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Dispose();
                        continue;
                    }

                    _activeProcess = process;
                    _activeJob = null;
                    _status = new GameProcessStatus(
                        GameProcessState.Running,
                        true,
                        process.Id,
                        name.Equals("StardewModdingAPI", StringComparison.OrdinalIgnoreCase) ? LaunchTarget.Smapi : LaunchTarget.Vanilla,
                        TryGetStartTime(process),
                        null);
                    return;
                }
                catch
                {
                    process.Dispose();
                }
            }
        }
    }

    private void RefreshTrackedProcess()
    {
        if (_activeProcess is null)
        {
            return;
        }

        try
        {
            var treeRunning = _activeJob is not null
                ? JobHasActiveProcesses(_activeJob)
                : !_activeProcess.HasExited;
            if (treeRunning)
            {
                return;
            }

            _status = _status with
            {
                State = GameProcessState.Exited,
                Running = false,
                ExitCode = TryGetExitCode(_activeProcess),
            };
        }
        catch
        {
            _status = GameProcessStatus.Stopped;
        }
        finally
        {
            if (!_status.Running)
            {
                CleanupTrackedProcess();
            }
        }
    }

    private void MarkExited(Process process)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeProcess, process))
            {
                return;
            }
            if (_stopping)
            {
                return;
            }
            try
            {
                if (_activeJob is not null && JobHasActiveProcesses(_activeJob))
                {
                    return;
                }
                _status = _status with
                {
                    State = GameProcessState.Exited,
                    Running = false,
                    ExitCode = TryGetExitCode(process),
                };
            }
            catch
            {
                _status = GameProcessStatus.Stopped;
            }
            CleanupTrackedProcess();
        }
    }

    private void CleanupTrackedProcess()
    {
        _activeJob?.Dispose();
        _activeJob = null;
        _activeProcess?.Dispose();
        _activeProcess = null;
    }

    private static SafeFileHandle CreatePrivateJob()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建游戏进程 Job Object。");
        }
        return new SafeFileHandle(handle, ownsHandle: true);
    }

    private static bool JobHasActiveProcesses(SafeFileHandle job)
    {
        if (!QueryInformationJobObject(
                job,
                JobObjectBasicAccountingInformation,
                out var information,
                (uint)Marshal.SizeOf<JobObjectBasicAccounting>(),
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取游戏 Job Object 状态。");
        }
        return information.ActiveProcesses > 0;
    }

    private static async Task WaitForJobToStopAsync(SafeFileHandle job)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (JobHasActiveProcesses(job))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("关闭命令已发送，但游戏进程树仍在运行。");
            }
            await Task.Delay(100);
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryGetStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return null;
        }
    }

    private sealed record LaunchRequest(
        GameInstallation Installation,
        LaunchTarget Target,
        IReadOnlyList<string> Arguments,
        string? ModsPath);

    private const int JobObjectBasicAccountingInformation = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        out JobObjectBasicAccounting information,
        uint informationLength,
        IntPtr returnLength);
}
