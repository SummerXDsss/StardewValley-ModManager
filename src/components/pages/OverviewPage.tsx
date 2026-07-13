import { Button, Empty, Progress } from "antd";
import { CloudDownloadOutlined, ReloadOutlined } from "@ant-design/icons";
import { PageTitle, StatusItem, ModTable } from "../shared";
import type { Dashboard, InstalledMod } from "../../types";

interface OverviewPageProps {
  dashboard: Dashboard;
  modChangesDisabled?: boolean;
  onRefresh: () => void;
  onPageChange: (page: string) => void;
  onToggleMod: (mod: InstalledMod, enabled: boolean) => void;
  onOpenFolder: (mod: InstalledMod) => void;
  onRemoveMod: (mod: InstalledMod) => void;
}

export function OverviewPage({
  dashboard,
  modChangesDisabled = false,
  onRefresh,
  onPageChange,
  onToggleMod,
  onOpenFolder,
  onRemoveMod,
}: OverviewPageProps) {
  const enabled = dashboard.mods.filter((mod) => mod.enabled).length;
  const updates = dashboard.mods.filter((mod) => mod.updateAvailable).length;

  return (
    <section>
      <PageTitle title="下午好，农场主" subtitle="环境已就绪，可以安全管理你的模组。" />
      <div className="status-strip">
        <StatusItem
          label="游戏目录"
          value={dashboard.installation ? "已连接" : "未找到"}
          ok={Boolean(dashboard.installation)}
          detail={dashboard.installation?.store}
        />
        <StatusItem
          label="SMAPI"
          value={dashboard.smapi.installed ? dashboard.smapi.version ?? "已安装" : "未安装"}
          ok={dashboard.smapi.installed}
        />
        <StatusItem label="已启用 Mod" value={`${enabled} / ${dashboard.mods.length}`} ok />
        <StatusItem label="待更新" value={`${updates} 个`} ok={updates === 0} warning={updates > 0} />
      </div>
      <div className="overview-grid">
        <div className="workspace-panel">
          <div className="section-heading">
            <div>
              <h2>需要关注</h2>
              <p>优先处理更新和环境问题</p>
            </div>
            <Button type="link" onClick={() => onPageChange("mods")}>
              查看全部
            </Button>
          </div>
          {updates ? (
            <div className="attention-row">
              <div className="attention-icon">
                <CloudDownloadOutlined />
              </div>
              <div>
                <strong>{updates} 个 Mod 可以更新</strong>
                <p>更新前会自动保留配置与回滚快照</p>
              </div>
              <Button>检查更新</Button>
            </div>
          ) : (
            <Empty description="一切都是最新状态" image={Empty.PRESENTED_IMAGE_SIMPLE} />
          )}
        </div>
        <aside className="health-panel">
          <div>
            <span className="eyebrow">环境健康度</span>
            <strong className="health-score">92</strong>
            <span>/ 100</span>
          </div>
          <Progress percent={92} showInfo={false} strokeColor="#2f6f4e" railColor="#dce3dc" />
          <p>SMAPI 与游戏版本匹配。处理 1 个更新后可达到最佳状态。</p>
        </aside>
      </div>
      <div className="recent-section">
        <div className="section-heading">
          <div>
            <h2>最近的 Mod</h2>
            <p>快速查看当前加载状态</p>
          </div>
          <Button icon={<ReloadOutlined />} onClick={onRefresh}>
            刷新
          </Button>
        </div>
        <div className="table-shell">
          <ModTable
            mods={dashboard.mods}
            compact
            mutationDisabled={modChangesDisabled}
            onToggleMod={onToggleMod}
            onOpenFolder={onOpenFolder}
            onRemoveMod={onRemoveMod}
          />
        </div>
      </div>
    </section>
  );
}
