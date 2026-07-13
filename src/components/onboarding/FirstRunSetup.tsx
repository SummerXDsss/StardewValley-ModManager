import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert, App, Button, Input, Modal, Space, Steps, Tag } from "antd";
import {
  CheckCircleOutlined,
  FolderOpenOutlined,
  ReloadOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import {
  chooseGameDirectory,
  downloadLatestSmapiInstaller,
  getLatestSmapiRelease,
  scanGamePath,
} from "../../api";
import type { Dashboard, SmapiReleaseInfo } from "../../types";
import { formatDisplayPath } from "../../utils/path";

interface FirstRunSetupProps {
  open: boolean;
  dashboard: Dashboard;
  onDashboardUpdate: (dashboard: Dashboard) => void;
  onComplete: () => void;
}

export function FirstRunSetup({ open, dashboard, onDashboardUpdate, onComplete }: FirstRunSetupProps) {
  const { message } = App.useApp();
  const detectedPath = dashboard.installation ? formatDisplayPath(dashboard.installation.path) : "";
  const [step, setStep] = useState(0);
  const [pathInput, setPathInput] = useState(detectedPath);
  const [verifiedPath, setVerifiedPath] = useState(detectedPath);
  const [verifying, setVerifying] = useState(false);
  const [smapiRelease, setSmapiRelease] = useState<SmapiReleaseInfo>();
  const [smapiReleaseError, setSmapiReleaseError] = useState<string>();
  const [smapiReleaseLoading, setSmapiReleaseLoading] = useState(false);
  const [smapiDownloading, setSmapiDownloading] = useState(false);

  const loadSmapiRelease = useCallback(async () => {
    setSmapiReleaseLoading(true);
    setSmapiReleaseError(undefined);
    try {
      setSmapiRelease(await getLatestSmapiRelease());
    } catch (error) {
      setSmapiReleaseError(String(error));
    } finally {
      setSmapiReleaseLoading(false);
    }
  }, []);

  useEffect(() => {
    if (!detectedPath) return;
    setPathInput(detectedPath);
    setVerifiedPath(detectedPath);
  }, [detectedPath]);

  useEffect(() => {
    if (!open || step !== 1 || smapiRelease || smapiReleaseError || smapiReleaseLoading) return;
    void loadSmapiRelease();
  }, [loadSmapiRelease, open, step, smapiRelease, smapiReleaseError, smapiReleaseLoading]);

  const pathReady = useMemo(
    () => Boolean(verifiedPath) && verifiedPath === pathInput.trim(),
    [pathInput, verifiedPath],
  );

  const choosePath = async () => {
    const selected = await chooseGameDirectory();
    if (selected) setPathInput(selected);
  };

  const verifyPath = async () => {
    const candidate = pathInput.trim();
    if (!candidate) return message.warning("请先填写游戏目录");
    setVerifying(true);
    try {
      const nextDashboard = await scanGamePath(candidate);
      onDashboardUpdate(nextDashboard);
      const normalized = nextDashboard.installation
        ? formatDisplayPath(nextDashboard.installation.path)
        : candidate;
      setPathInput(normalized);
      setVerifiedPath(normalized);
      message.success("游戏路径已确认");
    } catch (error) {
      setVerifiedPath("");
      message.error(String(error));
    } finally {
      setVerifying(false);
    }
  };

  const finish = () => {
    if (!dashboard.installation) return;
    onComplete();
  };

  const downloadSmapi = async () => {
    setSmapiDownloading(true);
    try {
      const downloaded = await downloadLatestSmapiInstaller();
      message.success(`已下载并校验 ${downloaded.fileName}`);
    } catch (error) {
      message.error(String(error));
    } finally {
      setSmapiDownloading(false);
    }
  };

  return (
    <Modal
      open={open}
      closable={false}
      maskClosable={false}
      keyboard={false}
      footer={null}
      width={720}
      title={(
        <div className="setup-heading">
          <span>Valley Steward</span>
          <h2>首次设置</h2>
        </div>
      )}
    >
      <div className="first-run-setup">
        <Steps
          current={step}
          size="small"
          items={[{ title: "游戏目录" }, { title: "SMAPI" }, { title: "完成" }]}
        />

        {step === 0 && (
          <div className="setup-step">
            {dashboard.installation ? (
              <Alert type="success" showIcon message="已自动获取游戏路径" description={dashboard.installation.store} />
            ) : (
              <Alert type="warning" showIcon message="未自动找到游戏" description="请选择或填写 Stardew Valley 安装目录。" />
            )}
            <label htmlFor="first-run-game-path">Stardew Valley 游戏目录</label>
            <Space.Compact block>
              <Input
                id="first-run-game-path"
                value={pathInput}
                onChange={(event) => setPathInput(event.target.value)}
              />
              <Button icon={<FolderOpenOutlined />} aria-label="选择游戏目录" onClick={() => void choosePath()} />
              <Button type="primary" loading={verifying} onClick={() => void verifyPath()}>
                验证路径
              </Button>
            </Space.Compact>
            <div className="setup-actions">
              <Button type="primary" disabled={!pathReady} onClick={() => setStep(1)}>
                继续
              </Button>
            </div>
          </div>
        )}

        {step === 1 && (
          <div className="setup-step setup-smapi-status">
            <SafetyCertificateOutlined />
            <div>
              <Tag color={dashboard.smapi.installed ? "green" : "default"}>
                {dashboard.smapi.installed ? "已安装" : "尚未安装"}
              </Tag>
              {smapiRelease && <Tag color="blue">最新 {smapiRelease.version}</Tag>}
              <h3>{dashboard.smapi.installed ? `SMAPI ${dashboard.smapi.version ?? "版本待检测"}` : "安装 SMAPI"}</h3>
              <p>
                {smapiRelease
                  ? `${smapiRelease.asset.name} · ${smapiRelease.installerEntry}`
                  : smapiReleaseLoading
                    ? "正在读取官方稳定版本..."
                    : smapiReleaseError ?? "安装后即可加载和管理 Stardew Valley Mod。"}
              </p>
              <Space wrap>
                <Button
                  icon={<SafetyCertificateOutlined />}
                  loading={smapiDownloading}
                  disabled={!smapiRelease || smapiReleaseLoading}
                  onClick={() => void downloadSmapi()}
                >
                  下载官方安装包
                </Button>
                {smapiReleaseError && (
                  <Button
                    icon={<ReloadOutlined />}
                    loading={smapiReleaseLoading}
                    onClick={() => void loadSmapiRelease()}
                  >
                    重试版本信息
                  </Button>
                )}
              </Space>
            </div>
            <div className="setup-actions split">
              <Button onClick={() => setStep(0)}>返回</Button>
              <Button type="primary" onClick={() => setStep(2)}>继续</Button>
            </div>
          </div>
        )}

        {step === 2 && (
          <div className="setup-step setup-complete">
            <CheckCircleOutlined />
            <h3>设置完成</h3>
            <div className="setup-summary">
              <span>游戏目录</span>
              <strong>{dashboard.installation ? "已确认" : "未设置"}</strong>
              <span>SMAPI</span>
              <strong>{dashboard.smapi.installed ? dashboard.smapi.version ?? "已安装" : "稍后安装"}</strong>
            </div>
            <div className="setup-actions split">
              <Button onClick={() => setStep(1)}>返回</Button>
              <Button type="primary" onClick={finish}>进入管理器</Button>
            </div>
          </div>
        )}
      </div>
    </Modal>
  );
}
