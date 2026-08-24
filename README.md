# Codex Control

Codex Control 是一套自托管的 Codex Control Plane，用于从手机或另一台电脑观察并干预 Windows 上的 Managed Codex。

系统由三部分组成：

1. `CodexControlAgent.exe`：Windows 本地 Agent，持有 `codex app-server`；
2. `CodexControlRelay`：公网/内网 Relay，负责身份、配对、Presence 和路由；
3. Codex Control PWA：浏览器控制端。

项目不抓取 Codex UI、不模拟鼠标键盘、不托管 OpenAI API Key，也不修改用户现有 Model Provider 或 Endpoint。

## MVP 状态

MVP 代码与自动化闭环已经实现：

- `.NET 8` 独立 Windows Agent；
- 自动发现 Codex Desktop 内置运行时并按 Desktop 版本原子暂存、复用；
- app-server stdio JSON-RPC 与 localhost WebSocket Proxy；
- 真实 `codex --remote` TUI；
- Thread/Turn/Item/Approval 状态、AgentMessage Delta 与 Domain Event；
- 真实 `thread/list` 历史、`recencyAt` 任务顺序、项目/最近分区、能力探测式历史读取、动态模型/批准等级、项目内新建 Thread、恢复历史 Thread 并启动真实 Turn；
- Steer、Interrupt、Command/File Approval first-valid-response-wins；
- Windows DPAPI + ECDSA P-256 Device Identity；
- Controller Web Crypto + IndexedDB 非导出私钥；
- 六位码、三分钟 TTL、五次证明失败限制、一次性销毁；
- Challenge/Signature 长期认证与 replay 防护；
- Relay WSS、SQLite/EF Core Migration、Presence、Snapshot Cache、权限与审计元数据；
- React/Vite PWA：配对、设备列表、同身份多标签稳定连接、Desktop 风格项目/最近侧栏、多活动 Thread 切换、模型/批准等级选择、实时回复、项目内新建、固定 Composer、Steer、Interrupt、Approval、解除配对；
- 聊天正文安全解析 GFM Markdown，显示受控本地图片缩略图、Turn 耗时和默认折叠过程摘要；
- ChatGPT 风格的信息架构：桌面会话侧栏、中央消息流、底部上下文输入框和移动端抽屉；
- Docker Compose、nginx HTTPS/WSS、SQLite volume；
- Fake、真实 Relay、真实浏览器和真实 Codex 测试。

现场部署仍需提供：可信 TLS 证书、正式域名/反向代理、每台目标电脑安装的 Codex Desktop 或可执行 Codex CLI，以及真实多电脑/手机网络环境验收。

## 目录

```text
docs/                       架构、协议、安全、开发与验证报告
src/agent/                  .NET 8 Windows Agent
src/shared/                 Agent/Relay 共用协议与密码学编码
src/relay/                  .NET 8 Relay + EF Core SQLite
src/web/                    React/Vite PWA + Playwright
tests/                      Agent、Relay、系统宿主和长稳测试
deploy/                     Docker Compose、nginx、证书脚本
tools/                      握手探针与 Release 脚本
```

## 文档

- [architecture.md](docs/architecture.md) — 组件、数据流和状态机
- [protocol.md](docs/protocol.md) — Relay v2 wire contract
- [security.md](docs/security.md) — 威胁模型与生产清单
- [development.md](docs/development.md) — 构建、测试、Migration、Docker 和 Release
- [feasibility.md](docs/feasibility.md) — 自动化/真实环境证据与现场边界
- [agent-desktop-experience-design.md](docs/agent-desktop-experience-design.md) — 托盘、设置、配对 v2 与安装设计
- [CHANGELOG.md](CHANGELOG.md) — 版本变更

## 快速运行

### 1. 本地 Relay

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:CODEX_CONTROL_DB = 'D:\Data\CodexControl\relay.db'
$env:CODEX_CONTROL_PAIRING_SECRET = '<至少32位随机字符串>'
$env:CODEX_CONTROL_ALLOWED_ORIGINS = 'http://127.0.0.1:5173'
dotnet run --project '.\src\relay\CodexControl.Relay.csproj' -- --urls http://127.0.0.1:5080
```

### 2. PWA

```powershell
Set-Location '.\src\web'
npm ci
npm run dev
```

二维码会带入 Relay 根地址；手工开发连接使用 `ws://127.0.0.1:5080/ws/controller`。
非本机环境必须使用 `wss://`。

### 3. Agent、设置与配对

```powershell
& '.\out\release\agent-win-x64\CodexControlAgent.exe'
```

首次启动在设置窗口填写 Relay 根地址 `http://127.0.0.1:5080` 和电脑名称。正常启动静默常驻托盘，
设置、DPAPI 身份、日志和 Runtime 缓存统一放在 EXE 同目录 `data\`。默认自动选择 loopback 端口，
“打开 Codex 终端”会使用实际端口执行 `codex --remote`。

默认会优先使用 PATH 中的独立 Codex CLI。若独立 PowerShell 的 PATH 不包含 Codex，Agent 会从当前用户的 AppX Package Repository 查找最高版本 `OpenAI.Codex`，严格校验 WindowsApps 包路径，再把同版本的固定 Runtime 原子暂存到 `data\codex-runtimes\desktop` 后使用；不会附加 Desktop 已打开的会话。

点击“生成配对码”后显示本地二维码和六位码。手机先确认 Relay/电脑摘要，随后电脑必须在 60 秒内
选择“完整控制”“仅查看”或拒绝；Claim 本身不会授予权限。

Headless 兼容入口为 `CodexControlAgent.exe --headless [options]`。生产使用可信 HTTPS/WSS；
明文 HTTP/WS 只允许 localhost/loopback 开发。

配对后无需先打开本地 TUI：手机设备详情会直接读取电脑的 Codex 历史和当前 `model/list`。项目优先使用公开 `project/list/projectId`，当前 Desktop 未持久化项目时以真实工作目录兼容分组；Codex 自动生成的日期会话目录放到“最近”，不显示“＋”。任务按 `recencyAt` 排序。由 Agent 创建或恢复的多个 Thread 可同时运行并分别 Steer/Interrupt/Approval；Domain Events 实时合并 AgentMessage Delta，显示运行、未读和完成未读状态，完成时只做一次有界历史核对。Desktop 原生会话明确显示为外部只读会话，不轮询或伪造实时状态。

## Docker 部署

```powershell
Set-Location '.\deploy'
Copy-Item '.env.example' '.env'
# 编辑 .env，设置随机 Pairing Secret、正式 Origin 和端口
docker compose up -d --build
```

正式发布优先使用 `tools/Build-Release.ps1` 生成的 `out/release/relay`、`web` 和 `deploy`，
再通过 `deploy/docker-compose.artifacts.yml` 构建制品镜像。生产 `.env`、TLS 证书和 SQLite
named volume 必须在版本目录之外保留或从上一版本受控复制，不能进入 Git 或发布压缩包。

生产证书通过部署 Secret 提供到：

```text
deploy/certs/fullchain.pem
deploy/certs/privkey.pem
```

本地自签名证书：

```powershell
& "C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo -NoProfile -File '.\generate-dev-cert.ps1'
```

## 构建、测试与发布

```powershell
dotnet run --project '.\tests\CodexControl.Agent.Tests\CodexControl.Agent.Tests.csproj'
dotnet run --project '.\tests\CodexControl.Relay.Tests\CodexControl.Relay.Tests.csproj'

Set-Location '.\src\web'
npm ci
npx playwright install webkit
npm run build
npm test

Set-Location '..\..'
& "C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo -NoProfile -File '.\tools\Build-Release.ps1'
```

真实 Codex Turn/Approval 测试需要显式提供完整 Codex 运行时并设置：

```text
CODEX_CONTROL_REAL_CODEX_PATH
CODEX_CONTROL_RUN_REAL_TURNS=1
```

## 关键安全规则

- Agent private key 只以 Current User DPAPI 密文落盘；
- PWA private key 在 IndexedDB 中保存为不可导出 `CryptoKey`；
- Relay 只保存 public key、Pairing 和必要元数据；
- Pairing Code 数据库只保存带服务端 Secret 的 HMAC；
- 本地 Codex Proxy 只监听 `127.0.0.1`；
- Approval 不自动批准；
- Relay 不持久化完整 Prompt、Agent Response、Shell Output 或 Diff；
- 生产 Relay 只通过 HTTPS/WSS 暴露。

详细边界见 [security.md](docs/security.md) 与 [feasibility.md](docs/feasibility.md)。
