import { useEffect, useState } from "react";
import { Alert, App, Button, Space, Tag } from "antd";
import {
  DeleteOutlined,
  ExportOutlined,
  ReloadOutlined,
  RocketOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import { PageTitle } from "../shared";
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
  const { message, modal } = App.useApp();
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
      message.warning("请先设置有效的游戏目录");
      return;
    }
    if (gameRunning) {
      message.warning("请先关闭游戏，再安装或更新 SMAPI");
      return;
    }

    modal.confirm({
      title: `${dashboard.smapi.installed ? "更新" : "安装"} SMAPI ${release?.version ?? "最新稳定版"}`,
      icon: <SafetyCertificateOutlined />,
      content: (
        <div className="smapi-install-confirm">
          <p>安装程序会下载、解压并修改 Stardew Valley 游戏目录中的 SMAPI 文件。</p>
          <p><strong>目标目录：</strong><code>{formatDisplayPath(gamePath)}</code></p>
          <p>继续前请确认游戏已经关闭，并保留现有 Mods 与配置的备份。</p>
        </div>
      ),
      okText: dashboard.smapi.installed ? "确认更新" : "确认安装",
      cancelText: "取消",
      okButtonProps: { disabled: installing },
      onOk: async () => {
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
          message.success(`SMAPI ${confirmedVersion} 已安装`);
        } catch (error) {
          const detail = String(error);
          setInstallError(detail);
          message.error(detail);
        } finally {
          setInstalling(false);
        }
      },
    });
  };

  const confirmUninstall = () => {
    const gamePath = dashboard.installation?.path;
    if (!gamePath) {
      message.warning("请先设置有效的游戏目录");
      return;
    }
    if (gameRunning) {
      message.warning("请先关闭游戏，再卸载 SMAPI");
      return;
    }

    modal.confirm({
      title: "卸载 SMAPI？",
      icon: <DeleteOutlined />,
      content: (
        <div className="smapi-install-confirm">
          <p>将运行 SMAPI 官方卸载器并移除游戏目录中的加载器文件。</p>
          <p><strong>目标目录：</strong><code>{formatDisplayPath(gamePath)}</code></p>
          <p>用户 Mods 将按官方卸载器行为保留；卸载前仍建议备份重要配置。</p>
        </div>
      ),
      okText: "卸载 SMAPI",
      cancelText: "取消",
      okButtonProps: { danger: true },
      onOk: async () => {
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
          message.success(result.version
            ? `SMAPI ${result.version} 已卸载，用户 Mods 已保留`
            : "SMAPI 已卸载，用户 Mods 已保留");
        } catch (error) {
          const detail = String(error);
          setUninstallError(detail);
          message.error(detail);
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
            <Alert
              type="warning"
              showIcon
              message="无法读取 SMAPI 官方 Release"
              description={releaseError}
              action={<Button icon={<ReloadOutlined />} onClick={() => void loadRelease()}>重试</Button>}
            />
          )}
          {installError && (
            <Alert
              type="error"
              showIcon
              closable
              message="SMAPI 安装未完成"
              description={installError}
              onClose={() => setInstallError(undefined)}
            />
          )}
          {uninstallError && (
            <Alert
              type="error"
              showIcon
              closable
              message="SMAPI 卸载未完成"
              description={uninstallError}
              onClose={() => setUninstallError(undefined)}
            />
          )}
        </div>
      )}
      <div className="smapi-panel">
        <SafetyCertificateOutlined className="smapi-mark" />
        <div>
          <Tag color={smapiInstalled ? "green" : "red"}>
            {smapiInstalled ? "已安装" : "未安装"}
          </Tag>
          {updateAvailable && <Tag color="gold">可更新</Tag>}
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
        <Space direction="vertical" align="end" size={8}>
          {dashboard.smapi.installed && (
            <Button
              type="primary"
              icon={<RocketOutlined />}
              disabled={gameRunning || installing || uninstalling}
              onClick={onLaunchSmapi}
            >
              {gameRunning ? "游戏运行中" : "启动 SMAPI"}
            </Button>
          )}
          <Button
            type={smapiInstalled ? "default" : "primary"}
            icon={<SafetyCertificateOutlined />}
            loading={installing}
            disabled={installing || uninstalling || releaseLoading || !dashboard.installation || gameRunning}
            aria-label={smapiInstalled ? "安装或更新 SMAPI" : "安装 SMAPI"}
            onClick={confirmInstall}
          >
            {updateAvailable ? "更新 SMAPI" : smapiInstalled ? "重新安装 SMAPI" : "安装 SMAPI"}
          </Button>
          {dashboard.smapi.installed && (
            <Button
              danger
              icon={<DeleteOutlined />}
              loading={uninstalling}
              disabled={installing || uninstalling || gameRunning}
              aria-label="卸载 SMAPI 并保留用户 Mods"
              onClick={confirmUninstall}
            >
              卸载 SMAPI
            </Button>
          )}
          {(releaseError || installError || uninstallError) && (
            <Button type="link" icon={<ExportOutlined />} onClick={onOpenSmapiDownload}>
              打开官方页面
            </Button>
          )}
        </Space>
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
