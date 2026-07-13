# 技术架构

## 分层

- `src/`：React 视图、状态与 Tauri API 绑定；
- `src-tauri/src/commands.rs`：前端可调用的窄命令接口；
- `src-tauri/src/services/`：路径探测、清单扫描和安全文件操作；
- `src-tauri/src/providers/`：Nexus Mods 与 GitHub 的实时数据适配器、缓存和可信 URL 边界；后续加入 ModDrop 与 SMAPI。

前端不直接持有文件系统权限。所有路径进入 Rust 后先规范化，并验证目标位于已确认的游戏目录或应用缓存目录。

## 游戏进程状态

- Rust 后端持有单实例受管进程状态，记录由 `launch_game` 创建的精确 PID、启动请求、启动时间和退出状态；
- 前端定时读取状态，用于显示运行、已退出或未运行，并只在受管进程存活时开放关闭与重启操作；
- 重启先等待被跟踪的进程树退出，再复用后端保存的启动目标、Mods 路径与附加参数创建新进程；
- Windows 将暂停创建的游戏进程先绑定到独立 Job Object 再恢复，并只终止该 Job；macOS 与 Linux 使用独立进程组，不使用 `taskkill`、`killall` 或模糊进程名匹配；
- 从 Steam、终端或其他工具外部启动的游戏没有受管 Job/进程组，不进入可关闭或可重启范围。

## 平台适配

- Windows：Steam、GOG、Xbox App，启动脚本自动加载 MSVC 与 Windows SDK；
- macOS：Steam、GOG 与用户 Applications，使用系统 `open` 打开目录和上游链接；
- Linux：原生 Steam、Flatpak、Snap 与 GOG，使用 `xdg-open` 打开目录和上游链接；
- 三个平台共用相同的 Rust 路径安全、Mod 扫描和 provider 逻辑；
- GitHub Actions 在 Windows、macOS 和 Ubuntu 上分别执行前端构建、Rust 格式检查、`cargo check` 与进程生命周期测试。

## 当前命令

- `get_dashboard`：探测环境并返回扫描结果；
- `scan_game_path`：验证指定目录并扫描；
- `set_mod_enabled`：通过目录重命名切换状态；
- `remove_mod`：移动到游戏目录下的管理器回收区。
- `launch_game`：校验目标后以参数数组启动原版游戏或 SMAPI。
- `get_game_process_status`：返回受管游戏进程的运行状态、PID、启动目标与启动时间。
- `stop_game`：仅终止后端持有的 Windows Job Object 或 Unix 进程组。
- `restart_game`：结束当前受管进程，并复用已保存的启动请求重新启动。
- `open_mod_folder`：校验 Mod 归属后调用系统文件管理器。
- `open_smapi_download`：仅打开固定的 SMAPI 官方下载地址。
- `discover_mods`：合并 Nexus 公开趋势榜与 GitHub Search/Releases 数据。
- `open_remote_url`：只允许打开受信任上游域名的 HTTPS 链接。
- `get_nexus_auth_status` / `set_nexus_api_key` / `clear_nexus_api_key`：管理系统凭据库中的 Nexus Key，不向前端返回原文；
- `get_nexus_mod_details`：读取 Mod、文件组和全部版本；
- `download_nexus_file`：通过官方下载链接接口流式写入应用缓存。


## 后续接口

- `inspect_archive` / `install_archive`：临时展开、路径穿越检查、清单确认；
- `check_updates` / `apply_update`：上游适配、配置保护、原子替换；
- `install_smapi`：下载官方包、校验来源、请求用户确认后启动安装器。
