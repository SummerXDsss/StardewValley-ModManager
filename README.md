# Valley Steward

Valley Steward 是一个面向《星露谷物语》PC 玩家的轻量 Mod 管理器。Windows 客户端正在迁移到原生 WinUI 3 / .NET 8，macOS 与 Linux 继续使用 Tauri 2、Rust、React 和 Fluent UI React v9。

项目目标是把游戏路径定位、SMAPI 检测、Mod 文件管理、上游发现和游戏启动整理成安全、可回退的桌面工作流，减少玩家直接操作 `Mods` 目录时的出错概率。

## 客户端路线

- **Windows**：`ValleySteward.sln` 与 `src-winui/` 是新的原生客户端。它使用 WinUI 3、Mica、`NavigationView` 和自定义标题栏，不通过 WebView 承载 React；当前已经接入真实的注册表/Steam 游戏路径、Steam 本机账号、SMAPI 版本、Mod 扫描与文件操作、游戏启停监视，以及 Nexus/GitHub 并行搜索。
- **macOS / Linux**：现有 `src/` 与 `src-tauri/` 继续提供完整跨平台客户端。
- WinUI 客户端已经迁入 SMAPI 安装/卸载、Nexus 凭据与下载、AI 翻译、GitHub 镜像和真实上游浏览；Windows 日常使用不依赖旧 Tauri WebView 客户端。

## 当前能力

### 界面与可用性

- 支持浅色与深色模式；首次启动跟随系统偏好，顶栏可一键切换并记住用户选择；
- Windows 原生客户端提供 Windows 11 蓝、经典蓝、XP 橄榄、Zune 棕橙、Windows 紫红与石墨中性六套强调色，切换后即时生效并持久化；
- 使用 Fluent UI React v9 完整实现 Windows 风格导航、表格、输入控件、Popover、Drawer、Dialog 与 Toast；主操作采用系统蓝色，中性表面承载内容，绿色只表示成功状态；
- 中文、英文与等宽代码内容提供 Windows、macOS、Linux 字体回退，长路径、URL 和 Mod 简介支持换行、截断或按需展开；
- 默认窗口与最小窗口下侧栏、顶栏和内容区独立布局，表格只在自身范围内横向滚动。

### 游戏与 SMAPI

- Windows 通过注册表定位 Steam，再解析 `libraryfolders.vdf` 与 App 413150 清单发现非默认库；同时保留 GOG、Xbox App 与跨平台常见路径；
- 支持手动填写并验证游戏路径；
- 自动清理 Windows `\\?\` 扩展路径前缀，并迁移旧配置，界面、配置和官方安装器参数只使用普通盘符或 UNC 路径；
- 首次启动引导用户确认自动检测路径、SMAPI 状态与安装包；顶部默认隐藏完整路径，点击后可查看或修改；
- 检测 Stardew Valley 与 SMAPI 可执行文件，并从 DLL 版本资源或官方 Mod 清单读取 SMAPI 版本；
- 读取 `Pathoschild/SMAPI` 最新稳定 Release，严格选择标准跨平台安装包，限制大小并计算 SHA-256；GitHub 提供摘要时会进一步核验；
- 支持确认后一键安装或更新 SMAPI：受限解压官方 ZIP，直接调用当前平台安装器，并在结束后重新检测实际安装版本；Windows 为官方安装器保留真实但隐藏的控制台缓冲区，兼容其 `Console.Clear()`，同时不显示黑色控制台窗口；
- 支持确认后一键卸载 SMAPI：同一游戏目录由管理器、Steam、桌面或终端启动时均拒绝操作，调用官方安装器后重新检测实际状态，不把安装器退出码单独视为成功；卸载保留用户 `Mods`，操作前仍建议备份；
- 设置页可选择 GitHub 直连、预置的 `https://gh-proxy.com/` 或自定义 HTTPS 镜像前缀；Release 元数据始终来自 GitHub 官方 API，镜像只用于带官方 SHA-256 摘要的 SMAPI 资产，下载后仍会核验摘要；
- 一键启动原版游戏或 SMAPI；
- 支持通过 `--mods-path` 启动指定 Mod 配置目录；
- SMAPI 启动参数提供官方单参数预设，同时保留按独立 argv 添加自定义参数的能力；
- 可选择“记住上次选择”，下次继续使用相同启动模式；
- 启动后持续监测由管理器创建的游戏进程，并显示运行状态；
- 运行期间可一键关闭或重启游戏；重启复用本次启动模式、Mods 路径和附加参数；
- 顶部状态栏集中显示游戏路径摘要与编辑入口、Steam 进程状态、SMAPI 实际版本和游戏启停操作；点击 Steam 状态可查看并复制本机 SteamID64 与数字好友码，账号卡左侧优先显示 Steam 本机头像缓存，缺失时回退为默认用户图标；
- Windows 优先读取 `ActiveProcess\ActiveUser`，注册表缺失时与 macOS/Linux 一样回退到 Steam `config/loginusers.vdf` 的最近登录记录；界面明确区分本机记录，不把它描述成 Steam 网络在线状态；
- SMAPI 未安装时打开官方获取页面。

### Mod 管理

- 递归扫描 `Mods` 目录中的 `manifest.json`；
- 展示名称、作者、版本、唯一 ID、依赖和健康状态；
- 搜索、筛选、启用和禁用 Mod；
- 打开 Mod 所在目录；
- 支持选择或拖入单个 `.zip` 安装包；安装前递归识别包内 `manifest.json`，展示 Mod、依赖、覆盖目标与 XNB 风险；
- 安装和更新先写入同卷 staging，再原子替换目标目录；失败时逆序回滚，默认保留 `config.json` 与管理器翻译 sidecar；
- 解析 `UpdateKeys` 并并行检查 Nexus 与 GitHub Releases；只对明确匹配的 Nexus 主文件或唯一可信 GitHub ZIP 开放单项一键更新；批量检查后可查看全部更新项，安全项默认勾选、不可自动更新项置灰并说明原因，用户可取消任意勾选后启动失败隔离的串行更新队列；
- 删除时先移动到 `.mod-manager-trash`，支持查看、恢复和清空管理器回收区；
- 概览页持久记录安装、更新、启停、回收区、下载、SMAPI 与游戏启动操作；历史记录采用原子写入并限制数量与文件大小，自动隐藏凭据、授权参数和本机完整路径，可由用户一键清空；
- 已安装 Mod 的名称与简介可逐项并行翻译，译文按 Mod 目录写入管理器专用 sidecar，不修改第三方 `manifest.json`；
- 简介默认收起为两行，可按项展开或收起，避免窗口缩放时列表高度跳动；
- 无效清单单独标记，不阻断其他 Mod 扫描。
- 兼容 SMAPI 清单中实际存在的 `UniqueID` 与 `UniqueId` 两种字段形式，避免把合法的内置 Mod 误标为清单错误。

### 发现 Mod

- Nexus Mods 官方公开趋势榜；
- 用户可从 Nexus Mods 官方设置页获取 Personal API Key；管理器会先调用官方端点验证，再写入并读回确认系统凭据库，应用凭据、SSO Token 与 JWT 不会被误当成个人 Key；
- 配置 Key 后为发现页补充 Nexus 趋势、最新更新和最新发布列表；
- 读取 Nexus Mod 详情、文件组、版本、类别和上传时间；
- 通过官方下载链接接口流式下载发布包到应用缓存；
- 远程 Mod 列表直接提供“下载并安装”：Nexus 仅自动选择唯一活动主文件，GitHub 仅自动选择最新稳定 Release 中的唯一可信 ZIP；存在歧义时转到文件列表或 Releases 由用户确认；
- 首次设置默认勾选安装 Nexus `41846` 推荐 Mod；它仍使用用户自己的 Nexus API Key、唯一主文件判断和统一安装预检，用户可在首次弹窗中取消；
- 远程 Mod 列表加载 Nexus `pictureUrl` / `thumbnailUrl` 与 GitHub owner avatar 作为封面缩略图；无可信 HTTPS 图片时显示本地图标占位；
- GitHub Search 与 Releases 官方 API，可从搜索框向 GitHub 发起真实仓库搜索；
- Nexus 搜索使用官方 v2 GraphQL，按名称、描述、作者和上传者发起真实关键词搜索，无需 API Key；
- 聚合搜索、来源筛选、缩略图、版本和热度；简介默认最多两行，仅在实际被截断时显示展开按钮；
- GitHub Release 存在唯一、可信的 `.zip` 发布包时可用于 UpdateKeys 自动更新；
- 分享大厅固定连接 `http://x-svalley-api.summercn.cn`；用户可发布自己的公开 Mod 列表，后端生成 10 位字母数字分享码并记录上传 IP，客户端只展示形如 `1.*.*.111` 的脱敏 IP；
- 分享大厅展示世界各地用户上传的电脑名称、Mod 数量、最多 10 个封面缩略图，并为 Nexus、GitHub 等已知来源显示原始页面；GitHub 来源会额外标记 Releases 链接；
- 支持用户配置 HTTP/HTTPS OpenAI-compatible Base URL、Model ID 和 API Key；可读取 `/models` 列表并直接选择模型、发送最小测试请求，并一键翻译 Mod 名称与简介；
- 翻译请求固定包含“适配游戏 星露谷物语”的上下文；远程与已安装 Mod 可逐项并行翻译，互不阻塞；
- 设置页展示规范化后的请求地址、脱敏请求内容、响应摘要与响应内容，便于诊断兼容接口；
- Nexus 下载会把当前翻译结果写入下载缓存旁的管理器专用 sidecar，供后续安装流程复用；
- 支持粘贴 `nxm://` 链接；授权参数只用于本次请求，不展示或写入日志，下载完成后直接进入统一安装预检；
- 仅允许打开受信任上游域名的 HTTPS 链接；
- Rust provider 缓存上游结果 15 分钟，降低限流风险。

Nexus Mods 的趋势榜与 v2 GraphQL 搜索不需要 API Key。文件详情和 API 直链下载使用用户自己的 Personal API Key，并遵守 Nexus 账号等级、作者设置与站点权限；直链被拒绝时可打开官方文件页继续。

## 技术栈

| 层 | 技术 |
| --- | --- |
| Windows 客户端 | WinUI 3、.NET 8、Windows App SDK 1.8 |
| 分享后端 | ASP.NET Core Minimal API、JSON 文件存储 |
| macOS / Linux 客户端 | Tauri 2、React 19、Fluent UI React v9 |
| 跨平台后端 | Rust stable、Serde、Reqwest、Keyring |
| 构建 | `dotnet`、Vite 7、Cargo |
| 数据源 | Nexus Mods v1 REST / v2 GraphQL、GitHub REST API |

## 环境要求

### Windows

- .NET 8 SDK；
- Windows 10/11 SDK；
- Windows 11 推荐使用；应用按 x64 自包含方式构建，不要求目标机器预装 .NET Runtime。

仅在维护旧版 Windows Tauri 客户端时，才需要 Node.js、Rust MSVC、Visual C++ Build Tools 与 WebView2 Runtime。

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

### Windows 原生客户端

```powershell
dotnet restore ValleySteward.sln --configfile NuGet.Config --runtime win-x64
dotnet build ValleySteward.sln -c Debug --no-restore
& .\src-winui\ValleySteward.WinUI\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\ValleySteward.WinUI.exe

# 生成无需预装 .NET 的 Release 目录
dotnet publish .\src-winui\ValleySteward.WinUI\ValleySteward.WinUI.csproj `
  -c Release --no-restore -o .\output\winui-native
```

运行不修改真实游戏目录的服务冒烟测试：

```powershell
& .\src-winui\ValleySteward.WinUI.Smoke\bin\Debug\net8.0-windows10.0.19041.0\win-x64\ValleySteward.WinUI.Smoke.exe
```

日常 Smoke 只使用临时目录与普通测试 ZIP，不复制或启动命令解释器。构造路径穿越、符号链接和伪安装器的对抗性 fixture 作为显式 opt-in 源码保留，不进入普通本机测试程序集。

### 分享后端极速部署

客户端固定请求 `http://x-svalley-api.summercn.cn`。公开发布接口内置轻量 IP 限流：同一上传 IP 默认每 10 分钟最多发布 12 次，超出后返回 `429` 与 `Retry-After`。

#### Docker 部署（推荐）

服务器需要先安装 Docker Engine 与 Docker Compose 插件。仓库已经包含 `docker-compose.yml` 和分享后端专用 `Dockerfile`，不需要服务器预装 .NET Runtime。

首次上线：

```bash
git clone https://github.com/SummerXDsss/StardewValley-ModManager.git
cd StardewValley-ModManager
docker compose up -d --build share-server
```

启动后检查：

```bash
docker compose ps
curl http://127.0.0.1:5088/health
```

运行日志：

```bash
docker compose logs -f share-server
```

Docker 默认只监听宿主机本机回环地址 `127.0.0.1:5088`，分享数据持久化在 `./data/share-server/shares.json`。这个目录已加入 `.gitignore`，可以安全备份但不要提交。不要把后端容器端口直接暴露到公网；公网入口应统一走 Nginx，这样 `X-Real-IP` 与 `X-Forwarded-*` 头才可信。

配置 Nginx HTTP 反代：

```bash
sudo tee /etc/nginx/conf.d/x-svalley-api.conf >/dev/null <<'EOF'
server {
    listen 80;
    server_name x-svalley-api.summercn.cn;

    location / {
        proxy_pass http://127.0.0.1:5088;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
EOF
sudo nginx -t
sudo systemctl reload nginx
curl http://x-svalley-api.summercn.cn/health
```

DNS 里给 `x-svalley-api.summercn.cn` 添加 A 记录，指向服务器公网 IP。解析生效后，客户端的“分享大厅”会固定请求这个域名。

更新后端：

```bash
git pull
docker compose up -d --build share-server
curl http://127.0.0.1:5088/health
curl http://127.0.0.1:5088/ | grep my-shares
curl http://x-svalley-api.summercn.cn/ | grep my-shares
```

如果 `/health` 正常但 `grep my-shares` 没有输出，说明容器仍在跑旧镜像或服务器还没拉到包含“我的分享 / IP 认领 / 删除分享”接口的新提交。重新执行 `git pull && docker compose up -d --build share-server`，再用 `docker compose logs --tail=80 share-server` 看启动日志。

备份数据：

```bash
mkdir -p backups
cp data/share-server/shares.json "backups/shares-$(date +%Y%m%d-%H%M%S).json"
```

重启、停止与删除容器：

```bash
docker compose restart share-server
docker compose stop share-server
docker compose down
```

排错：

- `curl http://127.0.0.1:5088/health` 不通：先看 `docker compose ps` 和 `docker compose logs share-server`。
- 端口被占用：修改 `docker-compose.yml` 里的宿主机端口，例如 `"127.0.0.1:5099:5088"`，同时把 Nginx `proxy_pass` 改成 `http://127.0.0.1:5099`。
- 分享数据丢失：检查 `./data/share-server/shares.json` 是否存在，并确认 compose 里的 volume 仍然是 `./data/share-server:/data`。

#### 手动 systemd 部署（备选）

如果不使用 Docker，服务器需要 .NET 8 ASP.NET Core Runtime、Nginx 和一个 HTTP 反代。

Ubuntu / Debian 服务器先安装运行库：

```bash
sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-8.0 nginx rsync
```

本机发布：

```powershell
dotnet publish .\src-winui\ValleySteward.ShareServer\ValleySteward.ShareServer.csproj `
  -c Release -o .\output\share-server
```

如果服务器不想单独安装 .NET 运行库，也可以发布自包含版本：

```powershell
dotnet publish .\src-winui\ValleySteward.ShareServer\ValleySteward.ShareServer.csproj `
  -c Release -r linux-x64 --self-contained true -o .\output\share-server-linux-x64
```

服务器快速上线：

```bash
sudo mkdir -p /opt/valley-steward-share /var/lib/valley-steward-share
sudo rsync -a ./output/share-server/ /opt/valley-steward-share/
sudo tee /etc/systemd/system/valley-steward-share.service >/dev/null <<'EOF'
[Unit]
Description=Valley Steward Share API
After=network-online.target

[Service]
WorkingDirectory=/opt/valley-steward-share
ExecStart=/opt/valley-steward-share/ValleySteward.ShareServer
Environment=ASPNETCORE_URLS=http://127.0.0.1:5088
Environment=VALLEY_STEWARD_SHARE_STORE=/var/lib/valley-steward-share/shares.json
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
EOF
sudo systemctl daemon-reload
sudo systemctl enable --now valley-steward-share
```

Nginx HTTP 配置：

```nginx
server {
    listen 80;
    server_name x-svalley-api.summercn.cn;

    location / {
        proxy_pass http://127.0.0.1:5088;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

DNS 里把 `x-svalley-api.summercn.cn` 解析到服务器公网 IP，然后执行：

```bash
sudo nginx -t
sudo systemctl reload nginx
curl http://x-svalley-api.summercn.cn/health
```

### Tauri 客户端（macOS / Linux）

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
|-- ValleySteward.sln           # Windows 原生客户端解决方案
|-- NuGet.Config                # WinUI 官方 NuGet 源与仓库内缓存配置
|-- src-winui/
|   |-- ValleySteward.WinUI/    # WinUI 3 窗口、页面、服务与 ViewModel
|   |-- ValleySteward.ShareServer/ # 分享大厅 HTTP Minimal API
|   `-- ValleySteward.WinUI.Smoke/ # 临时目录服务冒烟测试
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
- Rust 后端与 WinUI 服务都会规范化路径并校验目标位于游戏目录或 `Mods` 目录；
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
- Mod ZIP 安装拒绝路径穿越、绝对路径、设备名、大小写碰撞、符号链接、重解析点、特殊文件和压缩炸弹；预检后安装前会重新核验归档 SHA-256；
- GitHub Mod 更新仅接受与 UpdateKey 仓库、Release 标签和文件名绑定的官方 ZIP；镜像下载必须具备官方 SHA-256，Nexus 更新继续遵守用户账号与作者权限；
- API Key、Token、游戏路径和日志不会提交到仓库；
- SteamID64、好友码与缓存显示名只从本机 Steam 配置读取并在状态弹层展示，不写入管理器配置，也不会发送到上游服务；
- `.env` 与本地密钥文件已加入 Git 忽略规则。

## 当前限制

- Nexus 搜索当前展示官方 GraphQL 返回的前 24 个匹配结果；
- Nexus API 直链下载受账号等级、作者设置和站点权限限制，接口拒绝时不会绕过，可转到官方文件页手动下载；
- ModDrop 暂无已接入的稳定公开 API；
- Windows 原生客户端当前只安装 `.zip`；`.7z` 尚未接入安全解压链路；
- 浏览器预览无法执行本地文件操作，Windows 完整功能需要运行 WinUI 应用，macOS/Linux 完整功能需要运行 Tauri 应用。

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
