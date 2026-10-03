# Development

官方 Desktop 与网页同时使用同一 Thread 的设置、现场边界和回退步骤见 [共享会话实施与运行](shared-session-implementation.md)。下面的常规真实 Codex 测试默认覆盖独立 stdio 模式；共享实测有单独的显式 manifest 开关。

## Prerequisites

- Windows 10/11 x64；
- .NET 8 SDK（允许更新 SDK 编译 `net8.0`）；
- Node.js 24；
- Docker Desktop（部署验证）；
- Codex Desktop 或可执行的 Codex CLI；
- PowerShell 7。
- Inno Setup 6（Release 安装包；可通过 `winget install JRSoftware.InnoSetup` 安装）。

## Build

Relay 消息类型以 `src/shared/CodexControl.Protocol/RelayMessageTypes.cs` 为唯一来源。修改后先生成并校验 Web 常量：

```powershell
& '.\tools\Sync-ProtocolMessageTypes.ps1'
& '.\tools\Sync-ProtocolMessageTypes.ps1' -Check
```

```powershell
dotnet build '.\tests\CodexControl.Agent.Tests\CodexControl.Agent.Tests.csproj' --configuration Release
dotnet build '.\tests\CodexControl.Relay.Tests\CodexControl.Relay.Tests.csproj' --configuration Release

Set-Location '.\src\web'
npm ci
npx playwright install webkit
npm run build
```

## Environment variables

| Variable | Component | Required | Meaning |
|---|---|---|---|
| `CODEX_CONTROL_CODEX_PATH` | Agent | optional | 显式 Codex CLI 路径；未设置时自动发现 PATH 或 Codex Desktop |
| `CODEX_CONTROL_RELAY_URL` | Agent | optional | Relay root `wss://...`; `--relay-url` takes the same role |
| `CODEX_CONTROL_DB` | Relay | production recommended | SQLite file path |
| `CODEX_CONTROL_PAIRING_SECRET` | Relay | production required | Server HMAC secret, at least 32 random characters |
| `CODEX_CONTROL_ALLOWED_ORIGINS` | Relay | production required | Comma-separated PWA origins |
| `CODEX_CONTROL_HTTP_PORT` | Compose | optional | Gateway HTTP host port, default 80 |
| `CODEX_CONTROL_HTTPS_PORT` | Compose | optional | Gateway HTTPS host port, default 443 |

## Tests

```powershell
dotnet run --project '.\tests\CodexControl.Agent.Tests\CodexControl.Agent.Tests.csproj'
dotnet run --project '.\tests\CodexControl.Relay.Tests\CodexControl.Relay.Tests.csproj'

Set-Location '.\src\web'
npm test
```

Playwright 使用本机 Edge channel 做 Chromium/Android 路径，并使用 Playwright WebKit + iPhone 15 profile 覆盖 Safari/WebKit 路径。系统宿主按 project token 生成独立一次性配对码，因此两种浏览器都运行真实 Relay/Web Crypto 配对与控制测试；全部测试串行，避免一次性状态竞态。浏览器确定性场景还覆盖 `model/list` 选项、批准等级透传、两个活动 Thread 分别 Steer、第三个 Thread 并发创建，以及新建后历史索引延迟的乐观回显/重试。

## Real Codex

产品 Agent 先检查 PATH；若独立 PowerShell 无法解析 Codex，则从当前用户 AppX Package Repository 选择最高版本 `OpenAI.Codex`。校验 WindowsApps 包路径后，把以下同版本文件原子暂存到 `%LOCALAPPDATA%\CodexControl\codex-runtimes\desktop\<包版本>`；第二次启动直接复用：

```text
codex.exe
codex-code-mode-host.exe
codex-command-runner.exe
codex-windows-sandbox-setup.exe
```

真实测试 Runner 不调用产品启动入口，因此测试时仍把同版本文件复制到临时目录并设置：

```text
CODEX_CONTROL_REAL_CODEX_PATH=<temp>\codex.exe
CODEX_CONTROL_RUN_REAL_TURNS=1
```

真实测试覆盖两组边界：审批测试创建 ephemeral Thread，使用随机 `out/approval-probe-*.txt` 作为审批目标并必须 Decline；远程会话测试创建一个 persisted Thread，验证历史列表、首个 Turn、恢复、第二个 Turn、分页能力探测、`thread/turns/list` 状态/时间和兼容回退，finally 只用 `thread/delete` 删除这个测试创建的 Thread。模型/批准选项映射、多活动 Turn 状态隔离和跨 Thread 调度使用确定性 Agent 测试；Markdown/本地图片/过程摘要、文件行数统计、事件驱动输出、无周期历史读取、滚动锚点、运行/未读/完成未读状态和版本显示使用确定性 Agent 与 Playwright 测试覆盖；Desktop 外部会话必须显示不可订阅边界。

## Relay Migration

```powershell
dotnet tool restore
dotnet tool run dotnet-ef migrations add <Name> `
  --project '.\src\relay\CodexControl.Relay.csproj' `
  --startup-project '.\src\relay\CodexControl.Relay.csproj' `
  --output-dir 'Persistence\Migrations'
```

不得使用 `EnsureCreated` 替代 Migration。

## One-hour soak

```powershell
& '.\tests\CodexControl.Agent.Tests\bin\Release\net8.0-windows\CodexControlAgentTests.exe' --soak 3600
```

循环覆盖：TUI initialize/list/close、Steer、Interrupt，以及每 60 轮 app-server/Proxy restart。

## Docker

```powershell
& "C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo -NoProfile -File '.\deploy\generate-dev-cert.ps1'
$env:CODEX_CONTROL_PAIRING_SECRET = '<development-secret>'
$env:CODEX_CONTROL_ALLOWED_ORIGINS = 'https://localhost:18443'
$env:CODEX_CONTROL_HTTP_PORT = '18080'
$env:CODEX_CONTROL_HTTPS_PORT = '18443'
docker compose -f '.\deploy\docker-compose.yml' up -d --build
```

验证结束：

```powershell
docker compose -f '.\deploy\docker-compose.yml' down -v
```

## Release

```powershell
& "C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo -NoProfile -File '.\tools\Build-Release.ps1'
```

若默认 `out/release` 中的旧 Agent 正在运行，使用经过脚本边界校验的独立目录，不强杀进程：

```powershell
& "C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo -NoProfile -File '.\tools\Build-Release.ps1' `
  -OutputRoot 'out\release-0.7.0'
```

输出：

```text
out/release/agent-win-x64/
out/release/installer/CodexControl-Setup-UNSIGNED.exe
out/release/relay/
out/release/web/
out/release/deploy/
out/release/SHA256SUMS
```

Agent/便携版的运行数据统一位于 `CodexControlAgent.exe` 同目录 `data\`。安装版默认位于
`%LOCALAPPDATA%\Programs\CodexControl`；覆盖升级保留 `data\`，主动卸载默认删除但允许保留。
未提供 `-SignToolCommand` 时，Release 明确生成 `UNSIGNED` 内部/测试安装包。

## Production artifact rollout

生产服务器使用不可变版本目录和稳定 Compose project name，避免源码构建上下文、证书或 `.env`
混入 Git：

1. 本机执行完整 `tools/Build-Release.ps1`，校验 `out/release/SHA256SUMS`。
2. 只打包 `out/release/relay`、`web`、`deploy` 和 `SHA256SUMS`，上传到新的
   `/opt/codex-control/releases/<version>-<commit>`；不得覆盖旧版本目录。
3. 从当前版本受控复制 `.env` 与 `certs/` 到新版本的 `deploy/`，不输出其正文；复用固定
   Compose project name `codex-control`，从而继续挂载 `codex-control_relay-data`。
4. 在新版本执行：

   ```bash
   docker compose -p codex-control \
     --env-file deploy/.env \
     -f deploy/docker-compose.artifacts.yml \
     config --quiet
   docker compose -p codex-control \
     --env-file deploy/.env \
     -f deploy/docker-compose.artifacts.yml \
     up -d --build --remove-orphans
   ```

   若镜像已单独构建并核对 tag/digest，切流时改用 `up -d --no-build --remove-orphans`，避免
   切流阶段再次访问外部软件源。现场临时 Dockerfile 只能基于已核验的同框架、同 UID 基础镜像，
   并必须在验收报告中明确记录，不能反向覆盖仓库中的通用 Dockerfile。

5. Relay/Web healthy 且 HTTPS `/healthz`、PWA、Service Worker 和 WSS 握手通过后，原子切换
   `/opt/codex-control/current`。失败时恢复旧 symlink，并用旧版本的同一 Compose project name
   重新执行 `up -d`；保留旧版本目录和 named volume，不执行 `down -v`。
6. 若 TLS 由 Certbot 自动续期，renewal hook 必须通过稳定的
   `/opt/codex-control/current/deploy` 定位 `.env` 和 `certs/`，复制证书后用
   `up -d --no-build --pull never gateway` 恢复网关；每次改变 release 目录结构都要复核 hook。

## Evidence boundary

- Build：只证明可编译；
- Fake：证明路由和状态，不证明真实 Codex；
- Real Codex：证明当前 CLI/API 环境，不证明其他电脑版本；
- Playwright：证明 Edge/Chromium 路径，不等同 iOS Safari 实机；
- Docker：证明本机 Linux Engine，不等同生产域名/证书/公网；
- Soak：证明测试负载稳定，不替代生产负载和网络故障演练。
