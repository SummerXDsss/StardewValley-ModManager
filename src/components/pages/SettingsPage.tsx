import { useEffect, useRef, useState } from "react";
import { App, Button, Input, Select, Space, Switch, Tag } from "antd";
import { FolderOpenOutlined } from "@ant-design/icons";
import { PageTitle } from "../shared";
import {
  chooseGameDirectory,
  clearAiTranslationSettings,
  clearNexusApiKey,
  getAiTranslationSettings,
  getNexusAuthStatus,
  saveAiTranslationSettings,
  setNexusApiKey,
} from "../../api";
import type { AiTranslationStatus, Dashboard, LaunchArgumentSettings, LaunchTarget } from "../../types";
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
  }, []);

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
