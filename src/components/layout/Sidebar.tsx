import { Badge, Layout, Menu } from "antd";
import {
  AppstoreOutlined,
  CloudDownloadOutlined,
  SafetyCertificateOutlined,
  SettingOutlined,
  ToolOutlined,
} from "@ant-design/icons";

const { Sider } = Layout;

const navItems = [
  { key: "overview", icon: <AppstoreOutlined />, label: "概览" },
  { key: "mods", icon: <ToolOutlined />, label: "我的 Mod" },
  { key: "downloads", icon: <CloudDownloadOutlined />, label: "下载中心" },
  { key: "smapi", icon: <SafetyCertificateOutlined />, label: "SMAPI" },
  { key: "settings", icon: <SettingOutlined />, label: "设置" },
];

interface SidebarProps {
  currentPage: string;
  onPageChange: (page: string) => void;
}

export function Sidebar({ currentPage, onPageChange }: SidebarProps) {
  return (
    <Sider width={232} className="sidebar" breakpoint="lg" collapsedWidth={72}>
      <div className="brand">
        <span className="brand-mark">V</span>
        <div>
          <strong>Valley Steward</strong>
          <small>Mod 管理器</small>
        </div>
      </div>
      <Menu
        mode="inline"
        selectedKeys={[currentPage]}
        items={navItems}
        onClick={({ key }) => onPageChange(key)}
      />
      <div className="sidebar-foot">
        <Badge status="success" />
        <span>本地服务正常</span>
      </div>
    </Sider>
  );
}
