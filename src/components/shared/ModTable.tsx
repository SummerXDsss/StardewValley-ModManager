import { Button, Empty, Space, Switch, Table, Tag, Tooltip, Typography } from "antd";
import type { TableProps } from "antd";
import { CheckCircleOutlined, DeleteOutlined, FolderOpenOutlined, TranslationOutlined } from "@ant-design/icons";
import type { InstalledMod } from "../../types";

interface ModTableProps {
  mods: InstalledMod[];
  compact?: boolean;
  mutationDisabled?: boolean;
  onToggleMod: (mod: InstalledMod, enabled: boolean) => void;
  onOpenFolder: (mod: InstalledMod) => void;
  onRemoveMod: (mod: InstalledMod) => void;
  onTranslateMod?: (mod: InstalledMod) => void;
  translatingModPaths?: ReadonlySet<string>;
}

export function ModTable({
  mods,
  compact = false,
  mutationDisabled = false,
  onToggleMod,
  onOpenFolder,
  onRemoveMod,
  onTranslateMod,
  translatingModPaths,
}: ModTableProps) {
  const columns: TableProps<InstalledMod>["columns"] = [
    {
      title: "Mod",
      dataIndex: "name",
      width: compact ? 330 : 440,
      render: (_, mod) => (
        <div className="mod-name">
          <span className={`health-dot ${mod.health}`} />
          <div className="mod-name-content">
            <div className="mod-title-row">
              <strong>{mod.name}</strong>
              {mod.translated && (
                <Tag icon={<CheckCircleOutlined />} color="green">
                  已翻译
                </Tag>
              )}
            </div>
            {(mod.description || (!mod.translated && mod.health !== "error" && onTranslateMod)) && (
              <div className="mod-description-row">
                {mod.description && (
                  <Typography.Paragraph
                    className="mod-description"
                    ellipsis={{
                      rows: 2,
                      expandable: "collapsible",
                      symbol: (expanded) => expanded ? "收起" : "展开",
                    }}
                  >
                    {mod.description}
                  </Typography.Paragraph>
                )}
                {!mod.translated && mod.health !== "error" && onTranslateMod && (
                  <Button
                    type="text"
                    size="small"
                    icon={<TranslationOutlined />}
                    loading={translatingModPaths?.has(mod.path)}
                    disabled={translatingModPaths?.has(mod.path)}
                    onClick={() => onTranslateMod(mod)}
                  >
                    一键翻译
                  </Button>
                )}
              </div>
            )}
            <small>{mod.id}</small>
          </div>
        </div>
      ),
    },
    { title: "作者", dataIndex: "author", responsive: ["lg"] },
    {
      title: "版本",
      dataIndex: "version",
      render: (version, mod) => (
        <Space size={6}>
          <span>{version}</span>
          {mod.updateAvailable && <Tag color="gold">可更新 {mod.updateAvailable}</Tag>}
        </Space>
      ),
    },
    {
      title: "状态",
      dataIndex: "enabled",
      width: 100,
      render: (enabled, mod) => (
        <Tooltip title={mutationDisabled ? "游戏运行时不能更改 Mod 加载状态" : undefined}>
          <span className="disabled-control-tooltip">
            <Switch
              checked={enabled}
              checkedChildren="启用"
              unCheckedChildren="停用"
              disabled={mutationDisabled}
              onChange={(value) => onToggleMod(mod, value)}
            />
          </span>
        </Tooltip>
      ),
    },
    {
      title: "操作",
      key: "actions",
      width: 116,
      render: (_, mod) => (
        <Space size={2}>
          <Tooltip title="打开目录">
            <Button 
              type="text" 
              icon={<FolderOpenOutlined />} 
              onClick={() => onOpenFolder(mod)} 
            />
          </Tooltip>
          <Tooltip title={mutationDisabled ? "请先关闭游戏，再移除 Mod" : "移到回收区"}>
            <span className="disabled-control-tooltip">
              <Button
                type="text"
                danger
                disabled={mutationDisabled}
                icon={<DeleteOutlined />}
                onClick={() => onRemoveMod(mod)}
              />
            </span>
          </Tooltip>
        </Space>
      ),
    },
  ];

  return (
    <Table
      rowKey="path"
      columns={columns}
      dataSource={compact ? mods.slice(0, 5) : mods}
      pagination={compact ? false : { pageSize: 10, showSizeChanger: false }}
      locale={{ emptyText: <Empty description="没有匹配的 Mod" image={Empty.PRESENTED_IMAGE_SIMPLE} /> }}
      scroll={{ x: 840 }}
    />
  );
}
