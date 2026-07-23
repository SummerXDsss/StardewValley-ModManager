import { useEffect, useMemo, useState } from "react";
import {
  Badge,
  Button,
  Spinner,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Tooltip,
} from "@fluentui/react-components";
import {
  CheckmarkCircle16Regular,
  ChevronLeft20Regular,
  ChevronRight20Regular,
  Delete20Regular,
  ErrorCircle16Regular,
  FolderOpen20Regular,
  LocalLanguage20Regular,
  Warning16Regular,
} from "@fluentui/react-icons";
import type { InstalledMod } from "../../types";

const PAGE_SIZE = 10;

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
  const [page, setPage] = useState(1);
  const [expandedDescriptions, setExpandedDescriptions] = useState<Set<string>>(() => new Set());
  const pageCount = Math.max(1, Math.ceil(mods.length / PAGE_SIZE));

  useEffect(() => {
    setPage((current) => Math.min(current, pageCount));
  }, [pageCount]);

  const visibleMods = useMemo(() => {
    if (compact) return mods.slice(0, 5);
    return mods.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE);
  }, [compact, mods, page]);

  const toggleDescription = (path: string) => {
    setExpandedDescriptions((current) => {
      const next = new Set(current);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
  };

  if (!visibleMods.length) {
    return (
      <div className="empty-state compact-empty" role="status">
        <strong>没有匹配的 Mod</strong>
        <span>调整搜索或筛选条件后再试。</span>
      </div>
    );
  }

  return (
    <div className="mod-table-region">
      <div className="table-scroll-region">
        <Table className="mod-table" aria-label="已安装 Mod">
          <TableHeader>
            <TableRow>
              <TableHeaderCell>Mod</TableHeaderCell>
              <TableHeaderCell className="author-column">作者</TableHeaderCell>
              <TableHeaderCell className="version-column">版本</TableHeaderCell>
              <TableHeaderCell className="state-column">加载状态</TableHeaderCell>
              <TableHeaderCell className="actions-column">操作</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {visibleMods.map((mod) => {
              const expanded = expandedDescriptions.has(mod.path);
              const translating = translatingModPaths?.has(mod.path) ?? false;
              const invalidPlaceholder = mod.id.startsWith("invalid:");
              const healthMessage = mod.error ?? (invalidPlaceholder ? "manifest.json 无法读取" : undefined);
              return (
                <TableRow key={mod.path}>
                  <TableCell>
                    <div className="mod-name">
                      <div className="mod-title-row">
                        <strong>{mod.name}</strong>
                        {mod.health === "error" && (
                          <Badge appearance="tint" color="danger" icon={<ErrorCircle16Regular />}>清单错误</Badge>
                        )}
                        {mod.health === "warning" && (
                          <Badge appearance="tint" color="warning" icon={<Warning16Regular />}>需要处理</Badge>
                        )}
                        {mod.translated && (
                          <Badge appearance="tint" color="informative" icon={<CheckmarkCircle16Regular />}>已翻译</Badge>
                        )}
                      </div>
                      {healthMessage && <small className="mod-error-detail">{healthMessage}</small>}
                      {mod.description && (
                        <div className="mod-description-row">
                          <p className={`mod-description ${expanded ? "expanded" : ""}`}>{mod.description}</p>
                          {mod.description.length > 72 && (
                            <Button appearance="transparent" size="small" onClick={() => toggleDescription(mod.path)}>
                              {expanded ? "收起" : "展开"}
                            </Button>
                          )}
                        </div>
                      )}
                      {!invalidPlaceholder && <small className="mod-unique-id">{mod.id}</small>}
                      {!mod.translated && mod.health !== "error" && onTranslateMod && (
                        <Button
                          appearance="subtle"
                          size="small"
                          icon={translating ? <Spinner size="tiny" /> : <LocalLanguage20Regular />}
                          disabled={translating}
                          onClick={() => onTranslateMod(mod)}
                        >
                          一键翻译
                        </Button>
                      )}
                    </div>
                  </TableCell>
                  <TableCell className="author-column">{mod.author}</TableCell>
                  <TableCell className="version-column">
                    <div className="version-cell">
                      <span>{mod.version}</span>
                      {mod.updateAvailable && (
                        <Badge appearance="tint" color="warning">可更新 {mod.updateAvailable}</Badge>
                      )}
                    </div>
                  </TableCell>
                  <TableCell className="state-column">
                    <Tooltip
                      content={mutationDisabled ? "游戏运行时不能更改 Mod 加载状态" : mod.enabled ? "停用 Mod" : "启用 Mod"}
                      relationship="description"
                    >
                      <Switch
                        checked={mod.enabled}
                        label={mod.enabled ? "已启用" : "已停用"}
                        disabled={mutationDisabled}
                        onChange={(_, data) => onToggleMod(mod, data.checked)}
                      />
                    </Tooltip>
                  </TableCell>
                  <TableCell className="actions-column">
                    <div className="table-actions">
                      <Tooltip content="打开目录" relationship="label">
                        <Button
                          appearance="subtle"
                          icon={<FolderOpen20Regular />}
                          aria-label="打开目录"
                          onClick={() => onOpenFolder(mod)}
                        />
                      </Tooltip>
                      <Tooltip content={mutationDisabled ? "请先关闭游戏，再移除 Mod" : "移到回收区"} relationship="label">
                        <Button
                          appearance="subtle"
                          className="danger-icon-button"
                          disabled={mutationDisabled}
                          icon={<Delete20Regular />}
                          aria-label="移到回收区"
                          onClick={() => onRemoveMod(mod)}
                        />
                      </Tooltip>
                    </div>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </div>
      {!compact && pageCount > 1 && (
        <div className="table-pagination" aria-label="Mod 列表分页">
          <span>第 {page} / {pageCount} 页</span>
          <div>
            <Button
              appearance="subtle"
              icon={<ChevronLeft20Regular />}
              aria-label="上一页"
              disabled={page === 1}
              onClick={() => setPage((current) => Math.max(1, current - 1))}
            />
            <Button
              appearance="subtle"
              icon={<ChevronRight20Regular />}
              aria-label="下一页"
              disabled={page === pageCount}
              onClick={() => setPage((current) => Math.min(pageCount, current + 1))}
            />
          </div>
        </div>
      )}
    </div>
  );
}
