import { useEffect, useState } from "react";
import {
  Badge,
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
} from "@fluentui/react-components";
import {
  ArrowClockwise20Regular,
  Delete20Regular,
  Dismiss20Regular,
  Open20Regular,
  Rocket20Regular,
  ShieldCheckmark24Regular,
} from "@fluentui/react-icons";
import { PageTitle, useAppUi } from "../shared";
import { getLatestSmapiRelease, installLatestSmapi, uninstallSmapi } from "../../api";
import type { Dashboard, InstalledSmapiResult, SmapiReleaseInfo } from "../../types";
import { compareSemver } from "../../utils/semver";
import { formatDisplayPath } from "../../utils/path";

interface SmapiPageProps {
  dashboard: Dashboard;
  gameRunning?: boolean;
  onLaunchSmapi: () => void;
  onOpenSmapiDownload: () => void;
  onDashboardRefresh: () => Promise<Dashboard | undefined>;
}

function formatBytes(value?: number): string {
  if (!value) return "大小将在下载时验证";
  return `${(value / 1024 / 1024).toFixed(1)} MB`;
}

export function SmapiPage({
  dashboard,
  gameRunning = false,
  onLaunchSmapi,
  onOpenSmapiDownload,
  onDashboardRefresh,
}: SmapiPageProps) {
  const { confirm, notify } = useAppUi();
  const [release, setRelease] = useState<SmapiReleaseInfo>();
  const [releaseLoading, setReleaseLoading] = useState(true);
  const [releaseError, setReleaseError] = useState<string>();
  const [installing, setInstalling] = useState(false);
  const [installError, setInstallError] = useState<string>();
  const [uninstalling, setUninstalling] = useState(false);
  const [uninstallError, setUninstallError] = useState<string>();
  const [installedResult, setInstalledResult] = useState<InstalledSmapiResult>();

  const loadRelease = async () => {
    setReleaseLoading(true);
    setReleaseError(undefined);
    try {
      setRelease(await getLatestSmapiRelease());
    } catch (error) {
      setReleaseError(String(error));
    } finally {
      setReleaseLoading(false);
    }
  };

  useEffect(() => {
    void loadRelease();
  }, []);

  useEffect(() => {
    setInstalledResult(undefined);
    setInstallError(undefined);
    setUninstallError(undefined);
  }, [dashboard.installation?.path]);

  const confirmInstall = () => {
    const gamePath = dashboard.installation?.path;
    if (!gamePath) {
      notify("warning", "请先设置有效的游戏目录");
      return;
    }
    if (gameRunning) {
      notify("warning", "请先关闭游戏，再安装或更新 SMAPI");
      return;
    }

    confirm({
      title: `${dashboard.smapi.installed ? "更新" : "安装"} SMAPI ${release?.version ?? "最新稳定版"}`,
      content: (
        <div className="smapi-install-confirm">
          <p>安装程序会下载、解压并修改 Stardew Valley 游戏目录中的 SMAPI 文件。</p>
          <p><strong>目标目录：</strong><code>{formatDisplayPath(gamePath)}</code></p>
          <p>继续前请确认游戏已经关闭，并保留现有 Mods 与配置的备份。</p>
        </div>
      ),
      confirmLabel: dashboard.smapi.installed ? "确认更新" : "确认安装",
      cancelLabel: "取消",
      onConfirm: async () => {
        setInstalling(true);
        setInstallError(undefined);
        setUninstallError(undefined);
        try {
          const result = await installLatestSmapi(gamePath);
          const refreshed = await onDashboardRefresh();
          if (!refreshed) {
            throw new Error("安装程序已结束，但无法重新扫描游戏目录");
          }
          if (!refreshed.smapi.installed) {
            throw new Error("安装程序已结束，但重新扫描后仍未检测到 SMAPI");
          }
          const confirmedVersion = refreshed.smapi.version ?? result.version;
          setInstalledResult({ ...result, version: confirmedVersion });
          notify("success", `SMAPI ${confirmedVersion} 已安装`);
        } catch (error) {
          const detail = String(error);
          setInstallError(detail);
          notify("error", "SMAPI 安装未完成", detail);
        } finally {
          setInstalling(false);
        }
      },
    });
  };

  const confirmUninstall = () => {
    const gamePath = dashboard.installation?.path;
    if (!gamePath) {
      notify("warning", "请先设置有效的游戏目录");
      return;
    }
    if (gameRunning) {
      notify("warning", "请先关闭游戏，再卸载 SMAPI");
      return;
    }

    confirm({
      title: "卸载 SMAPI？",
      content: (
        <div className="smapi-install-confirm">
          <p>将运行 SMAPI 官方卸载器并移除游戏目录中的加载器文件。</p>
          <p><strong>目标目录：</strong><code>{formatDisplayPath(gamePath)}</code></p>
          <p>用户 Mods 将按官方卸载器行为保留；卸载前仍建议备份重要配置。</p>
        </div>
      ),
      confirmLabel: "卸载 SMAPI",
      cancelLabel: "取消",
      destructive: true,
      onConfirm: async () => {
        setUninstalling(true);
        setInstallError(undefined);
        setUninstallError(undefined);
        try {
          const result = await uninstallSmapi(gamePath);
          const refreshed = await onDashboardRefresh();
          if (!refreshed) {
            throw new Error("卸载程序已结束，但无法重新扫描游戏目录");
          }
          if (refreshed.smapi.installed) {
            throw new Error("卸载程序已结束，但重新扫描后仍检测到 SMAPI");
          }
          setInstalledResult(undefined);
          notify(
            "success",
            result.version
              ? `SMAPI ${result.version} 已卸载，用户 Mods 已保留`
              : "SMAPI 已卸载，用户 Mods 已保留",
          );
        } catch (error) {
          const detail = String(error);
          setUninstallError(detail);
          notify("error", "SMAPI 卸载未完成", detail);
        } finally {
          setUninstalling(false);
        }
      },
    });
  };

  const installedVersion = installedResult?.version ?? dashboard.smapi.version;
  const smapiInstalled = dashboard.smapi.installed || installedResult !== undefined;
  const versionComparison = release && installedVersion
    ? compareSemver(release.version, installedVersion)
    : undefined;
  const updateAvailable = versionComparison !== undefined && versionComparison > 0;

  return (
    <section>
      <PageTitle title="SMAPI" subtitle="模组加载器与运行环境" />
      {(releaseError || installError || uninstallError) && (
        <div className="smapi-alerts">
          {releaseError && (
            <MessageBar intent="warning">
              <MessageBarBody>
                <MessageBarTitle>无法读取 SMAPI 官方 Release</MessageBarTitle>
                {releaseError}
              </MessageBarBody>
              <MessageBarActions>
                <Button
                  appearance="transparent"
                  icon={<ArrowClockwise20Regular />}
                  onClick={() => void loadRelease()}
                >
                  重试
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}
          {installError && (
            <MessageBar intent="error">
              <MessageBarBody>
                <MessageBarTitle>SMAPI 安装未完成</MessageBarTitle>
                {installError}
              </MessageBarBody>
              <MessageBarActions
                containerAction={(
                  <Button
                    appearance="transparent"
                    icon={<Dismiss20Regular />}
                    aria-label="关闭 SMAPI 安装错误"
                    onClick={() => setInstallError(undefined)}
                  />
                )}
              />
            </MessageBar>
          )}
          {uninstallError && (
            <MessageBar intent="error">
              <MessageBarBody>
                <MessageBarTitle>SMAPI 卸载未完成</MessageBarTitle>
                {uninstallError}
              </MessageBarBody>
              <MessageBarActions
                containerAction={(
                  <Button
                    appearance="transparent"
                    icon={<Dismiss20Regular />}
                    aria-label="关闭 SMAPI 卸载错误"
                    onClick={() => setUninstallError(undefined)}
                  />
                )}
              />
            </MessageBar>
          )}
        </div>
      )}
      <div className="smapi-panel">
        <ShieldCheckmark24Regular className="smapi-mark" />
        <div>
          <Badge appearance="tint" color={smapiInstalled ? "success" : "danger"}>
            {smapiInstalled ? "已安装" : "未安装"}
          </Badge>
          {updateAvailable && <Badge appearance="tint" color="warning">可更新</Badge>}
          <h2>
            {smapiInstalled
              ? `SMAPI ${installedVersion ?? "版本未知"}`
              : release
                ? `可安装 SMAPI ${release.version}`
                : "需要安装 SMAPI"}
          </h2>
          <p>
            {dashboard.smapi.executable
              ?? release?.asset.name
              ?? (releaseError ? "官方稳定版本读取失败，请重试" : "正在读取官方稳定版本...")}
          </p>
        </div>
        <div className="smapi-actions">
          {dashboard.smapi.installed && (
            <Button
              appearance="primary"
              icon={<Rocket20Regular />}
              disabled={gameRunning || installing || uninstalling}
              onClick={onLaunchSmapi}
            >
              {gameRunning ? "游戏运行中" : "启动 SMAPI"}
            </Button>
          )}
          <Button
            appearance={smapiInstalled ? "secondary" : "primary"}
            icon={installing ? <Spinner size="tiny" /> : <ShieldCheckmark24Regular />}
            disabled={installing || uninstalling || releaseLoading || !dashboard.installation || gameRunning}
            aria-label={smapiInstalled ? "安装或更新 SMAPI" : "安装 SMAPI"}
            onClick={confirmInstall}
          >
            {updateAvailable ? "更新 SMAPI" : smapiInstalled ? "重新安装 SMAPI" : "安装 SMAPI"}
          </Button>
          {dashboard.smapi.installed && (
            <Button
              appearance="secondary"
              className="danger-button"
              icon={uninstalling ? <Spinner size="tiny" /> : <Delete20Regular />}
              disabled={installing || uninstalling || gameRunning}
              aria-label="卸载 SMAPI 并保留用户 Mods"
              onClick={confirmUninstall}
            >
              卸载 SMAPI
            </Button>
          )}
          {(releaseError || installError || uninstallError) && (
            <Button appearance="subtle" icon={<Open20Regular />} onClick={onOpenSmapiDownload}>
              打开官方页面
            </Button>
          )}
        </div>
      </div>
      <div className="info-list">
        <div>
          <span>已安装版本</span>
          <strong>{smapiInstalled ? installedVersion ?? "无法读取" : "未安装"}</strong>
        </div>
        <div>
          <span>官方最新稳定版</span>
          <strong>{releaseLoading ? "正在检查" : release?.version ?? "暂不可用"}</strong>
        </div>
        <div>
          <span>当前系统安装入口</span>
          <strong>{release?.installerEntry ?? "等待 Release 信息"}</strong>
        </div>
        <div>
          <span>官方安装资产</span>
          <strong>{release ? `${release.asset.name} · ${formatBytes(release.asset.size)}` : "等待 Release 信息"}</strong>
        </div>
        {installedResult && (
          <div>
            <span>最近安装结果</span>
            <strong>SMAPI {installedResult.version} · 退出码 {installedResult.exitCode}</strong>
          </div>
        )}
      </div>
    </section>
  );
}
