mod commands;
mod models;
mod providers;
mod services;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(services::game_process::GameProcessManager::new())
        .invoke_handler(tauri::generate_handler![
            commands::get_dashboard,
            commands::scan_game_path,
            commands::set_mod_enabled,
            commands::remove_mod,
            commands::open_mod_folder,
            commands::open_smapi_download,
            commands::discover_mods,
            commands::open_remote_url,
            commands::get_nexus_auth_status,
            commands::set_nexus_api_key,
            commands::clear_nexus_api_key,
            commands::get_nexus_mod_details,
            commands::download_nexus_file,
            commands::launch_game,
            commands::get_game_process_status,
            commands::stop_game,
            commands::restart_game,
        ])
        .run(tauri::generate_context!())
        .expect("failed to run Valley Steward");
}
