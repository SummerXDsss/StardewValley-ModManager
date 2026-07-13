import { Button, Checkbox, Dropdown, Popover, Space, Tooltip } from "antd";
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
import type { GameProcessStatus } from "../../types";
import { formatDisplayPath } from "../../utils/path";

export type GameProcessAction = "launching" | "stopping" | "restarting" | null;

interface TopbarProps {
  gamePath?: string;
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

  return (
    <header className="topbar">
      <Popover title="游戏目录" content={pathPopover} trigger="click" placement="bottomLeft">
        <button
          className={`path-chip ${gamePath ? "ready" : "missing"}`}
          type="button"
          aria-label={gamePath ? "游戏路径已获取，点击查看或修改" : "需要设置游戏路径"}
        >
          <FolderOpenOutlined />
          <span className="path-chip-label">{gamePath ? "游戏路径已获取" : "需要设置游戏路径"}</span>
          <DownOutlined className="path-chip-arrow" />
        </button>
      </Popover>
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
