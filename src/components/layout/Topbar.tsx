import {
  Badge,
  Button,
  Checkbox,
  Menu,
  MenuDivider,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  Tooltip,
} from "@fluentui/react-components";
import {
  ArrowClockwise20Regular,
  ChevronDown16Regular,
  Copy16Regular,
  Edit20Regular,
  Flash20Regular,
  FolderOpen20Regular,
  Play20Regular,
  Power20Regular,
  WeatherMoon20Regular,
  WeatherSunny20Regular,
} from "@fluentui/react-icons";
import type { LaunchPreference, ResolvedTheme } from "../../hooks";
import { useAppUi } from "../shared";
import type { GameProcessStatus, SmapiStatus, SteamIdentity } from "../../types";
import { formatDisplayPath } from "../../utils/path";

export type GameProcessAction = "launching" | "stopping" | "restarting" | null;

interface TopbarProps {
  gamePath?: string;
  smapi?: SmapiStatus;
  smapiLoading: boolean;
  smapiError?: string;
  steamRunning: boolean;
  steamIdentity?: SteamIdentity;
  steamLoading: boolean;
  steamError?: string;
  resolvedTheme: ResolvedTheme;
  onToggleTheme: () => void;
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
  steamIdentity,
  steamLoading,
  steamError,
  resolvedTheme,
  onToggleTheme,
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
  const { notify } = useAppUi();
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
  const themeActionLabel = resolvedTheme === "dark" ? "切换到浅色模式" : "切换到深色模式";
  const identitySource = steamIdentity?.source === "registryActiveUser"
    ? "Steam 活动账号记录"
    : "Steam 最近登录记录";

  const copyValue = async (label: string, value: string) => {
    try {
      await navigator.clipboard.writeText(value);
      notify("success", `${label} 已复制`);
    } catch (error) {
      notify("error", `无法复制${label}`, String(error));
    }
  };

  const pathContent = gamePath ? (
    <div className="path-popover">
      <code>{displayPath}</code>
      <Button appearance="subtle" icon={<Edit20Regular />} onClick={onEditGamePath}>
        修改路径
      </Button>
    </div>
  ) : (
    <Button appearance="primary" icon={<FolderOpen20Regular />} onClick={onEditGamePath}>
      设置游戏路径
    </Button>
  );

  const steamContent = (
    <div className="steam-account-popover">
      <div className="steam-account-heading">
        <div>
          <strong>{steamIdentity?.personaName ?? steamIdentity?.accountName ?? "未找到本机账号"}</strong>
          {steamIdentity?.personaName && steamIdentity.accountName && (
            <small>{steamIdentity.accountName}</small>
          )}
        </div>
        <Badge
          appearance="tint"
          color={steamIdentity?.active ? "success" : "subtle"}
        >
          {steamIdentity ? (steamIdentity.active ? "本机活动账号" : "最近登录账号") : "未检测到"}
        </Badge>
      </div>
      {steamIdentity ? (
        <dl className="steam-identity-list">
          <div>
            <dt>SteamID64</dt>
            <dd><code>{steamIdentity.steamId64}</code></dd>
            <Tooltip content="复制 SteamID64" relationship="label">
              <Button
                appearance="subtle"
                size="small"
                icon={<Copy16Regular />}
                aria-label="复制 SteamID64"
                onClick={() => void copyValue("SteamID64", steamIdentity.steamId64)}
              />
            </Tooltip>
          </div>
          <div>
            <dt>好友码</dt>
            <dd><code>{steamIdentity.friendCode}</code></dd>
            <Tooltip content="复制好友码" relationship="label">
              <Button
                appearance="subtle"
                size="small"
                icon={<Copy16Regular />}
                aria-label="复制好友码"
                onClick={() => void copyValue("好友码", steamIdentity.friendCode)}
              />
            </Tooltip>
          </div>
        </dl>
      ) : (
        <p className="steam-account-empty">请先在本机 Steam 客户端登录一次，再重新打开此处。</p>
      )}
      <small className="steam-account-source">
        {steamIdentity ? identitySource : "未读取到 loginusers.vdf"}。这里只表示本机进程和账号记录，不代表 Steam 网络在线状态。
      </small>
    </div>
  );

  return (
    <header className="topbar">
      <div className="topbar-environment">
        <Popover positioning="below-start" withArrow>
          <PopoverTrigger disableButtonEnhancement>
            <Button
              appearance="subtle"
              className={`path-chip ${gamePath ? "ready" : "missing"}`}
              icon={<FolderOpen20Regular />}
              aria-label={gamePath ? "查看完整游戏路径" : "需要设置游戏路径"}
            >
              <span className="path-chip-label">{gamePath ? pathSummary : "未设置路径"}</span>
              <ChevronDown16Regular className="path-chip-arrow" />
            </Button>
          </PopoverTrigger>
          <PopoverSurface>{pathContent}</PopoverSurface>
        </Popover>
        <Tooltip content="修改游戏路径" relationship="label">
          <Button
            appearance="subtle"
            className="path-edit-button"
            icon={<Edit20Regular />}
            aria-label="修改游戏路径"
            onClick={onEditGamePath}
          />
        </Tooltip>
        <Popover positioning="below-start" withArrow>
          <PopoverTrigger disableButtonEnhancement>
            <Button
              appearance="subtle"
              className={`steam-runtime-status ${steamTone}`}
              aria-label={`${steamLabel}，查看本机 Steam 账号`}
            >
              <i aria-hidden="true" />
              <span className="steam-runtime-label">{steamLabel}</span>
              <ChevronDown16Regular className="steam-runtime-arrow" />
            </Button>
          </PopoverTrigger>
          <PopoverSurface aria-label="本机 Steam 账号">{steamContent}</PopoverSurface>
        </Popover>
        <Tooltip content={smapiError ? `SMAPI 状态获取失败：${smapiError}` : smapiLabel} relationship="description">
          <Badge appearance="tint" color={smapi?.installed ? "success" : "subtle"} className="smapi-version-tag">
            {smapiLabel}
          </Badge>
        </Tooltip>
        <Tooltip content={themeActionLabel} relationship="label">
          <Button
            appearance="subtle"
            className="theme-toggle"
            icon={resolvedTheme === "dark" ? <WeatherSunny20Regular /> : <WeatherMoon20Regular />}
            aria-label={themeActionLabel}
            onClick={onToggleTheme}
          />
        </Tooltip>
      </div>
      <div className="game-process-controls">
        <Tooltip content={monitorError ? `最近一次状态检查失败：${monitorError}` : statusDetail ?? statusLabel} relationship="description">
          <div className={`game-process-status ${statusTone}`} role="status" aria-live="polite">
            <i aria-hidden="true" />
            <span>
              <strong>{statusLabel}</strong>
              {statusDetail && <small>{statusDetail}</small>}
            </span>
          </div>
        </Tooltip>
        {showProcessControls ? (
          <div className="process-action-group">
            <Button
              appearance="secondary"
              className="danger-button-outline"
              icon={<Power20Regular />}
              disabled={processAction !== null}
              onClick={onStop}
            >
              <span className="process-action-label-full">关闭游戏</span>
              <span className="process-action-label-compact">关闭</span>
            </Button>
            <Button
              appearance="primary"
              icon={<ArrowClockwise20Regular />}
              disabled={processAction !== null}
              onClick={onRestart}
            >
              <span className="process-action-label-full">重新启动</span>
              <span className="process-action-label-compact">重启</span>
            </Button>
          </div>
        ) : (
          <div className="launch-action-group">
            <Button
              appearance="primary"
              icon={<Play20Regular />}
              disabled={processAction === "launching"}
              onClick={() => onLaunch(launchPreference)}
            >
              {processAction === "launching" ? "正在启动" : "启动游戏"}
            </Button>
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button
                  appearance="primary"
                  className="launch-menu-button"
                  icon={<ChevronDown16Regular />}
                  aria-label="选择启动方式"
                  disabled={processAction === "launching"}
                />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<Flash20Regular />} onClick={() => onLaunch("smapi")}>使用 SMAPI 启动</MenuItem>
                  <MenuItem icon={<Play20Regular />} onClick={() => onLaunch("vanilla")}>启动原版游戏</MenuItem>
                  <MenuItem icon={<FolderOpen20Regular />} onClick={() => onLaunch("profile")}>使用指定 Mods 目录</MenuItem>
                  <MenuDivider />
                  <MenuItem persistOnClick>
                    <Checkbox
                      checked={rememberLaunch}
                      label="记住上次选择"
                      onClick={(event) => event.stopPropagation()}
                      onChange={(_, data) => onRememberLaunchChange(Boolean(data.checked))}
                    />
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          </div>
        )}
      </div>
    </header>
  );
}
