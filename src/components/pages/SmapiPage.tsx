import { Button, Tag } from "antd";
import { RocketOutlined, SafetyCertificateOutlined } from "@ant-design/icons";
import { PageTitle } from "../shared";
import type { Dashboard } from "../../types";

interface SmapiPageProps {
  dashboard: Dashboard;
  gameRunning?: boolean;
  onLaunchSmapi: () => void;
  onOpenSmapiDownload: () => void;
}

export function SmapiPage({ dashboard, gameRunning = false, onLaunchSmapi, onOpenSmapiDownload }: SmapiPageProps) {
  return (
    <section>
      <PageTitle title="SMAPI" subtitle="模组加载器与运行环境" />
      <div className="smapi-panel">
        <SafetyCertificateOutlined className="smapi-mark" />
        <div>
          <Tag color={dashboard.smapi.installed ? "green" : "red"}>
            {dashboard.smapi.installed ? "已安装" : "未安装"}
          </Tag>
          <h2>
            {dashboard.smapi.installed
              ? `SMAPI ${dashboard.smapi.version ?? "版本未知"}`
              : "需要安装 SMAPI"}
          </h2>
          <p>
            {dashboard.smapi.installed
              ? dashboard.smapi.executable
              : "从官方来源下载并确认后运行安装程序。"}
          </p>
        </div>
        <Button
          type="primary"
          icon={<RocketOutlined />}
          disabled={dashboard.smapi.installed && gameRunning}
          onClick={dashboard.smapi.installed ? onLaunchSmapi : onOpenSmapiDownload}
        >
          {dashboard.smapi.installed ? (gameRunning ? "游戏运行中" : "启动 SMAPI") : "获取 SMAPI"}
        </Button>
      </div>
      <div className="info-list">
        <div>
          <span>游戏兼容性</span>
          <strong>Stardew Valley {dashboard.installation?.version ?? "未知"}</strong>
        </div>
        <div>
          <span>日志诊断</span>
          <Button type="link">打开最新日志</Button>
        </div>
        <div>
          <span>更新通道</span>
          <strong>稳定版</strong>
        </div>
      </div>
    </section>
  );
}
