use std::{
    path::{Path, PathBuf},
    process::{Child, Command},
};

#[cfg(unix)]
use std::os::unix::process::CommandExt;
#[cfg(windows)]
use std::os::windows::process::CommandExt;

use crate::models::{GameInstallation, LaunchRequest, LaunchTarget, SmapiStatus};

pub fn detect_game() -> Option<GameInstallation> {
    candidate_paths()
        .into_iter()
        .find_map(|path| inspect_game(&path).ok())
}

pub fn inspect_game(path: &Path) -> Result<GameInstallation, String> {
    if !path.is_dir() {
        return Err("游戏目录不存在或不是文件夹".into());
    }

    let canonical = path
        .canonicalize()
        .map_err(|error| format!("无法读取游戏目录：{error}"))?;
    let executable = game_executable(&canonical)
        .ok_or_else(|| "目录中未找到 Stardew Valley 可执行文件".to_string())?;

    Ok(GameInstallation {
        path: canonical.to_string_lossy().into_owned(),
        executable: executable
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .into_owned(),
        store: infer_store(&canonical),
        version: None,
    })
}

pub fn inspect_smapi(path: &Path) -> SmapiStatus {
    let executable = smapi_executable(path);
    SmapiStatus {
        installed: executable.is_some(),
        version: None,
        executable: executable.and_then(|item| {
            item.file_name()
                .map(|name| name.to_string_lossy().into_owned())
        }),
    }
}

// On Windows the returned child is suspended until GameProcessManager assigns its Job Object.
pub(crate) fn spawn_managed(request: &LaunchRequest) -> Result<Child, String> {
    let installation = inspect_game(Path::new(&request.game_path))?;
    let game_path = PathBuf::from(&installation.path);
    let executable = match request.target {
        LaunchTarget::Smapi => smapi_executable(&game_path)
            .ok_or_else(|| "未检测到 SMAPI，请先安装后再启动".to_string())?,
        LaunchTarget::Vanilla => {
            game_executable(&game_path).ok_or_else(|| "未检测到游戏可执行文件".to_string())?
        }
    };

    let mut command = Command::new(executable);
    command.current_dir(&game_path);

    // The isolated process group is the safety boundary used for Unix shutdowns.
    #[cfg(unix)]
    command.process_group(0);
    // Assign the suspended process to its private Job Object before any child can escape it.
    #[cfg(windows)]
    command.creation_flags(0x0000_0004);

    if let Some(mods_path) = &request.mods_path {
        if !matches!(request.target, LaunchTarget::Smapi) {
            return Err("只有 SMAPI 启动方式支持指定 Mods 目录".into());
        }
        let requested = game_path.join(mods_path);
        let canonical = requested
            .canonicalize()
            .map_err(|_| "指定的 Mods 目录不存在".to_string())?;
        if !canonical.starts_with(&game_path) {
            return Err("Mods 目录必须位于游戏目录中".into());
        }
        #[cfg(target_os = "linux")]
        command.env("SMAPI_MODS_PATH", canonical);
        #[cfg(not(target_os = "linux"))]
        command.arg("--mods-path").arg(canonical);
    }

    if request.arguments.len() > 32 || request.arguments.iter().any(|arg| arg.len() > 512) {
        return Err("启动参数过多或过长".into());
    }
    command.args(&request.arguments);
    command
        .spawn()
        .map_err(|error| format!("启动失败：{error}"))
}

pub fn open_smapi_download() -> Result<(), String> {
    #[cfg(target_os = "windows")]
    let mut command = {
        let mut command = Command::new("explorer.exe");
        command.arg("https://smapi.io/");
        command
    };
    #[cfg(target_os = "macos")]
    let mut command = {
        let mut command = Command::new("open");
        command.arg("https://smapi.io/");
        command
    };
    #[cfg(target_os = "linux")]
    let mut command = {
        let mut command = Command::new("xdg-open");
        command.arg("https://smapi.io/");
        command
    };
    command
        .spawn()
        .map_err(|error| format!("无法打开 SMAPI 官方下载页：{error}"))?;
    Ok(())
}

fn game_executable(path: &Path) -> Option<PathBuf> {
    ["Stardew Valley.exe", "StardewValley.exe", "StardewValley"]
        .iter()
        .map(|name| path.join(name))
        .find(|candidate| candidate.is_file())
}

fn smapi_executable(path: &Path) -> Option<PathBuf> {
    [
        "StardewModdingAPI.exe",
        "StardewModdingAPI",
        "StardewModdingAPI.bin.osx",
    ]
    .iter()
    .map(|name| path.join(name))
    .find(|candidate| candidate.is_file())
}

fn infer_store(path: &Path) -> String {
    let text = path.to_string_lossy().to_lowercase();
    if text.contains("steamapps") {
        "Steam"
    } else if text.contains("gog") {
        "GOG"
    } else if text.contains("modifiablewindowsapps") || text.contains("xboxgames") {
        "Xbox App"
    } else {
        "手动安装"
    }
    .to_string()
}

fn candidate_paths() -> Vec<PathBuf> {
    let mut paths = Vec::new();
    #[cfg(target_os = "windows")]
    {
        paths.extend([
            PathBuf::from(r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"),
            PathBuf::from(r"C:\Program Files\Steam\steamapps\common\Stardew Valley"),
            PathBuf::from(r"C:\GOG Games\Stardew Valley"),
            PathBuf::from(r"C:\Program Files (x86)\GOG Galaxy\Games\Stardew Valley"),
            PathBuf::from(r"C:\Program Files\ModifiableWindowsApps\Stardew Valley"),
        ]);
    }
    #[cfg(target_os = "linux")]
    if let Some(home) = dirs::home_dir() {
        paths.push(home.join(".local/share/Steam/steamapps/common/Stardew Valley"));
        paths.push(home.join(".steam/steam/steamapps/common/Stardew Valley"));
        paths.push(home.join(".steam/debian-installation/steamapps/common/Stardew Valley"));
        paths.push(home.join(
            ".var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Stardew Valley",
        ));
        paths.push(
            home.join("snap/steam/common/.local/share/Steam/steamapps/common/Stardew Valley"),
        );
        paths.push(home.join("GOGGames/StardewValley/game"));
    }
    #[cfg(target_os = "macos")]
    {
        paths.push(PathBuf::from(
            "/Applications/Stardew Valley.app/Contents/MacOS",
        ));
        if let Some(home) = dirs::home_dir() {
            paths.push(home.join("Applications/Stardew Valley.app/Contents/MacOS"));
            paths.push(home.join(
                "Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS",
            ));
            paths.push(home.join(
                "Library/Application Support/Steam/SteamApps/common/Stardew Valley/Contents/MacOS",
            ));
        }
    }
    paths
}
