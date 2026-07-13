import { invoke } from "@tauri-apps/api/core";
import { demoDashboard } from "./mock";
import type {
  Dashboard,
  DownloadedModFile,
  GameProcessStatus,
  LaunchRequest,
  NexusAuthStatus,
  NexusModDetails,
  RemoteMod,
} from "./types";

const isTauri = () => "__TAURI_INTERNALS__" in window;
let demoGameProcessStatus: GameProcessStatus = { state: "stopped", running: false };

export async function getDashboard(): Promise<Dashboard> {
  return isTauri() ? invoke<Dashboard>("get_dashboard") : structuredClone(demoDashboard);
}

export async function scanGamePath(gamePath: string): Promise<Dashboard> {
  if (!isTauri()) {
    const dashboard = structuredClone(demoDashboard);
    if (dashboard.installation) dashboard.installation.path = gamePath;
    return dashboard;
  }
  return invoke<Dashboard>("scan_game_path", { gamePath });
}

export async function setModEnabled(gamePath: string, modPath: string, enabled: boolean) {
  if (!isTauri()) return;
  await invoke("set_mod_enabled", { gamePath, modPath, enabled });
}

export async function removeMod(gamePath: string, modPath: string) {
  if (!isTauri()) return;
  await invoke("remove_mod", { gamePath, modPath });
}

export async function openModFolder(gamePath: string, modPath: string) {
  if (!isTauri()) return;
  await invoke("open_mod_folder", { gamePath, modPath });
}

export async function launchGame(request: LaunchRequest): Promise<GameProcessStatus> {
  if (!isTauri()) {
    demoGameProcessStatus = {
      state: "running",
      running: true,
      target: request.target,
      startedAt: new Date().toISOString(),
    };
    return structuredClone(demoGameProcessStatus);
  }
  return invoke<GameProcessStatus>("launch_game", { request });
}

export async function getGameProcessStatus(): Promise<GameProcessStatus> {
  if (!isTauri()) return structuredClone(demoGameProcessStatus);
  return invoke<GameProcessStatus>("get_game_process_status");
}

export async function stopGame(): Promise<GameProcessStatus> {
  if (!isTauri()) {
    demoGameProcessStatus = { ...demoGameProcessStatus, state: "stopped", running: false };
    return structuredClone(demoGameProcessStatus);
  }
  return invoke<GameProcessStatus>("stop_game");
}

export async function restartGame(): Promise<GameProcessStatus> {
  if (!isTauri()) {
    if (!demoGameProcessStatus.target) throw new Error("没有可用于重启的上次启动配置");
    demoGameProcessStatus = {
      ...demoGameProcessStatus,
      state: "running",
      running: true,
      startedAt: new Date().toISOString(),
      exitCode: undefined,
    };
    return structuredClone(demoGameProcessStatus);
  }
  return invoke<GameProcessStatus>("restart_game");
}

export async function openSmapiDownload() {
  if (!isTauri()) {
    window.open("https://smapi.io/", "_blank", "noopener,noreferrer");
    return;
  }
  await invoke("open_smapi_download");
}

export async function discoverMods(): Promise<RemoteMod[]> {
  if (isTauri()) return invoke<RemoteMod[]>("discover_mods");

  const [githubResult, nexusResult] = await Promise.allSettled([
    fetch("https://api.github.com/search/repositories?q=topic%3Astardew-valley-mod+archived%3Afalse&sort=stars&order=desc&per_page=12", {
      headers: { Accept: "application/vnd.github+json" },
    }).then(async (response) => {
      if (!response.ok) throw new Error(`GitHub 请求失败：${response.status}`);
      const data = await response.json() as {
        items: Array<{
          id: number;
          name: string;
          owner: { login: string };
          description: string | null;
          html_url: string;
          stargazers_count: number;
          updated_at: string;
        }>;
      };
      return data.items.map((repo): RemoteMod => ({
        id: `github:${repo.id}`,
        name: repo.name,
        author: repo.owner.login,
        summary: repo.description ?? "GitHub 上的 Stardew Valley Mod",
        source: "GitHub",
        version: "源码",
        popularity: `${repo.stargazers_count} stars`,
        compatibility: "查看发布说明",
        updatedAt: repo.updated_at.slice(0, 10),
        pageUrl: repo.html_url,
        providerId: `${repo.owner.login}/${repo.name}`,
      }));
    }),
    fetch("https://api.nexusmods.com/v3/games/stardewvalley/trending-mods", {
      headers: { Accept: "application/json" },
    }).then(async (response) => {
      if (!response.ok) throw new Error(`Nexus Mods 请求失败：${response.status}`);
      const data = await response.json() as {
        data: { mods: Array<{ name: string; author?: string; summary?: string; picture_url?: string; mod_page_url: string }> };
      };
      return data.data.mods.map((mod): RemoteMod => ({
        id: `nexus:${mod.mod_page_url}`,
        name: mod.name,
        author: mod.author ?? "Nexus Mods 作者",
        summary: mod.summary ?? "Nexus Mods 热门 Mod",
        source: "Nexus Mods",
        version: "热门",
        popularity: "趋势榜",
        compatibility: "查看发布说明",
        updatedAt: "实时",
        pageUrl: mod.mod_page_url,
        imageUrl: mod.picture_url,
        providerId: mod.mod_page_url.split("/").filter(Boolean).at(-1),
      }));
    }),
  ]);

  const mods = [
    ...(nexusResult.status === "fulfilled" ? nexusResult.value : []),
    ...(githubResult.status === "fulfilled" ? githubResult.value : []),
  ];
  if (!mods.length) throw new Error("GitHub 与 Nexus Mods 数据源均不可用");
  return mods;
}

export async function openRemoteUrl(url: string) {
  if (!isTauri()) {
    window.open(url, "_blank", "noopener,noreferrer");
    return;
  }
  await invoke("open_remote_url", { url });
}

export async function getNexusAuthStatus(): Promise<NexusAuthStatus> {
  if (!isTauri()) return { configured: false };
  return invoke<NexusAuthStatus>("get_nexus_auth_status");
}

export async function setNexusApiKey(apiKey: string): Promise<NexusAuthStatus> {
  if (!isTauri()) throw new Error("请在 Tauri 桌面应用中配置 Nexus API Key");
  return invoke<NexusAuthStatus>("set_nexus_api_key", { apiKey });
}

export async function clearNexusApiKey(): Promise<NexusAuthStatus> {
  if (!isTauri()) return { configured: false };
  return invoke<NexusAuthStatus>("clear_nexus_api_key");
}

export async function getNexusModDetails(gameScopedId: string): Promise<NexusModDetails> {
  if (!isTauri()) throw new Error("完整 Nexus 详情需要在 Tauri 桌面应用中使用");
  return invoke<NexusModDetails>("get_nexus_mod_details", { gameScopedId });
}

export async function downloadNexusFile(modId: string, fileId: string): Promise<DownloadedModFile> {
  if (!isTauri()) throw new Error("Nexus 文件下载需要在 Tauri 桌面应用中使用");
  return invoke<DownloadedModFile>("download_nexus_file", { modId, fileId });
}
