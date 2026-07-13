use std::{
    fs,
    io::{ErrorKind, Write},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicU64, Ordering},
        Mutex, MutexGuard,
    },
};

use reqwest::Url;
use serde::{Deserialize, Serialize};

const CONFIG_FILE_NAME: &str = "github-download.json";
const CONFIG_BACKUP_FILE_NAME: &str = ".github-download.json.bak";
const CONFIG_VERSION: u8 = 1;
const MAX_CONFIG_BYTES: usize = 8 * 1024;
const MAX_PREFIX_BYTES: usize = 512;
const MAX_OFFICIAL_URL_BYTES: usize = 4 * 1024;
const MAX_APPLIED_URL_BYTES: usize = 8 * 1024;
const MAX_PATH_SEGMENT_BYTES: usize = 512;

static TEMP_FILE_COUNTER: AtomicU64 = AtomicU64::new(0);
static CONFIG_IO_LOCK: Mutex<()> = Mutex::new(());

#[derive(Debug, Clone, Copy, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum GithubDownloadMode {
    #[default]
    Direct,
    Custom,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GithubDownloadSettings {
    pub mode: GithubDownloadMode,
    pub custom_prefix: Option<String>,
}

impl Default for GithubDownloadSettings {
    fn default() -> Self {
        Self {
            mode: GithubDownloadMode::Direct,
            custom_prefix: None,
        }
    }
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SaveGithubDownloadSettingsRequest {
    pub mode: GithubDownloadMode,
    #[serde(default)]
    pub custom_prefix: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StoredGithubDownloadSettings {
    version: u8,
    mode: GithubDownloadMode,
    custom_prefix: Option<String>,
}

#[derive(Debug, Clone)]
pub struct AppliedGithubDownloadUrl {
    url: Url,
    mirrored: bool,
    expected_sha256: Option<String>,
    mirror_scheme: Option<String>,
    mirror_host: Option<String>,
}

impl AppliedGithubDownloadUrl {
    pub fn url(&self) -> &Url {
        &self.url
    }

    pub fn is_mirrored(&self) -> bool {
        self.mirrored
    }

    pub fn expected_sha256(&self) -> Option<&str> {
        self.expected_sha256.as_deref()
    }

    pub fn validate_mirror_response_url(&self, response_url: &Url) -> Result<(), String> {
        if !self.mirrored {
            return Ok(());
        }
        let same_origin = self.mirror_scheme.as_deref() == Some(response_url.scheme())
            && self.mirror_host.as_deref() == response_url.host_str()
            && response_url.port().is_none();
        if !same_origin
            || !response_url.username().is_empty()
            || response_url.password().is_some()
            || response_url.query().is_some()
            || response_url.fragment().is_some()
            || response_url.as_str().len() > MAX_APPLIED_URL_BYTES
        {
            return Err("GitHub mirror redirected to an untrusted URL".into());
        }
        Ok(())
    }
}

pub fn get_settings(config_dir: &Path) -> Result<GithubDownloadSettings, String> {
    let _guard = lock_config_io();
    let Some(stored) = read_config(config_dir)? else {
        return Ok(GithubDownloadSettings::default());
    };
    normalize_settings(stored.mode, stored.custom_prefix.as_deref())
}

pub fn save_settings(
    config_dir: &Path,
    request: SaveGithubDownloadSettingsRequest,
) -> Result<GithubDownloadSettings, String> {
    let _guard = lock_config_io();
    let settings = normalize_settings(request.mode, request.custom_prefix.as_deref())?;
    let stored = StoredGithubDownloadSettings {
        version: CONFIG_VERSION,
        mode: settings.mode,
        custom_prefix: settings.custom_prefix.clone(),
    };
    write_config(config_dir, &stored)?;
    Ok(settings)
}

fn lock_config_io() -> MutexGuard<'static, ()> {
    CONFIG_IO_LOCK
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
}

pub fn apply_github_mirror(
    settings: &GithubDownloadSettings,
    official_url: &Url,
    official_digest: Option<&str>,
) -> Result<AppliedGithubDownloadUrl, String> {
    validate_official_download_url(official_url)?;

    match settings.mode {
        GithubDownloadMode::Direct => Ok(AppliedGithubDownloadUrl {
            url: official_url.clone(),
            mirrored: false,
            expected_sha256: None,
            mirror_scheme: None,
            mirror_host: None,
        }),
        GithubDownloadMode::Custom => {
            let prefix = settings
                .custom_prefix
                .as_deref()
                .ok_or_else(|| "A custom GitHub mirror prefix is required".to_string())?;
            let prefix = normalize_custom_prefix(prefix)?;
            let expected_sha256 = normalize_sha256_digest(official_digest)?.ok_or_else(|| {
                "The official GitHub release does not provide a SHA-256 digest; refusing to use a third-party mirror"
                    .to_string()
            })?;
            let applied = format!("{prefix}{}", official_url.as_str());
            if applied.len() > MAX_APPLIED_URL_BYTES {
                return Err("The resolved GitHub mirror URL is too long".into());
            }
            let url = Url::parse(&applied)
                .map_err(|error| format!("The resolved GitHub mirror URL is invalid: {error}"))?;
            validate_applied_mirror_url(&url)?;
            let prefix_url = Url::parse(&prefix)
                .map_err(|error| format!("The GitHub mirror prefix is invalid: {error}"))?;

            Ok(AppliedGithubDownloadUrl {
                url,
                mirrored: true,
                expected_sha256: Some(expected_sha256),
                mirror_scheme: Some(prefix_url.scheme().to_string()),
                mirror_host: prefix_url.host_str().map(str::to_string),
            })
        }
    }
}

fn normalize_settings(
    mode: GithubDownloadMode,
    custom_prefix: Option<&str>,
) -> Result<GithubDownloadSettings, String> {
    match mode {
        GithubDownloadMode::Direct => Ok(GithubDownloadSettings::default()),
        GithubDownloadMode::Custom => {
            let prefix = custom_prefix
                .ok_or_else(|| "A custom GitHub mirror prefix is required".to_string())?;
            Ok(GithubDownloadSettings {
                mode,
                custom_prefix: Some(normalize_custom_prefix(prefix)?),
            })
        }
    }
}

fn normalize_custom_prefix(value: &str) -> Result<String, String> {
    let value = value.trim();
    if value.is_empty() {
        return Err("The GitHub mirror prefix cannot be empty".into());
    }
    if value.len() > MAX_PREFIX_BYTES {
        return Err(format!(
            "The GitHub mirror prefix exceeds the {MAX_PREFIX_BYTES} byte limit"
        ));
    }

    let mut url = Url::parse(value)
        .map_err(|error| format!("The GitHub mirror prefix is invalid: {error}"))?;
    validate_plain_https_url(&url, "GitHub mirror prefix")?;

    let path = url.path().trim_end_matches('/');
    let normalized_path = if path.is_empty() {
        "/".to_string()
    } else {
        format!("{path}/")
    };
    url.set_path(&normalized_path);
    let normalized = url.as_str().to_string();
    if normalized.len() > MAX_PREFIX_BYTES {
        return Err(format!(
            "The normalized GitHub mirror prefix exceeds the {MAX_PREFIX_BYTES} byte limit"
        ));
    }
    Ok(normalized)
}

fn validate_applied_mirror_url(url: &Url) -> Result<(), String> {
    validate_plain_https_url(url, "resolved GitHub mirror URL")?;
    if url.as_str().len() > MAX_APPLIED_URL_BYTES {
        return Err("The resolved GitHub mirror URL is too long".into());
    }
    Ok(())
}

fn validate_plain_https_url(url: &Url, label: &str) -> Result<(), String> {
    if url.scheme() != "https"
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.port().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return Err(format!(
            "The {label} must be a plain HTTPS URL without credentials, a custom port, query, or fragment"
        ));
    }
    Ok(())
}

fn validate_official_download_url(url: &Url) -> Result<(), String> {
    if url.as_str().len() > MAX_OFFICIAL_URL_BYTES {
        return Err("The official GitHub download URL is too long".into());
    }
    validate_plain_https_url(url, "official GitHub download URL")?;
    if url.host_str() != Some("github.com") {
        return Err("Only official github.com download URLs can use a mirror".into());
    }

    let segments = url
        .path_segments()
        .ok_or_else(|| "The official GitHub download URL has no path".to_string())?
        .collect::<Vec<_>>();
    if segments.len() != 6
        || !valid_repository_segment(segments[0])
        || !valid_repository_segment(segments[1])
        || !valid_download_tail(&segments[2..])
    {
        return Err(
            "Only official GitHub release asset and refs archive download URLs can use a mirror"
                .into(),
        );
    }
    Ok(())
}

fn valid_repository_segment(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 100
        && value != "."
        && value != ".."
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_'))
}

fn valid_download_tail(segments: &[&str]) -> bool {
    let route_is_allowed = matches!(segments, ["releases", "download", _, _])
        || matches!(segments, ["archive", "refs", "heads" | "tags", _]);
    route_is_allowed && segments.iter().all(|segment| valid_path_segment(segment))
}

fn valid_path_segment(value: &str) -> bool {
    let lowercase = value.to_ascii_lowercase();
    !value.is_empty()
        && value.len() <= MAX_PATH_SEGMENT_BYTES
        && value != "."
        && value != ".."
        && !lowercase.contains("%2f")
        && !lowercase.contains("%5c")
        && !value.bytes().any(|byte| byte.is_ascii_control())
}

fn normalize_sha256_digest(value: Option<&str>) -> Result<Option<String>, String> {
    let Some(value) = value else {
        return Ok(None);
    };
    let value = value.trim();
    let digest = value
        .strip_prefix("sha256:")
        .or_else(|| value.strip_prefix("SHA256:"))
        .ok_or_else(|| "The official GitHub digest is not SHA-256".to_string())?;
    if digest.len() != 64 || !digest.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err("The official GitHub SHA-256 digest is invalid".into());
    }
    Ok(Some(digest.to_ascii_lowercase()))
}

fn read_config(config_dir: &Path) -> Result<Option<StoredGithubDownloadSettings>, String> {
    let target = config_path(config_dir);
    let target_result = read_config_file(&target, "GitHub download settings file");
    if let Ok(Some(stored)) = &target_result {
        return Ok(Some(stored.clone()));
    }

    let backup = config_backup_path(config_dir);
    let backup_result = read_config_file(&backup, "GitHub download settings backup");
    let stored = match backup_result {
        Ok(Some(stored)) => stored,
        Ok(None) => return target_result,
        Err(backup_error) => {
            return match target_result {
                Ok(None) => Err(backup_error),
                Err(target_error) => Err(format!(
                    "{target_error}; the GitHub download settings backup is also unavailable: {backup_error}"
                )),
                Ok(Some(_)) => unreachable!("a valid target returned early"),
            };
        }
    };

    let bytes = serde_json::to_vec_pretty(&stored).map_err(|error| {
        format!("Could not serialize recovered GitHub download settings: {error}")
    })?;
    let temporary = write_temporary_config(config_dir, &bytes)?;
    if let Err(error) = replace_config_file(&temporary, &target) {
        let _ = fs::remove_file(&temporary);
        return Err(format!(
            "Could not recover GitHub download settings: {error}"
        ));
    }
    sync_directory_best_effort(config_dir);
    Ok(Some(stored))
}

fn read_config_file(
    path: &Path,
    label: &str,
) -> Result<Option<StoredGithubDownloadSettings>, String> {
    let bytes = match fs::read(path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(format!("Could not read the {label}: {error}")),
    };
    if bytes.len() > MAX_CONFIG_BYTES {
        return Err(format!("The {label} is too large"));
    }
    let stored: StoredGithubDownloadSettings = serde_json::from_slice(&bytes)
        .map_err(|error| format!("The {label} is invalid: {error}"))?;
    if stored.version != CONFIG_VERSION {
        return Err(format!(
            "Unsupported version in the {label}: {}",
            stored.version
        ));
    }
    Ok(Some(stored))
}

fn write_config(config_dir: &Path, config: &StoredGithubDownloadSettings) -> Result<(), String> {
    fs::create_dir_all(config_dir)
        .map_err(|error| format!("Could not create the app configuration directory: {error}"))?;
    let bytes = serde_json::to_vec_pretty(config)
        .map_err(|error| format!("Could not serialize GitHub download settings: {error}"))?;
    if bytes.len() > MAX_CONFIG_BYTES {
        return Err("The GitHub download settings are too large".into());
    }

    let target = config_path(config_dir);
    let backup = config_backup_path(config_dir);
    let temporary = write_temporary_config(config_dir, &bytes)?;
    let backup_temporary = match write_temporary_config(config_dir, &bytes) {
        Ok(path) => path,
        Err(error) => {
            let _ = fs::remove_file(&temporary);
            return Err(error);
        }
    };

    if let Err(error) = replace_config_file(&backup_temporary, &backup) {
        let _ = fs::remove_file(&temporary);
        let _ = fs::remove_file(&backup_temporary);
        return Err(format!(
            "Could not save the GitHub download settings backup: {error}"
        ));
    }

    if let Err(error) = replace_config_file(&temporary, &target) {
        let _ = fs::remove_file(&temporary);
        return Err(format!("Could not save GitHub download settings: {error}"));
    }
    sync_directory_best_effort(config_dir);
    Ok(())
}

fn write_temporary_config(config_dir: &Path, bytes: &[u8]) -> Result<PathBuf, String> {
    for _ in 0..16 {
        let path = temporary_config_path(config_dir);
        let mut file = match fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)
        {
            Ok(file) => file,
            Err(error) if error.kind() == ErrorKind::AlreadyExists => continue,
            Err(error) => {
                return Err(format!(
                    "Could not create temporary GitHub download settings: {error}"
                ));
            }
        };
        if let Err(error) = file.write_all(bytes).and_then(|()| file.sync_all()) {
            drop(file);
            let _ = fs::remove_file(&path);
            return Err(format!("Could not sync GitHub download settings: {error}"));
        }
        return Ok(path);
    }
    Err("Could not allocate a temporary GitHub download settings file".into())
}

#[cfg(windows)]
fn replace_config_file(source: &Path, target: &Path) -> Result<(), std::io::Error> {
    use std::os::windows::ffi::OsStrExt;

    use windows_sys::Win32::Storage::FileSystem::{
        MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
    };

    fn wide_null(path: &Path) -> Result<Vec<u16>, std::io::Error> {
        let mut value = path.as_os_str().encode_wide().collect::<Vec<_>>();
        if value.contains(&0) {
            return Err(std::io::Error::new(
                ErrorKind::InvalidInput,
                "configuration path contains a NUL character",
            ));
        }
        value.push(0);
        Ok(value)
    }

    let source = wide_null(source)?;
    let target = wide_null(target)?;
    let moved = unsafe {
        MoveFileExW(
            source.as_ptr(),
            target.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    };
    if moved == 0 {
        Err(std::io::Error::last_os_error())
    } else {
        Ok(())
    }
}

#[cfg(not(windows))]
fn replace_config_file(source: &Path, target: &Path) -> Result<(), std::io::Error> {
    fs::rename(source, target)
}

fn sync_directory_best_effort(path: &Path) {
    #[cfg(unix)]
    if let Ok(directory) = fs::File::open(path) {
        let _ = directory.sync_all();
    }

    #[cfg(not(unix))]
    let _ = path;
}

fn config_path(config_dir: &Path) -> PathBuf {
    config_dir.join(CONFIG_FILE_NAME)
}

fn config_backup_path(config_dir: &Path) -> PathBuf {
    config_dir.join(CONFIG_BACKUP_FILE_NAME)
}

fn temporary_config_path(config_dir: &Path) -> PathBuf {
    let sequence = TEMP_FILE_COUNTER.fetch_add(1, Ordering::Relaxed);
    config_dir.join(format!(
        ".github-download-{}-{sequence}.tmp",
        std::process::id()
    ))
}

#[cfg(test)]
mod tests {
    use std::{
        sync::mpsc,
        thread,
        time::{Duration, SystemTime, UNIX_EPOCH},
    };

    use super::*;

    struct TestDirectory(PathBuf);

    impl TestDirectory {
        fn new() -> Self {
            let nonce = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            let path = std::env::temp_dir().join(format!(
                "valley-steward-github-settings-{}-{nonce}",
                std::process::id()
            ));
            fs::create_dir_all(&path).unwrap();
            Self(path)
        }
    }

    impl Drop for TestDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn custom_request(prefix: &str) -> SaveGithubDownloadSettingsRequest {
        SaveGithubDownloadSettingsRequest {
            mode: GithubDownloadMode::Custom,
            custom_prefix: Some(prefix.to_string()),
        }
    }

    fn official_release_url() -> Url {
        Url::parse(
            "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip",
        )
        .unwrap()
    }

    fn digest() -> String {
        format!("sha256:{}", "a1".repeat(32))
    }

    #[test]
    fn missing_config_defaults_to_direct() {
        let directory = TestDirectory::new();
        assert_eq!(
            get_settings(&directory.0).unwrap(),
            GithubDownloadSettings::default()
        );
    }

    #[test]
    fn custom_settings_are_normalized_and_persisted_without_secrets() {
        let directory = TestDirectory::new();
        let saved = save_settings(
            &directory.0,
            custom_request("  https://mirror.example/github-proxy  "),
        )
        .unwrap();
        assert_eq!(saved.mode, GithubDownloadMode::Custom);
        assert_eq!(
            saved.custom_prefix.as_deref(),
            Some("https://mirror.example/github-proxy/")
        );
        assert_eq!(get_settings(&directory.0).unwrap(), saved);

        let stored = fs::read_to_string(config_path(&directory.0)).unwrap();
        assert!(stored.contains("github-proxy"));
        assert!(!stored.to_ascii_lowercase().contains("key"));
        assert!(!stored.to_ascii_lowercase().contains("token"));
    }

    #[test]
    fn direct_mode_clears_a_previous_custom_prefix() {
        let directory = TestDirectory::new();
        save_settings(
            &directory.0,
            custom_request("https://mirror.example/proxy/"),
        )
        .unwrap();
        let direct = save_settings(
            &directory.0,
            SaveGithubDownloadSettingsRequest {
                mode: GithubDownloadMode::Direct,
                custom_prefix: Some("https://ignored.example/".into()),
            },
        )
        .unwrap();
        assert_eq!(direct, GithubDownloadSettings::default());
        assert_eq!(get_settings(&directory.0).unwrap(), direct);
    }

    #[test]
    fn interrupted_save_recovers_the_valid_backup_instead_of_defaulting() {
        let directory = TestDirectory::new();
        let saved = save_settings(
            &directory.0,
            custom_request("https://mirror.example/recovered/"),
        )
        .unwrap();
        let target = config_path(&directory.0);
        let backup = config_backup_path(&directory.0);
        fs::remove_file(&target).unwrap();

        assert!(!target.exists());
        assert!(backup.exists());
        assert_eq!(get_settings(&directory.0).unwrap(), saved);
        assert!(target.exists());
        assert!(backup.exists());
        assert_eq!(get_settings(&directory.0).unwrap(), saved);
    }

    #[test]
    fn corrupted_primary_config_recovers_the_valid_backup() {
        let directory = TestDirectory::new();
        let saved = save_settings(
            &directory.0,
            custom_request("https://mirror.example/recovered-corruption/"),
        )
        .unwrap();
        fs::write(config_path(&directory.0), b"not json").unwrap();

        assert_eq!(get_settings(&directory.0).unwrap(), saved);
        assert_eq!(get_settings(&directory.0).unwrap(), saved);
    }

    #[test]
    fn invalid_backup_is_reported_instead_of_silently_defaulting() {
        let directory = TestDirectory::new();
        fs::write(config_backup_path(&directory.0), b"not json").unwrap();

        let error = get_settings(&directory.0).unwrap_err();
        assert!(error.contains("backup"), "unexpected error: {error}");
    }

    #[test]
    fn reads_wait_for_an_in_flight_configuration_operation() {
        let directory = TestDirectory::new();
        let expected = save_settings(
            &directory.0,
            custom_request("https://mirror.example/serialized/"),
        )
        .unwrap();
        let guard = lock_config_io();
        let path = directory.0.clone();
        let (started_sender, started_receiver) = mpsc::channel();
        let (finished_sender, finished_receiver) = mpsc::channel();
        let reader = thread::spawn(move || {
            started_sender.send(()).unwrap();
            finished_sender.send(get_settings(&path)).unwrap();
        });

        started_receiver.recv().unwrap();
        assert!(finished_receiver
            .recv_timeout(Duration::from_millis(100))
            .is_err());
        drop(guard);

        assert_eq!(
            finished_receiver
                .recv_timeout(Duration::from_secs(2))
                .unwrap()
                .unwrap(),
            expected
        );
        reader.join().unwrap();
    }

    #[test]
    fn rejects_unsafe_custom_prefixes() {
        for prefix in [
            "http://mirror.example/",
            "https://user:pass@mirror.example/",
            "https://mirror.example:8443/",
            "https://mirror.example/?target=github",
            "https://mirror.example/#fragment",
        ] {
            assert!(
                normalize_custom_prefix(prefix).is_err(),
                "accepted {prefix}"
            );
        }
        assert!(normalize_custom_prefix(&format!(
            "https://mirror.example/{}",
            "a".repeat(MAX_PREFIX_BYTES)
        ))
        .is_err());
    }

    #[test]
    fn applies_custom_prefix_only_with_official_sha256() {
        let settings = GithubDownloadSettings {
            mode: GithubDownloadMode::Custom,
            custom_prefix: Some("https://mirror.example/proxy/".into()),
        };
        let official = official_release_url();
        assert!(apply_github_mirror(&settings, &official, None).is_err());
        assert!(apply_github_mirror(&settings, &official, Some("sha512:abcd")).is_err());

        let applied = apply_github_mirror(&settings, &official, Some(&digest())).unwrap();
        assert!(applied.is_mirrored());
        assert_eq!(
            applied.url().as_str(),
            format!("https://mirror.example/proxy/{}", official.as_str())
        );
        let expected = "a1".repeat(32);
        assert_eq!(applied.expected_sha256(), Some(expected.as_str()));
        assert!(applied.validate_mirror_response_url(applied.url()).is_ok());
        assert!(applied
            .validate_mirror_response_url(
                &Url::parse("https://other.example/proxy/download.zip").unwrap()
            )
            .is_err());
        assert!(applied
            .validate_mirror_response_url(
                &Url::parse("https://mirror.example/proxy/download.zip?token=secret").unwrap()
            )
            .is_err());
    }

    #[test]
    fn direct_mode_keeps_the_official_url_without_requiring_a_digest() {
        let official = official_release_url();
        let applied =
            apply_github_mirror(&GithubDownloadSettings::default(), &official, None).unwrap();
        assert!(!applied.is_mirrored());
        assert_eq!(applied.url(), &official);
        assert_eq!(applied.expected_sha256(), None);
    }

    #[test]
    fn only_release_assets_and_ref_archives_can_be_mirrored() {
        for unsafe_url in [
            "https://github.com/Pathoschild/SMAPI",
            "https://github.com/Pathoschild/SMAPI/releases/latest",
            "https://github.com.evil.example/Pathoschild/SMAPI/releases/download/4.5.2/file.zip",
            "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/file.zip?raw=1",
            "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/%2Fescape.zip",
        ] {
            let parsed = Url::parse(unsafe_url).unwrap();
            assert!(
                validate_official_download_url(&parsed).is_err(),
                "accepted {unsafe_url}"
            );
        }

        let archive =
            Url::parse("https://github.com/owner/repository/archive/refs/tags/v1.0.0.zip").unwrap();
        assert!(validate_official_download_url(&archive).is_ok());
    }

    #[test]
    fn rejects_corrupted_and_oversized_configuration() {
        let directory = TestDirectory::new();
        fs::write(config_path(&directory.0), b"not json").unwrap();
        assert!(get_settings(&directory.0).is_err());

        fs::write(config_path(&directory.0), vec![b'x'; MAX_CONFIG_BYTES + 1]).unwrap();
        assert!(get_settings(&directory.0).is_err());
    }
}
