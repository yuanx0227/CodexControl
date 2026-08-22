# Development

## Prerequisites

- Windows 10/11 x64；
- .NET 8 SDK（允许更新 SDK 编译 `net8.0`）；
- Node.js 24；
- Docker Desktop（部署验证）；
- Codex Desktop 或可执行的 Codex CLI；
- PowerShell 7。

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

Playwright 使用本机 Edge channel 做 Chromium/Android 路径，并使用 Playwright WebKit + iPhone 15 profile 覆盖 Safari/WebKit 路径。系统宿主按 project token 生成独立一次性配对码，因此两种浏览器都运行真实 Relay/Web Crypto 配对与控制测试；全部测试串行，避免一次性状态竞态。

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

真实测试覆盖两组边界：审批测试创建 ephemeral Thread，使用随机 `out/approval-probe-*.txt` 作为审批目标并必须 Decline；远程会话测试创建一个 persisted Thread，验证历史列表、首个 Turn、恢复、第二个 Turn及 `thread/read` 返回的用户/助手消息，finally 只用 `thread/delete` 删除这个测试创建的 Thread。

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
& '.\tests\CodexControl.Agent.Tests\bin\Release\net8.0\CodexControlAgentTests.exe' --soak 3600
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

输出：

```text
out/release/agent-win-x64/
out/release/relay/
out/release/web/
out/release/deploy/
out/release/SHA256SUMS
```

## Evidence boundary

- Build：只证明可编译；
- Fake：证明路由和状态，不证明真实 Codex；
- Real Codex：证明当前 CLI/API 环境，不证明其他电脑版本；
- Playwright：证明 Edge/Chromium 路径，不等同 iOS Safari 实机；
- Docker：证明本机 Linux Engine，不等同生产域名/证书/公网；
- Soak：证明测试负载稳定，不替代生产负载和网络故障演练。
