use std::{
    sync::{Mutex, OnceLock},
    time::{Duration, Instant},
};

use serde::Deserialize;

use crate::models::RemoteMod;

const SEARCH_URL: &str = "https://api.github.com/search/repositories?q=topic%3Astardew-valley-mod+archived%3Afalse&sort=stars&order=desc&per_page=12";
const CACHE_TTL: Duration = Duration::from_secs(15 * 60);

static CACHE: OnceLock<Mutex<Option<CachedMods>>> = OnceLock::new();

#[derive(Clone)]
struct CachedMods {
    loaded_at: Instant,
    mods: Vec<RemoteMod>,
}

#[derive(Deserialize)]
struct SearchResponse {
    items: Vec<Repository>,
}

#[derive(Deserialize)]
struct Repository {
    id: u64,
    name: String,
    full_name: String,
    owner: Owner,
    description: Option<String>,
    html_url: String,
    stargazers_count: u64,
    updated_at: String,
}

#[derive(Deserialize)]
struct Owner {
    login: String,
}

#[derive(Deserialize)]
struct Release {
    tag_name: String,
    html_url: String,
    assets: Vec<ReleaseAsset>,
}

#[derive(Deserialize)]
struct ReleaseAsset {
    name: String,
    browser_download_url: String,
}

pub async fn discover() -> Result<Vec<RemoteMod>, String> {
    if let Some(mods) = cached_mods() {
        return Ok(mods);
    }

    let client = reqwest::Client::builder()
        .user_agent("Valley-Steward/0.1 (+https://github.com/)")
        .timeout(Duration::from_secs(15))
        .build()
        .map_err(|error| format!("无法初始化 GitHub 客户端：{error}"))?;

    let response = client
        .get(SEARCH_URL)
        .header("Accept", "application/vnd.github+json")
        .send()
        .await
        .map_err(|error| format!("无法连接 GitHub：{error}"))?
        .error_for_status()
        .map_err(|error| format!("GitHub 返回错误：{error}"))?
        .json::<SearchResponse>()
        .await
        .map_err(|error| format!("无法解析 GitHub 数据：{error}"))?;

    let mut mods = Vec::with_capacity(response.items.len());
    for repository in response.items {
        let release_url = format!(
            "https://api.github.com/repos/{}/releases/latest",
            repository.full_name
        );
        let release = match client
            .get(release_url)
            .header("Accept", "application/vnd.github+json")
            .send()
            .await
        {
            Ok(response) if response.status().is_success() => response.json::<Release>().await.ok(),
            _ => None,
        };
        mods.push(to_remote_mod(repository, release));
    }

    let cache = CACHE.get_or_init(|| Mutex::new(None));
    if let Ok(mut cached) = cache.lock() {
        *cached = Some(CachedMods {
            loaded_at: Instant::now(),
            mods: mods.clone(),
        });
    }
    Ok(mods)
}

fn cached_mods() -> Option<Vec<RemoteMod>> {
    let cache = CACHE.get_or_init(|| Mutex::new(None));
    let cached = cache.lock().ok()?;
    let entry = cached.as_ref()?;
    (entry.loaded_at.elapsed() < CACHE_TTL).then(|| entry.mods.clone())
}

fn to_remote_mod(repository: Repository, release: Option<Release>) -> RemoteMod {
    let (version, page_url, download_url) = match release {
        Some(release) => {
            let asset = release.assets.into_iter().find(|asset| {
                let name = asset.name.to_lowercase();
                name.ends_with(".zip") || name.ends_with(".7z")
            });
            (
                release.tag_name,
                release.html_url,
                asset.map(|asset| asset.browser_download_url),
            )
        }
        None => ("源码".into(), repository.html_url.clone(), None),
    };
    RemoteMod {
        id: format!("github:{}", repository.id),
        name: repository.name,
        author: repository.owner.login,
        summary: repository
            .description
            .unwrap_or_else(|| "GitHub 上的 Stardew Valley Mod".into()),
        source: "GitHub".into(),
        version,
        popularity: format!("{} stars", repository.stargazers_count),
        compatibility: "查看发布说明".into(),
        updated_at: repository.updated_at.chars().take(10).collect(),
        page_url,
        download_url,
        image_url: None,
        provider_id: Some(repository.full_name),
    }
}
