import { Button, Checkbox, Dropdown, Popover, Space, Tag, Tooltip } from "antd";
import type { MenuProps } from "antd";
import {
  DownOutlined,
  EditOutlined,
  FolderOpenOutlined,
  PlayCircleFilled,
  PoweroffOutlined,
  ReloadOutlined,
  ThunderboltOutlined,
} from "@ant-design/icons";
import type { LaunchPreference } from "../../hooks";
import type { GameProcessStatus, SmapiStatus } from "../../types";
import { formatDisplayPath } from "../../utils/path";

export type GameProcessAction = "launching" | "stopping" | "restarting" | null;

interface TopbarProps {
  gamePath?: string;
  smapi?: SmapiStatus;
  smapiLoading: boolean;
  smapiError?: string;
  steamRunning: boolean;
  steamLoading: boolean;
  steamError?: string;
  rememberLaunch: boolean;
  onRememberLaunchChange: (checked: boolean) => void;
  onLaunch: (choice: LaunchPreference) => void;
  launchPreference: LaunchPreference;
  processStatus: GameProcessStatus;
  processAction: GameProcessAction;
  monitoring: boolean;
  monitorError?: string;
  onEditGamePath: () => void;
  onStop: () => void;
  onRestart: () => void;
}

export function Topbar({
  gamePath,
  smapi,
  smapiLoading,
  smapiError,
  steamRunning,
  steamLoading,
  steamError,
  rememberLaunch,
  onRememberLaunchChange,
  onLaunch,
  launchPreference,
  processStatus,
  processAction,
  monitoring,
  monitorError,
  onEditGamePath,
  onStop,
  onRestart,
}: TopbarProps) {
  const launchMenu: MenuProps["items"] = [
    { key: "smapi", label: "使用 SMAPI 启动", icon: <ThunderboltOutlined /> },
    { key: "vanilla", label: "启动原版游戏", icon: <PlayCircleFilled /> },
    { key: "profile", label: "使用指定 Mods 目录", icon: <FolderOpenOutlined /> },
    { type: "divider" },
    {
      key: "remember",
      label: (
        <Checkbox
          checked={rememberLaunch}
          onClick={(event) => event.stopPropagation()}
          onChange={(event) => onRememberLaunchChange(event.target.checked)}
        >
          记住上次选择
        </Checkbox>
      ),
    },
  ];

  const running = processStatus.running || processStatus.state === "running";
  const showProcessControls = running || processAction === "stopping" || processAction === "restarting";
  const targetLabel = processStatus.target === "smapi" ? "SMAPI" : "原版游戏";
  const statusLabel = processAction === "launching"
    ? "正在启动"
    : processAction === "stopping"
      ? "正在关闭"
      : processAction === "restarting"
        ? "正在重启"
        : running
          ? "游戏运行中"
          : monitoring
            ? "正在检查"
            : monitorError
              ? "监测暂不可用"
              : processStatus.state === "exited"
                ? "游戏已退出"
                : "游戏未运行";
  const statusTone = processAction || monitoring
    ? "working"
    : running
      ? "running"
      : monitorError
        ? "warning"
        : "stopped";
  const statusDetail = running
    ? `${targetLabel}${processStatus.pid ? ` · PID ${processStatus.pid}` : ""}`
    : undefined;
  const pathPopover = gamePath ? (
    <div className="path-popover">
      <span>{formatDisplayPath(gamePath)}</span>
      <Button type="link" icon={<EditOutlined />} onClick={onEditGamePath}>
        修改路径
      </Button>
    </div>
  ) : (
    <Button type="primary" icon={<FolderOpenOutlined />} onClick={onEditGamePath}>
      设置游戏路径
    </Button>
  );
  const displayPath = gamePath ? formatDisplayPath(gamePath) : "";
  const pathSummary = displayPath.length > 6
    ? `${Array.from(displayPath).slice(0, 6).join("")}...`
    : displayPath;
  const steamLabel = steamLoading
    ? "Steam 检查中"
    : steamError
      ? "Steam 未知"
      : steamRunning
        ? "Steam 运行中"
        : "Steam 未运行";
  const steamTone = steamLoading
    ? "checking"
    : steamError
      ? "unknown"
      : steamRunning
        ? "running"
        : "stopped";
  const smapiLabel = smapi
    ? smapi.installed
      ? `SMAPI ${smapi.version ?? "已安装"}`
      : "SMAPI 未安装"
    : smapiLoading
      ? "SMAPI 检查中"
      : "SMAPI 未知";
  const smapiColor = smapi?.installed ? "green" : smapiLoading ? "processing" : undefined;

  return (
    <header className="topbar">
      <div className="topbar-environment">
        <Popover title="游戏目录" content={pathPopover} trigger="click" placement="bottomLeft">
          <button
            className={`path-chip ${gamePath ? "ready" : "missing"}`}
            type="button"
            aria-label={gamePath ? "查看完整游戏路径" : "需要设置游戏路径"}
          >
            <FolderOpenOutlined />
            <span className="path-chip-label">{gamePath ? pathSummary : "未设置路径"}</span>
            <DownOutlined className="path-chip-arrow" />
          </button>
        </Popover>
        <Tooltip title="修改游戏路径">
          <Button
            className="path-edit-button"
            type="text"
            icon={<EditOutlined />}
            aria-label="修改游戏路径"
            onClick={onEditGamePath}
          />
        </Tooltip>
        <Tooltip
          title={steamError
            ? `Steam 状态获取失败：${steamError}`
            : "根据本机 Steam 主进程判断，不代表账号的网络连接模式"}
        >
          <span
            className={`steam-runtime-status ${steamTone}`}
            role="status"
            aria-label={steamLabel}
            tabIndex={0}
          >
            <i aria-hidden="true" />
            <span className="steam-runtime-label">{steamLabel}</span>
          </span>
        </Tooltip>
        <Tooltip title={smapiError ? `SMAPI 状态获取失败：${smapiError}` : undefined}>
          <Tag color={smapiColor} className="smapi-version-tag">
            {smapiLabel}
          </Tag>
        </Tooltip>
      </div>
      <div className="game-process-controls">
        <Tooltip title={monitorError ? `最近一次状态检查失败：${monitorError}` : statusDetail}>
          <div className={`game-process-status ${statusTone}`} role="status" aria-live="polite">
            <i aria-hidden="true" />
            <span>
              <strong>{statusLabel}</strong>
              {statusDetail && <small>{statusDetail}</small>}
            </span>
          </div>
        </Tooltip>
        {showProcessControls ? (
          <Space.Compact className="process-action-group">
            <Button
              danger
              icon={<PoweroffOutlined />}
              disabled={processAction !== null}
              loading={processAction === "stopping"}
              onClick={onStop}
            >
              关闭游戏
            </Button>
            <Button
              type="primary"
              icon={<ReloadOutlined />}
              disabled={processAction !== null}
              loading={processAction === "restarting"}
              onClick={onRestart}
            >
              重新启动
            </Button>
          </Space.Compact>
        ) : (
          <Space.Compact className="launch-action-group">
            <Button
              type="primary"
              loading={processAction === "launching"}
              disabled={processAction === "launching"}
              onClick={() => onLaunch(launchPreference)}
            >
              {processAction === "launching" ? "正在启动" : "启动游戏"}
            </Button>
            <Dropdown
              trigger={["click"]}
              disabled={processAction === "launching"}
              menu={{
                items: launchMenu,
                onClick: ({ key }) => key !== "remember" && onLaunch(key as LaunchPreference),
              }}
            >
              <Button
                type="primary"
                icon={<DownOutlined />}
                aria-label="选择启动方式"
                disabled={processAction === "launching"}
              />
            </Dropdown>
          </Space.Compact>
        )}
      </div>
    </header>
  );
}
