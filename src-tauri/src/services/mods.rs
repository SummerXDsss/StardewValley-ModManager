use std::{
    fs,
    path::{Path, PathBuf},
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};

use walkdir::WalkDir;

use crate::models::{InstalledMod, Manifest};

pub fn scan(game_path: &Path) -> Vec<InstalledMod> {
    let mods_root = game_path.join("Mods");
    if !mods_root.is_dir() {
        return Vec::new();
    }

    WalkDir::new(&mods_root)
        .follow_links(false)
        .into_iter()
        .filter_map(Result::ok)
        .filter(|entry| entry.file_type().is_file() && entry.file_name() == "manifest.json")
        .filter(|entry| {
            !entry
                .path()
                .components()
                .any(|part| part.as_os_str() == ".mod-manager-trash")
        })
        .map(|entry| read_manifest(entry.path(), &mods_root))
        .collect()
}

pub fn set_enabled(game_path: &Path, mod_path: &Path, enabled: bool) -> Result<(), String> {
    let (mods_root, current) = validate_mod_path(game_path, mod_path)?;
    let name = current
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| "Mod 目录名称无效".to_string())?;
    let clean_name = name.trim_matches('.');
    if clean_name.is_empty() {
        return Err("Mod 目录名称无效".into());
    }
    let next_name = if enabled {
        clean_name.to_string()
    } else {
        format!(".{clean_name}")
    };
    let target = current.with_file_name(next_name);
    if target == current {
        return Ok(());
    }
    if target.exists() {
        return Err("启用状态切换失败：目标目录已存在".into());
    }
    if !target.starts_with(&mods_root) {
        return Err("拒绝在 Mods 目录之外重命名".into());
    }
    fs::rename(current, target).map_err(|error| format!("无法切换 Mod 状态：{error}"))
}

pub fn move_to_trash(game_path: &Path, mod_path: &Path) -> Result<(), String> {
    let (mods_root, current) = validate_mod_path(game_path, mod_path)?;
    let trash = mods_root.join(".mod-manager-trash");
    fs::create_dir_all(&trash).map_err(|error| format!("无法创建管理器回收区：{error}"))?;
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let name = current.file_name().unwrap_or_default().to_string_lossy();
    let target = trash.join(format!("{stamp}-{name}"));
    fs::rename(current, target).map_err(|error| format!("无法移动 Mod：{error}"))
}

pub fn open_folder(game_path: &Path, mod_path: &Path) -> Result<(), String> {
    let (_, current) = validate_mod_path(game_path, mod_path)?;
    #[cfg(target_os = "windows")]
    let mut command = Command::new("explorer.exe");
    #[cfg(target_os = "macos")]
    let mut command = Command::new("open");
    #[cfg(target_os = "linux")]
    let mut command = Command::new("xdg-open");

    command
        .arg(current)
        .spawn()
        .map_err(|error| format!("无法打开 Mod 目录：{error}"))?;
    Ok(())
}

fn validate_mod_path(game_path: &Path, mod_path: &Path) -> Result<(PathBuf, PathBuf), String> {
    let canonical_game = game_path
        .canonicalize()
        .map_err(|_| "游戏目录不存在".to_string())?;
    let mods_root = canonical_game
        .join("Mods")
        .canonicalize()
        .map_err(|_| "Mods 目录不存在".to_string())?;
    let requested = if mod_path.is_absolute() {
        mod_path.to_path_buf()
    } else {
        canonical_game.join(mod_path)
    };
    let current = requested
        .canonicalize()
        .map_err(|_| "Mod 目录不存在".to_string())?;
    if current == mods_root || !current.starts_with(&mods_root) {
        return Err("拒绝操作 Mods 目录之外的路径".into());
    }
    Ok((mods_root, current))
}

fn read_manifest(path: &Path, mods_root: &Path) -> InstalledMod {
    let directory = path.parent().unwrap_or(mods_root);
    let relative = directory
        .strip_prefix(mods_root.parent().unwrap_or(mods_root))
        .unwrap_or(directory);
    let enabled = directory
        .strip_prefix(mods_root)
        .ok()
        .map(|path| {
            !path
                .components()
                .any(|part| part.as_os_str().to_string_lossy().starts_with('.'))
        })
        .unwrap_or(true);

    match fs::read_to_string(path)
        .map_err(|error| error.to_string())
        .and_then(|content| {
            serde_json::from_str::<Manifest>(&content).map_err(|error| error.to_string())
        }) {
        Ok(manifest) => InstalledMod {
            id: manifest.unique_id,
            name: manifest.name,
            author: manifest.author,
            version: manifest.version,
            path: relative.to_string_lossy().into_owned(),
            enabled,
            health: "healthy".into(),
            update_available: None,
            dependencies: manifest
                .dependencies
                .into_iter()
                .filter(|dependency| dependency.is_required)
                .map(|dependency| dependency.unique_id)
                .collect(),
            error: None,
        },
        Err(error) => InstalledMod {
            id: format!("invalid:{}", relative.to_string_lossy()),
            name: directory
                .file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .into_owned(),
            author: "未知".into(),
            version: "未知".into(),
            path: relative.to_string_lossy().into_owned(),
            enabled,
            health: "error".into(),
            update_available: None,
            dependencies: Vec::new(),
            error: Some(format!("manifest.json 无法解析：{error}")),
        },
    }
}
