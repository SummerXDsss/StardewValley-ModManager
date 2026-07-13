import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert, App, Button, Input, Modal, Space, Steps, Tag } from "antd";
import {
  CheckCircleOutlined,
  ExportOutlined,
  FolderOpenOutlined,
  ReloadOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import {
  chooseGameDirectory,
  getLatestSmapiRelease,
  installLatestSmapi,
  openSmapiDownload,
  scanGamePath,
} from "../../api";
import type { Dashboard, InstalledSmapiResult, SmapiReleaseInfo } from "../../types";
import { formatDisplayPath } from "../../utils/path";

interface FirstRunSetupProps {
  open: boolean;
  dashboard: Dashboard;
  onDashboardUpdate: (dashboard: Dashboard) => void;
  onComplete: () => void;
}

export function FirstRunSetup({ open, dashboard, onDashboardUpdate, onComplete }: FirstRunSetupProps) {
  const { message, modal } = App.useApp();
  const detectedPath = dashboard.installation ? formatDisplayPath(dashboard.installation.path) : "";
  const [step, setStep] = useState(0);
  const [pathInput, setPathInput] = useState(detectedPath);
  const [verifiedPath, setVerifiedPath] = useState(detectedPath);
  const [verifying, setVerifying] = useState(false);
  const [smapiRelease, setSmapiRelease] = useState<SmapiReleaseInfo>();
  const [smapiReleaseError, setSmapiReleaseError] = useState<string>();
  const [smapiReleaseLoading, setSmapiReleaseLoading] = useState(false);
  const [smapiInstalling, setSmapiInstalling] = useState(false);
  const [smapiInstallError, setSmapiInstallError] = useState<string>();
  const [installedSmapi, setInstalledSmapi] = useState<InstalledSmapiResult>();

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
    setInstalledSmapi(undefined);
    setSmapiInstallError(undefined);
  }, [dashboard.installation?.path]);

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

  const confirmInstallSmapi = () => {
    const gamePath = dashboard.installation?.path;
    if (!gamePath) {
      message.warning("请先确认有效的游戏目录");
      return;
    }

    modal.confirm({
      title: `${dashboard.smapi.installed ? "更新" : "安装"} SMAPI ${smapiRelease?.version ?? "最新稳定版"}`,
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
      onOk: async () => {
        setSmapiInstalling(true);
        setSmapiInstallError(undefined);
        try {
          const result = await installLatestSmapi(gamePath);
          const refreshed = await scanGamePath(gamePath);
          if (!refreshed.smapi.installed) {
            throw new Error("安装程序已结束，但重新扫描后仍未检测到 SMAPI");
          }
          const confirmedVersion = refreshed.smapi.version ?? result.version;
          onDashboardUpdate(refreshed);
          setInstalledSmapi({ ...result, version: confirmedVersion });
          message.success(`SMAPI ${confirmedVersion} 已安装`);
        } catch (error) {
          const detail = String(error);
          setSmapiInstallError(detail);
          message.error(detail);
        } finally {
          setSmapiInstalling(false);
        }
      },
    });
  };

  const smapiInstalled = dashboard.smapi.installed || installedSmapi !== undefined;
  const installedSmapiVersion = installedSmapi?.version ?? dashboard.smapi.version;

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
              <Tag color={smapiInstalled ? "green" : "default"}>
                {smapiInstalled ? "已安装" : "尚未安装"}
              </Tag>
              {smapiRelease && <Tag color="blue">最新 {smapiRelease.version}</Tag>}
              <h3>{smapiInstalled ? `SMAPI ${installedSmapiVersion ?? "版本待检测"}` : "安装 SMAPI"}</h3>
              <p>
                {smapiRelease
                  ? `${smapiRelease.asset.name} · ${smapiRelease.installerEntry}`
                  : smapiReleaseLoading
                    ? "正在读取官方稳定版本..."
                    : smapiReleaseError ?? "安装后即可加载和管理 Stardew Valley Mod。"}
              </p>
              {installedSmapi && (
                <Alert type="success" showIcon message={`SMAPI ${installedSmapi.version} 已安装`} />
              )}
              {smapiInstallError && (
                <Alert
                  type="error"
                  showIcon
                  closable
                  message="SMAPI 安装状态需要处理"
                  description={smapiInstallError}
                  onClose={() => setSmapiInstallError(undefined)}
                />
              )}
              <Space wrap>
                <Button
                  icon={<SafetyCertificateOutlined />}
                  loading={smapiInstalling}
                  disabled={smapiInstalling || !dashboard.installation || smapiReleaseLoading}
                  aria-label="安装或更新 SMAPI"
                  onClick={confirmInstallSmapi}
                >
                  安装/更新 SMAPI
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
                {(smapiReleaseError || smapiInstallError) && (
                  <Button
                    type="link"
                    icon={<ExportOutlined />}
                    onClick={() => void openSmapiDownload()}
                  >
                    打开官方页面
                  </Button>
                )}
              </Space>
            </div>
            <div className="setup-actions split">
              <Button disabled={smapiInstalling} onClick={() => setStep(0)}>返回</Button>
              <Button type="primary" disabled={smapiInstalling} onClick={() => setStep(2)}>继续</Button>
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
              <strong>{smapiInstalled ? installedSmapiVersion ?? "已安装" : "稍后安装"}</strong>
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
