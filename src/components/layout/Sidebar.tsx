import { Button, PresenceBadge, Tooltip } from "@fluentui/react-components";
import {
  Apps24Regular,
  CloudArrowDown24Regular,
  LeafOne24Regular,
  Settings24Regular,
  ShieldCheckmark24Regular,
  Wrench24Regular,
} from "@fluentui/react-icons";
import type { ReactElement } from "react";

interface NavItem {
  key: string;
  icon: ReactElement;
  label: string;
}

const navItems: NavItem[] = [
  { key: "overview", icon: <Apps24Regular />, label: "概览" },
  { key: "mods", icon: <Wrench24Regular />, label: "我的 Mod" },
  { key: "downloads", icon: <CloudArrowDown24Regular />, label: "下载中心" },
  { key: "smapi", icon: <ShieldCheckmark24Regular />, label: "SMAPI" },
  { key: "settings", icon: <Settings24Regular />, label: "设置" },
];

interface SidebarProps {
  currentPage: string;
  onPageChange: (page: string) => void;
}

export function Sidebar({ currentPage, onPageChange }: SidebarProps) {
  return (
    <aside className="sidebar" aria-label="主导航">
      <div className="brand">
        <span className="brand-mark" aria-hidden="true"><LeafOne24Regular /></span>
        <div className="brand-copy">
          <strong>Valley Steward</strong>
          <small>Stardew Valley Mod 管理器</small>
        </div>
      </div>
      <nav className="sidebar-nav">
        {navItems.map((item) => (
          <Tooltip key={item.key} content={item.label} relationship="label" positioning="after">
            <Button
              appearance="subtle"
              size="large"
              icon={item.icon}
              className={`nav-button ${currentPage === item.key ? "active" : ""}`}
              aria-current={currentPage === item.key ? "page" : undefined}
              onClick={() => onPageChange(item.key)}
            >
              <span>{item.label}</span>
            </Button>
          </Tooltip>
        ))}
      </nav>
      <div className="sidebar-foot">
        <PresenceBadge status="available" size="small" />
        <span>本地服务正常</span>
      </div>
    </aside>
  );
}
