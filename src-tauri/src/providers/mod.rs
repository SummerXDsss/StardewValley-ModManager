pub mod github;
pub mod github_settings;
pub mod nexus;
pub mod smapi;
pub mod translation;

use std::{process::Command, sync::Mutex};

use keyring::Entry;

use crate::models::{
    RemoteMod, RemoteModSearchIssue, RemoteModSearchIssueKind, RemoteModSearchResult,
    RemoteModSearchSource, SearchRemoteModsRequest,
};

static KEYRING_ENTRY_INIT_LOCK: Mutex<()> = Mutex::new(());

pub(crate) fn keyring_entry(service: &str, user: &str) -> keyring::Result<Entry> {
    // keyring's v1 compatibility layer lazily installs its platform store on the first Entry.
    // Serialize construction so simultaneous settings requests cannot observe half-initialized state.
    let _guard = KEYRING_ENTRY_INIT_LOCK
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    Entry::new(service, user)
}

pub async fn discover() -> Result<Vec<RemoteMod>, String> {
    let mut mods = Vec::new();
    let mut errors = Vec::new();

    match nexus::discover().await {
        Ok(items) => mods.extend(items),
        Err(error) => errors.push(error),
    }
    match github::discover().await {
        Ok(items) => mods.extend(items),
        Err(error) => errors.push(error),
    }

    if mods.is_empty() {
        Err(errors.join("；"))
    } else {
        Ok(mods)
    }
}

pub async fn search(request: SearchRemoteModsRequest) -> Result<RemoteModSearchResult, String> {
    let query = normalize_search_query(&request.query)?;
    let mut mods = Vec::new();
    let mut issues = Vec::new();

    match request.source {
        RemoteModSearchSource::All => {
            let (nexus_result, github_result) =
                tokio::join!(nexus::search(&query), github::search(&query));
            let nexus_succeeded =
                collect_search_result(&mut mods, &mut issues, "Nexus Mods", nexus_result);
            collect_search_result(&mut mods, &mut issues, "GitHub", github_result);
            if nexus_succeeded {
                issues.push(nexus_search_scope_notice());
            }
        }
        RemoteModSearchSource::Nexus => {
            let nexus_succeeded = collect_search_result(
                &mut mods,
                &mut issues,
                "Nexus Mods",
                nexus::search(&query).await,
            );
            if nexus_succeeded {
                issues.push(nexus_search_scope_notice());
            }
        }
        RemoteModSearchSource::Github => {
            collect_search_result(
                &mut mods,
                &mut issues,
                "GitHub",
                github::search(&query).await,
            );
        }
    }

    Ok(RemoteModSearchResult { mods, issues })
}

fn normalize_search_query(value: &str) -> Result<String, String> {
    let query = value.trim();
    if query.is_empty() {
        return Err("请输入要搜索的 Mod 名称、作者或功能".into());
    }
    if query.chars().count() > 100 {
        return Err("搜索内容不能超过 100 个字符".into());
    }
    if query.chars().any(char::is_control) {
        return Err("搜索内容不能包含控制字符".into());
    }
    Ok(query.to_string())
}

fn collect_search_result(
    mods: &mut Vec<RemoteMod>,
    issues: &mut Vec<RemoteModSearchIssue>,
    source: &str,
    result: Result<Vec<RemoteMod>, String>,
) -> bool {
    match result {
        Ok(items) => {
            mods.extend(items);
            true
        }
        Err(message) => {
            issues.push(RemoteModSearchIssue {
                source: source.into(),
                kind: RemoteModSearchIssueKind::Error,
                message,
            });
            false
        }
    }
}

fn nexus_search_scope_notice() -> RemoteModSearchIssue {
    RemoteModSearchIssue {
        source: "Nexus Mods".into(),
        kind: RemoteModSearchIssueKind::Warning,
        message: "Nexus 官方公开 API 没有全站关键词搜索端点；当前结果来自官方热门、最近更新和最近新增列表中的匹配项。".into(),
    }
}

pub fn open_remote_url(value: &str) -> Result<(), String> {
    let url = reqwest::Url::parse(value).map_err(|_| "上游链接格式无效".to_string())?;
    if url.scheme() != "https" {
        return Err("只允许打开 HTTPS 上游链接".into());
    }
    let allowed = matches!(
        url.host_str(),
        Some("github.com")
            | Some("api.github.com")
            | Some("nexusmods.com")
            | Some("www.nexusmods.com")
            | Some("moddrop.com")
            | Some("www.moddrop.com")
            | Some("smapi.io")
    );
    if !allowed {
        return Err("该链接不属于受信任的 Mod 上游站点".into());
    }

    #[cfg(target_os = "windows")]
    let mut command = Command::new("explorer.exe");
    #[cfg(target_os = "macos")]
    let mut command = Command::new("open");
    #[cfg(target_os = "linux")]
    let mut command = Command::new("xdg-open");

    command
        .arg(url.as_str())
        .spawn()
        .map_err(|error| format!("无法打开上游链接：{error}"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::normalize_search_query;

    #[test]
    fn search_query_is_trimmed_and_bounded() {
        assert_eq!(
            normalize_search_query("  content patcher  ").unwrap(),
            "content patcher"
        );
        assert!(normalize_search_query("   ").is_err());
        assert!(normalize_search_query(&"a".repeat(101)).is_err());
        assert!(normalize_search_query("tractor\nmod").is_err());
    }

    #[tokio::test]
    #[ignore = "requires live upstream network access"]
    async fn discovers_live_mods_from_official_apis() {
        let mods = super::discover()
            .await
            .expect("live providers should respond");
        assert!(mods.iter().any(|item| item.source == "Nexus Mods"));
        assert!(mods.iter().any(|item| item.source == "GitHub"));
        assert!(mods
            .iter()
            .all(|item| item.page_url.starts_with("https://")));
    }
}
