use std::{
    io::ErrorKind,
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicU64, Ordering},
        Mutex, OnceLock,
    },
    time::{Duration, Instant},
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
const MAX_REDIRECTS: usize = 10;
pub const MAX_INSTALLER_SIZE: u64 = 256 * 1024 * 1024;

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
}
