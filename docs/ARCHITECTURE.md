# 技术架构

## 分层

- `src/`：React 视图、状态与 Tauri API 绑定；
- `src-tauri/src/commands.rs`：前端可调用的窄命令接口；
- `src-tauri/src/services/`：路径探测、清单扫描和安全文件操作；
- `src-tauri/src/providers/`：Nexus Mods 与 GitHub 的实时数据适配器、缓存和可信 URL 边界；后续加入 ModDrop 与 SMAPI。

前端不直接持有文件系统权限。所有路径进入 Rust 后先规范化，并验证目标位于已确认的游戏目录或应用缓存目录。

## SMAPI 安装、卸载与 GitHub 下载

- 安装、更新和卸载共用官方 SMAPI Release 解析、安全下载、受限解压与平台安装器执行链路；安装器参数以进程参数数组传递，Windows 隐藏控制台窗口并设置总超时；
- 操作前读取受管游戏状态，并按规范化可执行文件路径扫描同一目录的外部进程；从 Steam、桌面或终端启动的游戏也会阻止安装、更新或卸载，但不会被管理器终止；下载前与安装器执行前分别检查；
- 安装和更新完成后重新读取本地精确版本，卸载完成后重新确认 SMAPI 可执行文件与 DLL 已不可检测；安装器退出码只作为诊断信息，不能单独决定成功；
- 官方卸载器应保留用户 `Mods`，前端确认框仍提示先备份；后端不为完成卸载而递归删除 `Mods`；
- GitHub Release 元数据、资产清单与 `sha256` digest 始终直连 GitHub 官方 API，镜像设置不会改写这一信任来源；
- 下载方式为 `direct` 或 `custom`。自定义模式仅接受无凭据、端口、查询参数和片段的 HTTPS 前缀，并以“镜像前缀 + 官方资产 URL”构造下载地址；
- 镜像只可下载带 GitHub 官方 SHA-256 摘要的 SMAPI 资产，响应下载完成后仍与官方摘要比较；官方摘要缺失时明确拒绝镜像路径并要求切回直连，不静默降级或信任镜像响应。
- 镜像设置先写入同目录临时文件并同步，再执行平台原子替换；进程内读写串行化，并可恢复旧版本遗留的有效备份，避免崩溃窗口静默回退直连。

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
- `get_latest_smapi_release`：直连 GitHub 官方 API，返回最新稳定 Release、标准跨平台资产与官方摘要。
- `download_latest_smapi_installer`：按已保存的直连或镜像设置下载标准安装包，并将本地 SHA-256、大小和官方摘要核验状态写入结果。
- `install_latest_smapi`：下载并校验官方包，受限解压后调用当前平台安装器，并以安装后精确版本检测判断结果。
- `uninstall_smapi`：游戏未运行时调用官方安装器的卸载入口，并以卸载后重新检测判断结果；不主动删除用户 `Mods`。
- `get_github_download_settings`：返回 GitHub 下载方式与已保存的自定义镜像前缀。
- `save_github_download_settings`：校验并保存 `direct` 或 `custom` 设置；自定义前缀必须满足 HTTPS 与 URL 结构限制。
- `discover_mods`：合并 Nexus 公开趋势榜与 GitHub Search/Releases 数据。
- `open_remote_url`：只允许打开受信任上游域名的 HTTPS 链接。
- `get_nexus_auth_status` / `set_nexus_api_key` / `clear_nexus_api_key`：管理系统凭据库中的 Nexus Key，不向前端返回原文；
- `get_nexus_mod_details`：读取 Mod、文件组和全部版本；
- `download_nexus_file`：通过官方下载链接接口流式写入应用缓存。
- `get_ai_translation_settings` / `save_ai_translation_settings` / `clear_ai_translation_settings`：管理 OpenAI-compatible Base URL、Model ID 与系统凭据库中的 API Key。
- `list_ai_translation_models`：按当前表单 Base URL 与凭据读取受限大小的 `/models` 响应，去重后返回可选 Model ID；不保存配置。
- `test_ai_translation_connection`：按当前表单配置发送最小 Chat Completions 请求并返回短响应；不保存配置。
- `translate_mod`：使用已保存配置按《星露谷物语》语境翻译名称与简介，并严格解析 `name`/`summary` JSON。

AI provider 允许用户显式配置 HTTP 或 HTTPS；HTTP 的明文风险由前端持续提示。所有 AI 请求禁用重定向、限制连接/总超时与响应大小，错误文本会清理控制字符并遮盖 API Key。表单未填写新 Key 时，只有 Base URL 与已保存配置同源才可复用系统凭据。


## 后续接口

- `inspect_archive` / `install_archive`：临时展开、路径穿越检查、清单确认；
- `check_updates` / `apply_update`：上游适配、配置保护、原子替换。
