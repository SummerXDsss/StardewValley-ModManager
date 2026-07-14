use serde::{Deserialize, Serialize};
use std::{
    collections::HashSet,
    ffi::OsStr,
    fs,
    io::Read,
    path::{Path, PathBuf},
    process::{Child, Command},
};
use sysinfo::{ProcessRefreshKind, RefreshKind, System};
use walkdir::WalkDir;

#[cfg(unix)]
use std::os::unix::process::CommandExt;
#[cfg(windows)]
use std::os::windows::process::CommandExt;
#[cfg(windows)]
use winreg::{
    enums::{HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_32KEY, KEY_WOW64_64KEY},
    RegKey,
};

use crate::models::{GameInstallation, LaunchRequest, LaunchTarget, SmapiStatus};

const STEAM_APP_ID: &str = "413150";
const MAX_KEYVALUES_FILE_SIZE: u64 = 1024 * 1024;
const MAX_SMAPI_BUNDLED_MANIFEST_SIZE: usize = 256 * 1024;
const GAME_PATH_CONFIG_FILE: &str = "game-path.json";
const GAME_PATH_CONFIG_VERSION: u8 = 1;
const MAX_GAME_PATH_CONFIG_SIZE: u64 = 16 * 1024;
const GAME_EXECUTABLE_NAMES: [&str; 3] =
    ["Stardew Valley.exe", "StardewValley.exe", "StardewValley"];
const SMAPI_EXECUTABLE_NAMES: [&str; 3] = [
    "StardewModdingAPI.exe",
    "StardewModdingAPI",
    "StardewModdingAPI.bin.osx",
];

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StoredGamePath {
    version: u8,
    path: String,
}

#[derive(Clone, Copy)]
#[allow(dead_code)] // Each variant is used by its corresponding desktop target.
enum SteamLibraryPathStyle {
    Windows,
    Unix,
}

#[derive(Clone, Copy)]
#[allow(dead_code)] // The macOS layout is only constructed when compiling for macOS.
enum SteamGameLayout {
    Standard,
    MacosBundle,
}

pub fn detect_game() -> Option<GameInstallation> {
    candidate_paths()
        .into_iter()
        .find_map(|path| inspect_game(&path).ok())
}

pub fn load_saved_game(config_dir: &Path) -> Result<Option<GameInstallation>, String> {
    let config_path = config_dir.join(GAME_PATH_CONFIG_FILE);
    let metadata = match fs::metadata(&config_path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(format!("无法读取已保存的游戏路径：{error}")),
    };
    if metadata.len() > MAX_GAME_PATH_CONFIG_SIZE {
        return Err("已保存的游戏路径配置文件过大".into());
    }

    let bytes =
        fs::read(&config_path).map_err(|error| format!("无法读取已保存的游戏路径：{error}"))?;
    let stored: StoredGamePath = serde_json::from_slice(&bytes)
        .map_err(|error| format!("已保存的游戏路径配置无效：{error}"))?;
    if stored.version != GAME_PATH_CONFIG_VERSION {
        return Err(format!("不支持的游戏路径配置版本：{}", stored.version));
    }

    inspect_game(Path::new(&stored.path)).map(Some)
}

pub fn inspect_and_save_game(
    config_dir: &Path,
    game_path: &Path,
) -> Result<GameInstallation, String> {
    let installation = inspect_game(game_path)?;
    let stored = StoredGamePath {
        version: GAME_PATH_CONFIG_VERSION,
        path: installation.path.clone(),
    };
    let bytes = serde_json::to_vec_pretty(&stored)
        .map_err(|error| format!("无法序列化游戏路径配置：{error}"))?;
    fs::create_dir_all(config_dir).map_err(|error| format!("无法创建应用配置目录：{error}"))?;
    fs::write(config_dir.join(GAME_PATH_CONFIG_FILE), bytes)
        .map_err(|error| format!("无法保存游戏路径：{error}"))?;
    Ok(installation)
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
    let version = executable.as_ref().and_then(|_| smapi_version(path));
    SmapiStatus {
        installed: executable.is_some(),
        version,
        executable: executable.and_then(|item| {
            item.file_name()
                .map(|name| name.to_string_lossy().into_owned())
        }),
    }
}

pub fn steam_running() -> bool {
    System::new_with_specifics(RefreshKind::nothing().with_processes(ProcessRefreshKind::nothing()))
        .processes()
        .values()
        .any(|process| is_steam_process_name(process.name()))
}

fn is_steam_process_name(name: &OsStr) -> bool {
    let name = name.to_string_lossy().to_ascii_lowercase();
    matches!(name.as_str(), "steam" | "steam.exe" | "steam_osx")
}

pub(crate) fn ensure_game_processes_stopped(game_path: &Path) -> Result<(), String> {
    let installation = inspect_game(game_path)?;
    let canonical_game_path = PathBuf::from(installation.path);
    let candidates = GAME_EXECUTABLE_NAMES
        .iter()
        .chain(SMAPI_EXECUTABLE_NAMES.iter())
        .map(|name| canonical_game_path.join(name))
        .filter(|path| path.is_file())
        .map(|path| {
            path.canonicalize()
                .map_err(|error| format!("无法确认游戏可执行文件 {}：{error}", path.display()))
        })
        .collect::<Result<Vec<_>, _>>()?;

    let system = System::new_all();
    for process in system.processes().values() {
        if let Some(executable) = process.exe() {
            if let Ok(executable) = executable.canonicalize() {
                if candidates
                    .iter()
                    .any(|candidate| executable_paths_equal(candidate, &executable))
                {
                    return Err("检测到所选目录中的游戏正在运行，请先关闭游戏再继续".into());
                }
                continue;
            }
        }

        if candidates.iter().any(|candidate| {
            candidate
                .file_name()
                .is_some_and(|name| executable_names_equal(name, process.name()))
        }) {
            return Err(
                "检测到同名游戏进程，但无法确认其路径；为保护游戏文件，请先关闭该进程再继续".into(),
            );
        }
    }
    Ok(())
}

#[cfg(any(target_os = "windows", target_os = "macos"))]
fn executable_paths_equal(left: &Path, right: &Path) -> bool {
    left.to_string_lossy()
        .eq_ignore_ascii_case(&right.to_string_lossy())
}

#[cfg(not(any(target_os = "windows", target_os = "macos")))]
fn executable_paths_equal(left: &Path, right: &Path) -> bool {
    left == right
}

#[cfg(any(target_os = "windows", target_os = "macos"))]
fn executable_names_equal(left: &OsStr, right: &OsStr) -> bool {
    left.to_string_lossy()
        .eq_ignore_ascii_case(&right.to_string_lossy())
}

#[cfg(not(any(target_os = "windows", target_os = "macos")))]
fn executable_names_equal(left: &OsStr, right: &OsStr) -> bool {
    left == right
}

fn smapi_version(game_path: &Path) -> Option<String> {
    smapi_dll_version(&game_path.join("StardewModdingAPI.dll"))
        .or_else(|| smapi_bundled_mod_version(&game_path.join("Mods")))
}

fn smapi_dll_version(path: &Path) -> Option<String> {
    let bytes = fs::read(path).ok()?;
    let image = pelite::PeFile::from_bytes(&bytes).ok()?;
    let version_info = image.resources().ok()?.version_info().ok()?;

    for language in version_info.translation().iter().copied() {
        for key in ["ProductVersion", "FileVersion"] {
            if let Some(version) = version_info
                .value(language, key)
                .and_then(|value| normalize_smapi_version(&value))
            {
                return Some(version);
            }
        }
    }

    let fixed = version_info.fixed()?;
    normalize_smapi_version(&format!(
        "{}.{}.{}.{}",
        fixed.dwProductVersion.Major,
        fixed.dwProductVersion.Minor,
        fixed.dwProductVersion.Patch,
        fixed.dwProductVersion.Build,
    ))
}

#[derive(Deserialize)]
struct SmapiBundledManifest {
    #[serde(rename = "UniqueID")]
    unique_id: String,
    #[serde(rename = "Version")]
    version: String,
}

fn smapi_bundled_mod_version(mods_path: &Path) -> Option<String> {
    if !mods_path.is_dir() {
        return None;
    }
    let mut versions = HashSet::new();
    for entry in WalkDir::new(mods_path)
        .max_depth(5)
        .follow_links(false)
        .into_iter()
        .filter_entry(|entry| entry.file_name() != OsStr::new(".mod-manager-trash"))
        .filter_map(Result::ok)
        .filter(|entry| entry.file_type().is_file() && entry.file_name() == "manifest.json")
    {
        let Some(content) = read_utf8_file_limited(entry.path(), MAX_SMAPI_BUNDLED_MANIFEST_SIZE)
        else {
            continue;
        };
        let Ok(manifest) = serde_json::from_str::<SmapiBundledManifest>(&content) else {
            continue;
        };
        if matches!(
            manifest.unique_id.as_str(),
            "SMAPI.SaveBackup" | "SMAPI.ConsoleCommands"
        ) {
            if let Some(version) = normalize_smapi_version(&manifest.version) {
                versions.insert(version);
            }
        }
    }
    (versions.len() == 1)
        .then(|| versions.into_iter().next())
        .flatten()
}

fn read_utf8_file_limited(path: &Path, max_bytes: usize) -> Option<String> {
    let file = fs::File::open(path).ok()?;
    if file.metadata().ok()?.len() > max_bytes as u64 {
        return None;
    }

    let mut bytes = Vec::new();
    file.take((max_bytes as u64).checked_add(1)?)
        .read_to_end(&mut bytes)
        .ok()?;
    if bytes.len() > max_bytes {
        return None;
    }
    String::from_utf8(bytes).ok()
}

fn normalize_smapi_version(value: &str) -> Option<String> {
    let value = value.trim().trim_start_matches(['v', 'V']);
    let without_build = value.split('+').next()?.trim();
    if let Ok(version) = semver::Version::parse(without_build) {
        return Some(version.to_string());
    }

    let mut parts = without_build.split('.').collect::<Vec<_>>();
    if parts.len() == 4 && parts[3] == "0" {
        parts.pop();
        let normalized = parts.join(".");
        if let Ok(version) = semver::Version::parse(&normalized) {
            return Some(version.to_string());
        }
    }
    None
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
    GAME_EXECUTABLE_NAMES
        .iter()
        .map(|name| path.join(name))
        .find(|candidate| candidate.is_file())
}

fn smapi_executable(path: &Path) -> Option<PathBuf> {
    SMAPI_EXECUTABLE_NAMES
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
        paths.extend(windows_registry_game_paths());
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
        let steam_roots = [
            home.join(".local/share/Steam"),
            home.join(".steam/steam"),
            home.join(".steam/debian-installation"),
            home.join(".var/app/com.valvesoftware.Steam/.local/share/Steam"),
            home.join("snap/steam/common/.local/share/Steam"),
        ];
        for steam_root in steam_roots {
            paths.extend(steam_game_paths(
                &steam_root,
                SteamLibraryPathStyle::Unix,
                SteamGameLayout::Standard,
            ));
        }
        paths.push(home.join("GOGGames/StardewValley/game"));
    }
    #[cfg(target_os = "macos")]
    {
        paths.push(PathBuf::from(
            "/Applications/Stardew Valley.app/Contents/MacOS",
        ));
        if let Some(home) = dirs::home_dir() {
            paths.push(home.join("Applications/Stardew Valley.app/Contents/MacOS"));
            paths.extend(steam_game_paths(
                &home.join("Library/Application Support/Steam"),
                SteamLibraryPathStyle::Unix,
                SteamGameLayout::MacosBundle,
            ));
        }
    }
    deduplicate_paths(paths)
}

fn deduplicate_paths(paths: Vec<PathBuf>) -> Vec<PathBuf> {
    let mut unique = Vec::new();
    for path in paths {
        push_unique_path(&mut unique, path);
    }
    unique
}

fn push_unique_path(paths: &mut Vec<PathBuf>, path: PathBuf) {
    if path.as_os_str().is_empty() {
        return;
    }
    #[cfg(windows)]
    let exists = paths.iter().any(|candidate| {
        candidate
            .to_string_lossy()
            .eq_ignore_ascii_case(&path.to_string_lossy())
    });
    #[cfg(not(windows))]
    let exists = paths.contains(&path);

    if !exists {
        paths.push(path);
    }
}

fn read_keyvalues_file(path: &Path) -> Option<String> {
    let metadata = fs::metadata(path).ok()?;
    if metadata.len() > MAX_KEYVALUES_FILE_SIZE {
        return None;
    }
    fs::read_to_string(path).ok()
}

fn vdf_string<'a>(object: &'a keyvalues_parser::Obj<'_>, key: &str) -> Option<&'a str> {
    object
        .iter()
        .find(|(candidate, _)| candidate.eq_ignore_ascii_case(key))
        .and_then(|(_, values)| values.first())
        .and_then(keyvalues_parser::Value::get_str)
}

fn parse_steam_library_paths(content: &str, path_style: SteamLibraryPathStyle) -> Vec<PathBuf> {
    let Ok(document) = keyvalues_parser::parse(content) else {
        return Vec::new();
    };
    let Some(libraries) = document.value.get_obj() else {
        return Vec::new();
    };

    let mut paths = Vec::new();
    for (index, values) in libraries.iter() {
        if !index.chars().all(|character| character.is_ascii_digit()) {
            continue;
        }
        for value in values {
            let path = value
                .get_obj()
                .and_then(|library| vdf_string(library, "path"))
                .or_else(|| value.get_str());
            if let Some(path) = path.filter(|path| is_absolute_steam_library_path(path, path_style))
            {
                push_unique_path(&mut paths, PathBuf::from(path));
            }
        }
    }
    paths
}

fn is_absolute_steam_library_path(path: &str, path_style: SteamLibraryPathStyle) -> bool {
    if path.is_empty() || path.contains('\0') {
        return false;
    }
    match path_style {
        SteamLibraryPathStyle::Windows => is_absolute_windows_path(path),
        SteamLibraryPathStyle::Unix => path.starts_with('/'),
    }
}

fn is_absolute_windows_path(path: &str) -> bool {
    let bytes = path.as_bytes();
    (bytes.len() >= 3
        && bytes[0].is_ascii_alphabetic()
        && bytes[1] == b':'
        && matches!(bytes[2], b'\\' | b'/'))
        || path.starts_with(r"\\")
}

fn parse_app_manifest_install_dir(content: &str) -> Option<PathBuf> {
    let document = keyvalues_parser::parse(content).ok()?;
    let app_state = document.value.get_obj()?;
    if vdf_string(app_state, "appid")? != STEAM_APP_ID {
        return None;
    }

    let install_dir = vdf_string(app_state, "installdir")?.trim();
    let normalized = install_dir.replace('\\', "/");
    if normalized.is_empty()
        || normalized.starts_with('/')
        || normalized.contains(':')
        || normalized
            .split('/')
            .any(|component| component.is_empty() || matches!(component, "." | ".."))
    {
        return None;
    }
    Some(PathBuf::from(install_dir))
}

fn steam_game_paths(
    steam_root: &Path,
    path_style: SteamLibraryPathStyle,
    layout: SteamGameLayout,
) -> Vec<PathBuf> {
    let mut libraries = vec![steam_root.to_path_buf()];
    for steamapps_name in steamapps_directory_names(layout) {
        let library_file = steam_root.join(steamapps_name).join("libraryfolders.vdf");
        if let Some(content) = read_keyvalues_file(&library_file) {
            for path in parse_steam_library_paths(&content, path_style) {
                push_unique_path(&mut libraries, path);
            }
        }
    }

    let mut candidates = Vec::new();
    for library in libraries {
        for steamapps_name in steamapps_directory_names(layout) {
            let steamapps = library.join(steamapps_name);
            let manifest = steamapps.join(format!("appmanifest_{STEAM_APP_ID}.acf"));
            if let Some(content) = read_keyvalues_file(&manifest) {
                if let Some(install_dir) = parse_app_manifest_install_dir(&content) {
                    push_unique_path(
                        &mut candidates,
                        steam_game_directory(&steamapps, &install_dir, layout),
                    );
                }
            }
            push_unique_path(
                &mut candidates,
                steam_game_directory(&steamapps, Path::new("Stardew Valley"), layout),
            );
        }
    }
    candidates
}

fn steamapps_directory_names(layout: SteamGameLayout) -> &'static [&'static str] {
    match layout {
        SteamGameLayout::Standard => &["steamapps"],
        SteamGameLayout::MacosBundle => &["steamapps", "SteamApps"],
    }
}

fn steam_game_directory(steamapps: &Path, install_dir: &Path, layout: SteamGameLayout) -> PathBuf {
    let directory = steamapps.join("common").join(install_dir);
    match layout {
        SteamGameLayout::Standard => directory,
        SteamGameLayout::MacosBundle => directory.join("Contents/MacOS"),
    }
}

#[cfg(windows)]
fn registry_string(root: &RegKey, key: &str, value: &str, flags: u32) -> Option<String> {
    root.open_subkey_with_flags(key, flags)
        .ok()?
        .get_value::<String, _>(value)
        .ok()
        .map(|value| value.trim().trim_matches('"').to_string())
        .filter(|value| !value.is_empty())
}

#[cfg(windows)]
fn steam_roots_from_registry() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    for value_name in ["SteamPath", "InstallPath"] {
        if let Some(path) = registry_string(&hkcu, r"Software\Valve\Steam", value_name, KEY_READ) {
            push_unique_path(&mut roots, PathBuf::from(path));
        }
    }
    if let Some(executable) = registry_string(&hkcu, r"Software\Valve\Steam", "SteamExe", KEY_READ)
    {
        if let Some(parent) = Path::new(&executable).parent() {
            push_unique_path(&mut roots, parent.to_path_buf());
        }
    }

    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    for view in [KEY_WOW64_64KEY, KEY_WOW64_32KEY] {
        if let Some(path) = registry_string(
            &hklm,
            r"SOFTWARE\Valve\Steam",
            "InstallPath",
            KEY_READ | view,
        ) {
            push_unique_path(&mut roots, PathBuf::from(path));
        }
    }
    roots
}

#[cfg(windows)]
fn collect_gog_paths(root: &RegKey, flags: u32, paths: &mut Vec<PathBuf>) {
    let Ok(games) = root.open_subkey_with_flags(r"SOFTWARE\GOG.com\Games", flags) else {
        return;
    };
    for key_name in games.enum_keys().flatten() {
        let Ok(game) = games.open_subkey(&key_name) else {
            continue;
        };
        let game_name = game.get_value::<String, _>("gameName").unwrap_or_default();
        if key_name != "1453375253" && !game_name.eq_ignore_ascii_case("Stardew Valley") {
            continue;
        }
        for value_name in ["path", "workingDir"] {
            if let Ok(path) = game.get_value::<String, _>(value_name) {
                push_unique_path(paths, PathBuf::from(path.trim().trim_matches('"')));
            }
        }
        if let Ok(executable) = game.get_value::<String, _>("exe") {
            if let Some(parent) = Path::new(executable.trim().trim_matches('"')).parent() {
                push_unique_path(paths, parent.to_path_buf());
            }
        }
    }
}

#[cfg(windows)]
fn windows_registry_game_paths() -> Vec<PathBuf> {
    let mut paths = Vec::new();
    for steam_root in steam_roots_from_registry() {
        for path in steam_game_paths(
            &steam_root,
            SteamLibraryPathStyle::Windows,
            SteamGameLayout::Standard,
        ) {
            push_unique_path(&mut paths, path);
        }
    }

    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    collect_gog_paths(&hkcu, KEY_READ, &mut paths);
    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    for view in [KEY_WOW64_64KEY, KEY_WOW64_32KEY] {
        collect_gog_paths(&hklm, KEY_READ | view, &mut paths);
    }
    paths
}

#[cfg(test)]
mod detection_tests {
    use std::time::{SystemTime, UNIX_EPOCH};

    use super::*;

    #[test]
    fn recognizes_only_the_main_steam_process_names() {
        for name in ["steam", "Steam.exe", "steam_osx"] {
            assert!(is_steam_process_name(OsStr::new(name)));
        }
        for name in ["steamwebhelper.exe", "steamservice.exe", "not-steam"] {
            assert!(!is_steam_process_name(OsStr::new(name)));
        }
    }

    #[test]
    fn persists_and_revalidates_a_manually_selected_game_path() {
        let root = unique_temp_directory();
        let game_dir = root.join("game");
        let config_dir = root.join("config");
        fs::create_dir_all(&game_dir).unwrap();
        #[cfg(windows)]
        let executable = "StardewValley.exe";
        #[cfg(not(windows))]
        let executable = "StardewValley";
        fs::write(game_dir.join(executable), b"test executable").unwrap();

        let saved = inspect_and_save_game(&config_dir, &game_dir).unwrap();
        let loaded = load_saved_game(&config_dir).unwrap().unwrap();
        assert_eq!(loaded.path, saved.path);
        assert_eq!(loaded.executable, executable);

        fs::remove_file(game_dir.join(executable)).unwrap();
        assert!(load_saved_game(&config_dir).is_err());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn detects_a_game_started_outside_the_manager_by_its_exact_path() {
        let root = unique_temp_directory();
        let game_dir = root.join("game");
        fs::create_dir_all(&game_dir).unwrap();

        #[cfg(windows)]
        let (source, executable, arguments) = (
            PathBuf::from(std::env::var("WINDIR").unwrap())
                .join("System32")
                .join("ping.exe"),
            "StardewValley.exe",
            vec!["127.0.0.1", "-n", "30"],
        );
        #[cfg(unix)]
        let (source, executable, arguments) =
            (PathBuf::from("/bin/sleep"), "StardewValley", vec!["30"]);

        let target = game_dir.join(executable);
        fs::copy(source, &target).unwrap();
        let mut child = Command::new(&target).args(arguments).spawn().unwrap();

        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(3);
        let detected = loop {
            if ensure_game_processes_stopped(&game_dir).is_err() {
                break true;
            }
            if std::time::Instant::now() >= deadline {
                break false;
            }
            std::thread::sleep(std::time::Duration::from_millis(50));
        };

        let _ = child.kill();
        let _ = child.wait();
        assert!(
            detected,
            "the externally started game process was not detected"
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn parses_modern_and_legacy_steam_library_folders() {
        let modern = r#"
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "228980" "1" } }
                "1" { "path" "D:\\SteamLibrary" "apps" { "413150" "1" } }
            }
        "#;
        assert_eq!(
            parse_steam_library_paths(modern, SteamLibraryPathStyle::Windows),
            vec![
                PathBuf::from(r"C:\Program Files (x86)\Steam"),
                PathBuf::from(r"D:\SteamLibrary"),
            ]
        );

        let legacy = r#"
            "LibraryFolders"
            {
                "TimeNextStatsReport" "0"
                "1" "E:\\Steam Library"
            }
        "#;
        assert_eq!(
            parse_steam_library_paths(legacy, SteamLibraryPathStyle::Windows),
            vec![PathBuf::from(r"E:\Steam Library")]
        );
    }

    #[test]
    fn parses_unix_steam_libraries_and_applies_the_macos_bundle_layout() {
        let libraries = r#"
            "libraryfolders"
            {
                "0" { "path" "/Users/farmer/Library/Application Support/Steam" }
                "1" { "path" "/Volumes/Games/SteamLibrary" "apps" { "413150" "1" } }
                "2" { "path" "relative/library" }
            }
        "#;
        assert_eq!(
            parse_steam_library_paths(libraries, SteamLibraryPathStyle::Unix),
            vec![
                PathBuf::from("/Users/farmer/Library/Application Support/Steam"),
                PathBuf::from("/Volumes/Games/SteamLibrary"),
            ]
        );

        assert_eq!(
            steam_game_directory(
                Path::new("/Volumes/Games/SteamLibrary/steamapps"),
                Path::new("Stardew Valley"),
                SteamGameLayout::MacosBundle,
            ),
            PathBuf::from(
                "/Volumes/Games/SteamLibrary/steamapps/common/Stardew Valley/Contents/MacOS"
            )
        );
    }

    #[test]
    fn bundled_smapi_scan_ignores_the_manager_trash() {
        let root = unique_temp_directory();
        let mods = root.join("Mods");
        write_smapi_manifest(
            &mods.join("ConsoleCommands/manifest.json"),
            "SMAPI.ConsoleCommands",
            "4.5.2",
            0,
        );
        write_smapi_manifest(
            &mods.join(".mod-manager-trash/old/manifest.json"),
            "SMAPI.SaveBackup",
            "4.4.0",
            0,
        );

        assert_eq!(smapi_bundled_mod_version(&mods), Some("4.5.2".into()));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn bundled_smapi_scan_skips_oversized_manifests() {
        let root = unique_temp_directory();
        let mods = root.join("Mods");
        write_smapi_manifest(
            &mods.join("ConsoleCommands/manifest.json"),
            "SMAPI.ConsoleCommands",
            "4.5.2",
            0,
        );
        write_smapi_manifest(
            &mods.join("Oversized/manifest.json"),
            "SMAPI.SaveBackup",
            "9.9.9",
            MAX_SMAPI_BUNDLED_MANIFEST_SIZE,
        );

        assert_eq!(smapi_bundled_mod_version(&mods), Some("4.5.2".into()));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn parses_only_safe_stardew_valley_app_manifests() {
        let valid = r#"
            "AppState"
            {
                "appid" "413150"
                "installdir" "Stardew Valley"
            }
        "#;
        assert_eq!(
            parse_app_manifest_install_dir(valid),
            Some(PathBuf::from("Stardew Valley"))
        );

        let wrong_app = valid.replace("413150", "12345");
        assert_eq!(parse_app_manifest_install_dir(&wrong_app), None);

        let traversal = valid.replace("Stardew Valley", r"..\\outside");
        assert_eq!(parse_app_manifest_install_dir(&traversal), None);
    }

    #[test]
    fn normalizes_smapi_versions_for_display_and_comparison() {
        assert_eq!(
            normalize_smapi_version("4.5.2+commit"),
            Some("4.5.2".into())
        );
        assert_eq!(normalize_smapi_version("4.5.2.0"), Some("4.5.2".into()));
        assert_eq!(normalize_smapi_version("v4.5.2"), Some("4.5.2".into()));
        assert_eq!(
            normalize_smapi_version("v4.5.2-beta.1+commit"),
            Some("4.5.2-beta.1".into())
        );
        assert_eq!(normalize_smapi_version("6.0.0 runtime"), None);
    }

    fn write_smapi_manifest(path: &Path, unique_id: &str, version: &str, trailing_spaces: usize) {
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        let mut content = format!(r#"{{"UniqueID":"{unique_id}","Version":"{version}"}}"#);
        content.push_str(&" ".repeat(trailing_spaces));
        fs::write(path, content).unwrap();
    }

    fn unique_temp_directory() -> PathBuf {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!(
            "valley-steward-game-test-{}-{nonce}",
            std::process::id()
        ))
    }
}
