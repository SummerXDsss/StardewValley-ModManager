use std::{
    collections::HashSet,
    path::{Path, PathBuf},
    sync::{Mutex, OnceLock},
    time::{Duration, Instant},
};

use keyring::Entry;
use reqwest::{
    header::{HeaderMap, HeaderValue, CONTENT_DISPOSITION},
    Client,
};
use serde::Deserialize;
use tokio::io::AsyncWriteExt;

use crate::models::{
    DownloadedModFile, NexusAuthStatus, NexusFileVersion, NexusModDetails, NexusModFile, RemoteMod,
};

const API_BASE: &str = "https://api.nexusmods.com/v3";
const LEGACY_API_BASE: &str = "https://api.nexusmods.com/v1";
const TRENDING_URL: &str = "https://api.nexusmods.com/v3/games/stardewvalley/trending-mods";
const CACHE_TTL: Duration = Duration::from_secs(15 * 60);
const KEYRING_SERVICE: &str = "com.valleysteward.app";
const KEYRING_USER: &str = "nexus-api-key";

static CACHE: OnceLock<Mutex<Option<CachedMods>>> = OnceLock::new();

#[derive(Clone)]
struct CachedMods {
    loaded_at: Instant,
    mods: Vec<RemoteMod>,
}

#[derive(Deserialize)]
struct Envelope<T> {
    data: T,
}

#[derive(Deserialize)]
struct TrendingData {
    mods: Vec<TrendingMod>,
}

#[derive(Deserialize)]
struct TrendingMod {
    name: String,
    author: Option<String>,
    summary: Option<String>,
    picture_url: Option<String>,
    mod_page_url: String,
}

#[derive(Deserialize)]
struct ApiMod {
    id: String,
    game_scoped_id: String,
    name: Option<String>,
}

#[derive(Deserialize)]
struct ApiModFiles {
    mod_files: Vec<ApiModFile>,
}

#[derive(Deserialize)]
struct ApiModFile {
    id: String,
    name: String,
    is_active: bool,
    last_file_uploaded_at: Option<String>,
    versions_count: u64,
}

#[derive(Deserialize)]
struct ApiFileVersions {
    versions: Vec<ApiFileVersion>,
}

#[derive(Deserialize)]
struct ApiFileVersion {
    id: String,
    game_scoped_id: String,
    name: String,
    version: String,
    category: String,
    uploaded_at: String,
    #[serde(default)]
    is_primary: bool,
}

#[derive(Deserialize)]
struct DownloadMirror {
    #[serde(rename = "URI")]
    uri: String,
}

#[derive(Deserialize)]
struct LegacyMod {
    mod_id: u64,
    name: String,
    #[serde(default)]
    author: Option<String>,
    #[serde(default)]
    uploaded_by: Option<String>,
    #[serde(default)]
    summary: Option<String>,
    #[serde(default)]
    picture_url: Option<String>,
    #[serde(default)]
    version: Option<String>,
    #[serde(default)]
    mod_downloads: u64,
    #[serde(default)]
    updated_time: Option<String>,
}

pub async fn discover() -> Result<Vec<RemoteMod>, String> {
    if let Some(mods) = cached_mods() {
        return Ok(mods);
    }

    let response = public_client()?
        .get(TRENDING_URL)
        .header("Accept", "application/json")
        .send()
        .await
        .map_err(|error| format!("无法连接 Nexus Mods：{error}"))?
        .error_for_status()
        .map_err(|error| format!("Nexus Mods 返回错误：{error}"))?
        .json::<Envelope<TrendingData>>()
        .await
        .map_err(|error| format!("无法解析 Nexus Mods 数据：{error}"))?;

    let mut mods = response
        .data
        .mods
        .into_iter()
        .map(|item| {
            let provider_id = game_scoped_id_from_url(&item.mod_page_url);
            RemoteMod {
                id: format!("nexus:{}", item.mod_page_url),
                name: item.name,
                author: item.author.unwrap_or_else(|| "Nexus Mods 作者".into()),
                summary: item.summary.unwrap_or_else(|| "Nexus Mods 热门 Mod".into()),
                source: "Nexus Mods".into(),
                version: "热门".into(),
                popularity: "趋势榜".into(),
                compatibility: "查看发布说明".into(),
                updated_at: "实时".into(),
                page_url: item.mod_page_url,
                download_url: None,
                image_url: item.picture_url,
                provider_id,
            }
        })
        .collect::<Vec<_>>();

    if let Ok(client) = authenticated_client() {
        for endpoint in ["trending", "latest_updated", "latest_added"] {
            if let Ok(items) = get_json::<Vec<LegacyMod>>(
                &client,
                &format!("{LEGACY_API_BASE}/games/stardewvalley/mods/{endpoint}.json"),
            )
            .await
            {
                mods.extend(items.into_iter().map(legacy_remote_mod));
            }
        }
    }

    let mut seen = HashSet::new();
    mods.retain(|item| {
        item.provider_id
            .as_ref()
            .is_none_or(|provider_id| seen.insert(provider_id.clone()))
    });

    let cache = CACHE.get_or_init(|| Mutex::new(None));
    if let Ok(mut cached) = cache.lock() {
        *cached = Some(CachedMods {
            loaded_at: Instant::now(),
            mods: mods.clone(),
        });
    }
    Ok(mods)
}

pub fn auth_status() -> NexusAuthStatus {
    NexusAuthStatus {
        configured: api_key().is_ok(),
    }
}

pub async fn set_api_key(value: String) -> Result<NexusAuthStatus, String> {
    let key = value.trim();
    if key.len() < 20 || key.len() > 512 {
        return Err("Nexus API Key 长度无效".into());
    }

    client_with_key(key)?
        .get(format!("{LEGACY_API_BASE}/users/validate.json"))
        .send()
        .await
        .map_err(|error| format!("无法验证 Nexus API Key：{error}"))?
        .error_for_status()
        .map_err(|_| "Nexus API Key 验证失败".to_string())?;

    credential_entry()?
        .set_password(key)
        .map_err(|error| format!("无法保存 Nexus API Key：{error}"))?;
    clear_cache();
    Ok(NexusAuthStatus { configured: true })
}

pub fn clear_api_key() -> Result<NexusAuthStatus, String> {
    if let Ok(entry) = credential_entry() {
        let _ = entry.delete_credential();
    }
    clear_cache();
    Ok(NexusAuthStatus { configured: false })
}

pub async fn mod_details(game_scoped_id: &str) -> Result<NexusModDetails, String> {
    validate_numeric_id(game_scoped_id, "Mod ID")?;
    let client = authenticated_client()?;
    let mod_data = get_json::<Envelope<ApiMod>>(
        &client,
        &format!("{API_BASE}/games/stardewvalley/mods/{game_scoped_id}"),
    )
    .await?
    .data;
    let files = get_json::<Envelope<ApiModFiles>>(
        &client,
        &format!("{API_BASE}/mods/{}/files", mod_data.id),
    )
    .await?
    .data
    .mod_files;

    let mut result_files = Vec::with_capacity(files.len());
    for file in files {
        let versions = if file.versions_count > 0 {
            get_json::<Envelope<ApiFileVersions>>(
                &client,
                &format!("{API_BASE}/mod-files/{}/versions", file.id),
            )
            .await?
            .data
            .versions
            .into_iter()
            .map(|version| NexusFileVersion {
                id: version.id,
                game_scoped_id: version.game_scoped_id,
                name: version.name,
                version: version.version,
                category: version.category,
                uploaded_at: version.uploaded_at,
                is_primary: version.is_primary,
            })
            .collect()
        } else {
            Vec::new()
        };
        result_files.push(NexusModFile {
            id: file.id,
            name: file.name,
            is_active: file.is_active,
            last_file_uploaded_at: file.last_file_uploaded_at,
            versions_count: file.versions_count,
            versions,
        });
    }

    Ok(NexusModDetails {
        id: mod_data.id,
        game_scoped_id: mod_data.game_scoped_id,
        name: mod_data.name.unwrap_or_else(|| "Nexus Mod".into()),
        page_url: format!("https://www.nexusmods.com/stardewvalley/mods/{game_scoped_id}"),
        files: result_files,
    })
}

pub async fn download_file(
    cache_dir: &Path,
    mod_id: &str,
    file_id: &str,
) -> Result<DownloadedModFile, String> {
    validate_numeric_id(mod_id, "Mod ID")?;
    validate_numeric_id(file_id, "文件 ID")?;
    let mirrors = get_json::<Vec<DownloadMirror>>(
        &authenticated_client()?,
        &format!(
            "{LEGACY_API_BASE}/games/stardewvalley/mods/{mod_id}/files/{file_id}/download_link.json"
        ),
    )
    .await?;
    let mirror = mirrors
        .into_iter()
        .next()
        .ok_or_else(|| "Nexus Mods 未返回可用下载地址".to_string())?;
    let url = reqwest::Url::parse(&mirror.uri).map_err(|_| "Nexus 下载地址无效".to_string())?;
    if url.scheme() != "https" {
        return Err("Nexus 下载地址不是 HTTPS".into());
    }

    let mut response = public_client()?
        .get(url)
        .send()
        .await
        .map_err(|error| format!("下载 Nexus 文件失败：{error}"))?
        .error_for_status()
        .map_err(|error| format!("Nexus 文件服务器返回错误：{error}"))?;
    let file_name = response
        .headers()
        .get(CONTENT_DISPOSITION)
        .and_then(|value| value.to_str().ok())
        .and_then(file_name_from_content_disposition)
        .unwrap_or_else(|| format!("nexus-{mod_id}-{file_id}.zip"));
    let downloads_dir = cache_dir.join("downloads");
    tokio::fs::create_dir_all(&downloads_dir)
        .await
        .map_err(|error| format!("无法创建下载缓存：{error}"))?;
    let target = safe_download_target(&downloads_dir, &file_name)?;
    let mut file = tokio::fs::File::create(&target)
        .await
        .map_err(|error| format!("无法创建下载文件：{error}"))?;
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|error| format!("下载 Nexus 文件失败：{error}"))?
    {
        file.write_all(&chunk)
            .await
            .map_err(|error| format!("无法写入下载文件：{error}"))?;
    }
    file.flush()
        .await
        .map_err(|error| format!("无法保存下载文件：{error}"))?;
    Ok(DownloadedModFile {
        path: target.to_string_lossy().into_owned(),
        file_name,
    })
}

fn public_client() -> Result<Client, String> {
    Client::builder()
        .user_agent("Valley-Steward/0.1")
        .timeout(Duration::from_secs(30))
        .build()
        .map_err(|error| format!("无法初始化 Nexus Mods 客户端：{error}"))
}

fn authenticated_client() -> Result<Client, String> {
    client_with_key(&api_key()?)
}

fn client_with_key(key: &str) -> Result<Client, String> {
    let mut headers = HeaderMap::new();
    headers.insert(
        "apikey",
        HeaderValue::from_str(key).map_err(|_| "Nexus API Key 格式无效".to_string())?,
    );
    Client::builder()
        .user_agent("Valley-Steward/0.1")
        .default_headers(headers)
        .timeout(Duration::from_secs(30))
        .build()
        .map_err(|error| format!("无法初始化 Nexus Mods 客户端：{error}"))
}

async fn get_json<T: for<'de> Deserialize<'de>>(client: &Client, url: &str) -> Result<T, String> {
    client
        .get(url)
        .header("Accept", "application/json")
        .send()
        .await
        .map_err(|error| format!("Nexus Mods 请求失败：{error}"))?
        .error_for_status()
        .map_err(|error| format!("Nexus Mods 返回错误：{error}"))?
        .json::<T>()
        .await
        .map_err(|error| format!("无法解析 Nexus Mods 数据：{error}"))
}

fn credential_entry() -> Result<Entry, String> {
    Entry::new(KEYRING_SERVICE, KEYRING_USER)
        .map_err(|error| format!("无法访问系统凭据库：{error}"))
}

fn api_key() -> Result<String, String> {
    credential_entry()?
        .get_password()
        .map_err(|_| "请先在设置中保存 Nexus API Key".to_string())
}

fn validate_numeric_id(value: &str, label: &str) -> Result<(), String> {
    if value.is_empty() || !value.bytes().all(|byte| byte.is_ascii_digit()) {
        Err(format!("{label} 无效"))
    } else {
        Ok(())
    }
}

fn game_scoped_id_from_url(value: &str) -> Option<String> {
    value
        .split('/')
        .filter(|part| !part.is_empty())
        .next_back()
        .filter(|part| part.bytes().all(|byte| byte.is_ascii_digit()))
        .map(ToOwned::to_owned)
}

fn safe_download_target(directory: &Path, file_name: &str) -> Result<PathBuf, String> {
    let safe_name = Path::new(file_name)
        .file_name()
        .and_then(|name| name.to_str())
        .filter(|name| !name.is_empty())
        .ok_or_else(|| "下载文件名无效".to_string())?;
    let target = directory.join(safe_name);
    if target.parent() != Some(directory) {
        return Err("下载文件名无效".into());
    }
    Ok(target)
}

fn legacy_remote_mod(item: LegacyMod) -> RemoteMod {
    let provider_id = item.mod_id.to_string();
    RemoteMod {
        id: format!("nexus:{provider_id}"),
        name: item.name,
        author: item
            .author
            .or(item.uploaded_by)
            .unwrap_or_else(|| "Nexus Mods 作者".into()),
        summary: item.summary.unwrap_or_else(|| "Nexus Mods Mod".into()),
        source: "Nexus Mods".into(),
        version: item.version.unwrap_or_else(|| "未知".into()),
        popularity: format!("{} 下载", item.mod_downloads),
        compatibility: "查看发布说明".into(),
        updated_at: item
            .updated_time
            .map(|value| value.chars().take(10).collect())
            .unwrap_or_else(|| "最近更新".into()),
        page_url: format!("https://www.nexusmods.com/stardewvalley/mods/{provider_id}"),
        download_url: None,
        image_url: item.picture_url,
        provider_id: Some(provider_id),
    }
}

fn file_name_from_content_disposition(value: &str) -> Option<String> {
    value
        .split(';')
        .map(str::trim)
        .find_map(|part| part.strip_prefix("filename="))
        .map(|name| name.trim_matches('"').to_string())
        .filter(|name| !name.is_empty())
}

fn clear_cache() {
    let cache = CACHE.get_or_init(|| Mutex::new(None));
    if let Ok(mut cached) = cache.lock() {
        *cached = None;
    }
}

fn cached_mods() -> Option<Vec<RemoteMod>> {
    let cache = CACHE.get_or_init(|| Mutex::new(None));
    let cached = cache.lock().ok()?;
    let entry = cached.as_ref()?;
    (entry.loaded_at.elapsed() < CACHE_TTL).then(|| entry.mods.clone())
}
