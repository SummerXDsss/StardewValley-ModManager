use std::{
    process::{Child, ExitStatus},
    sync::{Arc, Mutex, MutexGuard, Weak},
    thread,
    time::{Duration, Instant},
};

#[cfg(windows)]
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};

use chrono::{SecondsFormat, Utc};

use crate::models::{GameProcessState, GameProcessStatus, LaunchRequest};

use super::game;

const MONITOR_INTERVAL: Duration = Duration::from_millis(750);
#[cfg(unix)]
const GRACEFUL_STOP_TIMEOUT: Duration = Duration::from_secs(3);
const FORCE_STOP_TIMEOUT: Duration = Duration::from_secs(3);

struct TrackedProcess {
    child: Child,
    leader_exit: Option<ExitStatus>,
    request: LaunchRequest,
    started_at: String,
    #[cfg(unix)]
    process_group: i32,
    #[cfg(windows)]
    job: OwnedHandle,
}

struct Runtime {
    active: Option<TrackedProcess>,
    last_request: Option<LaunchRequest>,
    status: GameProcessStatus,
}

impl Default for Runtime {
    fn default() -> Self {
        Self {
            active: None,
            last_request: None,
            status: GameProcessStatus {
                state: GameProcessState::Stopped,
                running: false,
                pid: None,
                target: None,
                started_at: None,
                exit_code: None,
            },
        }
    }
}

#[derive(Clone)]
pub struct GameProcessManager {
    inner: Arc<Mutex<Runtime>>,
}

impl GameProcessManager {
    pub fn new() -> Self {
        let inner = Arc::new(Mutex::new(Runtime::default()));
        start_monitor(Arc::downgrade(&inner));
        Self { inner }
    }

    pub fn launch(&self, request: LaunchRequest) -> Result<GameProcessStatus, String> {
        let mut runtime = self.lock()?;
        refresh(&mut runtime)?;
        launch_locked(&mut runtime, request)
    }

    pub fn status(&self) -> Result<GameProcessStatus, String> {
        let mut runtime = self.lock()?;
        refresh(&mut runtime)?;
        Ok(runtime.status.clone())
    }

    pub fn stop(&self) -> Result<GameProcessStatus, String> {
        let mut runtime = self.lock()?;
        refresh(&mut runtime)?;
        stop_locked(&mut runtime)
    }

    pub fn restart(&self) -> Result<GameProcessStatus, String> {
        let mut runtime = self.lock()?;
        refresh(&mut runtime)?;
        let request = runtime
            .last_request
            .clone()
            .ok_or_else(|| "没有可用于重启的上次启动配置".to_string())?;

        if runtime.active.is_some() {
            stop_locked(&mut runtime)?;
        }
        launch_locked(&mut runtime, request)
    }

    pub fn run_while_stopped<T>(
        &self,
        operation: impl FnOnce() -> Result<T, String>,
    ) -> Result<T, String> {
        let mut runtime = self.lock()?;
        refresh(&mut runtime)?;
        if runtime.active.is_some() {
            return Err("游戏运行期间不能修改 Mod 文件，请先关闭游戏".into());
        }
        operation()
    }

    fn lock(&self) -> Result<MutexGuard<'_, Runtime>, String> {
        self.inner
            .lock()
            .map_err(|_| "游戏进程状态锁已损坏".to_string())
    }
}

fn start_monitor(runtime: Weak<Mutex<Runtime>>) {
    thread::Builder::new()
        .name("game-process-monitor".into())
        .spawn(move || loop {
            thread::sleep(MONITOR_INTERVAL);
            let Some(runtime) = runtime.upgrade() else {
                break;
            };
            if let Ok(mut runtime) = runtime.lock() {
                let _ = refresh(&mut runtime);
            };
        })
        .expect("failed to start game process monitor");
}

fn launch_locked(
    runtime: &mut Runtime,
    request: LaunchRequest,
) -> Result<GameProcessStatus, String> {
    if runtime.active.is_some() {
        return Err("游戏已经在运行，请先关闭或使用重启功能".into());
    }

    let mut child = game::spawn_managed(&request)?;
    let pid = child.id();
    let target = request.target;
    let started_at = Utc::now().to_rfc3339_opts(SecondsFormat::Secs, true);

    #[cfg(unix)]
    let process_group = match i32::try_from(pid) {
        Ok(process_group) => process_group,
        Err(_) => {
            let _ = child.kill();
            let _ = child.wait();
            return Err("游戏进程 PID 超出平台支持范围".into());
        }
    };

    #[cfg(windows)]
    let job = match create_job_for_child(&child) {
        Ok(job) => job,
        Err(error) => {
            let _ = child.kill();
            let _ = child.wait();
            return Err(error);
        }
    };
    #[cfg(windows)]
    if let Err(error) = resume_child(&child) {
        let _ = child.kill();
        let _ = child.wait();
        return Err(error);
    }

    runtime.last_request = Some(request.clone());
    runtime.active = Some(TrackedProcess {
        child,
        leader_exit: None,
        request,
        started_at: started_at.clone(),
        #[cfg(unix)]
        process_group,
        #[cfg(windows)]
        job,
    });
    runtime.status = GameProcessStatus {
        state: GameProcessState::Running,
        running: true,
        pid: Some(pid),
        target: Some(target),
        started_at: Some(started_at),
        exit_code: None,
    };
    Ok(runtime.status.clone())
}

fn refresh(runtime: &mut Runtime) -> Result<(), String> {
    let completed = match runtime.active.as_mut() {
        Some(tracked) => {
            update_leader_exit(tracked)?;
            !process_tree_running(tracked)?
        }
        None => false,
    };

    if completed {
        let tracked = runtime
            .active
            .take()
            .expect("active process disappeared while refreshing");
        runtime.status = completed_status(&tracked, GameProcessState::Exited);
    }
    Ok(())
}

fn update_leader_exit(tracked: &mut TrackedProcess) -> Result<(), String> {
    if tracked.leader_exit.is_none() {
        tracked.leader_exit = tracked
            .child
            .try_wait()
            .map_err(|error| format!("无法读取游戏进程状态：{error}"))?;
    }
    Ok(())
}

fn stop_locked(runtime: &mut Runtime) -> Result<GameProcessStatus, String> {
    let Some(mut tracked) = runtime.active.take() else {
        return Ok(runtime.status.clone());
    };

    match terminate_process_tree(&mut tracked) {
        Ok(()) => {
            runtime.status = completed_status(&tracked, GameProcessState::Stopped);
            Ok(runtime.status.clone())
        }
        Err(error) => {
            runtime.active = Some(tracked);
            Err(error)
        }
    }
}

fn completed_status(tracked: &TrackedProcess, state: GameProcessState) -> GameProcessStatus {
    GameProcessStatus {
        state,
        running: false,
        pid: Some(tracked.child.id()),
        target: Some(tracked.request.target),
        started_at: Some(tracked.started_at.clone()),
        exit_code: tracked.leader_exit.as_ref().and_then(ExitStatus::code),
    }
}

#[cfg(windows)]
fn create_job_for_child(child: &Child) -> Result<OwnedHandle, String> {
    use std::ptr;
    use windows_sys::Win32::System::JobObjects::{AssignProcessToJobObject, CreateJobObjectW};

    let raw_job = unsafe { CreateJobObjectW(ptr::null(), ptr::null()) };
    if raw_job.is_null() {
        return Err(format!(
            "无法创建游戏进程作业：{}",
            std::io::Error::last_os_error()
        ));
    }
    let job = unsafe { OwnedHandle::from_raw_handle(raw_job) };
    let assigned = unsafe { AssignProcessToJobObject(job.as_raw_handle(), child.as_raw_handle()) };
    if assigned == 0 {
        return Err(format!(
            "无法将游戏进程加入受管作业：{}",
            std::io::Error::last_os_error()
        ));
    }
    Ok(job)
}

#[cfg(windows)]
fn resume_child(child: &Child) -> Result<(), String> {
    use std::mem::size_of;
    use windows_sys::Win32::{
        Foundation::INVALID_HANDLE_VALUE,
        System::{
            Diagnostics::ToolHelp::{
                CreateToolhelp32Snapshot, Thread32First, Thread32Next, TH32CS_SNAPTHREAD,
                THREADENTRY32,
            },
            Threading::{OpenThread, ResumeThread, THREAD_SUSPEND_RESUME},
        },
    };

    let raw_snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0) };
    if raw_snapshot == INVALID_HANDLE_VALUE {
        return Err(format!(
            "无法枚举游戏进程线程：{}",
            std::io::Error::last_os_error()
        ));
    }
    let snapshot = unsafe { OwnedHandle::from_raw_handle(raw_snapshot) };
    let mut entry = THREADENTRY32 {
        dwSize: size_of::<THREADENTRY32>() as u32,
        ..Default::default()
    };
    let mut has_entry = unsafe { Thread32First(snapshot.as_raw_handle(), &mut entry) };
    while has_entry != 0 {
        if entry.th32OwnerProcessID == child.id() {
            let raw_thread = unsafe { OpenThread(THREAD_SUSPEND_RESUME, 0, entry.th32ThreadID) };
            if raw_thread.is_null() {
                return Err(format!(
                    "无法打开游戏主线程：{}",
                    std::io::Error::last_os_error()
                ));
            }
            let thread = unsafe { OwnedHandle::from_raw_handle(raw_thread) };
            let previous_count = unsafe { ResumeThread(thread.as_raw_handle()) };
            if previous_count == u32::MAX {
                return Err(format!(
                    "无法恢复游戏主线程：{}",
                    std::io::Error::last_os_error()
                ));
            }
            return Ok(());
        }
        entry.dwSize = size_of::<THREADENTRY32>() as u32;
        has_entry = unsafe { Thread32Next(snapshot.as_raw_handle(), &mut entry) };
    }
    Err("未找到已暂停的游戏主线程".into())
}

#[cfg(windows)]
fn process_tree_running(tracked: &TrackedProcess) -> Result<bool, String> {
    use std::{mem::size_of, ptr};
    use windows_sys::Win32::System::JobObjects::{
        JobObjectBasicAccountingInformation, QueryInformationJobObject,
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION,
    };

    let mut information = JOBOBJECT_BASIC_ACCOUNTING_INFORMATION::default();
    let queried = unsafe {
        QueryInformationJobObject(
            tracked.job.as_raw_handle(),
            JobObjectBasicAccountingInformation,
            (&mut information as *mut JOBOBJECT_BASIC_ACCOUNTING_INFORMATION).cast(),
            size_of::<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>() as u32,
            ptr::null_mut(),
        )
    };
    if queried == 0 {
        return Err(format!(
            "无法读取游戏进程作业状态：{}",
            std::io::Error::last_os_error()
        ));
    }
    Ok(information.ActiveProcesses > 0)
}

#[cfg(windows)]
fn terminate_process_tree(tracked: &mut TrackedProcess) -> Result<(), String> {
    use windows_sys::Win32::System::JobObjects::TerminateJobObject;

    update_leader_exit(tracked)?;
    if !process_tree_running(tracked)? {
        return Ok(());
    }

    let terminated = unsafe { TerminateJobObject(tracked.job.as_raw_handle(), 1) };
    if terminated == 0 && process_tree_running(tracked)? {
        return Err(format!(
            "无法终止游戏进程作业：{}",
            std::io::Error::last_os_error()
        ));
    }
    if !wait_for_windows_tree(tracked, FORCE_STOP_TIMEOUT)? {
        return Err("终止命令已发送，但游戏进程树仍在运行".into());
    }
    Ok(())
}

#[cfg(windows)]
fn wait_for_windows_tree(tracked: &mut TrackedProcess, timeout: Duration) -> Result<bool, String> {
    let deadline = Instant::now() + timeout;
    loop {
        update_leader_exit(tracked)?;
        if !process_tree_running(tracked)? {
            return Ok(true);
        }
        if Instant::now() >= deadline {
            return Ok(false);
        }
        thread::sleep(Duration::from_millis(100));
    }
}

#[cfg(unix)]
fn process_tree_running(tracked: &TrackedProcess) -> Result<bool, String> {
    let result = unsafe { libc::kill(-tracked.process_group, 0) };
    if result == 0 {
        return Ok(true);
    }

    let error = std::io::Error::last_os_error();
    match error.raw_os_error() {
        Some(libc::ESRCH) => Ok(false),
        Some(libc::EPERM) => Ok(true),
        _ => Err(format!("无法读取游戏进程组状态：{error}")),
    }
}

#[cfg(unix)]
fn terminate_process_tree(tracked: &mut TrackedProcess) -> Result<(), String> {
    update_leader_exit(tracked)?;
    if !process_tree_running(tracked)? {
        return Ok(());
    }

    signal_process_group(tracked.process_group, libc::SIGTERM)?;
    if !wait_for_unix_tree(tracked, GRACEFUL_STOP_TIMEOUT)? {
        signal_process_group(tracked.process_group, libc::SIGKILL)?;
        if !wait_for_unix_tree(tracked, FORCE_STOP_TIMEOUT)? {
            return Err("终止命令已发送，但游戏进程组仍在运行".into());
        }
    }

    update_leader_exit(tracked)?;
    if tracked.leader_exit.is_none() {
        tracked.leader_exit = Some(
            tracked
                .child
                .wait()
                .map_err(|error| format!("无法回收游戏进程：{error}"))?,
        );
    }
    Ok(())
}

#[cfg(unix)]
fn signal_process_group(process_group: i32, signal: i32) -> Result<(), String> {
    let result = unsafe { libc::kill(-process_group, signal) };
    if result == 0 {
        return Ok(());
    }

    let error = std::io::Error::last_os_error();
    if error.raw_os_error() == Some(libc::ESRCH) {
        Ok(())
    } else {
        Err(format!("无法向游戏进程组发送终止信号：{error}"))
    }
}

#[cfg(unix)]
fn wait_for_unix_tree(tracked: &mut TrackedProcess, timeout: Duration) -> Result<bool, String> {
    let deadline = Instant::now() + timeout;
    loop {
        update_leader_exit(tracked)?;
        if !process_tree_running(tracked)? {
            return Ok(true);
        }
        if Instant::now() >= deadline {
            return Ok(false);
        }
        thread::sleep(Duration::from_millis(100));
    }
}

#[cfg(test)]
mod tests {
    use std::{fs, path::PathBuf, time::SystemTime};

    use super::*;
    use crate::models::LaunchTarget;

    struct TestGameDir(PathBuf);

    struct TestManager(GameProcessManager);

    impl Drop for TestGameDir {
        fn drop(&mut self) {
            for attempt in 0..10 {
                match fs::remove_dir_all(&self.0) {
                    Ok(()) => return,
                    Err(error) if error.kind() == std::io::ErrorKind::NotFound => return,
                    Err(_) if attempt < 9 => thread::sleep(Duration::from_millis(50)),
                    Err(_) => return,
                }
            }
        }
    }

    impl Drop for TestManager {
        fn drop(&mut self) {
            let _ = self.0.stop();
        }
    }

    #[test]
    fn launches_stops_and_restarts_the_tracked_process() {
        let test_dir = create_test_game();
        let manager = TestManager(GameProcessManager::new());
        let request = LaunchRequest {
            game_path: test_dir.0.to_string_lossy().into_owned(),
            target: LaunchTarget::Vanilla,
            mods_path: None,
            arguments: long_running_arguments(),
        };

        let first = manager
            .0
            .launch(request)
            .expect("test process should launch");
        assert!(first.running);
        assert!(matches!(first.state, GameProcessState::Running));

        let stopped = manager.0.stop().expect("test process should stop");
        assert!(!stopped.running);
        assert!(matches!(stopped.state, GameProcessState::Stopped));

        let restarted = manager.0.restart().expect("test process should restart");
        assert!(restarted.running);
        assert!(matches!(restarted.state, GameProcessState::Running));
        assert_ne!(first.pid, restarted.pid);

        manager
            .0
            .stop()
            .expect("restarted test process should stop");
    }

    #[test]
    fn keeps_tracking_the_tree_after_the_launcher_exits() {
        let test_dir = create_wrapper_game();
        let manager = TestManager(GameProcessManager::new());
        let request = LaunchRequest {
            game_path: test_dir.0.to_string_lossy().into_owned(),
            target: LaunchTarget::Vanilla,
            mods_path: None,
            arguments: wrapper_arguments(),
        };
        manager.0.launch(request).expect("wrapper should launch");

        let deadline = Instant::now() + Duration::from_secs(3);
        loop {
            let status = manager
                .0
                .status()
                .expect("wrapper status should be readable");
            let leader_exited = manager
                .0
                .lock()
                .expect("manager lock should be available")
                .active
                .as_ref()
                .is_some_and(|tracked| tracked.leader_exit.is_some());
            if leader_exited {
                assert!(
                    status.running,
                    "the descendant should keep the tree running"
                );
                break;
            }
            assert!(Instant::now() < deadline, "wrapper leader did not exit");
            thread::sleep(Duration::from_millis(50));
        }

        manager.0.stop().expect("wrapper descendants should stop");
    }

    fn create_test_game() -> TestGameDir {
        #[cfg(windows)]
        let source = PathBuf::from(std::env::var("WINDIR").expect("WINDIR should be set"))
            .join("System32")
            .join("ping.exe");
        #[cfg(unix)]
        let source = PathBuf::from("/bin/sleep");
        create_game_from(source)
    }

    fn create_wrapper_game() -> TestGameDir {
        #[cfg(windows)]
        let source = PathBuf::from(std::env::var("WINDIR").expect("WINDIR should be set"))
            .join("System32")
            .join("cmd.exe");
        #[cfg(unix)]
        let source = PathBuf::from("/bin/sh");
        create_game_from(source)
    }

    fn create_game_from(source: PathBuf) -> TestGameDir {
        let unique = SystemTime::now()
            .duration_since(SystemTime::UNIX_EPOCH)
            .expect("system clock should be valid")
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "valley-steward-process-test-{}-{unique}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("test game directory should be created");

        #[cfg(windows)]
        let executable = "StardewValley.exe";
        #[cfg(unix)]
        let executable = "StardewValley";

        fs::copy(source, path.join(executable)).expect("test executable should be copied");
        TestGameDir(path)
    }

    fn long_running_arguments() -> Vec<String> {
        #[cfg(windows)]
        return vec!["127.0.0.1".into(), "-n".into(), "30".into()];
        #[cfg(unix)]
        return vec!["30".into()];
    }

    fn wrapper_arguments() -> Vec<String> {
        #[cfg(windows)]
        return vec![
            "/D".into(),
            "/C".into(),
            "start".into(),
            "".into(),
            "/B".into(),
            "ping".into(),
            "127.0.0.1".into(),
            "-n".into(),
            "30".into(),
        ];
        #[cfg(unix)]
        return vec!["-c".into(), "sleep 30 &".into()];
    }
}
