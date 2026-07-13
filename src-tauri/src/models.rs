use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GameInstallation {
    pub path: String,
    pub executable: String,
    pub store: String,
    pub version: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SmapiStatus {
    pub installed: bool,
    pub version: Option<String>,
    pub executable: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InstalledMod {
    pub id: String,
    pub name: String,
    pub author: String,
    pub version: String,
    pub path: String,
    pub enabled: bool,
    pub health: String,
    pub update_available: Option<String>,
    pub dependencies: Vec<String>,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Dashboard {
    pub installation: Option<GameInstallation>,
    pub smapi: SmapiStatus,
    pub mods: Vec<InstalledMod>,
    pub warnings: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoteMod {
    pub id: String,
    pub name: String,
    pub author: String,
    pub summary: String,
    pub source: String,
    pub version: String,
    pub popularity: String,
    pub compatibility: String,
    pub updated_at: String,
    pub page_url: String,
    pub download_url: Option<String>,
    pub image_url: Option<String>,
    pub provider_id: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NexusAuthStatus {
    pub configured: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NexusModDetails {
    pub id: String,
    pub game_scoped_id: String,
    pub name: String,
    pub page_url: String,
    pub files: Vec<NexusModFile>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NexusModFile {
    pub id: String,
    pub name: String,
    pub is_active: bool,
    pub last_file_uploaded_at: Option<String>,
    pub versions_count: u64,
    pub versions: Vec<NexusFileVersion>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NexusFileVersion {
    pub id: String,
    pub game_scoped_id: String,
    pub name: String,
    pub version: String,
    pub category: String,
    pub uploaded_at: String,
    pub is_primary: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DownloadedModFile {
    pub path: String,
    pub file_name: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchRequest {
    pub game_path: String,
    pub target: LaunchTarget,
    pub mods_path: Option<String>,
    #[serde(default)]
    pub arguments: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GameProcessStatus {
    pub state: GameProcessState,
    pub running: bool,
    pub pid: Option<u32>,
    pub target: Option<LaunchTarget>,
    pub started_at: Option<String>,
    pub exit_code: Option<i32>,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum GameProcessState {
    Stopped,
    Running,
    Exited,
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum LaunchTarget {
    Smapi,
    Vanilla,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Manifest {
    pub name: String,
    pub author: String,
    pub version: String,
    #[serde(rename = "UniqueID")]
    pub unique_id: String,
    #[serde(default, rename = "Dependencies")]
    pub dependencies: Vec<ManifestDependency>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ManifestDependency {
    #[serde(rename = "UniqueID")]
    pub unique_id: String,
    #[serde(default = "required_by_default", rename = "IsRequired")]
    pub is_required: bool,
}

fn required_by_default() -> bool {
    true
}
