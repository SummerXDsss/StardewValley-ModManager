import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, App, Button, Input, Select, Space, Switch, Tag } from "antd";
import { ApiOutlined, ExportOutlined, FolderOpenOutlined, ReloadOutlined } from "@ant-design/icons";
import { PageTitle } from "../shared";
import {
  chooseGameDirectory,
  clearAiTranslationSettings,
  clearNexusApiKey,
  getAiTranslationSettings,
  getGithubDownloadSettings,
  getNexusAuthStatus,
  listAiTranslationModels,
  openRemoteUrl,
  saveAiTranslationSettings,
  saveGithubDownloadSettings,
  setNexusApiKey,
  testAiTranslationConnection,
} from "../../api";
import type {
  AiTranslationConnectionTestResult,
  AiTranslationModel,
  AiTranslationStatus,
  Dashboard,
  GithubDownloadMode,
  GithubDownloadSettings,
  LaunchArgumentSettings,
  LaunchTarget,
} from "../../types";
import { formatDisplayPath } from "../../utils/path";

const NEXUS_PERSONAL_API_KEYS_URL = "https://www.nexusmods.com/settings/api-keys";
const GITHUB_PROXY_PRESET = "https://gh-proxy.com/";

type GithubDownloadChoice = GithubDownloadMode | "gh-proxy";

const SMAPI_LAUNCH_ARGUMENT_OPTIONS = [
  { value: "--no-terminal", label: "--no-terminal · 关闭终端输出" },
  { value: "--developer-mode", label: "--developer-mode · 显示 TRACE 日志" },
  { value: "--developer-mode-off", label: "--developer-mode-off · 关闭开发者模式" },
  { value: "--use-current-shell", label: "--use-current-shell · 使用当前 Shell（macOS/Linux）" },
];

function githubDownloadChoiceFor(settings: GithubDownloadSettings): GithubDownloadChoice {
  if (settings.mode === "direct") return "direct";
  return settings.customPrefix === GITHUB_PROXY_PRESET ? "gh-proxy" : "custom";
}

function hasSameOrigin(left: string | undefined, right: string) {
  if (!left || !right.trim()) return false;
  try {
    return new URL(left).origin === new URL(right).origin;
  } catch {
    return false;
  }
}

function usesPlainHttp(value: string) {
  try {
    return new URL(value).protocol === "http:";
  } catch {
    return false;
  }
}

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
  const [nexusStatusLoading, setNexusStatusLoading] = useState(true);
  const [nexusMutation, setNexusMutation] = useState<"saving" | "clearing" | null>(null);
  const [nexusError, setNexusError] = useState<string>();
  const nexusMutationRef = useRef(false);
  const [translationStatus, setTranslationStatus] = useState<AiTranslationStatus>({
    configured: false,
    apiKeyConfigured: false,
  });
  const [translationBaseUrl, setTranslationBaseUrl] = useState("");
  const [translationModelId, setTranslationModelId] = useState("");
  const [translationApiKey, setTranslationApiKey] = useState("");
  const [translationStatusLoading, setTranslationStatusLoading] = useState(true);
  const [translationMutation, setTranslationMutation] = useState<"saving" | "clearing" | null>(null);
  const translationMutationRef = useRef(false);
  const [translationModels, setTranslationModels] = useState<AiTranslationModel[]>([]);
  const [translationModelsLoading, setTranslationModelsLoading] = useState(false);
  const [translationTestLoading, setTranslationTestLoading] = useState(false);
  const [translationTestResult, setTranslationTestResult] = useState<AiTranslationConnectionTestResult>();
  const [translationRequestError, setTranslationRequestError] = useState<{ title: string; detail: string }>();
  const translationNetworkRef = useRef(false);
  const [githubDownloadStatus, setGithubDownloadStatus] = useState<GithubDownloadSettings>({
    mode: "direct",
    customPrefix: null,
  });
  const [githubDownloadChoice, setGithubDownloadChoice] = useState<GithubDownloadChoice>("direct");
  const [githubCustomPrefix, setGithubCustomPrefix] = useState("");
  const [githubDownloadLoading, setGithubDownloadLoading] = useState(true);
  const [githubDownloadError, setGithubDownloadError] = useState<string>();
  const [githubDownloadMutation, setGithubDownloadMutation] = useState<"saving" | "clearing" | null>(null);
  const githubDownloadMutationRef = useRef(false);

  const loadNexusStatus = useCallback(async () => {
    setNexusStatusLoading(true);
    setNexusError(undefined);
    try {
      const status = await getNexusAuthStatus();
      setNexusConfigured(status.configured);
    } catch (error) {
      setNexusConfigured(false);
      setNexusError(String(error));
    } finally {
      setNexusStatusLoading(false);
    }
  }, []);

  const loadGithubSettings = useCallback(async () => {
    setGithubDownloadLoading(true);
    setGithubDownloadError(undefined);
    try {
      const status = await getGithubDownloadSettings();
      setGithubDownloadStatus(status);
      setGithubDownloadChoice(githubDownloadChoiceFor(status));
      setGithubCustomPrefix(status.customPrefix ?? "");
    } catch (error) {
      setGithubDownloadError(String(error));
    } finally {
      setGithubDownloadLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadNexusStatus();
    void getAiTranslationSettings()
      .then((status) => {
        setTranslationStatus(status);
        setTranslationBaseUrl(status.baseUrl ?? "");
        setTranslationModelId(status.modelId ?? "");
      })
      .catch((error) => message.error(String(error)))
      .finally(() => setTranslationStatusLoading(false));
    void loadGithubSettings();
  }, [loadGithubSettings, loadNexusStatus]);

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
    if (nexusMutationRef.current) return;
    const apiKey = nexusKey.trim();
    if (!apiKey) return;

    nexusMutationRef.current = true;
    setNexusMutation("saving");
    setNexusError(undefined);
    try {
      await setNexusApiKey(apiKey);
      const confirmedStatus = await getNexusAuthStatus();
      if (!confirmedStatus.configured) {
        throw new Error("Nexus API Key 已写入，但无法从系统凭据库重新读取，请重试");
      }
      setNexusConfigured(true);
      setNexusKey("");
      message.success("Nexus API Key 已验证并保存到系统凭据库");
    } catch (error) {
      const detail = String(error);
      setNexusError(detail);
      message.error(detail);
    } finally {
      nexusMutationRef.current = false;
      setNexusMutation(null);
    }
  };

  const removeNexusKey = async () => {
    if (nexusMutationRef.current) return;
    nexusMutationRef.current = true;
    setNexusMutation("clearing");
    setNexusError(undefined);
    try {
      const status = await clearNexusApiKey();
      setNexusConfigured(status.configured);
      setNexusKey("");
      message.success("Nexus API Key 已清除");
    } catch (error) {
      const detail = String(error);
      setNexusError(detail);
      message.error(detail);
    } finally {
      nexusMutationRef.current = false;
      setNexusMutation(null);
    }
  };

  const openNexusApiKeysPage = async () => {
    try {
      await openRemoteUrl(NEXUS_PERSONAL_API_KEYS_URL);
    } catch (error) {
      const detail = String(error);
      setNexusError(detail);
      message.error(detail);
    }
  };

  const loadTranslationModels = async () => {
    if (translationNetworkRef.current || translationMutationRef.current) return;
    translationNetworkRef.current = true;
    setTranslationModelsLoading(true);
    setTranslationRequestError(undefined);
    setTranslationTestResult(undefined);
    try {
      const result = await listAiTranslationModels({
        baseUrl: translationBaseUrl,
        apiKey: translationApiKey.trim() || undefined,
      });
      setTranslationModels(result.models);
      if (result.models.length === 0) {
        message.warning("接口未返回可用模型，仍可手动填写 Model ID");
      } else {
        message.success(`已获取 ${result.models.length} 个模型`);
      }
    } catch (error) {
      const detail = String(error);
      setTranslationRequestError({ title: "模型列表获取失败", detail });
      message.error(detail);
    } finally {
      translationNetworkRef.current = false;
      setTranslationModelsLoading(false);
    }
  };

  const sendTranslationTest = async () => {
    if (translationNetworkRef.current || translationMutationRef.current) return;
    translationNetworkRef.current = true;
    setTranslationTestLoading(true);
    setTranslationRequestError(undefined);
    setTranslationTestResult(undefined);
    try {
      const result = await testAiTranslationConnection({
        baseUrl: translationBaseUrl,
        modelId: translationModelId,
        apiKey: translationApiKey.trim() || undefined,
      });
      setTranslationModelId(result.modelId);
      setTranslationTestResult(result);
      message.success("AI 接口测试成功");
    } catch (error) {
      const detail = String(error);
      setTranslationRequestError({ title: "AI 接口测试失败", detail });
      message.error(detail);
    } finally {
      translationNetworkRef.current = false;
      setTranslationTestLoading(false);
    }
  };

  const saveTranslationSettings = async () => {
    if (translationMutationRef.current || translationNetworkRef.current) return;
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
      setTranslationRequestError(undefined);
      message.success("AI 翻译配置已保存");
    } catch (error) {
      message.error(String(error));
    } finally {
      translationMutationRef.current = false;
      setTranslationMutation(null);
    }
  };

  const removeTranslationSettings = async () => {
    if (translationMutationRef.current || translationNetworkRef.current) return;
    translationMutationRef.current = true;
    setTranslationMutation("clearing");
    try {
      const status = await clearAiTranslationSettings();
      setTranslationStatus(status);
      setTranslationBaseUrl("");
      setTranslationModelId("");
      setTranslationApiKey("");
      setTranslationModels([]);
      setTranslationRequestError(undefined);
      setTranslationTestResult(undefined);
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
        mode: githubDownloadChoice === "direct" ? "direct" : "custom",
        customPrefix: githubDownloadChoice === "gh-proxy"
          ? GITHUB_PROXY_PRESET
          : githubDownloadChoice === "custom"
            ? githubCustomPrefix.trim()
            : null,
      });
      setGithubDownloadStatus(status);
      setGithubDownloadChoice(githubDownloadChoiceFor(status));
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
      setGithubDownloadChoice("direct");
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

  const updateTranslationBaseUrl = (value: string) => {
    setTranslationBaseUrl(value);
    setTranslationModels([]);
    setTranslationRequestError(undefined);
    setTranslationTestResult(undefined);
  };

  const updateTranslationApiKey = (value: string) => {
    setTranslationApiKey(value);
    setTranslationModels([]);
    setTranslationRequestError(undefined);
    setTranslationTestResult(undefined);
  };

  const updateTranslationModelId = (value: string) => {
    setTranslationModelId(value);
    setTranslationRequestError(undefined);
    setTranslationTestResult(undefined);
  };

  const translationControlsBusy = translationStatusLoading
    || translationMutation !== null
    || translationModelsLoading
    || translationTestLoading;
  const translationCredentialAvailable = Boolean(translationApiKey.trim())
    || (translationStatus.apiKeyConfigured
      && hasSameOrigin(translationStatus.baseUrl, translationBaseUrl));
  const translationProviderReady = Boolean(translationBaseUrl.trim())
    && translationCredentialAvailable;
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
              options={SMAPI_LAUNCH_ARGUMENT_OPTIONS}
              optionFilterProp="label"
              showSearch
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
              {githubDownloadStatus.mode === "direct"
                ? "GitHub 直连"
                : githubDownloadStatus.customPrefix === GITHUB_PROXY_PRESET
                  ? "gh-proxy.com"
                  : "自定义镜像"}
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
              value={githubDownloadChoice}
              loading={githubDownloadLoading}
              disabled={githubDownloadLoading || githubDownloadMutation !== null}
              options={[
                { value: "direct", label: "GitHub 直连" },
                { value: "gh-proxy", label: "gh-proxy.com 镜像" },
                { value: "custom", label: "自定义 HTTPS 镜像" },
              ]}
              onChange={(value: GithubDownloadChoice) => {
                setGithubDownloadChoice(value);
                if (value === "custom" && githubCustomPrefix === GITHUB_PROXY_PRESET) {
                  setGithubCustomPrefix("");
                }
              }}
            />
          </div>
          {githubDownloadChoice === "custom" && (
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
              disabled={githubDownloadLoading || githubDownloadMutation !== null || (githubDownloadChoice === "custom" && !githubCustomPrefix.trim())}
              onClick={() => void saveGithubSettings()}
            >
              保存设置
            </Button>
            {(githubDownloadStatus.mode === "custom" || githubDownloadChoice !== "direct") && (
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
              <span>仅接受 Nexus Mods 的 Personal API Key，不支持应用凭据、SSO Token 或 JWT；热门列表无需 Key。</span>
            </div>
            <Tag color={nexusConfigured ? "green" : "default"}>
              {nexusStatusLoading ? "读取中" : nexusConfigured ? "已配置" : "未配置"}
            </Tag>
          </div>
          <Space.Compact block>
            <Input.Password
              value={nexusKey}
              disabled={nexusStatusLoading || nexusMutation !== null}
              onChange={(event) => setNexusKey(event.target.value)}
              placeholder={nexusConfigured ? "输入新 Key 可替换现有凭据" : "粘贴 Nexus Mods 个人 API Key"}
              autoComplete="new-password"
            />
            <Button
              type="primary"
              loading={nexusMutation === "saving"}
              disabled={nexusStatusLoading || nexusMutation !== null || !nexusKey.trim()}
              onClick={() => void saveNexusKey()}
            >
              验证并保存
            </Button>
            {nexusConfigured && (
              <Button
                danger
                loading={nexusMutation === "clearing"}
                disabled={nexusStatusLoading || nexusMutation !== null}
                onClick={() => void removeNexusKey()}
              >
                清除
              </Button>
            )}
          </Space.Compact>
          {nexusError && (
            <Alert
              type="error"
              showIcon
              closable
              message="Nexus API Key 配置失败"
              description={nexusError}
              action={(
                <Button
                  size="small"
                  icon={<ReloadOutlined />}
                  loading={nexusStatusLoading}
                  disabled={nexusMutation !== null}
                  onClick={() => void loadNexusStatus()}
                >
                  重试读取
                </Button>
              )}
              onClose={() => setNexusError(undefined)}
            />
          )}
          <Space wrap>
            <Button
              type="link"
              icon={<ExportOutlined />}
              disabled={nexusMutation !== null}
              onClick={() => void openNexusApiKeysPage()}
            >
              获取 Personal API Key
            </Button>
            <span>在 Nexus Mods 设置页的 Personal API Key 区域复制完整 Key。</span>
          </Space>
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
              disabled={translationControlsBusy}
              onChange={(event) => updateTranslationBaseUrl(event.target.value)}
              placeholder="例如 https://api.openai.com/v1 或 http://127.0.0.1:11434/v1"
              autoComplete="url"
            />
          </div>
          {usesPlainHttp(translationBaseUrl) && (
            <Alert
              type="warning"
              showIcon
              message="HTTP 连接不会加密 API Key"
              description="API Key、Mod 文本和模型回复会以明文在网络中传输。仅对完全信任的服务和网络使用 HTTP，公网服务建议改用 HTTPS。"
            />
          )}
          <div className="ai-translation-field">
            <label htmlFor="translation-api-key">API Key</label>
            <Input.Password
              id="translation-api-key"
              value={translationApiKey}
              disabled={translationControlsBusy}
              onChange={(event) => updateTranslationApiKey(event.target.value)}
              placeholder={translationStatus.apiKeyConfigured && hasSameOrigin(translationStatus.baseUrl, translationBaseUrl)
                ? "已安全保存；留空即可使用当前 API Key"
                : "请输入 API Key"}
              autoComplete="new-password"
            />
          </div>
          <div className="ai-translation-field">
            <label htmlFor="translation-model-id">Model ID</label>
            <div className="ai-translation-model-control">
              <Input
                id="translation-model-id"
                value={translationModelId}
                list={translationModels.length > 0 ? "translation-model-options" : undefined}
                disabled={translationControlsBusy}
                allowClear
                placeholder="获取模型后选择，或手动输入 Model ID"
                autoComplete="off"
                spellCheck={false}
                onChange={(event) => updateTranslationModelId(event.target.value)}
              />
              <datalist id="translation-model-options">
                {translationModels.map((model) => (
                  <option key={model.id} value={model.id} label={model.ownedBy ?? model.id} />
                ))}
              </datalist>
              <Button
                icon={<ReloadOutlined />}
                loading={translationModelsLoading}
                disabled={translationControlsBusy || !translationProviderReady}
                onClick={() => void loadTranslationModels()}
              >
                获取模型
              </Button>
            </div>
            {translationModels.length > 0 && (
              <small className="ai-translation-hint">
                已加载 {translationModels.length} 个模型，可搜索或继续手动输入。
              </small>
            )}
          </div>
          {translationRequestError && (
            <Alert
              type="error"
              showIcon
              closable
              message={translationRequestError.title}
              description={translationRequestError.detail}
              onClose={() => setTranslationRequestError(undefined)}
            />
          )}
          {translationTestResult && (
            <Alert
              type="success"
              showIcon
              message={`连接成功 · ${translationTestResult.modelId}`}
              description={`服务响应：${translationTestResult.message}`}
            />
          )}
          <Space wrap className="ai-translation-actions">
            <Button
              icon={<ApiOutlined />}
              loading={translationTestLoading}
              disabled={translationControlsBusy || !translationProviderReady || !translationModelId.trim()}
              onClick={() => void sendTranslationTest()}
            >
              发送测试
            </Button>
            <Button
              type="primary"
              loading={translationMutation === "saving"}
              disabled={translationControlsBusy || !translationProviderReady || !translationModelId.trim()}
              onClick={() => void saveTranslationSettings()}
            >
              保存配置
            </Button>
            {translationStatus.configured && (
              <Button
                danger
                loading={translationMutation === "clearing"}
                disabled={translationControlsBusy}
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
