import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Badge,
  Button,
  Dialog,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  ProgressBar,
  Spinner,
} from "@fluentui/react-components";
import {
  ArrowClockwise20Regular,
  CheckmarkCircle48Regular,
  Dismiss20Regular,
  FolderOpen20Regular,
  Open20Regular,
  ShieldCheckmark24Regular,
} from "@fluentui/react-icons";
import {
  chooseGameDirectory,
  getLatestSmapiRelease,
  installLatestSmapi,
  openSmapiDownload,
  scanGamePath,
} from "../../api";
import type { Dashboard, InstalledSmapiResult, SmapiReleaseInfo } from "../../types";
import { formatDisplayPath } from "../../utils/path";
import { useAppUi } from "../shared";

interface FirstRunSetupProps {
  open: boolean;
  dashboard: Dashboard;
  onDashboardUpdate: (dashboard: Dashboard) => void;
  onComplete: () => void;
}

export function FirstRunSetup({ open, dashboard, onDashboardUpdate, onComplete }: FirstRunSetupProps) {
  const { confirm, notify } = useAppUi();
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
    if (!candidate) {
      notify("warning", "请先填写游戏目录");
      return;
    }
    setVerifying(true);
    try {
      const nextDashboard = await scanGamePath(candidate);
      onDashboardUpdate(nextDashboard);
      const normalized = nextDashboard.installation
        ? formatDisplayPath(nextDashboard.installation.path)
        : candidate;
      setPathInput(normalized);
      setVerifiedPath(normalized);
      notify("success", "游戏路径已确认");
    } catch (error) {
      setVerifiedPath("");
      notify("error", "游戏路径验证失败", String(error));
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
      notify("warning", "请先确认有效的游戏目录");
      return;
    }

    confirm({
      title: `${dashboard.smapi.installed ? "更新" : "安装"} SMAPI ${smapiRelease?.version ?? "最新稳定版"}`,
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
          notify("success", `SMAPI ${confirmedVersion} 已安装`);
        } catch (error) {
          const detail = String(error);
          setSmapiInstallError(detail);
          notify("error", "SMAPI 安装未完成", detail);
        } finally {
          setSmapiInstalling(false);
        }
      },
    });
  };

  const smapiInstalled = dashboard.smapi.installed || installedSmapi !== undefined;
  const installedSmapiVersion = installedSmapi?.version ?? dashboard.smapi.version;
  const setupSteps = ["游戏目录", "SMAPI", "完成"];

  const openOfficialPage = async () => {
    try {
      await openSmapiDownload();
    } catch (error) {
      notify("error", "无法打开 SMAPI 官方页面", String(error));
    }
  };

  return (
    <Dialog
      open={open}
      modalType="alert"
    >
      <DialogSurface
        className="first-run-dialog"
        style={{ maxWidth: 720, width: "min(720px, calc(100vw - 32px))" }}
      >
        <DialogBody>
          <DialogTitle>
            <div className="setup-heading">
              <span>Valley Steward</span>
              <h2>首次设置</h2>
            </div>
          </DialogTitle>
          <DialogContent className="first-run-dialog-content">
            <div className="first-run-setup">
              <div className="setup-progress" aria-label={`首次设置步骤 ${step + 1} / ${setupSteps.length}`}>
                <ProgressBar value={(step + 1) / setupSteps.length} thickness="medium" />
                <div className="setup-progress-labels" role="list">
                  {setupSteps.map((label, index) => (
                    <div
                      className="setup-progress-item"
                      data-state={index < step ? "complete" : index === step ? "current" : "pending"}
                      key={label}
                      role="listitem"
                      aria-current={index === step ? "step" : undefined}
                    >
                      <Badge
                        appearance={index === step ? "filled" : "tint"}
                        color={index < step ? "success" : index === step ? "brand" : "subtle"}
                      >
                        {index + 1}
                      </Badge>
                      <span>{label}</span>
                    </div>
                  ))}
                </div>
              </div>

              {step === 0 && (
                <div className="setup-step">
                  {dashboard.installation ? (
                    <MessageBar intent="success">
                      <MessageBarBody>
                        <MessageBarTitle>已自动获取游戏路径</MessageBarTitle>
                        {dashboard.installation.store}
                      </MessageBarBody>
                    </MessageBar>
                  ) : (
                    <MessageBar intent="warning">
                      <MessageBarBody>
                        <MessageBarTitle>未自动找到游戏</MessageBarTitle>
                        请选择或填写 Stardew Valley 安装目录。
                      </MessageBarBody>
                    </MessageBar>
                  )}
                  <Field label={{ children: "Stardew Valley 游戏目录", htmlFor: "first-run-game-path" }}>
                    <div className="setup-path-controls">
                      <Input
                        id="first-run-game-path"
                        value={pathInput}
                        onChange={(event) => setPathInput(event.target.value)}
                      />
                      <Button
                        appearance="secondary"
                        icon={<FolderOpen20Regular />}
                        aria-label="选择游戏目录"
                        onClick={() => void choosePath()}
                      />
                      <Button
                        appearance="primary"
                        icon={verifying ? <Spinner size="tiny" /> : undefined}
                        disabled={verifying}
                        onClick={() => void verifyPath()}
                      >
                        {verifying ? "正在验证" : "验证路径"}
                      </Button>
                    </div>
                  </Field>
                  <div className="setup-actions">
                    <Button appearance="primary" disabled={!pathReady} onClick={() => setStep(1)}>
                      继续
                    </Button>
                  </div>
                </div>
              )}

              {step === 1 && (
                <div className="setup-step setup-smapi-status">
                  <ShieldCheckmark24Regular className="setup-status-icon" />
                  <div>
                    <div className="setup-badges">
                      <Badge appearance="tint" color={smapiInstalled ? "success" : "subtle"}>
                        {smapiInstalled ? "已安装" : "尚未安装"}
                      </Badge>
                      {smapiRelease && (
                        <Badge appearance="tint" color="informative">最新 {smapiRelease.version}</Badge>
                      )}
                    </div>
                    <h3>{smapiInstalled ? `SMAPI ${installedSmapiVersion ?? "版本待检测"}` : "安装 SMAPI"}</h3>
                    <p>
                      {smapiRelease
                        ? `${smapiRelease.asset.name} · ${smapiRelease.installerEntry}`
                        : smapiReleaseLoading
                          ? "正在读取官方稳定版本..."
                          : smapiReleaseError ?? "安装后即可加载和管理 Stardew Valley Mod。"}
                    </p>
                    <div className="setup-smapi-messages">
                      {installedSmapi && (
                        <MessageBar intent="success">
                          <MessageBarBody>
                            <MessageBarTitle>SMAPI {installedSmapi.version} 已安装</MessageBarTitle>
                          </MessageBarBody>
                        </MessageBar>
                      )}
                      {smapiInstallError && (
                        <MessageBar intent="error">
                          <MessageBarBody>
                            <MessageBarTitle>SMAPI 安装状态需要处理</MessageBarTitle>
                            {smapiInstallError}
                          </MessageBarBody>
                          <MessageBarActions
                            containerAction={(
                              <Button
                                appearance="transparent"
                                icon={<Dismiss20Regular />}
                                aria-label="关闭 SMAPI 安装错误"
                                onClick={() => setSmapiInstallError(undefined)}
                              />
                            )}
                          />
                        </MessageBar>
                      )}
                    </div>
                    <div className="setup-inline-actions">
                      <Button
                        appearance="secondary"
                        icon={smapiInstalling ? <Spinner size="tiny" /> : <ShieldCheckmark24Regular />}
                        disabled={smapiInstalling || !dashboard.installation || smapiReleaseLoading}
                        aria-label="安装或更新 SMAPI"
                        onClick={confirmInstallSmapi}
                      >
                        {smapiInstalling ? "正在安装" : "安装/更新 SMAPI"}
                      </Button>
                      {smapiReleaseError && (
                        <Button
                          appearance="secondary"
                          icon={smapiReleaseLoading ? <Spinner size="tiny" /> : <ArrowClockwise20Regular />}
                          disabled={smapiReleaseLoading}
                          onClick={() => void loadSmapiRelease()}
                        >
                          重试版本信息
                        </Button>
                      )}
                      {(smapiReleaseError || smapiInstallError) && (
                        <Button
                          appearance="subtle"
                          icon={<Open20Regular />}
                          onClick={() => void openOfficialPage()}
                        >
                          打开官方页面
                        </Button>
                      )}
                    </div>
                  </div>
                  <div className="setup-actions split">
                    <Button appearance="secondary" disabled={smapiInstalling} onClick={() => setStep(0)}>返回</Button>
                    <Button appearance="primary" disabled={smapiInstalling} onClick={() => setStep(2)}>继续</Button>
                  </div>
                </div>
              )}

              {step === 2 && (
                <div className="setup-step setup-complete">
                  <CheckmarkCircle48Regular className="setup-status-icon" />
                  <h3>设置完成</h3>
                  <div className="setup-summary">
                    <span>游戏目录</span>
                    <strong>{dashboard.installation ? "已确认" : "未设置"}</strong>
                    <span>SMAPI</span>
                    <strong>{smapiInstalled ? installedSmapiVersion ?? "已安装" : "稍后安装"}</strong>
                  </div>
                  <div className="setup-actions split">
                    <Button appearance="secondary" onClick={() => setStep(1)}>返回</Button>
                    <Button appearance="primary" onClick={finish}>进入管理器</Button>
                  </div>
                </div>
              )}
            </div>
          </DialogContent>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
