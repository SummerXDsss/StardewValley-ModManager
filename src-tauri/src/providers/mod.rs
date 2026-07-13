pub mod github;
pub mod github_settings;
pub mod nexus;
pub mod smapi;
pub mod translation;

use std::{process::Command, sync::Mutex};

use keyring::Entry;

use crate::models::RemoteMod;

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
