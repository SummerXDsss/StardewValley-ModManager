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
pub fn get_dashboard(app: tauri::AppHandle) -> Dashboard {
    let saved = app
        .path()
        .app_config_dir()
        .ok()
        .and_then(|config_dir| game::load_saved_game(&config_dir).ok().flatten());
    match saved.or_else(game::detect_game) {
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
pub fn scan_game_path(app: tauri::AppHandle, game_path: String) -> Result<Dashboard, String> {
    let config_dir = app
        .path()
        .app_config_dir()
        .map_err(|error| format!("无法确定应用配置目录：{error}"))?;
    let installation = game::inspect_and_save_game(&config_dir, Path::new(&game_path))?;
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
pub async fn get_latest_smapi_release() -> Result<providers::smapi::SmapiReleaseInfo, String> {
    providers::smapi::latest_release().await
}

#[tauri::command]
pub async fn download_latest_smapi_installer(
    app: tauri::AppHandle,
) -> Result<providers::smapi::DownloadedSmapiInstaller, String> {
    let cache_dir = app
        .path()
        .app_cache_dir()
        .map_err(|error| format!("无法确定应用缓存目录：{error}"))?;
    providers::smapi::download_latest_installer(&cache_dir).await
}

#[tauri::command]
pub async fn install_latest_smapi(
    app: tauri::AppHandle,
    manager: tauri::State<'_, GameProcessManager>,
    game_path: String,
) -> Result<providers::smapi::InstallSmapiResult, String> {
    let installation = game::inspect_game(Path::new(&game_path))?;
    let canonical_game_path = std::path::PathBuf::from(&installation.path);
    let manager = manager.inner().clone();
    manager.run_while_stopped(|| Ok(()))?;

    let cache_dir = app
        .path()
        .app_cache_dir()
        .map_err(|error| format!("Could not determine the app cache directory: {error}"))?;
    let downloaded = providers::smapi::download_latest_installer(&cache_dir).await?;

    tauri::async_runtime::spawn_blocking(move || {
        manager.run_while_stopped(|| {
            let before_install = game::inspect_game(&canonical_game_path)?;
            if std::path::Path::new(&before_install.path) != canonical_game_path {
                return Err(
                    "The game directory changed while preparing the SMAPI installer".into(),
                );
            }

            let execution = providers::smapi::install_downloaded_installer(
                &cache_dir,
                &downloaded,
                &canonical_game_path,
            )?;

            let after_install = game::inspect_game(&canonical_game_path)?;
            if std::path::Path::new(&after_install.path) != canonical_game_path {
                return Err("The game directory changed while installing SMAPI".into());
            }
            let status = game::inspect_smapi(&canonical_game_path);
            let version = verify_installed_smapi_version(
                &status,
                &execution.release_version,
                execution.exit_code,
            )?;
            Ok(providers::smapi::InstallSmapiResult {
                version: version.clone(),
                platform: execution.platform,
                installer_path: execution.installer_path,
                exit_code: execution.exit_code,
                installed_version: Some(version),
                game_path: after_install.path,
                message: "SMAPI installation verified successfully".into(),
            })
        })
    })
    .await
    .map_err(|error| format!("SMAPI installer task failed: {error}"))?
}

fn verify_installed_smapi_version(
    status: &SmapiStatus,
    expected_version: &str,
    exit_code: i32,
) -> Result<String, String> {
    if !status.installed {
        return Err(format!(
            "SMAPI installer exited with code {exit_code}, but no SMAPI executable was found"
        ));
    }
    match status.version.as_deref() {
        Some(version) if version == expected_version => Ok(version.to_string()),
        Some(version) => Err(format!(
            "SMAPI installer exited with code {exit_code}, but version {version} was detected instead of {expected_version}"
        )),
        None => Err(format!(
            "SMAPI installer exited with code {exit_code}, but the installed SMAPI version could not be verified"
        )),
    }
}

#[tauri::command]
pub fn get_ai_translation_settings(
    app: tauri::AppHandle,
) -> Result<providers::translation::AiTranslationStatus, String> {
    let config_dir = app
        .path()
        .app_config_dir()
        .map_err(|error| format!("无法确定应用配置目录：{error}"))?;
    providers::translation::get_status(&config_dir)
}

#[tauri::command]
pub fn save_ai_translation_settings(
    app: tauri::AppHandle,
    request: providers::translation::SaveAiTranslationSettingsRequest,
) -> Result<providers::translation::AiTranslationStatus, String> {
    let config_dir = app
        .path()
        .app_config_dir()
        .map_err(|error| format!("无法确定应用配置目录：{error}"))?;
    providers::translation::save_settings(&config_dir, request)
}

#[tauri::command]
pub fn clear_ai_translation_settings(
    app: tauri::AppHandle,
) -> Result<providers::translation::AiTranslationStatus, String> {
    let config_dir = app
        .path()
        .app_config_dir()
        .map_err(|error| format!("无法确定应用配置目录：{error}"))?;
    providers::translation::clear_settings(&config_dir)
}

#[tauri::command]
pub async fn translate_mod(
    app: tauri::AppHandle,
    request: providers::translation::TranslateModRequest,
) -> Result<providers::translation::TranslateModResult, String> {
    let config_dir = app
        .path()
        .app_config_dir()
        .map_err(|error| format!("无法确定应用配置目录：{error}"))?;
    providers::translation::translate_mod(&config_dir, request).await
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn smapi_install_verification_requires_the_expected_version() {
        let verified = SmapiStatus {
            installed: true,
            version: Some("4.5.2".into()),
            executable: Some("StardewModdingAPI.exe".into()),
        };
        assert_eq!(
            verify_installed_smapi_version(&verified, "4.5.2", 0).unwrap(),
            "4.5.2"
        );

        let missing_version = SmapiStatus {
            version: None,
            ..verified.clone()
        };
        assert!(verify_installed_smapi_version(&missing_version, "4.5.2", 0).is_err());

        let wrong_version = SmapiStatus {
            version: Some("4.4.0".into()),
            ..verified.clone()
        };
        assert!(verify_installed_smapi_version(&wrong_version, "4.5.2", 0).is_err());

        let missing_executable = SmapiStatus {
            installed: false,
            ..verified
        };
        assert!(verify_installed_smapi_version(&missing_executable, "4.5.2", 0).is_err());
    }
}
