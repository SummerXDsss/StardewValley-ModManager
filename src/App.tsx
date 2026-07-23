import { useRef, useState } from "react";
import { Spinner } from "@fluentui/react-components";
import { Sidebar, Topbar } from "./components/layout";
import { FirstRunSetup } from "./components/onboarding";
import { OverviewPage, ModsPage, DownloadsPage, SmapiPage, SettingsPage } from "./components/pages";
import { useAppUi } from "./components/shared";
import { useDashboard, useGameProcess, useLaunchArguments, useLaunchPreference, useSteamStatus } from "./hooks";
import type { LaunchPreference, ResolvedTheme } from "./hooks";
import {
  launchGame,
  openModFolder,
  openSmapiDownload,
  removeMod,
  restartGame,
  scanGamePath,
  setModEnabled,
  stopGame,
  translateInstalledMod,
} from "./api";
import type { GameProcessAction } from "./components/layout/Topbar";
import type { InstalledMod, LaunchTarget } from "./types";

const ONBOARDING_KEY = "valleySteward.onboardingComplete.v2";

interface AppProps {
  resolvedTheme: ResolvedTheme;
  onToggleTheme: () => void;
}

export default function App({ resolvedTheme, onToggleTheme }: AppProps) {
  const { notify, confirm } = useAppUi();
  const message = {
    success: (text: string) => notify("success", text),
    error: (text: string) => notify("error", "操作未完成", text),
    warning: (text: string) => notify("warning", text),
    info: (text: string) => notify("info", text),
  };
  const [page, setPage] = useState("overview");
  const [onboardingComplete, setOnboardingComplete] = useState(
    () => localStorage.getItem(ONBOARDING_KEY) === "true",
  );
  const {
    dashboard,
    setDashboard,
    loading,
    error: dashboardError,
    setLoading,
    refresh,
    updateMod,
  } = useDashboard();
  const { rememberLaunch, launchPreference, changeRememberLaunch, updateLaunchPreference } = useLaunchPreference();
  const { launchArguments, updateLaunchArguments } = useLaunchArguments();
  const steamStatus = useSteamStatus();
  const {
    status: gameProcess,
    monitoring,
    monitorError,
    acceptStatus,
  } = useGameProcess();
  const [gameProcessAction, setGameProcessAction] = useState<GameProcessAction>(null);
  const [translatingModPaths, setTranslatingModPaths] = useState<Set<string>>(() => new Set());
  const translatingModPathsRef = useRef(new Set<string>());
  const gameRunning = gameProcess.running || gameProcess.state === "running";
  const modChangesDisabled = monitoring || gameRunning || gameProcessAction !== null;

  const toggleMod = async (mod: InstalledMod, enabled: boolean) => {
    if (modChangesDisabled) return message.warning("请先关闭游戏，再更改 Mod 加载状态");
    if (!dashboard?.installation) return;
    updateMod(mod.path, { enabled });
    try {
      await setModEnabled(dashboard.installation.path, mod.path, enabled);
      message.success(`${mod.name} 已${enabled ? "启用" : "禁用"}`);
    } catch (error) {
      updateMod(mod.path, { enabled: !enabled });
      message.error(String(error));
    }
  };

  const handleOpenFolder = async (mod: InstalledMod) => {
    if (!dashboard?.installation) return;
    await openModFolder(dashboard.installation.path, mod.path);
  };

  const confirmRemove = (mod: InstalledMod) => {
    if (modChangesDisabled) return message.warning("请先关闭游戏，再移除 Mod");
    confirm({
      title: `移除 ${mod.name}？`,
      content: "Mod 将被移动到管理器回收区，可以在清空前恢复。",
      confirmLabel: "移到回收区",
      destructive: true,
      onConfirm: async () => {
        if (modChangesDisabled) return;
        if (!dashboard?.installation) return;
        await removeMod(dashboard.installation.path, mod.path);
        setDashboard((current) =>
          current ? { ...current, mods: current.mods.filter((item) => item.path !== mod.path) } : current,
        );
        message.success("已移到回收区");
      },
    });
  };

  const handleTranslateMod = async (mod: InstalledMod) => {
    const gamePath = dashboard?.installation?.path;
    if (!gamePath || translatingModPathsRef.current.has(mod.path)) return;
    translatingModPathsRef.current.add(mod.path);
    setTranslatingModPaths(new Set(translatingModPathsRef.current));
    try {
      const result = await translateInstalledMod(gamePath, mod.path);
      updateMod(mod.path, {
        name: result.name,
        description: result.summary,
        translated: true,
      });
      message.success(`${result.name} 翻译已保存`);
    } catch (error) {
      message.error(String(error));
    } finally {
      translatingModPathsRef.current.delete(mod.path);
      setTranslatingModPaths(new Set(translatingModPathsRef.current));
    }
  };

  const start = async (target: LaunchTarget, modsPath?: string) => {
    if (gameRunning || gameProcessAction !== null) return message.info("游戏已经在运行或正在处理启动操作");
    if (!dashboard?.installation) return message.warning("请先设置有效的游戏目录");
    if (target === "smapi" && !dashboard.smapi.installed) return message.warning("请先安装 SMAPI");
    setGameProcessAction("launching");
    try {
      const status = await launchGame({
        gamePath: dashboard.installation.path,
        target,
        modsPath,
        arguments: launchArguments[target],
      });
      acceptStatus(status);
      message.success(target === "smapi" ? "SMAPI 已启动" : "游戏已启动");
    } catch (error) {
      message.error(String(error));
    } finally {
      setGameProcessAction(null);
    }
  };

  const runLaunchChoice = (choice: LaunchPreference) => {
    if (gameRunning || gameProcessAction !== null) {
      message.info("游戏已经在运行或正在处理启动操作");
      return;
    }
    updateLaunchPreference(choice);
    if (choice === "vanilla") return void start("vanilla");
    if (choice === "profile") return void start("smapi", "Mods (multiplayer)");
    return void start("smapi");
  };

  const handleLaunchSmapi = () => void start("smapi");
  const handleOpenSmapiDownload = () => void openSmapiDownload();

  const handleStopGame = async () => {
    if (!gameRunning || gameProcessAction !== null) return;
    setGameProcessAction("stopping");
    try {
      acceptStatus(await stopGame());
      message.success("游戏已关闭");
    } catch (error) {
      message.error(String(error));
    } finally {
      setGameProcessAction(null);
    }
  };

  const handleRestartGame = async () => {
    if (!gameRunning || gameProcessAction !== null) return;
    setGameProcessAction("restarting");
    try {
      acceptStatus(await restartGame());
      message.success("游戏已按原启动方式重新启动");
    } catch (error) {
      message.error(String(error));
    } finally {
      setGameProcessAction(null);
    }
  };

  const renderPage = () => {
    if (!dashboard) return null;

    switch (page) {
      case "mods":
        return (
          <ModsPage
            dashboard={dashboard}
            modChangesDisabled={modChangesDisabled}
            onRefresh={refresh}
            onToggleMod={toggleMod}
            onOpenFolder={handleOpenFolder}
            onRemoveMod={confirmRemove}
            onTranslateMod={handleTranslateMod}
            translatingModPaths={translatingModPaths}
          />
        );
      case "downloads":
        return <DownloadsPage />;
      case "smapi":
        return (
          <SmapiPage
            dashboard={dashboard}
            gameRunning={gameRunning}
            onLaunchSmapi={handleLaunchSmapi}
            onOpenSmapiDownload={handleOpenSmapiDownload}
            onDashboardRefresh={refresh}
          />
        );
      case "settings":
        return (
          <SettingsPage
            dashboard={dashboard}
            onScanPath={scanGamePath}
            onDashboardUpdate={setDashboard}
            onLoadingChange={setLoading}
            launchArguments={launchArguments}
            onLaunchArgumentsChange={updateLaunchArguments}
          />
        );
      default:
        return (
          <OverviewPage
            dashboard={dashboard}
            modChangesDisabled={modChangesDisabled}
            onRefresh={refresh}
            onPageChange={setPage}
            onToggleMod={toggleMod}
            onOpenFolder={handleOpenFolder}
            onRemoveMod={confirmRemove}
          />
        );
    }
  };

  return (
    <>
      <div className="app-layout">
        <Sidebar currentPage={page} onPageChange={setPage} />
        <div className="app-main">
          <Topbar
            gamePath={dashboard?.installation?.path}
            smapi={dashboard?.smapi}
            smapiLoading={loading && !dashboard}
            smapiError={dashboard ? undefined : dashboardError}
            steamRunning={steamStatus.running}
            steamIdentity={steamStatus.identity}
            steamLoading={steamStatus.loading}
            steamError={steamStatus.error}
            resolvedTheme={resolvedTheme}
            onToggleTheme={onToggleTheme}
            rememberLaunch={rememberLaunch}
            onRememberLaunchChange={changeRememberLaunch}
            onLaunch={runLaunchChoice}
            launchPreference={launchPreference}
            processStatus={gameProcess}
            processAction={gameProcessAction}
            monitoring={monitoring}
            monitorError={monitorError}
            onEditGamePath={() => setPage("settings")}
            onStop={() => void handleStopGame()}
            onRestart={() => void handleRestartGame()}
          />
          <main key={page} className="content">
            {loading && (
              <div className="page-loading" role="status" aria-live="polite">
                <Spinner size="medium" label="正在读取本地状态" />
              </div>
            )}
            {renderPage()}
          </main>
        </div>
      </div>
      {dashboard && (
        <FirstRunSetup
          open={!onboardingComplete || !dashboard.installation}
          dashboard={dashboard}
          onDashboardUpdate={setDashboard}
          onComplete={() => {
            localStorage.setItem(ONBOARDING_KEY, "true");
            setOnboardingComplete(true);
          }}
        />
      )}
    </>
  );
}
