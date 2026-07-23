use std::{
    collections::HashSet,
    fs::{self, OpenOptions},
    io::{ErrorKind, Write},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicU64, Ordering},
        Mutex, OnceLock,
    },
    time::{Duration, Instant},
};

use keyring::{Entry, Error as KeyringError};
use reqwest::{
    header::{HeaderMap, HeaderValue, CONTENT_DISPOSITION},
    redirect::Policy,
    Client, Response, StatusCode,
};
use serde::{Deserialize, Serialize};
use tokio::io::AsyncWriteExt;

use crate::models::{
    DownloadedModFile, DownloadedModTranslation, NexusAuthStatus, NexusFileVersion,
    NexusModDetails, NexusModFile, RemoteMod,
};

const API_BASE: &str = "https://api.nexusmods.com/v3";
const LEGACY_API_BASE: &str = "https://api.nexusmods.com/v1";
const GRAPHQL_API_URL: &str = "https://api.nexusmods.com/v2/graphql";
const TRENDING_URL: &str = "https://api.nexusmods.com/v3/games/stardewvalley/trending-mods";
const SEARCH_LIST_ENDPOINTS: [&str; 3] = ["trending", "latest_updated", "latest_added"];
const SEARCH_RESULT_LIMIT: u8 = 24;
const SEARCH_MODS_QUERY: &str = r#"
query SearchMods($filter: ModsFilter, $offset: Int, $count: Int) {
  mods(filter: $filter, offset: $offset, count: $count) {
    nodes {
      modId
      name
      summary
      pictureUrl
      thumbnailUrl
      endorsements
      downloads
      version
      author
      updatedAt
      createdAt
      uploader { name }
    }
  }
}
"#;
const CACHE_TTL: Duration = Duration::from_secs(15 * 60);
const KEYRING_SERVICE: &str = "com.summerxdsss.valleysteward";
const KEYRING_USER: &str = "nexus-api-key";
const MIN_API_KEY_BYTES: usize = 20;
const MAX_API_KEY_BYTES: usize = 512;
const MAX_ERROR_RESPONSE_BYTES: usize = 16 * 1024;
const MAX_PROVIDER_ERROR_CHARS: usize = 300;
const RECENT_WRITE_WINDOW: Duration = Duration::from_secs(2);
const CREDENTIAL_READ_RETRY_DELAY: Duration = Duration::from_millis(40);
const CREDENTIAL_READ_RETRIES: usize = 3;
const MAX_TRANSLATED_NAME_CHARS: usize = 512;
const MAX_TRANSLATED_DESCRIPTION_CHARS: usize = 16_000;
const MAX_SIDECAR_TEMP_FILE_ATTEMPTS: usize = 32;

static CACHE: OnceLock<Mutex<Option<CachedMods>>> = OnceLock::new();
static KEYRING_LOCK: OnceLock<Mutex<()>> = OnceLock::new();
static LAST_KEYRING_WRITE: OnceLock<Mutex<Option<Instant>>> = OnceLock::new();
static SIDECAR_WRITE_LOCK: OnceLock<Mutex<()>> = OnceLock::new();
static SIDECAR_TEMP_FILE_COUNTER: AtomicU64 = AtomicU64::new(0);

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
struct NexusApiError {
    #[serde(default)]
    message: Option<String>,
    #[serde(default)]
    error: Option<String>,
}

#[derive(Deserialize)]
struct GraphqlSearchResponse {
    data: Option<GraphqlSearchData>,
    #[serde(default)]
    errors: Vec<GraphqlError>,
}

#[derive(Deserialize)]
struct GraphqlSearchData {
    mods: Option<GraphqlSearchMods>,
}

#[derive(Deserialize)]
struct GraphqlSearchMods {
    #[serde(default)]
    nodes: Vec<Option<GraphqlMod>>,
}

#[derive(Deserialize)]
struct GraphqlError {
    message: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct GraphqlMod {
    mod_id: u64,
    #[serde(default)]
    name: Option<String>,
    #[serde(default)]
    summary: Option<String>,
    #[serde(default)]
    picture_url: Option<String>,
    #[serde(default)]
    thumbnail_url: Option<String>,
    #[serde(default)]
    endorsements: Option<u64>,
    #[serde(default)]
    downloads: Option<u64>,
    #[serde(default)]
    version: Option<String>,
    #[serde(default)]
    author: Option<String>,
    #[serde(default)]
    updated_at: Option<String>,
    #[serde(default)]
    created_at: Option<String>,
    #[serde(default)]
    uploader: Option<GraphqlUploader>,
}

#[derive(Deserialize)]
struct GraphqlUploader {
    #[serde(default)]
    name: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DownloadTranslationSidecar<'a> {
    schema_version: u8,
    provider: &'static str,
    mod_id: &'a str,
    file_id: &'a str,
    name: &'a str,
    description: &'a str,
}

#[derive(Deserialize)]
struct LegacyMod {
    mod_id: u64,
    #[serde(default)]
    name: Option<String>,
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
    mod_downloads: Option<u64>,
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
        for endpoint in SEARCH_LIST_ENDPOINTS {
            if let Ok(items) = get_json::<Vec<Option<LegacyMod>>>(
                &client,
                &format!("{LEGACY_API_BASE}/games/stardewvalley/mods/{endpoint}.json"),
            )
            .await
            {
                mods.extend(items.into_iter().flatten().map(legacy_remote_mod));
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

pub async fn search(query: &str) -> Result<Vec<RemoteMod>, String> {
    let response = public_client()?
        .post(GRAPHQL_API_URL)
        .header("Accept", "application/json")
        .header("Application-Name", "Valley-Steward")
        .header("Application-Version", "0.1")
        .json(&graphql_search_body(query))
        .send()
        .await
        .map_err(|error| format!("Nexus Mods 搜索请求失败：{error}"))?
        .error_for_status()
        .map_err(|error| format!("Nexus Mods 搜索返回错误：{error}"))?;
    let payload =
        decode_json_response::<GraphqlSearchResponse>(response, "Nexus Mods 搜索数据").await?;
    let error_detail = graphql_error_detail(&payload.errors);
    let nodes = payload
        .data
        .and_then(|data| data.mods)
        .map(|mods| mods.nodes)
        .ok_or_else(|| {
            error_detail
                .map(|detail| format!("Nexus Mods 搜索失败：{detail}"))
                .unwrap_or_else(|| "Nexus Mods 搜索未返回结果数据".to_string())
        })?;
    let mut mods = nodes
        .into_iter()
        .flatten()
        .map(graphql_remote_mod)
        .collect::<Vec<_>>();

    let mut seen = HashSet::new();
    mods.retain(|item| {
        item.provider_id
            .as_ref()
            .is_none_or(|provider_id| seen.insert(provider_id.clone()))
    });
    Ok(mods)
}

pub fn auth_status() -> Result<NexusAuthStatus, String> {
    Ok(NexusAuthStatus {
        configured: read_api_key()?.is_some(),
    })
}

pub async fn set_api_key(value: String) -> Result<NexusAuthStatus, String> {
    let key = normalize_api_key(&value)?;
    let response = client_with_key(&key)?
        .get(format!("{LEGACY_API_BASE}/users/validate.json"))
        .header("Accept", "application/json")
        .send()
        .await
        .map_err(|error| nexus_connection_error(&error))?;
    validate_api_key_response(response, &key).await?;

    persist_api_key(&key)?;
    clear_cache();
    Ok(NexusAuthStatus { configured: true })
}

pub fn clear_api_key() -> Result<NexusAuthStatus, String> {
    let _guard = credential_lock()?;
    match credential_entry()?.delete_credential() {
        Ok(()) | Err(KeyringError::NoEntry) => {}
        Err(error) => return Err(format!("无法从系统凭据库清除 Nexus API Key：{error}")),
    }
    mark_keyring_write(None);
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
    translation: Option<DownloadedModTranslation>,
) -> Result<DownloadedModFile, String> {
    validate_numeric_id(mod_id, "Mod ID")?;
    validate_numeric_id(file_id, "文件 ID")?;
    let mirrors = request_download_mirrors(mod_id, file_id).await?;
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
    let metadata_path = match translation {
        Some(translation) => {
            let (name, description) = normalize_download_translation(&translation)?;
            let metadata_path =
                downloads_dir.join(format!("nexus-{mod_id}-{file_id}.valley-steward.json"));
            let content = serde_json::to_vec_pretty(&DownloadTranslationSidecar {
                schema_version: 1,
                provider: "Nexus Mods",
                mod_id,
                file_id,
                name,
                description,
            })
            .map_err(|error| format!("无法序列化下载翻译元数据：{error}"))?;
            write_download_translation_sidecar_atomic(&downloads_dir, &metadata_path, &content)
                .map_err(|error| format!("Mod 已下载，但无法保存翻译元数据：{error}"))?;
            Some(metadata_path.to_string_lossy().into_owned())
        }
        None => None,
    };
    Ok(DownloadedModFile {
        path: target.to_string_lossy().into_owned(),
        file_name,
        metadata_path,
    })
}

fn normalize_download_translation(
    translation: &DownloadedModTranslation,
) -> Result<(&str, &str), String> {
    let name = translation.name.trim();
    let description = translation.description.trim();
    if name.is_empty() || name.chars().count() > MAX_TRANSLATED_NAME_CHARS {
        return Err("下载译名为空或过长".into());
    }
    if description.is_empty() || description.chars().count() > MAX_TRANSLATED_DESCRIPTION_CHARS {
        return Err("下载译文为空或过长".into());
    }
    if name.chars().any(char::is_control)
        || description
            .chars()
            .any(|character| character.is_control() && !matches!(character, '\n' | '\r' | '\t'))
    {
        return Err("下载翻译元数据包含无效控制字符".into());
    }
    Ok((name, description))
}

fn write_download_translation_sidecar_atomic(
    directory: &Path,
    target: &Path,
    content: &[u8],
) -> Result<(), String> {
    let _guard = SIDECAR_WRITE_LOCK
        .get_or_init(|| Mutex::new(()))
        .lock()
        .map_err(|_| "下载翻译元数据写入锁已损坏".to_string())?;
    if target.parent() != Some(directory) {
        return Err("翻译元数据路径无效".into());
    }
    match fs::symlink_metadata(target) {
        Ok(metadata) if !metadata.file_type().is_file() => {
            return Err("拒绝覆盖不是普通文件的下载翻译元数据".into());
        }
        Ok(_) => {}
        Err(error) if error.kind() == ErrorKind::NotFound => {}
        Err(error) => return Err(format!("无法检查下载翻译元数据：{error}")),
    }

    let mut last_collision = None;
    for _ in 0..MAX_SIDECAR_TEMP_FILE_ATTEMPTS {
        let sequence = SIDECAR_TEMP_FILE_COUNTER.fetch_add(1, Ordering::Relaxed);
        let temporary = directory.join(format!(
            ".valley-steward-nexus-translation-{}-{sequence}.tmp",
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
            Err(error) => return Err(format!("无法创建下载翻译临时文件：{error}")),
        };

        let write_result = file.write_all(content).and_then(|()| file.sync_all());
        drop(file);
        if let Err(error) = write_result {
            let _ = fs::remove_file(&temporary);
            return Err(format!("无法写入下载翻译临时文件：{error}"));
        }
        if let Err(error) = atomic_replace_sidecar(&temporary, target) {
            let _ = fs::remove_file(&temporary);
            return Err(format!("无法原子替换下载翻译元数据：{error}"));
        }
        sync_sidecar_parent_directory(directory);
        return Ok(());
    }

    Err(format!(
        "无法创建唯一的下载翻译临时文件：{}",
        last_collision
            .map(|error| error.to_string())
            .unwrap_or_else(|| "文件名冲突".into())
    ))
}

#[cfg(windows)]
fn atomic_replace_sidecar(source: &Path, target: &Path) -> std::io::Result<()> {
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
fn atomic_replace_sidecar(source: &Path, target: &Path) -> std::io::Result<()> {
    fs::rename(source, target)
}

#[cfg(unix)]
fn sync_sidecar_parent_directory(directory: &Path) {
    if let Ok(file) = fs::File::open(directory) {
        let _ = file.sync_all();
    }
}

#[cfg(not(unix))]
fn sync_sidecar_parent_directory(_directory: &Path) {}

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
        .redirect(Policy::none())
        .connect_timeout(Duration::from_secs(10))
        .timeout(Duration::from_secs(30))
        .build()
        .map_err(|error| format!("无法初始化 Nexus Mods 客户端：{error}"))
}

async fn validate_api_key_response(response: Response, key: &str) -> Result<(), String> {
    let status = response.status();
    if status.is_success() {
        return Ok(());
    }

    let provider_message = read_provider_error(response, key).await;
    Err(validation_error(status, provider_message.as_deref()))
}

async fn read_provider_error(mut response: Response, key: &str) -> Option<String> {
    let mut body = Vec::new();
    while let Ok(Some(chunk)) = response.chunk().await {
        let remaining = MAX_ERROR_RESPONSE_BYTES.saturating_sub(body.len());
        if remaining == 0 {
            break;
        }
        body.extend_from_slice(&chunk[..chunk.len().min(remaining)]);
        if body.len() == MAX_ERROR_RESPONSE_BYTES {
            break;
        }
    }

    let parsed = serde_json::from_slice::<NexusApiError>(&body).ok();
    let raw = parsed
        .and_then(|error| error.message.or(error.error))
        .or_else(|| String::from_utf8(body).ok());
    raw.and_then(|message| sanitize_provider_error(&message, key))
}

fn sanitize_provider_error(message: &str, key: &str) -> Option<String> {
    let redacted = message.replace(key, "[已隐藏]");
    let compact = redacted.split_whitespace().collect::<Vec<_>>().join(" ");
    if compact.is_empty() {
        return None;
    }
    Some(compact.chars().take(MAX_PROVIDER_ERROR_CHARS).collect())
}

fn validation_error(status: StatusCode, provider_message: Option<&str>) -> String {
    let detail = provider_message
        .filter(|message| !message.is_empty())
        .map(|message| format!("：{message}"))
        .unwrap_or_default();
    match status {
        StatusCode::UNAUTHORIZED | StatusCode::FORBIDDEN => format!(
            "Nexus Mods 拒绝了此 Personal API Key（HTTP {}）{detail}。请在 Nexus Mods 的 API Keys 页面生成 Personal API Key；应用凭据和 Bearer JWT 不能填写在这里",
            status.as_u16()
        ),
        StatusCode::TOO_MANY_REQUESTS => format!(
            "Nexus Mods 请求过于频繁（HTTP 429）{detail}，请稍后重试"
        ),
        status if status.is_server_error() => format!(
            "Nexus Mods 验证服务暂时不可用（HTTP {}）{detail}，请稍后重试",
            status.as_u16()
        ),
        _ => format!(
            "Nexus API Key 验证失败（HTTP {}）{detail}",
            status.as_u16()
        ),
    }
}

fn nexus_connection_error(error: &reqwest::Error) -> String {
    if error.is_timeout() {
        "连接 Nexus Mods 验证服务超时，请检查网络或代理后重试".into()
    } else if error.is_connect() {
        format!("无法连接 Nexus Mods 验证服务，请检查网络或代理：{error}")
    } else {
        format!("无法验证 Nexus API Key：{error}")
    }
}

async fn get_json<T: for<'de> Deserialize<'de>>(client: &Client, url: &str) -> Result<T, String> {
    let response = client
        .get(url)
        .header("Accept", "application/json")
        .send()
        .await
        .map_err(|error| format!("Nexus Mods 请求失败：{error}"))?
        .error_for_status()
        .map_err(|error| format!("Nexus Mods 返回错误：{error}"))?;
    decode_json_response(response, "Nexus Mods 数据").await
}

async fn request_download_mirrors(
    mod_id: &str,
    file_id: &str,
) -> Result<Vec<DownloadMirror>, String> {
    let key = api_key()?;
    let response = client_with_key(&key)?
        .get(format!(
            "{LEGACY_API_BASE}/games/stardewvalley/mods/{mod_id}/files/{file_id}/download_link.json"
        ))
        .header("Accept", "application/json")
        .send()
        .await
        .map_err(|error| format!("Nexus 下载地址请求失败：{error}"))?;
    let status = response.status();
    if !status.is_success() {
        let provider_message = read_provider_error(response, &key).await;
        return Err(download_link_error(status, provider_message.as_deref()));
    }
    decode_json_response(response, "Nexus 下载地址").await
}

async fn decode_json_response<T: for<'de> Deserialize<'de>>(
    response: Response,
    label: &str,
) -> Result<T, String> {
    let body = response
        .bytes()
        .await
        .map_err(|error| format!("无法读取{label}：{error}"))?;
    serde_json::from_slice(&body).map_err(|error| format!("无法解析{label}：{error}"))
}

fn download_link_error(status: StatusCode, provider_message: Option<&str>) -> String {
    let detail = provider_message
        .filter(|message| !message.is_empty())
        .map(|message| format!("（Nexus：{message}）"))
        .unwrap_or_default();
    match status {
        StatusCode::FORBIDDEN => format!(
            "Nexus 拒绝生成该文件的 API 直链（HTTP 403）{detail}。常见原因是账号没有 Premium 直链权限，或作者禁止管理器下载；请点击“文件页”在 Nexus 官网继续下载"
        ),
        StatusCode::TOO_MANY_REQUESTS => {
            format!("Nexus 下载请求过于频繁（HTTP 429）{detail}，请稍后重试")
        }
        status if status.is_server_error() => format!(
            "Nexus 下载服务暂时不可用（HTTP {}）{detail}，请稍后重试",
            status.as_u16()
        ),
        _ => format!(
            "Nexus 无法生成下载地址（HTTP {}）{detail}，请打开官方文件页继续",
            status.as_u16()
        ),
    }
}

fn credential_entry() -> Result<Entry, String> {
    super::keyring_entry(KEYRING_SERVICE, KEYRING_USER)
        .map_err(|error| format!("无法访问系统凭据库：{error}"))
}

fn api_key() -> Result<String, String> {
    read_api_key()?.ok_or_else(|| "请先在设置中保存 Nexus Personal API Key".to_string())
}

fn normalize_api_key(value: &str) -> Result<String, String> {
    let key = value.trim();
    if key.is_empty() {
        return Err("请填写 Nexus Personal API Key".into());
    }
    if key.len() < MIN_API_KEY_BYTES || key.len() > MAX_API_KEY_BYTES {
        return Err(format!(
            "Nexus Personal API Key 长度无效（应为 {MIN_API_KEY_BYTES}-{MAX_API_KEY_BYTES} 字节）"
        ));
    }
    if key.chars().any(char::is_whitespace) {
        return Err("Nexus Personal API Key 中不能包含空格或换行".into());
    }
    HeaderValue::from_str(key).map_err(|_| "Nexus Personal API Key 格式无效".to_string())?;
    Ok(key.to_string())
}

fn credential_lock() -> Result<std::sync::MutexGuard<'static, ()>, String> {
    KEYRING_LOCK
        .get_or_init(|| Mutex::new(()))
        .lock()
        .map_err(|_| "Nexus 凭据操作锁已损坏，请重启应用后重试".to_string())
}

fn read_api_key() -> Result<Option<String>, String> {
    let _guard = credential_lock()?;
    read_api_key_unlocked()
}

fn read_api_key_unlocked() -> Result<Option<String>, String> {
    let entry = credential_entry()?;
    match get_password_with_recent_write_retry(&entry) {
        Ok(value) => normalize_api_key(&value)
            .map(Some)
            .map_err(|_| "系统凭据库中的 Nexus API Key 格式无效，请清除后重新保存".to_string()),
        Err(KeyringError::NoEntry) => Ok(None),
        Err(error) => Err(format!("无法从系统凭据库读取 Nexus API Key：{error}")),
    }
}

fn read_raw_api_key_unlocked() -> Result<Option<String>, String> {
    match credential_entry()?.get_password() {
        Ok(value) => Ok(Some(value)),
        Err(KeyringError::NoEntry) => Ok(None),
        Err(error) => Err(format!("无法从系统凭据库读取现有 Nexus API Key：{error}")),
    }
}

fn persist_api_key(key: &str) -> Result<(), String> {
    let _guard = credential_lock()?;
    let previous = read_raw_api_key_unlocked()?;
    let entry = credential_entry()?;
    entry
        .set_password(key)
        .map_err(|error| format!("无法将 Nexus API Key 写入系统凭据库：{error}"))?;

    match entry.get_password() {
        Ok(stored) if stored == key => {
            mark_keyring_write(Some(Instant::now()));
            Ok(())
        }
        Ok(_) => {
            rollback_after_failed_write(&entry, previous.as_deref(), "写入后读回的 Key 不一致")
        }
        Err(error) => rollback_after_failed_write(
            &entry,
            previous.as_deref(),
            &format!("写入后无法读回 Key：{error}"),
        ),
    }
}

fn get_password_with_recent_write_retry(entry: &Entry) -> Result<String, KeyringError> {
    let should_retry = LAST_KEYRING_WRITE
        .get_or_init(|| Mutex::new(None))
        .lock()
        .ok()
        .and_then(|written_at| *written_at)
        .is_some_and(|written_at| written_at.elapsed() < RECENT_WRITE_WINDOW);

    for attempt in 0..=CREDENTIAL_READ_RETRIES {
        match entry.get_password() {
            Err(KeyringError::NoEntry) if should_retry && attempt < CREDENTIAL_READ_RETRIES => {
                std::thread::sleep(CREDENTIAL_READ_RETRY_DELAY);
            }
            result => return result,
        }
    }
    unreachable!("credential retry loop always returns on its final attempt")
}

fn mark_keyring_write(value: Option<Instant>) {
    if let Ok(mut written_at) = LAST_KEYRING_WRITE.get_or_init(|| Mutex::new(None)).lock() {
        *written_at = value;
    }
}

fn rollback_after_failed_write(
    entry: &Entry,
    previous: Option<&str>,
    reason: &str,
) -> Result<(), String> {
    let rollback = match previous {
        Some(value) => entry.set_password(value),
        None => match entry.delete_credential() {
            Ok(()) | Err(KeyringError::NoEntry) => Ok(()),
            Err(error) => Err(error),
        },
    };
    match rollback {
        Ok(()) => Err(format!("Nexus API Key 未保存：{reason}；已恢复原凭据")),
        Err(error) => Err(format!(
            "Nexus API Key 未保存：{reason}；同时无法恢复原凭据：{error}"
        )),
    }
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
        .rsplit('/')
        .find(|part| !part.is_empty())
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
        name: non_empty(item.name).unwrap_or_else(|| format!("Nexus Mod #{provider_id}")),
        author: item
            .author
            .or(item.uploaded_by)
            .and_then(|value| non_empty(Some(value)))
            .unwrap_or_else(|| "Nexus Mods 作者".into()),
        summary: non_empty(item.summary).unwrap_or_else(|| "Nexus Mods Mod".into()),
        source: "Nexus Mods".into(),
        version: non_empty(item.version).unwrap_or_else(|| "未知".into()),
        popularity: format!("{} 下载", item.mod_downloads.unwrap_or_default()),
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

fn graphql_search_body(query: &str) -> serde_json::Value {
    serde_json::json!({
        "query": SEARCH_MODS_QUERY,
        "variables": {
            "filter": {
                "op": "AND",
                "filter": [
                    {
                        "gameDomainName": [
                            { "value": "stardewvalley", "op": "EQUALS" }
                        ]
                    },
                    {
                        "op": "OR",
                        "filter": [
                            { "nameStemmed": [{ "value": query, "op": "MATCHES" }] },
                            { "description": [{ "value": query, "op": "MATCHES" }] },
                            { "author": [{ "value": query, "op": "MATCHES" }] },
                            { "uploader": [{ "value": query, "op": "MATCHES" }] }
                        ]
                    }
                ]
            },
            "offset": 0,
            "count": SEARCH_RESULT_LIMIT
        }
    })
}

fn graphql_remote_mod(item: GraphqlMod) -> RemoteMod {
    let provider_id = item.mod_id.to_string();
    let author = non_empty(item.author)
        .or_else(|| item.uploader.and_then(|uploader| non_empty(uploader.name)))
        .unwrap_or_else(|| "Nexus Mods 作者".into());
    let updated_at = non_empty(item.updated_at)
        .or_else(|| non_empty(item.created_at))
        .map(|value| value.chars().take(10).collect())
        .unwrap_or_else(|| "最近更新".into());
    let downloads = item.downloads.unwrap_or_default();
    let endorsements = item.endorsements.unwrap_or_default();
    RemoteMod {
        id: format!("nexus:{provider_id}"),
        name: non_empty(item.name).unwrap_or_else(|| format!("Nexus Mod #{provider_id}")),
        author,
        summary: non_empty(item.summary).unwrap_or_else(|| "Nexus Mods Mod".into()),
        source: "Nexus Mods".into(),
        version: non_empty(item.version).unwrap_or_else(|| "未知".into()),
        popularity: if downloads > 0 {
            format!("{downloads} 下载")
        } else {
            format!("{endorsements} 认可")
        },
        compatibility: "查看发布说明".into(),
        updated_at,
        page_url: format!("https://www.nexusmods.com/stardewvalley/mods/{provider_id}"),
        download_url: None,
        image_url: non_empty(item.picture_url).or_else(|| non_empty(item.thumbnail_url)),
        provider_id: Some(provider_id),
    }
}

fn graphql_error_detail(errors: &[GraphqlError]) -> Option<String> {
    let detail = errors
        .iter()
        .filter_map(|error| non_empty(Some(error.message.clone())))
        .take(2)
        .collect::<Vec<_>>()
        .join("；");
    (!detail.is_empty()).then(|| detail.chars().take(MAX_PROVIDER_ERROR_CHARS).collect())
}

fn non_empty(value: Option<String>) -> Option<String> {
    value.and_then(|value| {
        let trimmed = value.trim();
        (!trimmed.is_empty()).then(|| trimmed.to_string())
    })
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

#[cfg(test)]
mod tests {
    use std::{
        sync::{Arc, Barrier},
        time::{SystemTime, UNIX_EPOCH},
    };

    use super::*;

    #[test]
    fn api_key_is_trimmed_but_internal_whitespace_is_rejected() {
        let key = "a".repeat(64);
        assert_eq!(normalize_api_key(&format!("  {key}\r\n")).unwrap(), key);
        assert!(normalize_api_key(&format!("{} {}", "a".repeat(31), "b".repeat(32))).is_err());
    }

    #[test]
    fn provider_error_redacts_the_api_key_and_limits_output() {
        let key = "secret-api-key-that-must-not-leak";
        let message = format!("invalid {key} {}", "x".repeat(500));
        let sanitized = sanitize_provider_error(&message, key).unwrap();
        assert!(!sanitized.contains(key));
        assert!(sanitized.contains("[已隐藏]"));
        assert!(sanitized.chars().count() <= MAX_PROVIDER_ERROR_CHARS);
    }

    #[test]
    fn unauthorized_error_explains_the_supported_credential_type() {
        let error = validation_error(StatusCode::UNAUTHORIZED, Some("invalid"));
        assert!(error.contains("Personal API Key"));
        assert!(error.contains("Bearer JWT"));
        assert!(error.contains("HTTP 401"));
    }

    #[test]
    fn download_translation_metadata_is_bounded_and_trimmed() {
        let translation = DownloadedModTranslation {
            name: "  洒水器助手  ".into(),
            description: "  自动照料农场。  ".into(),
        };
        assert_eq!(
            normalize_download_translation(&translation).unwrap(),
            ("洒水器助手", "自动照料农场。")
        );

        let invalid = DownloadedModTranslation {
            name: "无效\0名称".into(),
            description: "译文".into(),
        };
        assert!(normalize_download_translation(&invalid).is_err());
    }

    #[test]
    fn concurrent_sidecar_writes_are_atomic_and_leave_no_temporary_files() {
        const WRITER_COUNT: usize = 8;

        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let directory = std::env::temp_dir().join(format!(
            "valley-steward-nexus-sidecar-test-{}-{nonce}",
            std::process::id()
        ));
        fs::create_dir_all(&directory).unwrap();
        let target = directory.join("nexus-1-2.valley-steward.json");
        let barrier = Arc::new(Barrier::new(WRITER_COUNT));
        let payloads = (0..WRITER_COUNT)
            .map(|index| format!(r#"{{"writer":{index},"description":"译文 {index}"}}"#))
            .collect::<Vec<_>>();

        std::thread::scope(|scope| {
            for payload in &payloads {
                let barrier = Arc::clone(&barrier);
                let directory = &directory;
                let target = &target;
                scope.spawn(move || {
                    barrier.wait();
                    write_download_translation_sidecar_atomic(
                        directory,
                        target,
                        payload.as_bytes(),
                    )
                    .unwrap();
                });
            }
        });

        let saved = fs::read_to_string(&target).unwrap();
        assert!(payloads.contains(&saved));
        let leftovers = fs::read_dir(&directory)
            .unwrap()
            .filter_map(Result::ok)
            .filter(|entry| entry.file_name().to_string_lossy().ends_with(".tmp"))
            .count();
        assert_eq!(leftovers, 0);
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn legacy_mod_list_tolerates_null_items_and_nullable_display_fields() {
        let mut items = serde_json::from_str::<Vec<Option<LegacyMod>>>(
            r#"[
                null,
                {
                    "mod_id": 5098,
                    "name": null,
                    "author": null,
                    "uploaded_by": "Uploader",
                    "summary": null,
                    "picture_url": null,
                    "version": null,
                    "mod_downloads": null,
                    "updated_time": null
                }
            ]"#,
        )
        .unwrap();
        let remote = legacy_remote_mod(items.pop().flatten().unwrap());
        assert_eq!(remote.name, "Nexus Mod #5098");
        assert_eq!(remote.author, "Uploader");
        assert_eq!(remote.popularity, "0 下载");
        assert!(items.pop().flatten().is_none());
    }

    #[test]
    fn graphql_search_body_covers_name_description_author_and_uploader() {
        let body = graphql_search_body("Tax");
        let serialized = serde_json::to_string(&body).unwrap();
        assert!(serialized.contains("stardewvalley"));
        assert!(serialized.contains("nameStemmed"));
        assert!(serialized.contains("description"));
        assert!(serialized.contains("author"));
        assert!(serialized.contains("uploader"));
        assert!(serialized.contains("Tax"));
        assert_eq!(body["variables"]["count"], SEARCH_RESULT_LIMIT);
    }

    #[test]
    fn graphql_response_maps_nullable_fields_to_safe_remote_mod_defaults() {
        let payload = serde_json::from_str::<GraphqlSearchResponse>(
            r#"{
                "data": {
                    "mods": {
                        "nodes": [{
                            "modId": 48386,
                            "name": "Regional Banking and Taxes System",
                            "summary": null,
                            "pictureUrl": null,
                            "thumbnailUrl": "https://staticdelivery.nexusmods.com/thumb.png",
                            "endorsements": 1,
                            "downloads": 153,
                            "version": "1",
                            "author": null,
                            "updatedAt": "2026-07-03T11:58:26Z",
                            "createdAt": "2026-07-03T11:58:26Z",
                            "uploader": {"name": "dark8807"}
                        }]
                    }
                }
            }"#,
        )
        .unwrap();
        let remote = graphql_remote_mod(
            payload
                .data
                .and_then(|data| data.mods)
                .and_then(|mods| mods.nodes.into_iter().flatten().next())
                .unwrap(),
        );
        assert_eq!(remote.author, "dark8807");
        assert_eq!(remote.summary, "Nexus Mods Mod");
        assert_eq!(remote.popularity, "153 下载");
        assert_eq!(remote.updated_at, "2026-07-03");
        assert_eq!(remote.provider_id.as_deref(), Some("48386"));
    }

    #[test]
    fn forbidden_download_link_error_explains_official_file_page_fallback() {
        let error = download_link_error(StatusCode::FORBIDDEN, Some("premium required"));
        assert!(error.contains("HTTP 403"));
        assert!(error.contains("Premium"));
        assert!(error.contains("文件页"));
    }

    #[test]
    #[ignore = "writes a uniquely named temporary entry to the operating-system credential store"]
    fn system_credential_store_round_trip_smoke() {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let user = format!("nexus-key-smoke-{}-{nonce}", std::process::id());
        let entry = Entry::new("com.summerxdsss.valleysteward.smoke", &user).unwrap();
        let value = "temporary-nexus-credential-smoke-value";

        entry.set_password(value).unwrap();
        assert_eq!(entry.get_password().unwrap(), value);
        entry.delete_credential().unwrap();
        assert!(matches!(entry.get_password(), Err(KeyringError::NoEntry)));
    }

    #[tokio::test]
    #[ignore = "requires live access to the Nexus Mods validation API"]
    async fn invalid_key_returns_a_safe_live_validation_error() {
        let key = "0".repeat(64);
        let error = set_api_key(key.clone()).await.unwrap_err();
        assert!(error.contains("Personal API Key"));
        assert!(error.contains("HTTP 401") || error.contains("HTTP 403"));
        assert!(!error.contains(&key));
    }

    #[tokio::test]
    #[ignore = "requires live access to the public Nexus Mods GraphQL API"]
    async fn graphql_search_returns_live_tax_results() {
        let mods = search("Tax")
            .await
            .expect("public GraphQL search should respond");
        assert!(!mods.is_empty());
        assert!(mods.iter().all(|item| item.source == "Nexus Mods"));
        assert!(mods.iter().all(|item| item.provider_id.is_some()));
    }
}
