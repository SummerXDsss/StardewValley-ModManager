use std::{
    collections::HashSet,
    fs::File,
    io::{self, ErrorKind, Read, Seek, SeekFrom, Write},
    path::{Component, Path, PathBuf},
    sync::{
        atomic::{AtomicU64, Ordering},
        Mutex, OnceLock,
    },
    time::{Duration, Instant},
};

#[cfg(windows)]
use std::{
    ffi::OsStr,
    mem::size_of,
    os::windows::{
        ffi::OsStrExt,
        io::{AsRawHandle, FromRawHandle, OwnedHandle},
    },
    ptr,
};
#[cfg(unix)]
use std::{
    process::{Command, Stdio},
    thread,
};

use reqwest::{redirect::Policy, Client, StatusCode, Url};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tokio::io::AsyncWriteExt;

const API_URL: &str = "https://api.github.com/repos/Pathoschild/SMAPI/releases/latest";
const LATEST_RELEASE_URL: &str = "https://github.com/Pathoschild/SMAPI/releases/latest";
const RELEASE_TAG_PATH_PREFIX: &str = "/Pathoschild/SMAPI/releases/tag/";
const RELEASE_DOWNLOAD_PATH_PREFIX: &str = "/Pathoschild/SMAPI/releases/download/";
const USER_AGENT: &str =
    "Valley-Steward/0.1 (+https://github.com/SummerXDsss/StardewValley-ModManager)";
const CACHE_TTL: Duration = Duration::from_secs(15 * 60);
const METADATA_TIMEOUT: Duration = Duration::from_secs(15);
const DOWNLOAD_TIMEOUT: Duration = Duration::from_secs(10 * 60);
const INSTALL_TIMEOUT: Duration = Duration::from_secs(5 * 60);
#[cfg(unix)]
const INSTALL_POLL_INTERVAL: Duration = Duration::from_millis(100);
const MAX_REDIRECTS: usize = 10;
pub const MAX_INSTALLER_SIZE: u64 = 256 * 1024 * 1024;
const MAX_ARCHIVE_ENTRIES: usize = 2_048;
const MAX_EXTRACTED_FILE_SIZE: u64 = 64 * 1024 * 1024;
const MAX_EXTRACTED_TOTAL_SIZE: u64 = 512 * 1024 * 1024;
const MAX_ARCHIVE_PATH_BYTES: usize = 1_024;
const MAX_ARCHIVE_COMPONENTS: usize = 32;

static RELEASE_CACHE: OnceLock<Mutex<Option<CachedRelease>>> = OnceLock::new();
static PART_FILE_SEQUENCE: AtomicU64 = AtomicU64::new(0);

#[derive(Debug, Clone)]
struct CachedRelease {
    loaded_at: Instant,
    release: SmapiReleaseInfo,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
#[allow(dead_code)] // The other variants are constructed when compiling for their target OS.
pub enum SmapiPlatform {
    Windows,
    Macos,
    Linux,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum SmapiReleaseSource {
    GithubApi,
    GithubLatestRedirect,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SmapiReleaseAsset {
    pub id: Option<u64>,
    pub name: String,
    pub size: Option<u64>,
    pub download_url: String,
    pub digest: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SmapiReleaseInfo {
    pub version: String,
    pub tag_name: String,
    pub page_url: String,
    pub published_at: Option<String>,
    pub platform: SmapiPlatform,
    pub installer_entry: String,
    pub source: SmapiReleaseSource,
    pub asset: SmapiReleaseAsset,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DownloadedSmapiInstaller {
    pub path: String,
    pub file_name: String,
    pub version: String,
    pub size: u64,
    pub sha256: String,
    pub digest_verified: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SmapiInstallerExecution {
    pub release_version: String,
    pub platform: SmapiPlatform,
    pub installer_path: String,
    pub exit_code: i32,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallSmapiResult {
    pub version: String,
    pub platform: SmapiPlatform,
    pub installer_path: String,
    pub exit_code: i32,
    pub installed_version: Option<String>,
    pub game_path: String,
    pub message: String,
}

#[derive(Debug, Clone, Copy)]
struct ExtractionLimits {
    max_entries: usize,
    max_file_size: u64,
    max_total_size: u64,
}

const EXTRACTION_LIMITS: ExtractionLimits = ExtractionLimits {
    max_entries: MAX_ARCHIVE_ENTRIES,
    max_file_size: MAX_EXTRACTED_FILE_SIZE,
    max_total_size: MAX_EXTRACTED_TOTAL_SIZE,
};

struct ExtractedInstaller {
    directory: PathBuf,
    root: PathBuf,
    executable: PathBuf,
}

#[derive(Debug, Deserialize)]
struct ApiRelease {
    tag_name: String,
    html_url: String,
    published_at: Option<String>,
    #[serde(default)]
    draft: bool,
    #[serde(default)]
    prerelease: bool,
    #[serde(default)]
    assets: Vec<ApiReleaseAsset>,
}

#[derive(Debug, Deserialize)]
struct ApiReleaseAsset {
    id: u64,
    name: String,
    size: u64,
    browser_download_url: String,
    #[serde(default)]
    digest: Option<String>,
}

struct DownloadOutcome {
    size: u64,
    sha256: String,
    digest_verified: bool,
}

pub async fn latest_release() -> Result<SmapiReleaseInfo, String> {
    if let Some(release) = cached_release() {
        return Ok(release);
    }

    let client = metadata_client()?;
    let release = fetch_latest_release(&client).await?;
    store_cached_release(release.clone());
    Ok(release)
}

pub async fn download_latest_installer(
    cache_dir: &Path,
) -> Result<DownloadedSmapiInstaller, String> {
    let release = latest_release().await?;
    download_installer(cache_dir, &release).await
}

pub async fn download_installer(
    cache_dir: &Path,
    release: &SmapiReleaseInfo,
) -> Result<DownloadedSmapiInstaller, String> {
    validate_tag(&release.tag_name)?;
    let expected_name = expected_asset_name(&release.tag_name);
    if release.asset.name != expected_name
        || release
            .asset
            .name
            .to_ascii_lowercase()
            .contains("double-zipped")
    {
        return Err("SMAPI release did not select the standard installer asset".into());
    }
    if let Some(size) = release.asset.size {
        validate_asset_size(size)?;
    }

    let download_url = Url::parse(&release.asset.download_url)
        .map_err(|error| format!("Invalid SMAPI download URL: {error}"))?;
    validate_initial_download_url(&download_url, &release.tag_name, &expected_name)?;

    let release_dir = cache_dir
        .join("downloads")
        .join("smapi")
        .join(&release.tag_name);
    tokio::fs::create_dir_all(&release_dir)
        .await
        .map_err(|error| format!("Could not create the SMAPI cache directory: {error}"))?;

    let target = safe_download_target(&release_dir, &expected_name)?;
    let part = unique_part_path(&target)?;
    let client = download_client()?;
    let outcome = download_to_part(&client, download_url, release, &part).await;
    let outcome = match outcome {
        Ok(outcome) => outcome,
        Err(error) => {
            remove_if_exists(&part).await;
            return Err(error);
        }
    };

    if let Err(error) = replace_file(&part, &target).await {
        remove_if_exists(&part).await;
        return Err(error);
    }

    Ok(DownloadedSmapiInstaller {
        path: target.to_string_lossy().into_owned(),
        file_name: expected_name,
        version: release.version.clone(),
        size: outcome.size,
        sha256: outcome.sha256,
        digest_verified: outcome.digest_verified,
    })
}

pub fn install_downloaded_installer(
    cache_dir: &Path,
    downloaded: &DownloadedSmapiInstaller,
    game_path: &Path,
) -> Result<SmapiInstallerExecution, String> {
    validate_tag(&downloaded.version)?;
    if !game_path.is_absolute() || !game_path.is_dir() {
        return Err("The SMAPI game path must be an existing absolute directory".into());
    }

    let cache_root = cache_dir
        .canonicalize()
        .map_err(|error| format!("Could not resolve the app cache directory: {error}"))?;
    let archive_path = PathBuf::from(&downloaded.path)
        .canonicalize()
        .map_err(|error| format!("Could not resolve the downloaded SMAPI installer: {error}"))?;
    if !archive_path.starts_with(&cache_root)
        || archive_path.file_name() != Some(downloaded.file_name.as_ref())
    {
        return Err("The downloaded SMAPI installer is outside the app cache".into());
    }

    let platform = current_platform()?;
    let extraction_parent = cache_root
        .join("installers")
        .join("smapi")
        .join(&downloaded.version);
    std::fs::create_dir_all(&extraction_parent)
        .map_err(|error| format!("Could not create the SMAPI extraction directory: {error}"))?;
    let sequence = PART_FILE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    let extraction_directory =
        extraction_parent.join(format!(".install-{}-{sequence}", std::process::id()));
    std::fs::create_dir(&extraction_directory).map_err(|error| {
        format!("Could not create a private SMAPI extraction directory: {error}")
    })?;

    let extracted = match extract_installer_archive(
        &archive_path,
        &extraction_directory,
        downloaded,
        platform,
        EXTRACTION_LIMITS,
    ) {
        Ok(extracted) => extracted,
        Err(error) => {
            let _ = std::fs::remove_dir_all(&extraction_directory);
            return Err(error);
        }
    };

    let run_result = run_native_installer(&extracted.executable, &extracted.root, game_path);
    let _ = std::fs::remove_dir_all(&extracted.directory);
    let exit_code = run_result?;

    Ok(SmapiInstallerExecution {
        release_version: downloaded.version.clone(),
        platform,
        installer_path: archive_path.to_string_lossy().into_owned(),
        exit_code,
    })
}

fn extract_installer_archive(
    archive_path: &Path,
    destination: &Path,
    downloaded: &DownloadedSmapiInstaller,
    platform: SmapiPlatform,
    limits: ExtractionLimits,
) -> Result<ExtractedInstaller, String> {
    let mut archive_file = File::open(archive_path)
        .map_err(|error| format!("Could not open the SMAPI installer archive: {error}"))?;
    verify_downloaded_archive(&mut archive_file, downloaded)?;

    let mut archive = zip::ZipArchive::new(archive_file)
        .map_err(|error| format!("Could not read the SMAPI installer ZIP: {error}"))?;
    if archive.is_empty() {
        return Err("The SMAPI installer ZIP is empty".into());
    }
    if archive.len() > limits.max_entries {
        return Err(format!(
            "The SMAPI installer ZIP has too many entries ({} > {})",
            archive.len(),
            limits.max_entries
        ));
    }

    let expected_root = PathBuf::from(format!("SMAPI {} installer", downloaded.version));
    let mut extracted_paths = HashSet::with_capacity(archive.len());
    let mut total_size = 0_u64;

    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .map_err(|error| format!("Could not read SMAPI ZIP entry {index}: {error}"))?;
        validate_archive_entry_type(entry.is_dir(), entry.is_symlink(), entry.unix_mode())?;
        let relative_path = safe_archive_entry_path(&entry, &expected_root)?;
        let path_key = archive_path_key(&relative_path);
        if !extracted_paths.insert(path_key) {
            return Err(format!(
                "The SMAPI installer ZIP contains a duplicate path: {}",
                relative_path.display()
            ));
        }

        if entry.size() > limits.max_file_size {
            return Err(format!(
                "A SMAPI installer file exceeds the {} byte limit: {}",
                limits.max_file_size,
                relative_path.display()
            ));
        }
        total_size = total_size
            .checked_add(entry.size())
            .ok_or_else(|| "SMAPI installer extracted size overflowed".to_string())?;
        if total_size > limits.max_total_size {
            return Err(format!(
                "The extracted SMAPI installer exceeds the {} byte limit",
                limits.max_total_size
            ));
        }

        let output_path = destination.join(&relative_path);
        if entry.is_dir() {
            std::fs::create_dir_all(&output_path).map_err(|error| {
                format!(
                    "Could not create SMAPI installer directory {}: {error}",
                    relative_path.display()
                )
            })?;
            set_extracted_permissions(&output_path, entry.unix_mode(), true)?;
            continue;
        }

        if let Some(parent) = output_path.parent() {
            std::fs::create_dir_all(parent).map_err(|error| {
                format!("Could not create a SMAPI installer parent directory: {error}")
            })?;
        }
        let mut output = File::create(&output_path).map_err(|error| {
            format!(
                "Could not create SMAPI installer file {}: {error}",
                relative_path.display()
            )
        })?;
        let copied = io::copy(
            &mut entry.by_ref().take(limits.max_file_size + 1),
            &mut output,
        )
        .map_err(|error| {
            format!(
                "Could not extract SMAPI installer file {}: {error}",
                relative_path.display()
            )
        })?;
        if copied != entry.size() {
            return Err(format!(
                "SMAPI installer file size changed while extracting {}",
                relative_path.display()
            ));
        }
        output
            .flush()
            .map_err(|error| format!("Could not flush a SMAPI installer file: {error}"))?;
        set_extracted_permissions(&output_path, entry.unix_mode(), false)?;
    }

    let root = destination.join(&expected_root);
    let platform_directory = root
        .join("internal")
        .join(platform_directory_name(platform));
    let executable = platform_directory.join(platform_installer_name(platform));
    for required in [
        executable.as_path(),
        platform_directory.join("SMAPI.Installer.dll").as_path(),
        platform_directory.join("install.dat").as_path(),
    ] {
        if !required.is_file() {
            return Err(format!(
                "The SMAPI installer ZIP is missing {}",
                required
                    .strip_prefix(destination)
                    .unwrap_or(required)
                    .display()
            ));
        }
    }

    ensure_installer_executable(&executable)?;
    Ok(ExtractedInstaller {
        directory: destination.to_path_buf(),
        root,
        executable,
    })
}

fn verify_downloaded_archive(
    archive_file: &mut File,
    downloaded: &DownloadedSmapiInstaller,
) -> Result<(), String> {
    validate_asset_size(downloaded.size)?;
    if downloaded.sha256.len() != 64
        || !downloaded
            .sha256
            .bytes()
            .all(|byte| byte.is_ascii_hexdigit())
    {
        return Err("The cached SMAPI installer has an invalid SHA-256 value".into());
    }
    let metadata = archive_file
        .metadata()
        .map_err(|error| format!("Could not inspect the cached SMAPI installer: {error}"))?;
    if metadata.len() != downloaded.size {
        return Err(format!(
            "The cached SMAPI installer size changed (expected {}, received {})",
            downloaded.size,
            metadata.len()
        ));
    }

    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let count = archive_file
            .read(&mut buffer)
            .map_err(|error| format!("Could not verify the cached SMAPI installer: {error}"))?;
        if count == 0 {
            break;
        }
        hasher.update(&buffer[..count]);
    }
    let actual = format!("{:x}", hasher.finalize());
    if !actual.eq_ignore_ascii_case(&downloaded.sha256) {
        return Err(format!(
            "The cached SMAPI installer SHA-256 changed (expected {}, received {actual})",
            downloaded.sha256
        ));
    }
    archive_file
        .seek(SeekFrom::Start(0))
        .map_err(|error| format!("Could not rewind the SMAPI installer ZIP: {error}"))?;
    Ok(())
}

fn safe_archive_entry_path(
    entry: &zip::read::ZipFile<'_, File>,
    expected_root: &Path,
) -> Result<PathBuf, String> {
    let raw_name = std::str::from_utf8(entry.name_raw())
        .map_err(|_| "The SMAPI installer ZIP contains a non-UTF-8 path".to_string())?;
    if raw_name.is_empty()
        || raw_name.len() > MAX_ARCHIVE_PATH_BYTES
        || raw_name.contains('\\')
        || raw_name.contains('\0')
    {
        return Err("The SMAPI installer ZIP contains an unsafe path".into());
    }

    let enclosed = entry
        .enclosed_name()
        .ok_or_else(|| format!("Unsafe path in the SMAPI installer ZIP: {raw_name}"))?;
    let mut component_count = 0_usize;
    for component in enclosed.components() {
        let Component::Normal(component) = component else {
            return Err(format!(
                "Unsafe path component in the SMAPI installer ZIP: {raw_name}"
            ));
        };
        component_count += 1;
        let component = component
            .to_str()
            .ok_or_else(|| "The SMAPI installer ZIP path is not UTF-8".to_string())?;
        if component.is_empty()
            || component.ends_with([' ', '.'])
            || component.contains(':')
            || component.chars().any(char::is_control)
        {
            return Err(format!(
                "Unsafe path component in the SMAPI installer ZIP: {raw_name}"
            ));
        }
    }
    if component_count == 0 || component_count > MAX_ARCHIVE_COMPONENTS {
        return Err(format!(
            "The SMAPI installer ZIP path has too many components: {raw_name}"
        ));
    }
    if !enclosed.starts_with(expected_root) {
        return Err(format!(
            "The SMAPI installer ZIP contains a file outside {}: {raw_name}",
            expected_root.display()
        ));
    }
    if enclosed == expected_root && !entry.is_dir() {
        return Err("The SMAPI installer root entry must be a directory".into());
    }
    Ok(enclosed)
}

fn validate_archive_entry_type(
    is_directory: bool,
    is_symlink: bool,
    unix_mode: Option<u32>,
) -> Result<(), String> {
    if is_symlink {
        return Err("The SMAPI installer ZIP contains a symbolic link".into());
    }
    if let Some(mode) = unix_mode {
        let file_type = mode & 0o170_000;
        let expected_type = if is_directory { 0o040_000 } else { 0o100_000 };
        if file_type != 0 && file_type != expected_type {
            return Err("The SMAPI installer ZIP contains a special filesystem entry".into());
        }
    }
    Ok(())
}

fn archive_path_key(path: &Path) -> String {
    let key = path.to_string_lossy().replace('\\', "/");
    #[cfg(any(target_os = "windows", target_os = "macos"))]
    return key.to_lowercase();
    #[cfg(not(any(target_os = "windows", target_os = "macos")))]
    key
}

#[cfg(unix)]
fn set_extracted_permissions(
    path: &Path,
    archive_mode: Option<u32>,
    is_directory: bool,
) -> Result<(), String> {
    use std::os::unix::fs::PermissionsExt;

    let executable = archive_mode.is_some_and(|mode| mode & 0o111 != 0);
    let mode = if is_directory || executable {
        0o700
    } else {
        0o600
    };
    std::fs::set_permissions(path, std::fs::Permissions::from_mode(mode))
        .map_err(|error| format!("Could not set SMAPI installer permissions: {error}"))
}

#[cfg(not(unix))]
fn set_extracted_permissions(
    _path: &Path,
    _archive_mode: Option<u32>,
    _is_directory: bool,
) -> Result<(), String> {
    Ok(())
}

#[cfg(unix)]
fn ensure_installer_executable(path: &Path) -> Result<(), String> {
    use std::os::unix::fs::PermissionsExt;

    std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o700))
        .map_err(|error| format!("Could not make the SMAPI installer executable: {error}"))
}

#[cfg(not(unix))]
fn ensure_installer_executable(_path: &Path) -> Result<(), String> {
    Ok(())
}

fn platform_directory_name(platform: SmapiPlatform) -> &'static str {
    match platform {
        SmapiPlatform::Windows => "windows",
        SmapiPlatform::Macos => "macOS",
        SmapiPlatform::Linux => "linux",
    }
}

fn platform_installer_name(platform: SmapiPlatform) -> &'static str {
    match platform {
        SmapiPlatform::Windows => "SMAPI.Installer.exe",
        SmapiPlatform::Macos | SmapiPlatform::Linux => "SMAPI.Installer",
    }
}

#[cfg(unix)]
fn run_native_installer(
    executable: &Path,
    installer_root: &Path,
    game_path: &Path,
) -> Result<i32, String> {
    #[cfg(target_os = "macos")]
    clear_macos_quarantine(&installer_root.join("internal"));

    let mut child = Command::new(executable)
        .arg("--install")
        .arg("--game-path")
        .arg(game_path)
        .arg("--no-prompt")
        .current_dir(installer_root)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|error| format!("Could not start the SMAPI installer: {error}"))?;

    let started = Instant::now();
    loop {
        if let Some(status) = child
            .try_wait()
            .map_err(|error| format!("Could not read the SMAPI installer status: {error}"))?
        {
            return Ok(status.code().unwrap_or(-1));
        }
        if started.elapsed() >= INSTALL_TIMEOUT {
            let _ = child.kill();
            let _ = child.wait();
            return Err("The SMAPI installer timed out and was stopped".into());
        }
        thread::sleep(INSTALL_POLL_INTERVAL);
    }
}

#[cfg(target_os = "macos")]
fn clear_macos_quarantine(internal_directory: &Path) {
    let _ = Command::new("/usr/bin/xattr")
        .args(["-r", "-d", "com.apple.quarantine"])
        .arg(internal_directory)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status();
}

#[cfg(windows)]
fn run_native_installer(
    executable: &Path,
    installer_root: &Path,
    game_path: &Path,
) -> Result<i32, String> {
    use windows_sys::Win32::{
        Foundation::{WAIT_OBJECT_0, WAIT_TIMEOUT},
        System::Threading::{
            CreateProcessW, GetExitCodeProcess, TerminateProcess, WaitForSingleObject,
            CREATE_NEW_CONSOLE, PROCESS_INFORMATION, STARTF_USESHOWWINDOW, STARTUPINFOW,
        },
        UI::WindowsAndMessaging::SW_HIDE,
    };

    let application = wide_null(executable.as_os_str(), "SMAPI installer path")?;
    let current_directory = wide_null(installer_root.as_os_str(), "SMAPI installer directory")?;
    let arguments = [
        OsStr::new("--install"),
        OsStr::new("--game-path"),
        game_path.as_os_str(),
        OsStr::new("--no-prompt"),
    ];
    let mut command_line = windows_command_line(executable.as_os_str(), &arguments)?;

    let startup = STARTUPINFOW {
        cb: size_of::<STARTUPINFOW>() as u32,
        dwFlags: STARTF_USESHOWWINDOW,
        wShowWindow: SW_HIDE as u16,
        ..Default::default()
    };
    let mut process_information = PROCESS_INFORMATION::default();
    let created = unsafe {
        CreateProcessW(
            application.as_ptr(),
            command_line.as_mut_ptr(),
            ptr::null(),
            ptr::null(),
            0,
            CREATE_NEW_CONSOLE,
            ptr::null(),
            current_directory.as_ptr(),
            &startup,
            &mut process_information,
        )
    };
    if created == 0 {
        return Err(format!(
            "Could not start the hidden SMAPI installer: {}",
            std::io::Error::last_os_error()
        ));
    }

    let process = unsafe { OwnedHandle::from_raw_handle(process_information.hProcess) };
    let thread_handle = unsafe { OwnedHandle::from_raw_handle(process_information.hThread) };
    drop(thread_handle);

    let timeout_millis = u32::try_from(INSTALL_TIMEOUT.as_millis()).unwrap_or(u32::MAX);
    let wait_result = unsafe { WaitForSingleObject(process.as_raw_handle(), timeout_millis) };
    if wait_result == WAIT_TIMEOUT {
        let terminated = unsafe { TerminateProcess(process.as_raw_handle(), 1) };
        if terminated != 0 {
            let _ = unsafe { WaitForSingleObject(process.as_raw_handle(), 5_000) };
        }
        return Err("The SMAPI installer timed out and was stopped".into());
    }
    if wait_result != WAIT_OBJECT_0 {
        let terminated = unsafe { TerminateProcess(process.as_raw_handle(), 1) };
        if terminated != 0 {
            let _ = unsafe { WaitForSingleObject(process.as_raw_handle(), 5_000) };
        }
        return Err(format!(
            "Could not wait for the SMAPI installer: {}",
            std::io::Error::last_os_error()
        ));
    }

    let mut exit_code = 0_u32;
    let read_exit_code = unsafe { GetExitCodeProcess(process.as_raw_handle(), &mut exit_code) };
    if read_exit_code == 0 {
        return Err(format!(
            "Could not read the SMAPI installer exit code: {}",
            std::io::Error::last_os_error()
        ));
    }
    Ok(exit_code as i32)
}

#[cfg(windows)]
fn wide_null(value: &OsStr, label: &str) -> Result<Vec<u16>, String> {
    let mut wide = value.encode_wide().collect::<Vec<_>>();
    if wide.contains(&0) {
        return Err(format!("The {label} contains a NUL character"));
    }
    wide.push(0);
    Ok(wide)
}

#[cfg(windows)]
fn windows_command_line(executable: &OsStr, arguments: &[&OsStr]) -> Result<Vec<u16>, String> {
    let mut command_line = Vec::new();
    append_quoted_windows_argument(&mut command_line, executable)?;
    for argument in arguments {
        command_line.push(b' ' as u16);
        append_quoted_windows_argument(&mut command_line, argument)?;
    }
    if command_line.len() >= 32_767 {
        return Err("The SMAPI installer command line is too long".into());
    }
    command_line.push(0);
    Ok(command_line)
}

#[cfg(windows)]
fn append_quoted_windows_argument(output: &mut Vec<u16>, argument: &OsStr) -> Result<(), String> {
    let units = argument.encode_wide().collect::<Vec<_>>();
    if units.contains(&0) {
        return Err("A SMAPI installer argument contains a NUL character".into());
    }

    output.push(b'"' as u16);
    let mut backslashes = 0_usize;
    for unit in units {
        if unit == b'\\' as u16 {
            backslashes += 1;
            continue;
        }
        if unit == b'"' as u16 {
            output.extend(std::iter::repeat_n(b'\\' as u16, backslashes * 2 + 1));
            output.push(unit);
        } else {
            output.extend(std::iter::repeat_n(b'\\' as u16, backslashes));
            output.push(unit);
        }
        backslashes = 0;
    }
    output.extend(std::iter::repeat_n(b'\\' as u16, backslashes * 2));
    output.push(b'"' as u16);
    Ok(())
}

async fn fetch_latest_release(client: &Client) -> Result<SmapiReleaseInfo, String> {
    let response = client
        .get(API_URL)
        .header("Accept", "application/vnd.github+json")
        .header("X-GitHub-Api-Version", "2022-11-28")
        .send()
        .await
        .map_err(|error| format!("Could not connect to the GitHub Releases API: {error}"))?;

    if response.status() == StatusCode::FORBIDDEN {
        return fetch_latest_release_from_redirect(client).await;
    }

    let release = response
        .error_for_status()
        .map_err(|error| format!("GitHub Releases API returned an error: {error}"))?
        .json::<ApiRelease>()
        .await
        .map_err(|error| format!("Could not parse the GitHub release metadata: {error}"))?;
    release_from_api(release)
}

async fn fetch_latest_release_from_redirect(client: &Client) -> Result<SmapiReleaseInfo, String> {
    let response = client
        .get(LATEST_RELEASE_URL)
        .send()
        .await
        .map_err(|error| format!("Could not resolve the latest SMAPI release: {error}"))?
        .error_for_status()
        .map_err(|error| format!("GitHub latest release page returned an error: {error}"))?;
    release_from_redirect_url(response.url())
}

fn release_from_api(release: ApiRelease) -> Result<SmapiReleaseInfo, String> {
    if release.draft || release.prerelease {
        return Err("GitHub returned a non-stable SMAPI release".into());
    }
    validate_tag(&release.tag_name)?;

    let page_url = Url::parse(&release.html_url)
        .map_err(|error| format!("Invalid SMAPI release page URL: {error}"))?;
    validate_release_page_url(&page_url, &release.tag_name)?;

    let expected_name = expected_asset_name(&release.tag_name);
    let mut matching_assets = release.assets.into_iter().filter(|asset| {
        asset.name == expected_name && !asset.name.to_ascii_lowercase().contains("double-zipped")
    });
    let asset = matching_assets
        .next()
        .ok_or_else(|| format!("Release does not contain {expected_name}"))?;
    if matching_assets.next().is_some() {
        return Err(format!(
            "Release contains more than one standard installer named {expected_name}"
        ));
    }
    validate_asset_size(asset.size)?;

    let download_url = Url::parse(&asset.browser_download_url)
        .map_err(|error| format!("Invalid SMAPI asset URL: {error}"))?;
    validate_initial_download_url(&download_url, &release.tag_name, &expected_name)?;
    let digest = normalize_digest(asset.digest)?;
    let platform = current_platform()?;

    Ok(SmapiReleaseInfo {
        version: display_version(&release.tag_name).to_string(),
        tag_name: release.tag_name,
        page_url: page_url.into(),
        published_at: release.published_at,
        platform,
        installer_entry: installer_entry_for(platform).to_string(),
        source: SmapiReleaseSource::GithubApi,
        asset: SmapiReleaseAsset {
            id: Some(asset.id),
            name: expected_name,
            size: Some(asset.size),
            download_url: download_url.into(),
            digest,
        },
    })
}

fn release_from_redirect_url(url: &Url) -> Result<SmapiReleaseInfo, String> {
    validate_github_authority(url)?;
    if url.query().is_some() || url.fragment().is_some() {
        return Err("GitHub latest release redirect contained unexpected URL data".into());
    }
    let tag = url
        .path()
        .strip_prefix(RELEASE_TAG_PATH_PREFIX)
        .filter(|tag| !tag.contains('/'))
        .ok_or_else(|| "GitHub latest release did not redirect to an SMAPI tag".to_string())?;
    validate_tag(tag)?;
    validate_release_page_url(url, tag)?;

    let expected_name = expected_asset_name(tag);
    let download_url =
        format!("https://github.com/Pathoschild/SMAPI/releases/download/{tag}/{expected_name}");
    let parsed_download_url = Url::parse(&download_url)
        .map_err(|error| format!("Could not construct the SMAPI asset URL: {error}"))?;
    validate_initial_download_url(&parsed_download_url, tag, &expected_name)?;
    let platform = current_platform()?;

    Ok(SmapiReleaseInfo {
        version: display_version(tag).to_string(),
        tag_name: tag.to_string(),
        page_url: url.as_str().to_string(),
        published_at: None,
        platform,
        installer_entry: installer_entry_for(platform).to_string(),
        source: SmapiReleaseSource::GithubLatestRedirect,
        asset: SmapiReleaseAsset {
            id: None,
            name: expected_name,
            size: None,
            download_url,
            digest: None,
        },
    })
}

async fn download_to_part(
    client: &Client,
    url: Url,
    release: &SmapiReleaseInfo,
    part: &Path,
) -> Result<DownloadOutcome, String> {
    let mut response = client
        .get(url)
        .send()
        .await
        .map_err(|error| format!("Could not download the SMAPI installer: {error}"))?
        .error_for_status()
        .map_err(|error| format!("SMAPI installer server returned an error: {error}"))?;
    validate_redirect_url(response.url())?;

    if let Some(content_length) = response.content_length() {
        validate_asset_size(content_length)?;
        if let Some(expected_size) = release.asset.size {
            if content_length != expected_size {
                return Err(format!(
                    "SMAPI installer size changed (expected {expected_size}, received {content_length})"
                ));
            }
        }
    }

    let mut file = tokio::fs::File::create(part)
        .await
        .map_err(|error| format!("Could not create the temporary SMAPI file: {error}"))?;
    let mut total = 0_u64;
    let mut hasher = Sha256::new();
    let mut signature = Vec::with_capacity(4);
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|error| format!("SMAPI installer download failed: {error}"))?
    {
        total = total
            .checked_add(chunk.len() as u64)
            .ok_or_else(|| "SMAPI installer size overflowed".to_string())?;
        if total > MAX_INSTALLER_SIZE {
            return Err(format!(
                "SMAPI installer exceeds the {} byte limit",
                MAX_INSTALLER_SIZE
            ));
        }
        if signature.len() < 4 {
            let needed = 4 - signature.len();
            signature.extend_from_slice(&chunk[..chunk.len().min(needed)]);
        }
        hasher.update(&chunk);
        file.write_all(&chunk)
            .await
            .map_err(|error| format!("Could not write the SMAPI installer: {error}"))?;
    }
    if total == 0 {
        return Err("SMAPI installer download was empty".into());
    }
    if signature.as_slice() != b"PK\x03\x04" {
        return Err("SMAPI installer response is not a ZIP archive".into());
    }
    if let Some(expected_size) = release.asset.size {
        if total != expected_size {
            return Err(format!(
                "SMAPI installer is incomplete (expected {expected_size}, received {total})"
            ));
        }
    }

    file.flush()
        .await
        .map_err(|error| format!("Could not flush the SMAPI installer: {error}"))?;
    file.sync_all()
        .await
        .map_err(|error| format!("Could not persist the SMAPI installer: {error}"))?;
    drop(file);

    let sha256 = format!("{:x}", hasher.finalize());
    let digest_verified = verify_sha256_digest(release.asset.digest.as_deref(), &sha256)?;

    Ok(DownloadOutcome {
        size: total,
        sha256,
        digest_verified,
    })
}

fn metadata_client() -> Result<Client, String> {
    Client::builder()
        .user_agent(USER_AGENT)
        .timeout(METADATA_TIMEOUT)
        .build()
        .map_err(|error| format!("Could not initialize the GitHub client: {error}"))
}

fn download_client() -> Result<Client, String> {
    Client::builder()
        .user_agent(USER_AGENT)
        .timeout(DOWNLOAD_TIMEOUT)
        .redirect(Policy::custom(|attempt| {
            if attempt.previous().len() >= MAX_REDIRECTS {
                return attempt.error(std::io::Error::new(
                    ErrorKind::InvalidData,
                    "too many SMAPI download redirects",
                ));
            }
            if let Err(error) = validate_redirect_url(attempt.url()) {
                return attempt.error(std::io::Error::new(ErrorKind::PermissionDenied, error));
            }
            attempt.follow()
        }))
        .build()
        .map_err(|error| format!("Could not initialize the SMAPI download client: {error}"))
}

fn cached_release() -> Option<SmapiReleaseInfo> {
    let cache = RELEASE_CACHE.get_or_init(|| Mutex::new(None));
    let cached = cache.lock().ok()?;
    let entry = cached.as_ref()?;
    (entry.loaded_at.elapsed() < CACHE_TTL).then(|| entry.release.clone())
}

fn store_cached_release(release: SmapiReleaseInfo) {
    let cache = RELEASE_CACHE.get_or_init(|| Mutex::new(None));
    if let Ok(mut cached) = cache.lock() {
        *cached = Some(CachedRelease {
            loaded_at: Instant::now(),
            release,
        });
    }
}

fn expected_asset_name(tag: &str) -> String {
    format!("SMAPI-{tag}-installer.zip")
}

fn display_version(tag: &str) -> &str {
    tag.strip_prefix('v')
        .or_else(|| tag.strip_prefix('V'))
        .filter(|version| version.starts_with(|character: char| character.is_ascii_digit()))
        .unwrap_or(tag)
}

fn validate_tag(tag: &str) -> Result<(), String> {
    let valid = !tag.is_empty()
        && tag.len() <= 64
        && !tag.starts_with('.')
        && !tag.contains("..")
        && tag
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_' | b'+'));
    if valid {
        Ok(())
    } else {
        Err("SMAPI release tag is not safe to use".into())
    }
}

fn validate_asset_size(size: u64) -> Result<(), String> {
    if size == 0 {
        Err("SMAPI installer has an empty size".into())
    } else if size > MAX_INSTALLER_SIZE {
        Err(format!(
            "SMAPI installer exceeds the {} byte limit",
            MAX_INSTALLER_SIZE
        ))
    } else {
        Ok(())
    }
}

fn normalize_digest(digest: Option<String>) -> Result<Option<String>, String> {
    let Some(digest) = digest else {
        return Ok(None);
    };
    let digest = digest.trim();
    if let Some(value) = digest
        .strip_prefix("sha256:")
        .or_else(|| digest.strip_prefix("SHA256:"))
    {
        if value.len() != 64 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err("GitHub returned an invalid SHA-256 digest".into());
        }
        return Ok(Some(format!("sha256:{}", value.to_ascii_lowercase())));
    }
    Ok(Some(digest.to_string()))
}

fn verify_sha256_digest(expected_digest: Option<&str>, actual: &str) -> Result<bool, String> {
    let Some(expected) = expected_digest.and_then(|digest| digest.strip_prefix("sha256:")) else {
        return Ok(false);
    };
    if actual.eq_ignore_ascii_case(expected) {
        Ok(true)
    } else {
        Err(format!(
            "SMAPI installer SHA-256 mismatch (expected {expected}, received {actual})"
        ))
    }
}

fn validate_github_authority(url: &Url) -> Result<(), String> {
    if url.scheme() != "https"
        || url.host_str() != Some("github.com")
        || !url.username().is_empty()
        || url.password().is_some()
        || url.port().is_some()
    {
        return Err("URL is not an allowed GitHub HTTPS URL".into());
    }
    Ok(())
}

fn validate_release_page_url(url: &Url, tag: &str) -> Result<(), String> {
    validate_github_authority(url)?;
    let expected_path = format!("{RELEASE_TAG_PATH_PREFIX}{tag}");
    if url.path() != expected_path || url.query().is_some() || url.fragment().is_some() {
        return Err("URL is not the expected official SMAPI release page".into());
    }
    Ok(())
}

fn validate_initial_download_url(url: &Url, tag: &str, asset_name: &str) -> Result<(), String> {
    validate_github_authority(url)?;
    let expected_path = format!("{RELEASE_DOWNLOAD_PATH_PREFIX}{tag}/{asset_name}");
    if url.path() != expected_path || url.query().is_some() || url.fragment().is_some() {
        return Err("URL is not the expected official SMAPI release asset".into());
    }
    Ok(())
}

fn validate_redirect_url(url: &Url) -> Result<(), String> {
    if url.scheme() != "https"
        || !url.username().is_empty()
        || url.password().is_some()
        || url.port().is_some()
    {
        return Err("SMAPI download redirect is not a plain HTTPS URL".into());
    }

    match url.host_str() {
        Some("github.com") if url.path().starts_with(RELEASE_DOWNLOAD_PATH_PREFIX) => Ok(()),
        Some("release-assets.githubusercontent.com")
        | Some("objects.githubusercontent.com")
        | Some("github-releases.githubusercontent.com") => Ok(()),
        _ => Err("SMAPI download redirected to an untrusted host".into()),
    }
}

fn safe_download_target(directory: &Path, file_name: &str) -> Result<PathBuf, String> {
    let safe_name = Path::new(file_name)
        .file_name()
        .and_then(|name| name.to_str())
        .filter(|name| *name == file_name && !name.is_empty())
        .ok_or_else(|| "SMAPI installer file name is not safe".to_string())?;
    let target = directory.join(safe_name);
    if target.parent() != Some(directory) {
        return Err("SMAPI installer file name escaped the cache directory".into());
    }
    Ok(target)
}

fn unique_part_path(target: &Path) -> Result<PathBuf, String> {
    let file_name = target
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| "SMAPI installer cache path is invalid".to_string())?;
    let sequence = PART_FILE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    Ok(target.with_file_name(format!(
        ".{file_name}.{}.{}.part",
        std::process::id(),
        sequence
    )))
}

async fn replace_file(part: &Path, target: &Path) -> Result<(), String> {
    match tokio::fs::remove_file(target).await {
        Ok(()) => {}
        Err(error) if error.kind() == ErrorKind::NotFound => {}
        Err(error) => {
            return Err(format!(
                "Could not replace the cached SMAPI installer: {error}"
            ));
        }
    }
    tokio::fs::rename(part, target)
        .await
        .map_err(|error| format!("Could not finalize the SMAPI installer download: {error}"))
}

async fn remove_if_exists(path: &Path) {
    let _ = tokio::fs::remove_file(path).await;
}

fn current_platform() -> Result<SmapiPlatform, String> {
    #[cfg(target_os = "windows")]
    {
        return Ok(SmapiPlatform::Windows);
    }
    #[cfg(target_os = "macos")]
    {
        return Ok(SmapiPlatform::Macos);
    }
    #[cfg(target_os = "linux")]
    {
        return Ok(SmapiPlatform::Linux);
    }
    #[allow(unreachable_code)]
    Err("SMAPI installer is only supported on Windows, macOS, and Linux".into())
}

fn installer_entry_for(platform: SmapiPlatform) -> &'static str {
    match platform {
        SmapiPlatform::Windows => "install on Windows.bat",
        SmapiPlatform::Macos => "install on macOS.command",
        SmapiPlatform::Linux => "install on Linux.sh",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use zip::write::SimpleFileOptions;

    struct TestDirectory(PathBuf);

    impl TestDirectory {
        fn new() -> Self {
            let sequence = PART_FILE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "valley-steward-smapi-test-{}-{sequence}",
                std::process::id()
            ));
            std::fs::create_dir(&path).unwrap();
            Self(path)
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TestDirectory {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn api_asset(name: &str, url: &str) -> ApiReleaseAsset {
        ApiReleaseAsset {
            id: 42,
            name: name.into(),
            size: 41_800_000,
            browser_download_url: url.into(),
            digest: Some(
                "sha256:dd01ddca7b566bfe0d3b3d2d03833496abc56c53da976241f2ab443f5484acc4".into(),
            ),
        }
    }

    fn api_release(assets: Vec<ApiReleaseAsset>) -> ApiRelease {
        ApiRelease {
            tag_name: "4.5.2".into(),
            html_url: "https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2".into(),
            published_at: Some("2026-03-25T03:02:01Z".into()),
            draft: false,
            prerelease: false,
            assets,
        }
    }

    fn write_test_archive(directory: &Path, entries: &[(&str, &[u8])]) -> DownloadedSmapiInstaller {
        let file_name = "SMAPI-4.5.2-installer.zip";
        let path = directory.join(file_name);
        let file = File::create(&path).unwrap();
        let mut archive = zip::ZipWriter::new(file);
        for (name, content) in entries {
            let options = SimpleFileOptions::default()
                .compression_method(zip::CompressionMethod::Stored)
                .unix_permissions(if name.ends_with("SMAPI.Installer.exe") {
                    0o755
                } else {
                    0o644
                });
            if name.ends_with('/') {
                archive.add_directory(*name, options).unwrap();
            } else {
                archive.start_file(*name, options).unwrap();
                archive.write_all(content).unwrap();
            }
        }
        archive.finish().unwrap();

        let bytes = std::fs::read(&path).unwrap();
        DownloadedSmapiInstaller {
            path: path.to_string_lossy().into_owned(),
            file_name: file_name.into(),
            version: "4.5.2".into(),
            size: bytes.len() as u64,
            sha256: format!("{:x}", Sha256::digest(&bytes)),
            digest_verified: true,
        }
    }

    fn valid_windows_archive_entries() -> Vec<(&'static str, &'static [u8])> {
        vec![
            ("SMAPI 4.5.2 installer/", b""),
            ("SMAPI 4.5.2 installer/internal/", b""),
            ("SMAPI 4.5.2 installer/internal/windows/", b""),
            (
                "SMAPI 4.5.2 installer/internal/windows/SMAPI.Installer.exe",
                b"test executable",
            ),
            (
                "SMAPI 4.5.2 installer/internal/windows/SMAPI.Installer.dll",
                b"test library",
            ),
            (
                "SMAPI 4.5.2 installer/internal/windows/install.dat",
                b"test payload",
            ),
        ]
    }

    #[test]
    fn selects_only_the_standard_installer_asset() {
        let standard_name = "SMAPI-4.5.2-installer.zip";
        let release = release_from_api(api_release(vec![
            api_asset(
                "SMAPI-4.5.2-installer-double-zipped.zip",
                "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer-double-zipped.zip",
            ),
            api_asset(
                standard_name,
                "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip",
            ),
        ]))
        .expect("the standard SMAPI installer should be selected");

        assert_eq!(release.version, "4.5.2");
        assert_eq!(release.asset.name, standard_name);
        assert_eq!(release.source, SmapiReleaseSource::GithubApi);
        assert!(release.asset.digest.is_some());
    }

    #[test]
    fn rejects_a_release_with_only_the_double_zipped_asset() {
        let result = release_from_api(api_release(vec![api_asset(
            "SMAPI-4.5.2-installer-double-zipped.zip",
            "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer-double-zipped.zip",
        )]));
        assert!(result.is_err());
    }

    #[test]
    fn rejects_non_stable_releases() {
        let mut release = api_release(vec![]);
        release.prerelease = true;
        assert!(release_from_api(release).is_err());
    }

    #[test]
    fn maps_each_supported_platform_to_the_archive_entry() {
        assert_eq!(
            installer_entry_for(SmapiPlatform::Windows),
            "install on Windows.bat"
        );
        assert_eq!(
            installer_entry_for(SmapiPlatform::Macos),
            "install on macOS.command"
        );
        assert_eq!(
            installer_entry_for(SmapiPlatform::Linux),
            "install on Linux.sh"
        );
    }

    #[test]
    fn parses_only_the_official_latest_release_redirect() {
        let url = Url::parse("https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2")
            .expect("valid test URL");
        let release = release_from_redirect_url(&url).expect("official redirect should work");
        assert_eq!(release.tag_name, "4.5.2");
        assert_eq!(release.source, SmapiReleaseSource::GithubLatestRedirect);
        assert_eq!(release.asset.name, "SMAPI-4.5.2-installer.zip");
        assert_eq!(release.asset.size, None);

        let wrong_repo = Url::parse("https://github.com/other/SMAPI/releases/tag/4.5.2").unwrap();
        assert!(release_from_redirect_url(&wrong_repo).is_err());
        let nested_tag =
            Url::parse("https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2/extra").unwrap();
        assert!(release_from_redirect_url(&nested_tag).is_err());
    }

    #[test]
    fn validates_initial_and_redirect_download_hosts() {
        let initial = Url::parse("https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip").unwrap();
        assert!(
            validate_initial_download_url(&initial, "4.5.2", "SMAPI-4.5.2-installer.zip").is_ok()
        );

        let wrong_repo = Url::parse(
            "https://github.com/other/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip",
        )
        .unwrap();
        assert!(
            validate_initial_download_url(&wrong_repo, "4.5.2", "SMAPI-4.5.2-installer.zip")
                .is_err()
        );

        let release_cdn =
            Url::parse("https://release-assets.githubusercontent.com/github-production-release-asset/file?sp=x")
                .unwrap();
        assert!(validate_redirect_url(&release_cdn).is_ok());
        let lookalike =
            Url::parse("https://release-assets.githubusercontent.com.evil.test/file").unwrap();
        assert!(validate_redirect_url(&lookalike).is_err());
        let insecure = Url::parse("http://release-assets.githubusercontent.com/file").unwrap();
        assert!(validate_redirect_url(&insecure).is_err());
    }

    #[test]
    fn rejects_unsafe_tags_and_cache_file_names() {
        for tag in ["", ".", "..", "../4.5.2", "4.5.2/evil", "4.5.2?x"] {
            assert!(validate_tag(tag).is_err(), "tag should be rejected: {tag}");
        }
        assert!(validate_tag("4.5.2-alpha+1").is_ok());
        assert!(safe_download_target(Path::new("cache"), "../installer.zip").is_err());
    }

    #[test]
    fn normalizes_only_valid_sha256_digests() {
        let digest = normalize_digest(Some(
            "SHA256:DD01DDCA7B566BFE0D3B3D2D03833496ABC56C53DA976241F2AB443F5484ACC4".into(),
        ))
        .expect("valid digest");
        assert_eq!(
            digest.as_deref(),
            Some("sha256:dd01ddca7b566bfe0d3b3d2d03833496abc56c53da976241f2ab443f5484acc4")
        );
        assert!(normalize_digest(Some("sha256:1234".into())).is_err());
        assert_eq!(
            normalize_digest(Some("sha512:abcd".into())).unwrap(),
            Some("sha512:abcd".into())
        );
    }

    #[test]
    fn verifies_sha256_when_github_provides_it() {
        let actual = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        assert!(verify_sha256_digest(Some(&format!("sha256:{actual}")), actual).unwrap());
        assert!(verify_sha256_digest(
            Some("sha256:dd01ddca7b566bfe0d3b3d2d03833496abc56c53da976241f2ab443f5484acc4"),
            actual,
        )
        .is_err());
        assert!(!verify_sha256_digest(None, actual).unwrap());
        assert!(!verify_sha256_digest(Some("sha512:abcd"), actual).unwrap());
    }

    #[test]
    fn enforces_the_installer_size_limit() {
        assert!(validate_asset_size(0).is_err());
        assert!(validate_asset_size(MAX_INSTALLER_SIZE).is_ok());
        assert!(validate_asset_size(MAX_INSTALLER_SIZE + 1).is_err());
    }

    #[test]
    fn safely_extracts_only_the_expected_installer_tree() {
        let temporary = TestDirectory::new();
        let downloaded = write_test_archive(temporary.path(), &valid_windows_archive_entries());
        let destination = temporary.path().join("extract");
        std::fs::create_dir(&destination).unwrap();

        let extracted = extract_installer_archive(
            Path::new(&downloaded.path),
            &destination,
            &downloaded,
            SmapiPlatform::Windows,
            EXTRACTION_LIMITS,
        )
        .unwrap();
        assert!(extracted.executable.is_file());
        assert!(extracted.root.starts_with(&destination));
        assert_eq!(
            std::fs::read(extracted.executable).unwrap(),
            b"test executable"
        );
    }

    #[test]
    fn rejects_zip_slip_and_files_outside_the_release_root() {
        for unsafe_name in [
            "../outside.txt",
            "SMAPI 4.5.2 installer/../../outside.txt",
            "another root/file.txt",
            r"SMAPI 4.5.2 installer\outside.txt",
        ] {
            let temporary = TestDirectory::new();
            let downloaded = write_test_archive(temporary.path(), &[(unsafe_name, b"bad")]);
            let destination = temporary.path().join("extract");
            std::fs::create_dir(&destination).unwrap();
            assert!(extract_installer_archive(
                Path::new(&downloaded.path),
                &destination,
                &downloaded,
                SmapiPlatform::Windows,
                EXTRACTION_LIMITS,
            )
            .is_err());
            assert!(!temporary.path().join("outside.txt").exists());
        }
    }

    #[test]
    fn enforces_extraction_limits_and_rejects_special_entries() {
        let temporary = TestDirectory::new();
        let downloaded = write_test_archive(temporary.path(), &valid_windows_archive_entries());
        let destination = temporary.path().join("extract");
        std::fs::create_dir(&destination).unwrap();
        let tiny_limits = ExtractionLimits {
            max_entries: 32,
            max_file_size: 4,
            max_total_size: 64,
        };
        assert!(extract_installer_archive(
            Path::new(&downloaded.path),
            &destination,
            &downloaded,
            SmapiPlatform::Windows,
            tiny_limits,
        )
        .is_err());

        assert!(validate_archive_entry_type(false, true, Some(0o120_777)).is_err());
        assert!(validate_archive_entry_type(false, false, Some(0o010_644)).is_err());
        assert!(validate_archive_entry_type(false, false, Some(0o100_644)).is_ok());
    }

    #[test]
    fn rechecks_the_cached_archive_hash_before_extraction() {
        let temporary = TestDirectory::new();
        let mut downloaded = write_test_archive(temporary.path(), &valid_windows_archive_entries());
        downloaded.sha256 = "0".repeat(64);
        let destination = temporary.path().join("extract");
        std::fs::create_dir(&destination).unwrap();
        assert!(extract_installer_archive(
            Path::new(&downloaded.path),
            &destination,
            &downloaded,
            SmapiPlatform::Windows,
            EXTRACTION_LIMITS,
        )
        .is_err());
    }

    #[cfg(windows)]
    #[test]
    fn quotes_windows_installer_arguments_without_shell_parsing() {
        let arguments = [
            OsStr::new("--install"),
            OsStr::new("--game-path"),
            OsStr::new(r"D:\Games\Stardew Valley"),
            OsStr::new("--no-prompt"),
        ];
        let command = windows_command_line(
            OsStr::new(r"C:\SMAPI Installer\SMAPI.Installer.exe"),
            &arguments,
        )
        .unwrap();
        let command = String::from_utf16(&command[..command.len() - 1]).unwrap();
        assert_eq!(
            command,
            r#""C:\SMAPI Installer\SMAPI.Installer.exe" "--install" "--game-path" "D:\Games\Stardew Valley" "--no-prompt""#
        );

        let mut quoted = Vec::new();
        append_quoted_windows_argument(&mut quoted, OsStr::new(r#"a\b"c\"#)).unwrap();
        assert_eq!(String::from_utf16(&quoted).unwrap(), r#""a\b\"c\\""#);
    }
}
