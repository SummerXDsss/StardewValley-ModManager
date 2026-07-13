import { useEffect, useState } from "react";
import { App, Button, Input, Select, Space, Switch, Tag } from "antd";
import { PageTitle } from "../shared";
import { clearNexusApiKey, getNexusAuthStatus, setNexusApiKey } from "../../api";
import type { Dashboard, LaunchArgumentSettings, LaunchTarget } from "../../types";

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
  const [pathInput, setPathInput] = useState(dashboard.installation?.path ?? "");
  const [nexusKey, setNexusKey] = useState("");
  const [nexusConfigured, setNexusConfigured] = useState(false);
  const [savingNexus, setSavingNexus] = useState(false);

  useEffect(() => {
    void getNexusAuthStatus().then((status) => setNexusConfigured(status.configured));
  }, []);

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
      </div>
    </section>
  );
}
