use std::{
    fs,
    fs::OpenOptions,
    io::{ErrorKind, Write},
    path::{Path, PathBuf},
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
    time::{SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use walkdir::WalkDir;

use crate::models::{InstalledMod, Manifest};

const MANIFEST_FILE_NAME: &str = "manifest.json";
const TRANSLATION_SIDECAR_FILE_NAME: &str = ".valley-steward-translation.json";
const TRANSLATION_SIDECAR_VERSION: u8 = 1;
const MAX_MANIFEST_BYTES: u64 = 256 * 1024;
const MAX_TRANSLATION_SIDECAR_BYTES: usize = 256 * 1024;
const MAX_NAME_CHARS: usize = 512;
const MAX_VERSION_CHARS: usize = 128;
const MAX_SOURCE_DESCRIPTION_CHARS: usize = 12_000;
const MAX_TRANSLATED_DESCRIPTION_CHARS: usize = 16_000;
const MAX_TEMP_FILE_ATTEMPTS: u64 = 32;

static TEMP_FILE_COUNTER: AtomicU64 = AtomicU64::new(0);

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModTranslationSource {
    pub unique_id: String,
    pub version: String,
    pub name: String,
    pub description: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModTranslation {
    pub name: String,
    pub description: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TranslationSidecar {
    schema_version: u8,
    source: TranslationSidecarSource,
    translation: TranslationSidecarContent,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TranslationSidecarSource {
    unique_id: String,
    version: String,
    name: String,
    description: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TranslationSidecarContent {
    name: String,
    description: String,
}

pub fn scan(game_path: &Path) -> Vec<InstalledMod> {
    let mods_root = game_path.join("Mods");
    if !mods_root.is_dir() {
        return Vec::new();
    }

    WalkDir::new(&mods_root)
        .follow_links(false)
        .into_iter()
        .filter_map(Result::ok)
        .filter(|entry| entry.file_type().is_file() && entry.file_name() == MANIFEST_FILE_NAME)
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

pub fn translation_source(
    game_path: &Path,
    mod_path: &Path,
) -> Result<ModTranslationSource, String> {
    let (_, directory) = validate_mod_directory(game_path, mod_path)?;
    let manifest = read_manifest_file(&directory.join(MANIFEST_FILE_NAME))?;
    let source = source_from_manifest(&manifest);
    validate_source(&source)?;
    Ok(source)
}

pub fn save_translation(
    game_path: &Path,
    mod_path: &Path,
    expected_source: &ModTranslationSource,
    translation: ModTranslation,
) -> Result<(), String> {
    let (_, directory) = validate_mod_directory(game_path, mod_path)?;
    let current_manifest = read_manifest_file(&directory.join(MANIFEST_FILE_NAME))?;
    let current_source = source_from_manifest(&current_manifest);
    validate_source(&current_source)?;
    if &current_source != expected_source {
        return Err("AI 翻译期间 Mod 清单已发生变化，请重新翻译".into());
    }
    validate_translation(&translation)?;

    let sidecar = TranslationSidecar {
        schema_version: TRANSLATION_SIDECAR_VERSION,
        source: TranslationSidecarSource::from(expected_source),
        translation: TranslationSidecarContent {
            name: translation.name,
            description: translation.description,
        },
    };
    let content = serde_json::to_vec_pretty(&sidecar)
        .map_err(|error| format!("无法序列化 Mod 翻译：{error}"))?;
    if content.len() > MAX_TRANSLATION_SIDECAR_BYTES {
        return Err(format!(
            "Mod 翻译文件不能超过 {} KiB",
            MAX_TRANSLATION_SIDECAR_BYTES / 1024
        ));
    }
    write_sidecar_atomic(&directory, &content)
}

fn validate_mod_directory(game_path: &Path, mod_path: &Path) -> Result<(PathBuf, PathBuf), String> {
    let (mods_root, directory) = validate_mod_path(game_path, mod_path)?;
    if !directory.is_dir() {
        return Err("Mod 路径必须指向 Mods 目录内的文件夹".into());
    }
    Ok((mods_root, directory))
}

fn read_manifest_file(path: &Path) -> Result<Manifest, String> {
    let metadata = fs::symlink_metadata(path)
        .map_err(|error| format!("无法读取 manifest.json 元数据：{error}"))?;
    if !metadata.file_type().is_file() {
        return Err("manifest.json 必须是普通文件，不能是符号链接".into());
    }
    if metadata.len() > MAX_MANIFEST_BYTES {
        return Err(format!(
            "manifest.json 不能超过 {} KiB",
            MAX_MANIFEST_BYTES / 1024
        ));
    }
    let content = fs::read(path).map_err(|error| format!("无法读取 manifest.json：{error}"))?;
    if content.len() as u64 > MAX_MANIFEST_BYTES {
        return Err(format!(
            "manifest.json 不能超过 {} KiB",
            MAX_MANIFEST_BYTES / 1024
        ));
    }
    serde_json::from_slice(&content).map_err(|error| format!("manifest.json 无法解析：{error}"))
}

fn source_from_manifest(manifest: &Manifest) -> ModTranslationSource {
    ModTranslationSource {
        unique_id: manifest.unique_id.clone(),
        version: manifest.version.clone(),
        name: manifest.name.clone(),
        description: manifest.description.clone().unwrap_or_default(),
    }
}

fn validate_source(source: &ModTranslationSource) -> Result<(), String> {
    validate_text(&source.unique_id, "Mod UniqueID", MAX_NAME_CHARS, false)?;
    validate_text(&source.version, "Mod 版本", MAX_VERSION_CHARS, false)?;
    validate_text(&source.name, "Mod 名称", MAX_NAME_CHARS, false)?;
    validate_text(
        &source.description,
        "Mod Description",
        MAX_SOURCE_DESCRIPTION_CHARS,
        true,
    )
}

fn validate_translation(translation: &ModTranslation) -> Result<(), String> {
    validate_text(
        &translation.name,
        "翻译后的 Mod 名称",
        MAX_NAME_CHARS,
        false,
    )?;
    validate_text(
        &translation.description,
        "翻译后的 Mod Description",
        MAX_TRANSLATED_DESCRIPTION_CHARS,
        false,
    )
}

fn validate_text(
    value: &str,
    label: &str,
    max_chars: usize,
    allow_empty: bool,
) -> Result<(), String> {
    if !allow_empty && value.trim().is_empty() {
        return Err(format!("{label}不能为空"));
    }
    if value.chars().count() > max_chars {
        return Err(format!("{label}不能超过 {max_chars} 个字符"));
    }
    if value
        .chars()
        .any(|character| character.is_control() && !matches!(character, '\n' | '\r' | '\t'))
    {
        return Err(format!("{label}包含无效控制字符"));
    }
    Ok(())
}

impl From<&ModTranslationSource> for TranslationSidecarSource {
    fn from(source: &ModTranslationSource) -> Self {
        Self {
            unique_id: source.unique_id.clone(),
            version: source.version.clone(),
            name: source.name.clone(),
            description: source.description.clone(),
        }
    }
}

fn read_translation_sidecar(
    directory: &Path,
    source: &ModTranslationSource,
) -> Result<Option<ModTranslation>, String> {
    let path = directory.join(TRANSLATION_SIDECAR_FILE_NAME);
    let metadata = match fs::symlink_metadata(&path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(format!("无法读取 Mod 翻译文件元数据：{error}")),
    };
    if !metadata.file_type().is_file() {
        return Err("Mod 翻译文件必须是普通文件，不能是符号链接".into());
    }
    if metadata.len() > MAX_TRANSLATION_SIDECAR_BYTES as u64 {
        return Err(format!(
            "Mod 翻译文件不能超过 {} KiB",
            MAX_TRANSLATION_SIDECAR_BYTES / 1024
        ));
    }
    let content = fs::read(&path).map_err(|error| format!("无法读取 Mod 翻译文件：{error}"))?;
    if content.len() > MAX_TRANSLATION_SIDECAR_BYTES {
        return Err(format!(
            "Mod 翻译文件不能超过 {} KiB",
            MAX_TRANSLATION_SIDECAR_BYTES / 1024
        ));
    }
    let sidecar: TranslationSidecar = serde_json::from_slice(&content)
        .map_err(|error| format!("Mod 翻译文件无法解析：{error}"))?;
    if sidecar.schema_version != TRANSLATION_SIDECAR_VERSION {
        return Err("Mod 翻译文件版本不受支持".into());
    }
    if sidecar.source != TranslationSidecarSource::from(source) {
        return Err("Mod 翻译文件与当前 manifest.json 不匹配".into());
    }
    let translation = ModTranslation {
        name: sidecar.translation.name,
        description: sidecar.translation.description,
    };
    validate_translation(&translation)?;
    Ok(Some(translation))
}

fn write_sidecar_atomic(directory: &Path, content: &[u8]) -> Result<(), String> {
    let target = directory.join(TRANSLATION_SIDECAR_FILE_NAME);
    match fs::symlink_metadata(&target) {
        Ok(metadata) if !metadata.file_type().is_file() => {
            return Err("拒绝覆盖不是普通文件的 Mod 翻译 sidecar".into());
        }
        Ok(_) => {}
        Err(error) if error.kind() == ErrorKind::NotFound => {}
        Err(error) => return Err(format!("无法检查 Mod 翻译文件：{error}")),
    }

    let mut last_collision = None;
    for _ in 0..MAX_TEMP_FILE_ATTEMPTS {
        let sequence = TEMP_FILE_COUNTER.fetch_add(1, Ordering::Relaxed);
        let temporary = directory.join(format!(
            ".valley-steward-translation-{}-{sequence}.tmp",
            std::process::id()
        ));
        let mut file = match OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)
        {
            Ok(file) => file,
            Err(error) if error.kind() == ErrorKind::AlreadyExists => {
                last_collision = Some(error);
                continue;
            }
            Err(error) => return Err(format!("无法创建 Mod 翻译临时文件：{error}")),
        };

        let write_result = file
            .write_all(content)
            .and_then(|_| file.sync_all())
            .and_then(|_| atomic_replace(&temporary, &target));
        drop(file);
        if let Err(error) = write_result {
            let _ = fs::remove_file(&temporary);
            return Err(format!("无法安全保存 Mod 翻译：{error}"));
        }
        sync_parent_directory(directory);
        return Ok(());
    }
    Err(format!(
        "无法创建唯一的 Mod 翻译临时文件：{}",
        last_collision
            .map(|error| error.to_string())
            .unwrap_or_else(|| "文件名冲突".into())
    ))
}

#[cfg(windows)]
fn atomic_replace(source: &Path, target: &Path) -> std::io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Storage::FileSystem::{
        MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
    };

    let source = source
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    let target = target
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    let result = unsafe {
        MoveFileExW(
            source.as_ptr(),
            target.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    };
    if result == 0 {
        Err(std::io::Error::last_os_error())
    } else {
        Ok(())
    }
}

#[cfg(not(windows))]
fn atomic_replace(source: &Path, target: &Path) -> std::io::Result<()> {
    fs::rename(source, target)
}

#[cfg(unix)]
fn sync_parent_directory(directory: &Path) {
    if let Ok(file) = fs::File::open(directory) {
        let _ = file.sync_all();
    }
}

#[cfg(not(unix))]
fn sync_parent_directory(_directory: &Path) {}

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

    match read_manifest_file(path) {
        Ok(manifest) => {
            let source = source_from_manifest(&manifest);
            let translation = validate_source(&source)
                .ok()
                .and_then(|_| read_translation_sidecar(directory, &source).ok().flatten());
            let translated = translation.is_some();
            let (name, description) = match translation {
                Some(translation) => (translation.name, Some(translation.description)),
                None => (manifest.name.clone(), manifest.description.clone()),
            };
            InstalledMod {
                id: manifest.unique_id,
                name,
                description,
                translated,
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
            }
        }
        Err(error) => InstalledMod {
            id: format!("invalid:{}", relative.to_string_lossy()),
            name: directory
                .file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .into_owned(),
            description: None,
            translated: false,
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

#[cfg(test)]
mod tests {
    use std::{
        fs,
        path::{Path, PathBuf},
        time::{SystemTime, UNIX_EPOCH},
    };

    use serde_json::json;

    use super::*;

    struct TestDirectory(PathBuf);

    impl TestDirectory {
        fn new() -> Self {
            let nonce = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos();
            let path = std::env::temp_dir().join(format!(
                "valley-steward-mod-translation-{}-{nonce}",
                std::process::id()
            ));
            fs::create_dir_all(&path).unwrap();
            Self(path)
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TestDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn create_mod() -> (TestDirectory, PathBuf, PathBuf) {
        let root = TestDirectory::new();
        let game = root.path().join("Stardew Valley");
        let mod_directory = game.join("Mods").join("ExampleMod");
        fs::create_dir_all(&mod_directory).unwrap();
        write_manifest(
            &mod_directory,
            "Example Mod",
            "An example description.",
            "1.0.0",
        );
        (root, game, mod_directory)
    }

    fn write_manifest(directory: &Path, name: &str, description: &str, version: &str) {
        let manifest = json!({
            "Name": name,
            "Description": description,
            "Author": "Test Author",
            "Version": version,
            "UniqueId": "Test.Author.ExampleMod",
            "Dependencies": []
        });
        fs::write(
            directory.join(MANIFEST_FILE_NAME),
            serde_json::to_vec_pretty(&manifest).unwrap(),
        )
        .unwrap();
    }

    fn translated(name: &str, description: &str) -> ModTranslation {
        ModTranslation {
            name: name.into(),
            description: description.into(),
        }
    }

    #[test]
    fn translation_path_must_be_a_mod_directory_inside_mods() {
        let (root, game, mod_directory) = create_mod();
        let source = translation_source(&game, Path::new("Mods/ExampleMod")).unwrap();
        assert_eq!(source.name, "Example Mod");
        assert_eq!(source.description, "An example description.");

        let manifest_path = mod_directory.join(MANIFEST_FILE_NAME);
        assert!(translation_source(&game, &manifest_path).is_err());
        assert!(translation_source(&game, &game.join("Mods")).is_err());

        let outside = root.path().join("OutsideMod");
        fs::create_dir_all(&outside).unwrap();
        write_manifest(&outside, "Outside", "Outside Mods.", "1.0.0");
        assert!(translation_source(&game, &outside).is_err());
    }

    #[test]
    fn valid_sidecar_is_preferred_without_modifying_manifest() {
        let (_root, game, mod_directory) = create_mod();
        let manifest_before = fs::read(mod_directory.join(MANIFEST_FILE_NAME)).unwrap();
        let source = translation_source(&game, &mod_directory).unwrap();

        save_translation(
            &game,
            &mod_directory,
            &source,
            translated("示例 Mod", "一个示例简介。"),
        )
        .unwrap();

        let mods = scan(&game);
        assert_eq!(mods.len(), 1);
        assert_eq!(mods[0].name, "示例 Mod");
        assert_eq!(mods[0].description.as_deref(), Some("一个示例简介。"));
        assert!(mods[0].translated);
        assert_eq!(
            fs::read(mod_directory.join(MANIFEST_FILE_NAME)).unwrap(),
            manifest_before
        );
    }

    #[test]
    fn corrupt_unsupported_and_stale_sidecars_are_ignored() {
        let (_root, game, mod_directory) = create_mod();
        let sidecar_path = mod_directory.join(TRANSLATION_SIDECAR_FILE_NAME);
        let source = translation_source(&game, &mod_directory).unwrap();
        save_translation(
            &game,
            &mod_directory,
            &source,
            translated("示例 Mod", "一个示例简介。"),
        )
        .unwrap();

        fs::write(&sidecar_path, b"{not-json").unwrap();
        let corrupt = scan(&game);
        assert_eq!(corrupt[0].name, "Example Mod");
        assert!(!corrupt[0].translated);

        let unsupported = json!({
            "schemaVersion": 99,
            "source": {
                "uniqueId": source.unique_id,
                "version": source.version,
                "name": source.name,
                "description": source.description
            },
            "translation": { "name": "示例 Mod", "description": "一个示例简介。" }
        });
        fs::write(&sidecar_path, serde_json::to_vec(&unsupported).unwrap()).unwrap();
        assert!(!scan(&game)[0].translated);

        write_manifest(
            &mod_directory,
            "Example Mod Updated",
            "An updated description.",
            "2.0.0",
        );
        let stale = scan(&game);
        assert_eq!(stale[0].name, "Example Mod Updated");
        assert!(!stale[0].translated);
    }

    #[test]
    fn oversized_sidecar_and_translation_are_rejected() {
        let (_root, game, mod_directory) = create_mod();
        let sidecar_path = mod_directory.join(TRANSLATION_SIDECAR_FILE_NAME);
        fs::write(&sidecar_path, vec![b'x'; MAX_TRANSLATION_SIDECAR_BYTES + 1]).unwrap();
        let scanned = scan(&game);
        assert_eq!(scanned[0].name, "Example Mod");
        assert!(!scanned[0].translated);

        let source = translation_source(&game, &mod_directory).unwrap();
        let error = save_translation(
            &game,
            &mod_directory,
            &source,
            translated(
                "示例 Mod",
                &"译".repeat(MAX_TRANSLATED_DESCRIPTION_CHARS + 1),
            ),
        )
        .unwrap_err();
        assert!(error.contains("不能超过"));
    }

    #[test]
    fn atomic_write_replaces_sidecar_and_preserves_previous_value_on_validation_error() {
        let (_root, game, mod_directory) = create_mod();
        let source = translation_source(&game, &mod_directory).unwrap();
        save_translation(
            &game,
            &mod_directory,
            &source,
            translated("第一次翻译", "第一次简介。"),
        )
        .unwrap();
        save_translation(
            &game,
            &mod_directory,
            &source,
            translated("第二次翻译", "第二次简介。"),
        )
        .unwrap();

        let sidecar_path = mod_directory.join(TRANSLATION_SIDECAR_FILE_NAME);
        let before_failed_write = fs::read(&sidecar_path).unwrap();
        let error = save_translation(&game, &mod_directory, &source, translated("", "不会写入。"))
            .unwrap_err();
        assert!(error.contains("不能为空"));
        assert_eq!(fs::read(&sidecar_path).unwrap(), before_failed_write);

        let saved: TranslationSidecar = serde_json::from_slice(&before_failed_write).unwrap();
        assert_eq!(saved.translation.name, "第二次翻译");
        assert_eq!(saved.translation.description, "第二次简介。");
        assert!(fs::read_dir(&mod_directory).unwrap().all(|entry| {
            !entry
                .unwrap()
                .file_name()
                .to_string_lossy()
                .ends_with(".tmp")
        }));
    }

    #[test]
    fn save_rechecks_manifest_after_translation_request() {
        let (_root, game, mod_directory) = create_mod();
        let source = translation_source(&game, &mod_directory).unwrap();
        write_manifest(
            &mod_directory,
            "Example Mod",
            "An example description.",
            "2.0.0",
        );

        let error = save_translation(
            &game,
            &mod_directory,
            &source,
            translated("示例 Mod", "一个示例简介。"),
        )
        .unwrap_err();
        assert!(error.contains("发生变化"));
        assert!(!mod_directory.join(TRANSLATION_SIDECAR_FILE_NAME).exists());
    }
}
