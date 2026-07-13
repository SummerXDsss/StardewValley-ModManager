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

export interface InstalledMod {
  id: string;
  name: string;
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

export interface TranslateModResult {
  name: string;
  summary: string;
}
