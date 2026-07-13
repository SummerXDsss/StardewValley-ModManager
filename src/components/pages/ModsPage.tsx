import { useMemo, useState } from "react";
import { Button, Input, Segmented } from "antd";
import { ReloadOutlined, SearchOutlined } from "@ant-design/icons";
import { PageTitle, ModTable } from "../shared";
import type { Dashboard, InstalledMod } from "../../types";

interface ModsPageProps {
  dashboard: Dashboard;
  modChangesDisabled?: boolean;
  onRefresh: () => void;
  onToggleMod: (mod: InstalledMod, enabled: boolean) => void;
  onOpenFolder: (mod: InstalledMod) => void;
  onRemoveMod: (mod: InstalledMod) => void;
}

export function ModsPage({
  dashboard,
  modChangesDisabled = false,
  onRefresh,
  onToggleMod,
  onOpenFolder,
  onRemoveMod,
}: ModsPageProps) {
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState("全部");

  const enabled = dashboard.mods.filter((mod) => mod.enabled).length;

  const visibleMods = useMemo(() => {
    return dashboard.mods.filter((mod) => {
      const matchesText = `${mod.name} ${mod.author} ${mod.id}`.toLowerCase().includes(query.toLowerCase());
      const matchesFilter =
        filter === "全部" ||
        (filter === "已启用" && mod.enabled) ||
        (filter === "已禁用" && !mod.enabled) ||
        (filter === "可更新" && Boolean(mod.updateAvailable));
      return matchesText && matchesFilter;
    });
  }, [dashboard.mods, filter, query]);

  return (
    <section>
      <PageTitle title="我的 Mod" subtitle={`${dashboard.mods.length} 个已安装，${enabled} 个正在启用`} />
      <div className="toolbar">
        <Input
          prefix={<SearchOutlined />}
          placeholder="搜索名称、作者或唯一 ID"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          allowClear
        />
        <Segmented options={["全部", "已启用", "已禁用", "可更新"]} value={filter} onChange={(value) => setFilter(String(value))} />
        <Button icon={<ReloadOutlined />} onClick={onRefresh}>
          重新扫描
        </Button>
      </div>
      <div className="table-shell">
        <ModTable
          mods={visibleMods}
          mutationDisabled={modChangesDisabled}
          onToggleMod={onToggleMod}
          onOpenFolder={onOpenFolder}
          onRemoveMod={onRemoveMod}
        />
      </div>
    </section>
  );
}
