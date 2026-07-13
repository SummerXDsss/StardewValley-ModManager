import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, App, Button, Input, Select, Space, Switch, Tag } from "antd";
import { FolderOpenOutlined, ReloadOutlined } from "@ant-design/icons";
import { PageTitle } from "../shared";
import {
  chooseGameDirectory,
  clearAiTranslationSettings,
  clearNexusApiKey,
  getAiTranslationSettings,
  getGithubDownloadSettings,
  getNexusAuthStatus,
  saveAiTranslationSettings,
  saveGithubDownloadSettings,
  setNexusApiKey,
} from "../../api";
import type {
  AiTranslationStatus,
  Dashboard,
  GithubDownloadMode,
  GithubDownloadSettings,
  LaunchArgumentSettings,
  LaunchTarget,
} from "../../types";
import { formatDisplayPath } from "../../utils/path";

interface SettingsPageProps {
  dashboard: Dashboard;
  onScanPath: (path: string) => Promise<Dashboard>;
  onDashboardUpdate: (dashboard: Dashboard) => void;
  onLoadingChange: (loading: boolean) => void;
  launchArguments: LaunchArgumentSettings;
  onLaunchArgumentsChange: (target: LaunchTarget, argumentsList: string[]) => void;
}

export function SettingsPage({
  dashboard,
  onScanPath,
  onDashboardUpdate,
  onLoadingChange,
  launchArguments,
  onLaunchArgumentsChange,
}: SettingsPageProps) {
  const { message } = App.useApp();
  const [pathInput, setPathInput] = useState(
    dashboard.installation ? formatDisplayPath(dashboard.installation.path) : "",
  );
  const [nexusKey, setNexusKey] = useState("");
  const [nexusConfigured, setNexusConfigured] = useState(false);
  const [savingNexus, setSavingNexus] = useState(false);
  const [translationStatus, setTranslationStatus] = useState<AiTranslationStatus>({
    configured: false,
    apiKeyConfigured: false,
  });
  const [translationBaseUrl, setTranslationBaseUrl] = useState("");
  const [translationModelId, setTranslationModelId] = useState("");
  const [translationApiKey, setTranslationApiKey] = useState("");
  const [translationMutation, setTranslationMutation] = useState<"saving" | "clearing" | null>(null);
  const translationMutationRef = useRef(false);
  const [githubDownloadStatus, setGithubDownloadStatus] = useState<GithubDownloadSettings>({
    mode: "direct",
    customPrefix: null,
  });
  const [githubDownloadMode, setGithubDownloadMode] = useState<GithubDownloadMode>("direct");
  const [githubCustomPrefix, setGithubCustomPrefix] = useState("");
  const [githubDownloadLoading, setGithubDownloadLoading] = useState(true);
  const [githubDownloadError, setGithubDownloadError] = useState<string>();
  const [githubDownloadMutation, setGithubDownloadMutation] = useState<"saving" | "clearing" | null>(null);
  const githubDownloadMutationRef = useRef(false);

  const loadGithubSettings = useCallback(async () => {
    setGithubDownloadLoading(true);
    setGithubDownloadError(undefined);
    try {
      const status = await getGithubDownloadSettings();
      setGithubDownloadStatus(status);
      setGithubDownloadMode(status.mode);
      setGithubCustomPrefix(status.customPrefix ?? "");
    } catch (error) {
      setGithubDownloadError(String(error));
    } finally {
      setGithubDownloadLoading(false);
    }
  }, []);

  useEffect(() => {
    void getNexusAuthStatus()
      .then((status) => setNexusConfigured(status.configured))
      .catch(() => setNexusConfigured(false));
    void getAiTranslationSettings()
      .then((status) => {
        setTranslationStatus(status);
        setTranslationBaseUrl(status.baseUrl ?? "");
        setTranslationModelId(status.modelId ?? "");
      })
      .catch((error) => message.error(String(error)));
    void loadGithubSettings();
  }, [loadGithubSettings]);

  useEffect(() => {
    setPathInput(dashboard.installation ? formatDisplayPath(dashboard.installation.path) : "");
  }, [dashboard.installation?.path]);

  const handleVerifyPath = async () => {
    onLoadingChange(true);
    try {
      const newDashboard = await onScanPath(pathInput);
      onDashboardUpdate(newDashboard);
      message.success("路径有效");
    } catch (e) {
      message.error(String(e));
    } finally {
      onLoadingChange(false);
    }
  };

  const choosePath = async () => {
    const selected = await chooseGameDirectory();
    if (selected) setPathInput(selected);
  };

  const saveNexusKey = async () => {
    setSavingNexus(true);
    try {
      const status = await setNexusApiKey(nexusKey);
      setNexusConfigured(status.configured);
      setNexusKey("");
      message.success("Nexus API Key 已验证并保存到系统凭据库");
    } catch (error) {
      message.error(String(error));
    } finally {
      setSavingNexus(false);
    }
  };

  const removeNexusKey = async () => {
    const status = await clearNexusApiKey();
    setNexusConfigured(status.configured);
    setNexusKey("");
    message.success("Nexus API Key 已清除");
  };

  const saveTranslationSettings = async () => {
    if (translationMutationRef.current) return;
    translationMutationRef.current = true;
    setTranslationMutation("saving");
    try {
      const status = await saveAiTranslationSettings({
        baseUrl: translationBaseUrl,
        modelId: translationModelId,
        apiKey: translationApiKey.trim() || undefined,
      });
      setTranslationStatus(status);
      setTranslationBaseUrl(status.baseUrl ?? translationBaseUrl);
      setTranslationModelId(status.modelId ?? translationModelId);
      setTranslationApiKey("");
      message.success("AI 翻译配置已保存");
    } catch (error) {
      message.error(String(error));
    } finally {
      translationMutationRef.current = false;
      setTranslationMutation(null);
    }
  };

  const removeTranslationSettings = async () => {
    if (translationMutationRef.current) return;
    translationMutationRef.current = true;
    setTranslationMutation("clearing");
    try {
      const status = await clearAiTranslationSettings();
      setTranslationStatus(status);
      setTranslationBaseUrl("");
      setTranslationModelId("");
      setTranslationApiKey("");
      message.success("AI 翻译配置已清除");
    } catch (error) {
      message.error(String(error));
    } finally {
      translationMutationRef.current = false;
      setTranslationMutation(null);
    }
  };

  const saveGithubSettings = async () => {
    if (githubDownloadMutationRef.current) return;
    githubDownloadMutationRef.current = true;
    setGithubDownloadMutation("saving");
    setGithubDownloadError(undefined);
    try {
      const status = await saveGithubDownloadSettings({
        mode: githubDownloadMode,
        customPrefix: githubDownloadMode === "custom" ? githubCustomPrefix.trim() : null,
      });
      setGithubDownloadStatus(status);
      setGithubDownloadMode(status.mode);
      setGithubCustomPrefix(status.customPrefix ?? "");
      message.success("GitHub 下载设置已保存");
    } catch (error) {
      const detail = String(error);
      setGithubDownloadError(detail);
      message.error(detail);
    } finally {
      githubDownloadMutationRef.current = false;
      setGithubDownloadMutation(null);
    }
  };

  const clearGithubSettings = async () => {
    if (githubDownloadMutationRef.current) return;
    githubDownloadMutationRef.current = true;
    setGithubDownloadMutation("clearing");
    setGithubDownloadError(undefined);
    try {
      const status = await saveGithubDownloadSettings({ mode: "direct", customPrefix: null });
      setGithubDownloadStatus(status);
      setGithubDownloadMode("direct");
      setGithubCustomPrefix("");
      message.success("已清除镜像设置并恢复 GitHub 直连");
    } catch (error) {
      const detail = String(error);
      setGithubDownloadError(detail);
      message.error(detail);
    } finally {
      githubDownloadMutationRef.current = false;
      setGithubDownloadMutation(null);
    }
  };

  const updateArguments = (target: LaunchTarget, argumentsList: string[]) => {
    if (argumentsList.some((argument) => argument.length > 512)) {
      message.warning("单个启动参数不能超过 512 个字符");
      return;
    }
    onLaunchArgumentsChange(target, argumentsList);
  };

  return (
    <section>
      <PageTitle title="设置" subtitle="游戏位置、启动方式与安全策略" />
      <div className="settings-form">
        <label>游戏目录</label>
        <Space.Compact block>
          <Input value={pathInput} onChange={(e) => setPathInput(e.target.value)} />
          <Button icon={<FolderOpenOutlined />} aria-label="选择游戏目录" onClick={() => void choosePath()} />
          <Button onClick={handleVerifyPath}>验证并保存</Button>
        </Space.Compact>
        <label>启动参数</label>
        <div className="launch-argument-settings">
          <div className="launch-argument-row">
            <Tag color="green">SMAPI</Tag>
            <Select
              mode="tags"
              aria-label="SMAPI 启动参数"
              value={launchArguments.smapi}
              maxCount={32}
              maxTagCount="responsive"
              options={[]}
              open={false}
              allowClear
              placeholder="添加 SMAPI 参数"
              onChange={(values) => updateArguments("smapi", values)}
            />
          </div>
          <div className="launch-argument-row">
            <Tag>原版</Tag>
            <Select
              mode="tags"
              aria-label="原版启动参数"
              value={launchArguments.vanilla}
              maxCount={32}
              maxTagCount="responsive"
              options={[]}
              open={false}
              allowClear
              placeholder="添加原版游戏参数"
              onChange={(values) => updateArguments("vanilla", values)}
            />
          </div>
        </div>
        <label>删除策略</label>
        <div className="setting-row">
          <span>Mod 先移动到管理器回收区</span>
          <Switch defaultChecked />
        </div>
        <label>更新策略</label>
        <div className="setting-row">
          <span>保留 config.json 与回滚快照</span>
          <Switch defaultChecked disabled />
        </div>
        <h2 className="settings-section-title">GitHub 下载加速</h2>
        <div className="github-download-setting">
          <div className="nexus-setting-status">
            <div>
              <strong>SMAPI 安装包下载方式</strong>
              <span>仅影响 SMAPI 安装、更新和卸载所需的 GitHub Release 资产；Mod 发布页下载不受影响。</span>
            </div>
            <Tag color={githubDownloadStatus.mode === "custom" ? "gold" : "default"}>
              {githubDownloadStatus.mode === "custom" ? "自定义镜像" : "GitHub 直连"}
            </Tag>
          </div>
          <Alert
            type="warning"
            showIcon
            message="第三方镜像服务风险"
            description="自定义镜像会接收 SMAPI 的 GitHub 资产下载地址。请仅使用你信任的 HTTPS 服务；管理器只验证地址格式，不声明服务可用性或安全性。"
          />
          <div className="github-download-field">
            <label htmlFor="github-download-mode">下载模式</label>
            <Select
              id="github-download-mode"
              value={githubDownloadMode}
              loading={githubDownloadLoading}
              disabled={githubDownloadLoading || githubDownloadMutation !== null}
              options={[
                { value: "direct", label: "GitHub 直连" },
                { value: "custom", label: "自定义 HTTPS 镜像" },
              ]}
              onChange={(value: GithubDownloadMode) => setGithubDownloadMode(value)}
            />
          </div>
          {githubDownloadMode === "custom" && (
            <div className="github-download-field">
              <label htmlFor="github-custom-prefix">镜像 Base URL</label>
              <Input
                id="github-custom-prefix"
                value={githubCustomPrefix}
                disabled={githubDownloadMutation !== null}
                placeholder="https://mirror.example.com/"
                autoComplete="url"
                onChange={(event) => setGithubCustomPrefix(event.target.value)}
              />
            </div>
          )}
          {githubDownloadError && (
            <Alert
              type="error"
              showIcon
              closable
              message="GitHub 下载设置不可用"
              description={githubDownloadError}
              action={(
                <Button
                  size="small"
                  icon={<ReloadOutlined />}
                  loading={githubDownloadLoading}
                  onClick={() => void loadGithubSettings()}
                >
                  重试
                </Button>
              )}
              onClose={() => setGithubDownloadError(undefined)}
            />
          )}
          <Space wrap>
            <Button
              type="primary"
              loading={githubDownloadMutation === "saving"}
              disabled={githubDownloadLoading || githubDownloadMutation !== null || (githubDownloadMode === "custom" && !githubCustomPrefix.trim())}
              onClick={() => void saveGithubSettings()}
            >
              保存设置
            </Button>
            {(githubDownloadStatus.mode === "custom" || githubDownloadMode === "custom" || githubCustomPrefix) && (
              <Button
                loading={githubDownloadMutation === "clearing"}
                disabled={githubDownloadLoading || githubDownloadMutation !== null}
                onClick={() => void clearGithubSettings()}
              >
                清除并恢复直连
              </Button>
            )}
          </Space>
          <small>保存时后端只验证 HTTPS 地址格式；实际下载还会核验 GitHub 官方 SHA-256，不执行网络连通性测试。</small>
        </div>
        <label>Nexus Mods API</label>
        <div className="nexus-setting">
          <div className="nexus-setting-status">
            <div>
              <strong>个人 API Key</strong>
              <span>用于读取文件版本和获取官方下载地址；热门列表无需 Key。</span>
            </div>
            <Tag color={nexusConfigured ? "green" : "default"}>{nexusConfigured ? "已配置" : "未配置"}</Tag>
          </div>
          <Space.Compact block>
            <Input.Password
              value={nexusKey}
              onChange={(event) => setNexusKey(event.target.value)}
              placeholder={nexusConfigured ? "输入新 Key 可替换现有凭据" : "粘贴 Nexus Mods 个人 API Key"}
              autoComplete="off"
            />
            <Button type="primary" loading={savingNexus} disabled={!nexusKey.trim()} onClick={() => void saveNexusKey()}>
              验证并保存
            </Button>
            {nexusConfigured && <Button danger onClick={() => void removeNexusKey()}>清除</Button>}
          </Space.Compact>
        </div>
        <h2 className="settings-section-title">AI 翻译</h2>
        <div className="ai-translation-setting">
          <div className="nexus-setting-status">
            <div>
              <strong>OpenAI-compatible 接口</strong>
              <span>API Key 保存到系统凭据库；名称与简介按星露谷物语语境翻译。</span>
            </div>
            <Tag color={translationStatus.configured ? "green" : "default"}>
              {translationStatus.configured ? "已配置" : "未配置"}
            </Tag>
          </div>
          <div className="ai-translation-field">
            <label htmlFor="translation-base-url">Base URL</label>
            <Input
              id="translation-base-url"
              value={translationBaseUrl}
              onChange={(event) => setTranslationBaseUrl(event.target.value)}
              placeholder="例如 https://api.openai.com/v1"
              autoComplete="url"
            />
          </div>
          <div className="ai-translation-field">
            <label htmlFor="translation-model-id">Model ID</label>
            <Input
              id="translation-model-id"
              value={translationModelId}
              onChange={(event) => setTranslationModelId(event.target.value)}
              placeholder="例如 gpt-4.1-mini"
              autoComplete="off"
            />
          </div>
          <div className="ai-translation-field">
            <label htmlFor="translation-api-key">API Key</label>
            <Input.Password
              id="translation-api-key"
              value={translationApiKey}
              onChange={(event) => setTranslationApiKey(event.target.value)}
              placeholder={translationStatus.apiKeyConfigured ? "留空以保留当前 API Key" : "请输入 API Key"}
              autoComplete="new-password"
            />
          </div>
          <Space>
            <Button
              type="primary"
              loading={translationMutation === "saving"}
              disabled={translationMutation !== null || !translationBaseUrl.trim() || !translationModelId.trim() || (!translationStatus.apiKeyConfigured && !translationApiKey.trim())}
              onClick={() => void saveTranslationSettings()}
            >
              保存配置
            </Button>
            {translationStatus.configured && (
              <Button
                danger
                loading={translationMutation === "clearing"}
                disabled={translationMutation !== null}
                onClick={() => void removeTranslationSettings()}
              >
                清除
              </Button>
            )}
          </Space>
        </div>
      </div>
    </section>
  );
}
