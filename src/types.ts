export type Health = "healthy" | "warning" | "error";

export interface GameInstallation {
  path: string;
  executable: string;
  store: string;
  version?: string;
}

export interface SmapiStatus {
  installed: boolean;
  version?: string;
  executable?: string;
}

export interface SteamStatus {
  running: boolean;
}

export interface InstalledMod {
  id: string;
  name: string;
  description?: string;
  translated: boolean;
  author: string;
  version: string;
  path: string;
  enabled: boolean;
  health: Health;
  updateAvailable?: string;
  dependencies: string[];
  error?: string;
}

export interface Dashboard {
  installation?: GameInstallation;
  smapi: SmapiStatus;
  mods: InstalledMod[];
  warnings: string[];
}

export type LaunchTarget = "smapi" | "vanilla";

export interface LaunchRequest {
  gamePath: string;
  target: LaunchTarget;
  modsPath?: string;
  arguments: string[];
}

export type LaunchArgumentSettings = Record<LaunchTarget, string[]>;

export type GameProcessState = "stopped" | "running" | "exited";

export interface GameProcessStatus {
  state: GameProcessState;
  running: boolean;
  pid?: number;
  target?: LaunchTarget;
  startedAt?: string;
  exitCode?: number;
}

export interface RemoteMod {
  id: string;
  name: string;
  author: string;
  summary: string;
  source: string;
  version: string;
  popularity: string;
  compatibility: string;
  updatedAt: string;
  pageUrl: string;
  downloadUrl?: string;
  imageUrl?: string;
  providerId?: string;
  translated?: boolean;
}

export type RemoteModSearchSource = "all" | "nexus" | "github";

export interface SearchRemoteModsRequest {
  query: string;
  source: RemoteModSearchSource;
}

export interface RemoteModSearchIssue {
  source: string;
  kind: "warning" | "error";
  message: string;
}

export interface RemoteModSearchResult {
  mods: RemoteMod[];
  issues: RemoteModSearchIssue[];
}

export interface NexusAuthStatus {
  configured: boolean;
}

export interface NexusModDetails {
  id: string;
  gameScopedId: string;
  name: string;
  pageUrl: string;
  files: NexusModFile[];
}

export interface NexusModFile {
  id: string;
  name: string;
  isActive: boolean;
  lastFileUploadedAt?: string;
  versionsCount: number;
  versions: NexusFileVersion[];
}

export interface NexusFileVersion {
  id: string;
  gameScopedId: string;
  name: string;
  version: string;
  category: string;
  uploadedAt: string;
  isPrimary: boolean;
}

export interface DownloadedModFile {
  path: string;
  fileName: string;
  metadataPath?: string;
}

export interface DownloadedModTranslation {
  name: string;
  description: string;
}

export type SmapiPlatform = "windows" | "macos" | "linux";

export interface SmapiReleaseInfo {
  version: string;
  tagName: string;
  pageUrl: string;
  publishedAt?: string;
  platform: SmapiPlatform;
  installerEntry: string;
  source: "githubApi" | "githubLatestRedirect";
  asset: {
    id?: number;
    name: string;
    size?: number;
    downloadUrl: string;
    digest?: string;
  };
}

export interface DownloadedSmapiInstaller {
  path: string;
  fileName: string;
  version: string;
  size: number;
  sha256: string;
  digestVerified: boolean;
}

export interface InstalledSmapiResult {
  version: string;
  platform: SmapiPlatform;
  installerPath: string;
  exitCode: number;
}

export interface UninstalledSmapiResult {
  version: string | null;
  platform: SmapiPlatform;
  gamePath: string;
  installerPath: string;
  exitCode: number;
  message: string;
}

export type GithubDownloadMode = "direct" | "custom";

export interface GithubDownloadSettings {
  mode: GithubDownloadMode;
  customPrefix: string | null;
}

export interface AiTranslationStatus {
  configured: boolean;
  apiKeyConfigured: boolean;
  baseUrl?: string;
  modelId?: string;
}

export interface SaveAiTranslationSettingsRequest {
  baseUrl: string;
  modelId: string;
  apiKey?: string;
}

export interface ListAiTranslationModelsRequest {
  baseUrl: string;
  apiKey?: string;
}

export interface AiTranslationModel {
  id: string;
  ownedBy?: string | null;
}

export interface AiTranslationRequestMetadata {
  method: string;
  endpoint: string;
  body?: string | null;
}

export interface AiTranslationResponseMetadata {
  status: number;
  summary: string;
  content?: string | null;
}

export interface AiTranslationModelList {
  models: AiTranslationModel[];
  request: AiTranslationRequestMetadata;
  response: AiTranslationResponseMetadata;
}

export interface TestAiTranslationConnectionRequest {
  baseUrl: string;
  modelId: string;
  apiKey?: string;
}

export interface AiTranslationConnectionTestResult {
  modelId: string;
  message: string;
  request: AiTranslationRequestMetadata;
  response: AiTranslationResponseMetadata;
}

export interface TranslateModResult {
  name: string;
  summary: string;
}
