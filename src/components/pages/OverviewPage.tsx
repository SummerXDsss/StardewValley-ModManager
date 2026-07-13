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
  const invalidMods = dashboard.mods.filter((mod) => mod.health === "error").length;
  const healthScore = Math.max(
    0,
    (dashboard.installation ? 40 : 0)
      + (dashboard.smapi.installed ? 30 : 0)
      + Math.max(0, 20 - invalidMods * 5)
      + Math.max(0, 10 - updates * 2),
  );
  const healthSummary = !dashboard.installation
    ? "尚未确认游戏目录，请先完成首次设置。"
    : !dashboard.smapi.installed
      ? "尚未检测到 SMAPI，可在 SMAPI 页面查看官方稳定版。"
      : invalidMods > 0
        ? `${invalidMods} 个 Mod 清单无法解析，请在 Mod 列表中处理。`
        : updates > 0
          ? `检测到 ${updates} 个 Mod 标记了可用更新。`
          : `已检测到 SMAPI ${dashboard.smapi.version ?? "已安装"}，当前未发现 Mod 清单错误。`;

  return (
    <section>
      <PageTitle
        title="下午好，农场主"
        subtitle={dashboard.installation ? "已读取本地游戏环境与 Mod 状态。" : "请先设置 Stardew Valley 游戏目录。"}
      />
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
                <p>进入 Mod 列表查看版本与文件状态</p>
              </div>
              <Button onClick={() => onPageChange("mods")}>查看 Mod</Button>
            </div>
          ) : (
            <Empty description="当前未发现待处理更新" image={Empty.PRESENTED_IMAGE_SIMPLE} />
          )}
        </div>
        <aside className="health-panel">
          <div>
            <span className="eyebrow">环境健康度</span>
            <strong className="health-score">{healthScore}</strong>
            <span>/ 100</span>
          </div>
          <Progress percent={healthScore} showInfo={false} strokeColor="#2f6f4e" railColor="#dce3dc" />
          <p>{healthSummary}</p>
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
