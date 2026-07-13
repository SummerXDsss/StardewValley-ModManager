import { useEffect, useState } from "react";
import { Alert, App, Button, Space, Tag } from "antd";
import {
  CloudDownloadOutlined,
  ExportOutlined,
  ReloadOutlined,
  RocketOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import { PageTitle } from "../shared";
import { downloadLatestSmapiInstaller, getLatestSmapiRelease } from "../../api";
import type { Dashboard, DownloadedSmapiInstaller, SmapiReleaseInfo } from "../../types";
import { compareSemver } from "../../utils/semver";

interface SmapiPageProps {
  dashboard: Dashboard;
  gameRunning?: boolean;
  onLaunchSmapi: () => void;
  onOpenSmapiDownload: () => void;
}

function formatBytes(value?: number): string {
  if (!value) return "大小将在下载时验证";
  return `${(value / 1024 / 1024).toFixed(1)} MB`;
}

export function SmapiPage({ dashboard, gameRunning = false, onLaunchSmapi, onOpenSmapiDownload }: SmapiPageProps) {
  const { message } = App.useApp();
  const [release, setRelease] = useState<SmapiReleaseInfo>();
  const [releaseLoading, setReleaseLoading] = useState(true);
  const [releaseError, setReleaseError] = useState<string>();
  const [downloading, setDownloading] = useState(false);
  const [downloaded, setDownloaded] = useState<DownloadedSmapiInstaller>();

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

  const downloadRelease = async () => {
    setDownloading(true);
    try {
      const result = await downloadLatestSmapiInstaller();
      setDownloaded(result);
      message.success(`已下载并校验 ${result.fileName}`);
    } catch (error) {
      message.error(String(error));
    } finally {
      setDownloading(false);
    }
  };

  const installedVersion = dashboard.smapi.version;
  const versionComparison = release && installedVersion
    ? compareSemver(release.version, installedVersion)
    : undefined;
  const updateAvailable = versionComparison !== undefined && versionComparison > 0;

  return (
    <section>
      <PageTitle title="SMAPI" subtitle="模组加载器与运行环境" />
      {releaseError && (
        <Alert
          type="warning"
          showIcon
          message="无法读取 SMAPI 官方 Release"
          description={releaseError}
          action={<Button icon={<ReloadOutlined />} onClick={() => void loadRelease()}>重试</Button>}
        />
      )}
      <div className="smapi-panel">
        <SafetyCertificateOutlined className="smapi-mark" />
        <div>
          <Tag color={dashboard.smapi.installed ? "green" : "red"}>
            {dashboard.smapi.installed ? "已安装" : "未安装"}
          </Tag>
          {updateAvailable && <Tag color="gold">可更新</Tag>}
          <h2>
            {dashboard.smapi.installed
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
              disabled={gameRunning}
              onClick={onLaunchSmapi}
            >
              {gameRunning ? "游戏运行中" : "启动 SMAPI"}
            </Button>
          )}
          <Button
            type={dashboard.smapi.installed ? "default" : "primary"}
            icon={<CloudDownloadOutlined />}
            loading={releaseLoading || downloading}
            disabled={!release}
            onClick={() => void downloadRelease()}
          >
            {updateAvailable ? "下载更新" : dashboard.smapi.installed ? "重新下载安装包" : "下载官方安装包"}
          </Button>
          {releaseError && (
            <Button type="link" icon={<ExportOutlined />} onClick={onOpenSmapiDownload}>
              打开官方页面
            </Button>
          )}
        </Space>
      </div>
      <div className="info-list">
        <div>
          <span>已安装版本</span>
          <strong>{dashboard.smapi.installed ? installedVersion ?? "无法读取" : "未安装"}</strong>
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
        {downloaded && (
          <div>
            <span>下载校验</span>
            <strong>{downloaded.digestVerified ? "SHA-256 已验证" : `SHA-256 ${downloaded.sha256.slice(0, 12)}...`}</strong>
          </div>
        )}
      </div>
    </section>
  );
}
