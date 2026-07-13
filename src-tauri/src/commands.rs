use std::path::Path;

use tauri::Manager;

use crate::{
    models::{
        Dashboard, DownloadedModFile, GameProcessStatus, LaunchRequest, NexusAuthStatus,
        NexusModDetails, RemoteMod, SmapiStatus,
    },
    providers,
    services::{game, game_process::GameProcessManager, mods},
};

#[tauri::command]
pub fn get_dashboard() -> Dashboard {
    match game::detect_game() {
        Some(installation) => dashboard_for(&installation.path),
        None => Dashboard {
            installation: None,
            smapi: SmapiStatus {
                installed: false,
                version: None,
                executable: None,
            },
            mods: Vec::new(),
            warnings: vec!["未自动找到游戏，请手动设置安装目录".into()],
        },
    }
}

#[tauri::command]
pub fn scan_game_path(game_path: String) -> Result<Dashboard, String> {
    let installation = game::inspect_game(Path::new(&game_path))?;
    Ok(dashboard_for(&installation.path))
}

#[tauri::command]
pub fn set_mod_enabled(
    manager: tauri::State<'_, GameProcessManager>,
    game_path: String,
    mod_path: String,
    enabled: bool,
) -> Result<(), String> {
    manager.run_while_stopped(|| {
        mods::set_enabled(Path::new(&game_path), Path::new(&mod_path), enabled)
    })
}

#[tauri::command]
pub fn remove_mod(
    manager: tauri::State<'_, GameProcessManager>,
    game_path: String,
    mod_path: String,
) -> Result<(), String> {
    manager.run_while_stopped(|| mods::move_to_trash(Path::new(&game_path), Path::new(&mod_path)))
}

#[tauri::command]
pub fn open_mod_folder(game_path: String, mod_path: String) -> Result<(), String> {
    mods::open_folder(Path::new(&game_path), Path::new(&mod_path))
}

#[tauri::command]
pub fn open_smapi_download() -> Result<(), String> {
    game::open_smapi_download()
}

#[tauri::command]
pub async fn discover_mods() -> Result<Vec<RemoteMod>, String> {
    providers::discover().await
}

#[tauri::command]
pub fn open_remote_url(url: String) -> Result<(), String> {
    providers::open_remote_url(&url)
}

#[tauri::command]
pub fn get_nexus_auth_status() -> NexusAuthStatus {
    providers::nexus::auth_status()
}

#[tauri::command]
pub async fn set_nexus_api_key(api_key: String) -> Result<NexusAuthStatus, String> {
    providers::nexus::set_api_key(api_key).await
}

#[tauri::command]
pub fn clear_nexus_api_key() -> Result<NexusAuthStatus, String> {
    providers::nexus::clear_api_key()
}

#[tauri::command]
pub async fn get_nexus_mod_details(game_scoped_id: String) -> Result<NexusModDetails, String> {
    providers::nexus::mod_details(&game_scoped_id).await
}

#[tauri::command]
pub async fn download_nexus_file(
    app: tauri::AppHandle,
    mod_id: String,
    file_id: String,
) -> Result<DownloadedModFile, String> {
    let cache_dir = app
        .path()
        .app_cache_dir()
        .map_err(|error| format!("无法确定应用缓存目录：{error}"))?;
    providers::nexus::download_file(&cache_dir, &mod_id, &file_id).await
}

#[tauri::command]
pub fn launch_game(
    manager: tauri::State<'_, GameProcessManager>,
    request: LaunchRequest,
) -> Result<GameProcessStatus, String> {
    manager.launch(request)
}

#[tauri::command]
pub async fn get_game_process_status(
    manager: tauri::State<'_, GameProcessManager>,
) -> Result<GameProcessStatus, String> {
    let manager = manager.inner().clone();
    tauri::async_runtime::spawn_blocking(move || manager.status())
        .await
        .map_err(|error| format!("读取游戏状态任务失败：{error}"))?
}

#[tauri::command]
pub async fn stop_game(
    manager: tauri::State<'_, GameProcessManager>,
) -> Result<GameProcessStatus, String> {
    let manager = manager.inner().clone();
    tauri::async_runtime::spawn_blocking(move || manager.stop())
        .await
        .map_err(|error| format!("关闭游戏任务失败：{error}"))?
}

#[tauri::command]
pub async fn restart_game(
    manager: tauri::State<'_, GameProcessManager>,
) -> Result<GameProcessStatus, String> {
    let manager = manager.inner().clone();
    tauri::async_runtime::spawn_blocking(move || manager.restart())
        .await
        .map_err(|error| format!("重启游戏任务失败：{error}"))?
}

fn dashboard_for(game_path: &str) -> Dashboard {
    let path = Path::new(game_path);
    let installation = game::inspect_game(path).ok();
    let smapi = game::inspect_smapi(path);
    let installed_mods = mods::scan(path);
    let mut warnings = Vec::new();
    if !smapi.installed {
        warnings.push("未检测到 SMAPI".into());
    }
    let invalid = installed_mods
        .iter()
        .filter(|item| item.health == "error")
        .count();
    if invalid > 0 {
        warnings.push(format!("{invalid} 个 Mod 的 manifest.json 无法解析"));
    }
    Dashboard {
        installation,
        smapi,
        mods: installed_mods,
        warnings,
    }
}
