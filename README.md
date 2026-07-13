# Valley Steward

Valley Steward 是一个面向《星露谷物语》PC 玩家的轻量 Mod 管理器，采用 Tauri 2、Rust、React 和 Ant Design 构建。

项目目标是把游戏路径定位、SMAPI 检测、Mod 文件管理、上游发现和游戏启动整理成安全、可回退的桌面工作流，减少玩家直接操作 `Mods` 目录时的出错概率。

## 当前能力

### 游戏与 SMAPI

- Windows 通过注册表定位 Steam，再解析 `libraryfolders.vdf` 与 App 413150 清单发现非默认库；同时保留 GOG、Xbox App 与跨平台常见路径；
- 支持手动填写并验证游戏路径；
- 首次启动引导用户确认自动检测路径、SMAPI 状态与安装包；顶部默认隐藏完整路径，点击后可查看或修改；
- 检测 Stardew Valley 与 SMAPI 可执行文件，并从 DLL 版本资源或官方 Mod 清单读取 SMAPI 版本；
- 读取 `Pathoschild/SMAPI` 最新稳定 Release，严格选择标准跨平台安装包，限制大小并计算 SHA-256；GitHub 提供摘要时会进一步核验；
- 支持确认后一键安装或更新 SMAPI：受限解压官方 ZIP，直接调用当前平台安装器，并在结束后重新检测实际安装版本；
- 支持确认后一键卸载 SMAPI：同一游戏目录由管理器、Steam、桌面或终端启动时均拒绝操作，调用官方安装器后重新检测实际状态，不把安装器退出码单独视为成功；卸载保留用户 `Mods`，操作前仍建议备份；
- 设置页可选择 GitHub 直连、预置的 `https://gh-proxy.com/` 或自定义 HTTPS 镜像前缀；Release 元数据始终来自 GitHub 官方 API，镜像只用于带官方 SHA-256 摘要的 SMAPI 资产，下载后仍会核验摘要；
- 一键启动原版游戏或 SMAPI；
- 支持通过 `--mods-path` 启动指定 Mod 配置目录；
- SMAPI 启动参数提供官方单参数预设，同时保留按独立 argv 添加自定义参数的能力；
- 可选择“记住上次选择”，下次继续使用相同启动模式；
- 启动后持续监测由管理器创建的游戏进程，并显示运行状态；
- 运行期间可一键关闭或重启游戏；重启复用本次启动模式、Mods 路径和附加参数；
- SMAPI 未安装时打开官方获取页面。

### Mod 管理

- 递归扫描 `Mods` 目录中的 `manifest.json`；
- 展示名称、作者、版本、唯一 ID、依赖和健康状态；
- 搜索、筛选、启用和禁用 Mod；
- 打开 Mod 所在目录；
- 删除时先移动到 `.mod-manager-trash`，避免直接永久删除；
- 无效清单单独标记，不阻断其他 Mod 扫描。

### 发现 Mod

- Nexus Mods 官方公开趋势榜；
- 用户可从 Nexus Mods 官方设置页获取 Personal API Key；管理器会先调用官方端点验证，再写入并读回确认系统凭据库，应用凭据、SSO Token 与 JWT 不会被误当成个人 Key；
- 配置 Key 后合并 Nexus 趋势、最新更新和最新发布列表；
- 读取 Nexus Mod 详情、文件组、版本、类别和上传时间；
- 通过官方下载链接接口流式下载发布包到应用缓存；
- GitHub Search 与 Releases 官方 API；
- 聚合搜索、来源筛选、缩略图、版本和热度；
- GitHub Release 存在 `.zip` 或 `.7z` 发布包时提供直接下载入口；
- 支持用户配置 HTTP/HTTPS OpenAI-compatible Base URL、Model ID 和 API Key；可读取 `/models` 列表、发送最小测试请求，并一键翻译 Mod 名称与简介；
- 仅允许打开受信任上游域名的 HTTPS 链接；
- Rust provider 缓存上游结果 15 分钟，降低限流风险。

Nexus Mods 的趋势榜端点是公开接口，不需要 API Key。文件详情和下载使用用户自己的个人 API Key，并遵守 Nexus 账号权限与下载限制。

## 技术栈

| 层 | 技术 |
| --- | --- |
| 桌面容器 | Tauri 2 |
| 后端 | Rust stable、Serde、Reqwest、Keyring |
| 前端 | React 19、TypeScript、Ant Design 6 |
| 构建 | Vite 7、Cargo |
| 数据源 | Nexus Mods API v3、GitHub REST API |

## 环境要求

### Windows

- Node.js 20 或更高版本；
- Rust stable MSVC 工具链；
- Microsoft Visual C++ Build Tools 2022；
- Windows 10/11 SDK；
- Microsoft Edge WebView2 Runtime。

仓库中的 Tauri 包装脚本会自动加载 Build Tools 与 Windows SDK，无需手动打开 Developer PowerShell。

### macOS

- macOS 10.15 或更高版本；
- Node.js 20 或更高版本；
- Rust stable；
- Xcode Command Line Tools：

```bash
xcode-select --install
```

支持 Steam、GOG 和用户 `Applications` 目录下的游戏安装位置。

### Linux

- Node.js 20 或更高版本；
- Rust stable；
- WebKit2GTK 4.1 与 Tauri 系统依赖。

Ubuntu / Debian：

```bash
sudo apt-get update
sudo apt-get install -y \
  libwebkit2gtk-4.1-dev build-essential curl wget file \
  libxdo-dev libssl-dev libayatana-appindicator3-dev \
  librsvg2-dev patchelf
```

自动探测原生 Steam、Flatpak Steam、Snap Steam 与 GOG 的常见安装位置。

## 快速开始

安装依赖：

```bash
npm install
```

启动完整 Tauri 应用：

```bash
npm run tauri -- dev
```

仅启动浏览器预览：

```bash
npm run dev
```

浏览器预览会直接请求 Nexus Mods 和 GitHub 的公开 API；Tauri 应用则通过 Rust provider 请求、缓存并合并数据。

## 常用命令

```bash
# 前端类型检查与生产构建
npm run build

# 查看 Tauri 与本机工具链状态
npm run tauri -- info

# 构建桌面程序
npm run tauri -- build

# 构建独立标识的 Windows Smoke 变体（不会读写正式应用配置）
npm run tauri -- build --debug --no-bundle --config src-tauri/tauri.smoke.conf.json
```

Windows 下 Rust 后端检查需要加载 MSVC 环境。可以使用项目脚本进入 Tauri 构建流程，或在 Developer PowerShell 中执行：

```powershell
cargo check --manifest-path src-tauri/Cargo.toml
cargo test --manifest-path src-tauri/Cargo.toml --lib
```

## 项目结构

```text
.
|-- docs/                       # PRD 与技术架构
|-- scripts/tauri.mjs           # 跨平台 Tauri 启动包装器
|-- src/                        # React 界面、类型和 Tauri API 绑定
|-- src-tauri/
|   |-- icons/                  # 桌面与安装包图标
|   |-- src/
|   |   |-- providers/          # Nexus Mods、GitHub 数据适配器
|   |   |-- services/           # 游戏探测与 Mod 文件操作
|   |   |-- commands.rs         # Tauri 命令边界
|   |   `-- models.rs           # 前后端数据模型
|   |-- tauri.smoke.conf.json   # 隔离配置与 WebView 数据的 Smoke 构建覆盖
|   `-- tauri.conf.json
|-- package.json
`-- vite.config.ts
```

## 安全边界

- 前端不直接获得本地文件系统权限；
- Rust 后端会规范化路径并校验目标位于游戏目录或 `Mods` 目录；
- 启动参数通过进程参数数组传递，不拼接 Shell 命令；
- 游戏控制仅作用于管理器创建的 Windows Job Object 或 Unix 进程组，不按模糊进程名批量结束进程；
- 从 Steam、终端或其他工具外部启动的游戏不纳入当前会话的关闭与重启范围；
- 删除操作默认移动到管理器回收区；
- 上游链接限制为受信任域名和 HTTPS；
- API Key 由 Windows Credential Manager、macOS Keychain 或 Linux Secret Service 保存，前端无法读取原文；
- AI 翻译 API Key 同样进入系统凭据库；Base URL 支持 HTTP 与 HTTPS，HTTP 会在设置页提示 API Key 和请求内容以明文传输；模型读取、测试和翻译均禁止重定向并限制响应大小；
- SMAPI 安装只执行校验后的官方 Release，拒绝路径穿越、符号链接、特殊文件和超限压缩内容；Windows 安装器使用隐藏的新控制台并设有总超时；
- SMAPI 卸载同样只调用已校验的官方安装器；修改前按规范化可执行文件路径检测受管与外部游戏进程，检测到运行即拒绝执行，完成后以重新检测结果为准；用户 `Mods` 不主动删除，但操作前仍建议备份；
- GitHub 镜像只接受无凭据、端口、查询参数和片段的 HTTPS 前缀；官方 Release 元数据与摘要不经过镜像，缺少官方 SHA-256 摘要时拒绝镜像下载并提示切回直连；
- API Key、Token、游戏路径和日志不会提交到仓库；
- `.env` 与本地密钥文件已加入 Git 忽略规则。

## 当前限制

- Nexus V3 没有面向玩家的全文搜索端点，应用会聚合官方趋势、最新更新和最新发布列表后在本地搜索；
- Nexus 直接下载受账号等级、作者设置和站点权限限制，接口拒绝时不会绕过；
- ModDrop 暂无已接入的稳定公开 API；
- 本地 Mod 压缩包安装与更新回滚仍处于后续迭代；
- 浏览器预览无法执行本地文件操作，完整功能需要运行 Tauri 应用。

## 文档

- [产品需求文档](docs/PRD.md)
- [技术架构](docs/ARCHITECTURE.md)
- [星露谷物语 Wiki：Mod 使用指南](https://zh.stardewvalleywiki.com/%E6%A8%A1%E7%BB%84:%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97/%E5%85%A5%E9%97%A8)

## 作者

- SummerXDsss
- 3104391686@qq.com
- [GitHub](https://github.com/SummerXDsss)

## 许可证

当前仓库尚未选择开源许可证。在添加许可证前，默认保留所有权利。
