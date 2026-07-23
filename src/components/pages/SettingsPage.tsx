import { useCallback, useEffect, useRef, useState } from "react";
import {
  Badge,
  Button,
  Combobox,
  Dropdown,
  Field,
  Input,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Option,
  Spinner,
  Switch,
  Tag,
  TagGroup,
} from "@fluentui/react-components";
import {
  AddRegular,
  ArrowSyncRegular,
  DeleteRegular,
  DismissRegular,
  FolderOpenRegular,
  KeyRegular,
  OpenRegular,
  SaveRegular,
  SendRegular,
} from "@fluentui/react-icons";
import { PageTitle, useAppUi } from "../shared";
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
  AiTranslationModel,
  AiTranslationRequestMetadata,
  AiTranslationResponseMetadata,
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
const TRANSLATION_CONNECTION_TEST_PROMPT = "请只回复：连接成功";

type GithubDownloadChoice = GithubDownloadMode | "gh-proxy";
type TranslationRequestPhase = "loading" | "success" | "error";

interface TranslationRequestActivity {
  phase: TranslationRequestPhase;
  request: AiTranslationRequestMetadata;
  response?: AiTranslationResponseMetadata;
  error?: string;
}

const SMAPI_LAUNCH_ARGUMENT_OPTIONS = [
  { value: "--no-terminal", label: "--no-terminal · 关闭终端输出" },
  { value: "--developer-mode", label: "--developer-mode · 显示 TRACE 日志" },
  { value: "--developer-mode-off", label: "--developer-mode-off · 关闭开发者模式" },
  { value: "--use-current-shell", label: "--use-current-shell · 使用当前 Shell（macOS/Linux）" },
];

interface LaunchArgumentEditorProps {
  target: LaunchTarget;
  label: string;
  argumentsList: string[];
  presets?: typeof SMAPI_LAUNCH_ARGUMENT_OPTIONS;
  onChange: (target: LaunchTarget, argumentsList: string[]) => void;
  onInvalid: (message: string) => void;
}

function LaunchArgumentEditor({
  target,
  label,
  argumentsList,
  presets = [],
  onChange,
  onInvalid,
}: LaunchArgumentEditorProps) {
  const [draft, setDraft] = useState("");

  const addArgument = (value: string) => {
    if (!value.trim()) return;
    if (value.length > 512) {
      onInvalid("单个启动参数不能超过 512 个字符");
      return;
    }
    if (argumentsList.length >= 32) {
      onInvalid("每种启动方式最多保存 32 个参数");
      return;
    }
    onChange(target, [...argumentsList, value]);
    setDraft("");
  };

  return (
    <div className="launch-argument-editor">
      <div className="launch-argument-editor-header">
        <Badge appearance="tint" color={target === "smapi" ? "success" : "subtle"}>{label}</Badge>
        <div>
          <span>{argumentsList.length} / 32</span>
          {argumentsList.length > 0 && (
            <Button
              appearance="transparent"
              size="small"
              icon={<DismissRegular />}
              onClick={() => onChange(target, [])}
            >
              清空
            </Button>
          )}
        </div>
      </div>
      <div className="launch-argument-inputs">
        {presets.length > 0 && (
          <Dropdown
            aria-label={`${label} 参数预设`}
            value="选择预设参数"
            selectedOptions={[]}
            disabled={argumentsList.length >= 32}
            onOptionSelect={(_, data) => {
              if (data.optionValue) addArgument(data.optionValue);
            }}
          >
            {presets.map((preset) => (
              <Option
                key={preset.value}
                value={preset.value}
                text={preset.label}
              >
                {preset.label}
              </Option>
            ))}
          </Dropdown>
        )}
        <Input
          value={draft}
          aria-label={`手动添加${label}启动参数`}
          placeholder={target === "smapi" ? "手动输入 SMAPI 参数" : "手动输入原版游戏参数"}
          onChange={(_, data) => setDraft(data.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter") {
              event.preventDefault();
              addArgument(draft);
            }
          }}
        />
        <Button icon={<AddRegular />} disabled={!draft.trim()} onClick={() => addArgument(draft)}>
          添加
        </Button>
      </div>
      {argumentsList.length > 0 ? (
        <TagGroup
          className="launch-argument-tags"
          dismissible
          aria-label={`${label} 启动参数`}
          onDismiss={(_, data) => {
            const index = Number(data.value);
            onChange(target, argumentsList.filter((_, argumentIndex) => argumentIndex !== index));
          }}
        >
          {argumentsList.map((argument, index) => (
            <Tag key={`${argument}:${index}`} value={String(index)}>{argument}</Tag>
          ))}
        </TagGroup>
      ) : (
        <span className="launch-argument-empty">未添加参数</span>
      )}
    </div>
  );
}

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

function redactTranslationSecret(value: string, apiKey: string) {
  return apiKey ? value.split(apiKey).join("[已隐藏]") : value;
}

function formatRequestError(error: unknown) {
  const detail = error instanceof Error ? error.message : String(error);
  return detail.replace(/^(?:Error:\s*)+/i, "").trim() || "请求失败";
}

function translationRequestPreview(
  baseUrl: string,
  relativePath: "models" | "chat/completions",
  method: "GET" | "POST",
  body: string | undefined,
  apiKey: string,
): AiTranslationRequestMetadata {
  let endpoint: string;
  try {
    const url = new URL(baseUrl.trim());
    const path = url.pathname.replace(/\/+$/, "");
    url.pathname = `${path}/${relativePath}`;
    url.search = "";
    url.hash = "";
    endpoint = url.toString();
  } catch {
    endpoint = `${baseUrl.trim().replace(/\/+$/, "")}/${relativePath}`;
  }

  return {
    method,
    endpoint: redactTranslationSecret(endpoint, apiKey),
    body: body ? redactTranslationSecret(body, apiKey) : undefined,
  };
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
  const { notify } = useAppUi();
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
  const [translationModelPickerValue, setTranslationModelPickerValue] = useState("");
  const [translationApiKey, setTranslationApiKey] = useState("");
  const [translationStatusLoading, setTranslationStatusLoading] = useState(true);
  const [translationMutation, setTranslationMutation] = useState<"saving" | "clearing" | null>(null);
  const translationMutationRef = useRef(false);
  const [translationModels, setTranslationModels] = useState<AiTranslationModel[]>([]);
  const [translationModelsLoading, setTranslationModelsLoading] = useState(false);
  const [translationTestLoading, setTranslationTestLoading] = useState(false);
  const [translationRequestActivity, setTranslationRequestActivity] = useState<TranslationRequestActivity>();
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
      .catch((error) => notify("error", "无法读取 AI 翻译配置", String(error)))
      .finally(() => setTranslationStatusLoading(false));
    void loadGithubSettings();
  }, [loadGithubSettings, loadNexusStatus, notify]);

  useEffect(() => {
    setPathInput(dashboard.installation ? formatDisplayPath(dashboard.installation.path) : "");
  }, [dashboard.installation?.path]);

  const handleVerifyPath = async () => {
    onLoadingChange(true);
    try {
      const newDashboard = await onScanPath(pathInput);
      onDashboardUpdate(newDashboard);
      notify("success", "路径有效");
    } catch (e) {
      notify("error", "游戏目录验证失败", String(e));
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
      notify("success", "Nexus API Key 已验证并安全保存");
    } catch (error) {
      const detail = String(error);
      setNexusError(detail);
      notify("error", "Nexus API Key 保存失败", detail);
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
      notify("success", "Nexus API Key 已清除");
    } catch (error) {
      const detail = String(error);
      setNexusError(detail);
      notify("error", "Nexus API Key 清除失败", detail);
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
      notify("error", "无法打开 Nexus 设置页", detail);
    }
  };

  const loadTranslationModels = async () => {
    if (translationNetworkRef.current || translationMutationRef.current) return;
    const apiKey = translationApiKey.trim();
    const request = translationRequestPreview(
      translationBaseUrl,
      "models",
      "GET",
      undefined,
      apiKey,
    );
    translationNetworkRef.current = true;
    setTranslationModelsLoading(true);
    setTranslationRequestActivity({ phase: "loading", request });
    try {
      const result = await listAiTranslationModels({
        baseUrl: translationBaseUrl,
        apiKey: apiKey || undefined,
      });
      setTranslationModels(result.models);
      setTranslationModelPickerValue(
        result.models.some((model) => model.id === translationModelId) ? translationModelId : "",
      );
      setTranslationRequestActivity({
        phase: "success",
        request: result.request,
        response: result.response,
      });
      if (result.models.length === 0) {
        notify("warning", "接口未返回可用模型", "仍可手动填写 Model ID。");
      } else {
        notify("success", `已获取 ${result.models.length} 个模型`);
      }
    } catch (error) {
      const detail = formatRequestError(error);
      setTranslationRequestActivity({ phase: "error", request, error: detail });
      notify("error", "模型列表请求失败", detail);
    } finally {
      translationNetworkRef.current = false;
      setTranslationModelsLoading(false);
    }
  };

  const sendTranslationTest = async () => {
    if (translationNetworkRef.current || translationMutationRef.current) return;
    const apiKey = translationApiKey.trim();
    const requestBody = JSON.stringify({
      model: translationModelId.trim(),
      messages: [{ role: "user", content: TRANSLATION_CONNECTION_TEST_PROMPT }],
      max_tokens: 16,
    }, null, 2);
    const request = translationRequestPreview(
      translationBaseUrl,
      "chat/completions",
      "POST",
      requestBody,
      apiKey,
    );
    translationNetworkRef.current = true;
    setTranslationTestLoading(true);
    setTranslationRequestActivity({ phase: "loading", request });
    try {
      const result = await testAiTranslationConnection({
        baseUrl: translationBaseUrl,
        modelId: translationModelId,
        apiKey: apiKey || undefined,
      });
      setTranslationModelId(result.modelId);
      setTranslationModelPickerValue(
        translationModels.some((model) => model.id === result.modelId) ? result.modelId : "",
      );
      setTranslationRequestActivity({
        phase: "success",
        request: result.request,
        response: result.response,
      });
      notify("success", "AI 接口测试成功");
    } catch (error) {
      const detail = formatRequestError(error);
      setTranslationRequestActivity({ phase: "error", request, error: detail });
      notify("error", "AI 接口测试失败", detail);
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
      notify("success", "AI 翻译配置已保存");
    } catch (error) {
      notify("error", "AI 翻译配置保存失败", String(error));
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
      setTranslationModelPickerValue("");
      setTranslationApiKey("");
      setTranslationModels([]);
      setTranslationRequestActivity(undefined);
      notify("success", "AI 翻译配置已清除");
    } catch (error) {
      notify("error", "AI 翻译配置清除失败", String(error));
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
      notify("success", "GitHub 下载设置已保存");
    } catch (error) {
      const detail = String(error);
      setGithubDownloadError(detail);
      notify("error", "GitHub 下载设置保存失败", detail);
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
      notify("success", "已恢复 GitHub 直连");
    } catch (error) {
      const detail = String(error);
      setGithubDownloadError(detail);
      notify("error", "GitHub 下载设置清除失败", detail);
    } finally {
      githubDownloadMutationRef.current = false;
      setGithubDownloadMutation(null);
    }
  };

  const updateArguments = (target: LaunchTarget, argumentsList: string[]) => {
    if (argumentsList.some((argument) => argument.length > 512)) {
      notify("warning", "启动参数未保存", "单个启动参数不能超过 512 个字符。");
      return;
    }
    if (argumentsList.length > 32) {
      notify("warning", "启动参数未保存", "每种启动方式最多保存 32 个参数。");
      return;
    }
    onLaunchArgumentsChange(target, argumentsList);
  };

  const updateTranslationBaseUrl = (value: string) => {
    setTranslationBaseUrl(value);
    setTranslationModels([]);
    setTranslationModelPickerValue("");
    setTranslationRequestActivity(undefined);
  };

  const updateTranslationApiKey = (value: string) => {
    setTranslationApiKey(value);
    setTranslationModels([]);
    setTranslationModelPickerValue("");
    setTranslationRequestActivity(undefined);
  };

  const updateTranslationModelId = (value: string) => {
    setTranslationModelId(value);
    setTranslationModelPickerValue(
      translationModels.some((model) => model.id === value) ? value : "",
    );
    setTranslationRequestActivity(undefined);
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
  const selectedTranslationModel = translationModels.some((model) => model.id === translationModelId)
    ? translationModelId
    : undefined;
  const githubDownloadLabel = githubDownloadStatus.mode === "direct"
    ? "GitHub 直连"
    : githubDownloadStatus.customPrefix === GITHUB_PROXY_PRESET
      ? "gh-proxy.com"
      : "自定义镜像";
  const githubChoiceLabel = githubDownloadChoice === "direct"
    ? "GitHub 直连"
    : githubDownloadChoice === "gh-proxy"
      ? "gh-proxy.com 镜像"
      : "自定义 HTTPS 镜像";

  return (
    <section>
      <PageTitle title="设置" subtitle="游戏位置、启动方式与安全策略" />
      <div className="settings-form fluent-settings-form">
        <Field label="游戏目录">
          <div className="settings-inline-control game-path-setting">
            <Input
              value={pathInput}
              aria-label="Stardew Valley 游戏目录"
              onChange={(_, data) => setPathInput(data.value)}
            />
            <Button
              icon={<FolderOpenRegular />}
              aria-label="选择游戏目录"
              onClick={() => void choosePath()}
            />
            <Button appearance="primary" onClick={() => void handleVerifyPath()}>
              验证并保存
            </Button>
          </div>
        </Field>

        <Field label="启动参数">
          <div className="launch-argument-settings fluent-launch-argument-settings">
            <LaunchArgumentEditor
              target="smapi"
              label="SMAPI"
              argumentsList={launchArguments.smapi}
              presets={SMAPI_LAUNCH_ARGUMENT_OPTIONS}
              onChange={updateArguments}
              onInvalid={(detail) => notify("warning", "启动参数未保存", detail)}
            />
            <LaunchArgumentEditor
              target="vanilla"
              label="原版"
              argumentsList={launchArguments.vanilla}
              onChange={updateArguments}
              onInvalid={(detail) => notify("warning", "启动参数未保存", detail)}
            />
          </div>
        </Field>

        <Field label="删除策略">
          <div className="setting-row">
            <span>Mod 先移动到管理器回收区</span>
            <Switch defaultChecked aria-label="Mod 先移动到管理器回收区" />
          </div>
        </Field>

        <Field label="更新策略">
          <div className="setting-row">
            <span>保留 config.json 与回滚快照</span>
            <Switch defaultChecked disabled aria-label="保留 config.json 与回滚快照" />
          </div>
        </Field>

        <h2 className="settings-section-title">GitHub 下载加速</h2>
        <div className="github-download-setting">
          <div className="nexus-setting-status">
            <div>
              <strong>SMAPI 安装包下载方式</strong>
              <span>仅影响 SMAPI 安装、更新和卸载所需的 GitHub Release 资产；Mod 发布页下载不受影响。</span>
            </div>
            <Badge
              appearance="tint"
              color={githubDownloadStatus.mode === "custom" ? "warning" : "subtle"}
            >
              {githubDownloadLoading ? "读取中" : githubDownloadLabel}
            </Badge>
          </div>

          <MessageBar intent="warning" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>第三方镜像服务风险</MessageBarTitle>
              自定义镜像会接收 SMAPI 的 GitHub 资产下载地址。请仅使用你信任的 HTTPS 服务；管理器只验证地址格式，不声明服务可用性或安全性。
            </MessageBarBody>
          </MessageBar>

          <Field label="下载模式">
            <Dropdown
              id="github-download-mode"
              value={githubChoiceLabel}
              selectedOptions={[githubDownloadChoice]}
              disabled={githubDownloadLoading || githubDownloadMutation !== null}
              onOptionSelect={(_, data) => {
                const value = data.optionValue;
                if (value !== "direct" && value !== "gh-proxy" && value !== "custom") return;
                setGithubDownloadChoice(value);
                if (value === "custom" && githubCustomPrefix === GITHUB_PROXY_PRESET) {
                  setGithubCustomPrefix("");
                }
              }}
            >
              <Option value="direct">GitHub 直连</Option>
              <Option value="gh-proxy">gh-proxy.com 镜像</Option>
              <Option value="custom">自定义 HTTPS 镜像</Option>
            </Dropdown>
          </Field>

          {githubDownloadChoice === "custom" && (
            <Field label="镜像 Base URL">
              <Input
                id="github-custom-prefix"
                value={githubCustomPrefix}
                disabled={githubDownloadMutation !== null}
                placeholder="https://mirror.example.com/"
                autoComplete="url"
                onChange={(_, data) => setGithubCustomPrefix(data.value)}
              />
            </Field>
          )}

          {githubDownloadError && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>GitHub 下载设置不可用</MessageBarTitle>
                {githubDownloadError}
              </MessageBarBody>
              <MessageBarActions>
                <Button
                  icon={githubDownloadLoading ? <Spinner size="tiny" /> : <ArrowSyncRegular />}
                  disabled={githubDownloadLoading}
                  onClick={() => void loadGithubSettings()}
                >
                  重试
                </Button>
                <Button
                  appearance="transparent"
                  icon={<DismissRegular />}
                  aria-label="关闭 GitHub 下载设置错误"
                  onClick={() => setGithubDownloadError(undefined)}
                />
              </MessageBarActions>
            </MessageBar>
          )}

          <div className="settings-action-row">
            <Button
              appearance="primary"
              icon={githubDownloadMutation === "saving" ? <Spinner size="tiny" /> : <SaveRegular />}
              disabled={
                githubDownloadLoading
                || githubDownloadMutation !== null
                || (githubDownloadChoice === "custom" && !githubCustomPrefix.trim())
              }
              onClick={() => void saveGithubSettings()}
            >
              保存设置
            </Button>
            {(githubDownloadStatus.mode === "custom" || githubDownloadChoice !== "direct") && (
              <Button
                icon={githubDownloadMutation === "clearing" ? <Spinner size="tiny" /> : <ArrowSyncRegular />}
                disabled={githubDownloadLoading || githubDownloadMutation !== null}
                onClick={() => void clearGithubSettings()}
              >
                清除并恢复直连
              </Button>
            )}
          </div>
          <small>保存时后端只验证 HTTPS 地址格式；实际下载还会核验 GitHub 官方 SHA-256，不执行网络连通性测试。</small>
        </div>

        <h2 className="settings-section-title">Nexus Mods API</h2>
        <div className="nexus-setting">
          <div className="nexus-setting-status">
            <div>
              <strong>个人 API Key</strong>
              <span>仅接受 Nexus Mods 的 Personal API Key，不支持应用凭据、SSO Token 或 JWT；热门列表无需 Key。</span>
            </div>
            <Badge appearance="tint" color={nexusConfigured ? "success" : "subtle"}>
              {nexusStatusLoading ? "读取中" : nexusConfigured ? "已配置" : "未配置"}
            </Badge>
          </div>

          <Field label="Personal API Key">
            <div className="settings-inline-control nexus-key-control">
              <Input
                type="password"
                value={nexusKey}
                disabled={nexusStatusLoading || nexusMutation !== null}
                onChange={(_, data) => setNexusKey(data.value)}
                placeholder={nexusConfigured ? "输入新 Key 可替换现有凭据" : "粘贴 Nexus Mods 个人 API Key"}
                autoComplete="new-password"
              />
              <Button
                appearance="primary"
                icon={nexusMutation === "saving" ? <Spinner size="tiny" /> : <KeyRegular />}
                disabled={nexusStatusLoading || nexusMutation !== null || !nexusKey.trim()}
                onClick={() => void saveNexusKey()}
              >
                验证并保存
              </Button>
              {nexusConfigured && (
                <Button
                  className="danger-button"
                  icon={nexusMutation === "clearing" ? <Spinner size="tiny" /> : <DeleteRegular />}
                  disabled={nexusStatusLoading || nexusMutation !== null}
                  onClick={() => void removeNexusKey()}
                >
                  清除
                </Button>
              )}
            </div>
          </Field>

          {nexusError && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Nexus API Key 配置失败</MessageBarTitle>
                {nexusError}
              </MessageBarBody>
              <MessageBarActions>
                <Button
                  icon={nexusStatusLoading ? <Spinner size="tiny" /> : <ArrowSyncRegular />}
                  disabled={nexusMutation !== null || nexusStatusLoading}
                  onClick={() => void loadNexusStatus()}
                >
                  重试读取
                </Button>
                <Button
                  appearance="transparent"
                  icon={<DismissRegular />}
                  aria-label="关闭 Nexus API Key 错误"
                  onClick={() => setNexusError(undefined)}
                />
              </MessageBarActions>
            </MessageBar>
          )}

          <div className="nexus-key-help">
            <Button
              appearance="subtle"
              icon={<OpenRegular />}
              disabled={nexusMutation !== null}
              onClick={() => void openNexusApiKeysPage()}
            >
              获取 Personal API Key
            </Button>
            <span>在 Nexus Mods 设置页的 Personal API Key 区域复制完整 Key。</span>
          </div>
        </div>

        <h2 className="settings-section-title">AI 翻译</h2>
        <div className="ai-translation-setting">
          <div className="nexus-setting-status">
            <div>
              <strong>OpenAI-compatible 接口</strong>
              <span>API Key 保存到系统凭据库；名称与简介按星露谷物语语境翻译。</span>
            </div>
            <Badge appearance="tint" color={translationStatus.configured ? "success" : "subtle"}>
              {translationStatusLoading ? "读取中" : translationStatus.configured ? "已配置" : "未配置"}
            </Badge>
          </div>

          <Field label="Base URL">
            <Input
              id="translation-base-url"
              value={translationBaseUrl}
              disabled={translationControlsBusy}
              onChange={(_, data) => updateTranslationBaseUrl(data.value)}
              placeholder="例如 https://api.openai.com/v1 或 http://127.0.0.1:11434/v1"
              autoComplete="url"
            />
          </Field>

          {usesPlainHttp(translationBaseUrl) && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>HTTP 连接不会加密 API Key</MessageBarTitle>
                API Key、Mod 文本和模型回复会以明文在网络中传输。仅对完全信任的服务和网络使用 HTTP，公网服务建议改用 HTTPS。
              </MessageBarBody>
            </MessageBar>
          )}

          <Field label="API Key">
            <Input
              id="translation-api-key"
              type="password"
              value={translationApiKey}
              disabled={translationControlsBusy}
              onChange={(_, data) => updateTranslationApiKey(data.value)}
              placeholder={
                translationStatus.apiKeyConfigured
                && hasSameOrigin(translationStatus.baseUrl, translationBaseUrl)
                  ? "已安全保存；留空即可使用当前 API Key"
                  : "请输入 API Key"
              }
              autoComplete="new-password"
            />
          </Field>

          <Field label="Model ID">
            <div className="ai-translation-model-control">
              <Input
                id="translation-model-id"
                value={translationModelId}
                disabled={translationControlsBusy}
                placeholder="手动输入 Model ID"
                autoComplete="off"
                spellCheck={false}
                contentAfter={translationModelId ? (
                  <Button
                    appearance="transparent"
                    size="small"
                    icon={<DismissRegular />}
                    aria-label="清空 Model ID"
                    onClick={() => updateTranslationModelId("")}
                  />
                ) : undefined}
                onChange={(_, data) => updateTranslationModelId(data.value)}
              />
              <Combobox
                aria-label="选择已获取的模型"
                value={translationModelPickerValue}
                selectedOptions={selectedTranslationModel ? [selectedTranslationModel] : []}
                disabled={translationControlsBusy || translationModels.length === 0}
                placeholder={translationModels.length > 0 ? "搜索并选择模型" : "暂无模型"}
                onChange={(event) => setTranslationModelPickerValue(event.target.value)}
                onOptionSelect={(_, data) => {
                  if (!data.optionValue) return;
                  updateTranslationModelId(data.optionValue);
                  setTranslationModelPickerValue(data.optionText ?? data.optionValue);
                }}
              >
                {translationModels.map((model) => (
                  <Option key={model.id} value={model.id} text={model.id}>
                    <div className="ai-model-option">
                      <span>{model.id}</span>
                      {model.ownedBy && <small>{model.ownedBy}</small>}
                    </div>
                  </Option>
                ))}
              </Combobox>
              <Button
                icon={translationModelsLoading ? <Spinner size="tiny" /> : <ArrowSyncRegular />}
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
          </Field>

          {translationRequestActivity && (
            <MessageBar
              className="ai-translation-activity"
              intent={
                translationRequestActivity.phase === "error"
                  ? "error"
                  : translationRequestActivity.phase === "success"
                    ? "success"
                    : "info"
              }
              layout="multiline"
            >
              <MessageBarBody>
                <MessageBarTitle>
                  {translationRequestActivity.phase === "loading" ? (
                    <>
                      正在向 <code>{translationRequestActivity.request.endpoint}</code> 发起请求...
                    </>
                  ) : translationRequestActivity.phase === "success" ? "请求完成" : "请求失败"}
                </MessageBarTitle>
                <div className="ai-translation-activity-details">
                  <div className="ai-translation-request-target">
                    <Badge appearance="outline">{translationRequestActivity.request.method}</Badge>
                    <code>{translationRequestActivity.request.endpoint}</code>
                  </div>
                  <div className="ai-translation-payload">
                    <strong>请求内容</strong>
                    <pre>{translationRequestActivity.request.body ?? "无（GET 请求）"}</pre>
                  </div>
                  {translationRequestActivity.response && (
                    <div className="ai-translation-payload">
                      <strong>回复摘要</strong>
                      <span>{translationRequestActivity.response.summary}</span>
                      {translationRequestActivity.response.content && (
                        <>
                          <strong>回复内容</strong>
                          <pre>{translationRequestActivity.response.content}</pre>
                        </>
                      )}
                    </div>
                  )}
                  {translationRequestActivity.error && (
                    <div className="ai-translation-payload">
                      <strong>错误</strong>
                      <span>{translationRequestActivity.error}</span>
                    </div>
                  )}
                </div>
              </MessageBarBody>
              {translationRequestActivity.phase !== "loading" && (
                <MessageBarActions>
                  <Button
                    appearance="transparent"
                    icon={<DismissRegular />}
                    aria-label="关闭请求详情"
                    onClick={() => setTranslationRequestActivity(undefined)}
                  />
                </MessageBarActions>
              )}
            </MessageBar>
          )}

          <div className="ai-translation-actions settings-action-row">
            <Button
              icon={translationTestLoading ? <Spinner size="tiny" /> : <SendRegular />}
              disabled={
                translationControlsBusy
                || !translationProviderReady
                || !translationModelId.trim()
              }
              onClick={() => void sendTranslationTest()}
            >
              发送测试
            </Button>
            <Button
              appearance="primary"
              icon={translationMutation === "saving" ? <Spinner size="tiny" /> : <SaveRegular />}
              disabled={
                translationControlsBusy
                || !translationProviderReady
                || !translationModelId.trim()
              }
              onClick={() => void saveTranslationSettings()}
            >
              保存配置
            </Button>
            {translationStatus.configured && (
              <Button
                className="danger-button"
                icon={translationMutation === "clearing" ? <Spinner size="tiny" /> : <DeleteRegular />}
                disabled={translationControlsBusy}
                onClick={() => void removeTranslationSettings()}
              >
                清除
              </Button>
            )}
          </div>
        </div>
      </div>
    </section>
  );
}
