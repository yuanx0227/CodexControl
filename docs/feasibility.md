# MVP 可行性与验收报告

日期：2026-08-22（Asia/Shanghai）

## 1. 结论

Codex Control 的 MVP 代码、自动化协议闭环、真实 Codex 控制闭环和容器部署链已经实现。

已真实验证的核心链：

```text
PWA
  -> Relay permission/routing
  -> Agent RemoteControlDispatcher
  -> codex app-server
  -> active Turn

Codex Event / Approval
  -> Agent Domain Event
  -> Relay
  -> PWA
```

### 1.1 2026-08-23 桌面体验与配对 v2 增量

已实现并在当前 Windows 环境验证：

- 同一 `CodexControlAgent.exe` 的 WinForms 托盘、四页设置、GUI/Headless 双模式和每用户单实例；
- EXE 同目录 `data\` 的设置、last-good、DPAPI 身份、日志、Runtime 缓存和离线撤销队列；
- Runtime Coordinator、动态 loopback 端口、一键本地 TUI、暂停远程访问和安全重启；
- 本地二维码、六位码备用、PWA Relay 摘要、Controller 名称、60 秒电脑确认；
- Pairing v2 Pending、Full/ViewOnly、Pairing Alias、权限更新、Device 撤销和 Ready Gate；
- EF Migration `PairingApprovalV2`，按用户确认清理旧测试 Pairing；
- Inno Setup 当前用户安装、两套 .NET 8 Runtime 检测、覆盖升级保留 `data\`、卸载默认删除；
- 内部/测试安装包 `CodexControl-Setup-UNSIGNED.exe` 和 SHA-256 清单。

当前增量证据：Agent 自动化 11 通过，Relay 自动化 7 通过，Playwright 32 通过，
真实 Codex 3 通过；本机静默安装、覆盖升级数据保留、Headless 输出和卸载删除通过。

0.5.1 另外验证安全 GFM Markdown、本地图片缩略图与路径脱敏、历史/实时 Turn 耗时、
默认折叠过程摘要，以及已认证但 Pairing 被清除的 Controller 无需清站点数据即可重新配对。

0.5.2 增加每文件及 Turn 总计的新增/删除行数，并以 WebSocket 增量加活动 Turn 轻量追平解决
漏事件后必须手工刷新会话的问题；这些是确定性 Agent/PWA 证据，真实移动弱网仍属于现场边界。

0.5.3 进一步验证独立 Agent 为 Idle、但所选 Thread 正由 Codex Desktop 运行的场景：PWA 只读追平
公共历史，显示运行状态和动态已运行时间；它不证明或实现对 Desktop 所有 Turn 的远程 Steer/停止。

0.5.4 覆盖 PWA 已打开后 Desktop 才启动 Turn 的时序，防止初始时钟早于 `startedAt` 导致永久 Idle。

0.6.0 撤销跨 app-server 状态推断和完整历史轮询：只有 Agent 托管 Thread 使用事件驱动实时状态；
Desktop 原生 Thread 明确为外部只读。新增滚动锚点、运行/未读/完成未读标志与版本展示验证。

仍未证明：独立 Windows 10 与 Windows 11 干净机矩阵、缺少 Runtime 时真实 UAC 补齐、
真实手机扫码、正式签名/SmartScreen、生产域名/证书和企业网络。

本机证据不等于生产现场验收。正式上线仍需可信 CA 证书、正式域名、多台实体开发电脑、Android/iOS 实机和企业网络环境测试。

## 2. 固定基线

### Agent / Relay

```text
Target Framework: net8.0
EF Core SQLite: 8.0.30
Windows: x64 Agent
Relay: Linux container / ASP.NET Core 8
```

### Web

```text
React: 19.2.8
Vite: 8.2.2
TypeScript: 7.0.2
Playwright: 1.62.1
```

### Codex

```text
codex-cli 0.149.0-alpha.4.1
OpenAI.Codex_26.818.5229.0_x64
```

当前 TUI 在 `thread/start` 使用实验字段 `runtimeWorkspaceRoots`，因此 Agent 的 app-server 握手必须显式声明 `experimentalApi=true`。升级 Codex 后必须重新生成 Schema 并复跑真实 TUI/Turn 测试。

## 3. Agent 测试

快速测试：

```text
PASS JsonRpcProtocol
PASS AgentOptions_SecureRelay
PASS CodexStateManager
PASS CodexDesktopRuntimeResolver
PASS CodexExecutableProbe
PASS AppServerBridge_LocalWsProxy
PASS RemoteControl_ApprovalArbitration
PASS AgentRelay_DPAPI_Authentication_Pairing
```

本机 Codex Desktop 兼容验证：

```text
Desktop package: OpenAI.Codex_26.818.5229.0_x64__2p2nqsd0c76g0
first launch: STAGED
second launch: REUSED
four runtime executable SHA-256 comparisons: PASS
capability probe: --remote=true, app-server stdio=true
PATH without Codex: AppX registry fallback STAGED / PASS
PATH with only WindowsApps alias: AppX registry fallback REUSED / PASS
```

真实 Codex：

```text
PASS RealCodex_AppServerBridge
PASS RealCodex_Steer_Approval_Interrupt
PASS RealCodex_History_Create_Resume
```

真实控制测试使用完整同版本 Codex 运行时：

```text
thread/start (ephemeral)
turn/start
真实 Command Approval Request
turn/steer while approval pending
remote decline
first turn terminal
second turn/start
turn/interrupt
turn/completed status=interrupted
```

随机测试文件审批被拒绝，文件未创建。

真实远程会话测试另外执行：

```text
thread/list
thread/start (persisted)
turn/start
thread/list contains new appServer thread
thread/resume
second turn/start
thread/delete test-created thread
```

## 4. Relay 测试

```text
PASS Relay_Health_Migration
PASS Relay_Auth_Pairing_Routing_Reconnect
PASS Relay_Expiry_AttemptLimit_ChallengeReplay
PASS Relay_OneController_ThreeDevices_Isolation
PASS Relay_Persistence_NoPlaintextCode
```

覆盖：

- Device TOFU registration + ECDSA challenge；
- Controller Web identity；
- 正确/错误/过期 Pairing；
- 五次错误 Proof；
- Challenge replay；
- 错误 Signature；
- Presence offline/online；
- Snapshot 缓存；
- Control 往返；
- Request ID 幂等/冲突；
- forged Control Result 拒绝；
- revoke 后权限拒绝；
- 一个 Controller 三台 Device；
- 同一 Controller 身份多标签并存；
- 未配对 Controller 零数据；
- SQLite Migration 与 Audit。

## 5. PWA 与系统测试

Playwright mobile Edge/Chromium + iPhone WebKit：

```text
PASS signed pairing proof + recovered snapshot
PASS steer + interrupt + exact approval decision
PASS real history list/read + thread start + thread resume
PASS pairing toast automatic dismissal
PASS mobile layout + manifest + service worker
PASS real Relay + browser Web Crypto + reload authentication recovery (Chromium + WebKit)
PASS iPhone WebKit pairing/control/PWA metadata
```

总计 12 个浏览器用例通过（Chromium 6 + WebKit 6）。

真实系统测试不是 WebSocket Mock：Playwright 浏览器用 Web Crypto 生成不可导出私钥，Relay 实际验证 Pairing Proof 和 Auth Signature，真实路由 Control 到签名 Device 客户端。

独立 in-app Browser 视觉检查：

```text
viewport: 390 x 844
document scroll width: 390
pairing card: 358 px
horizontal overflow: 0
console errors/warnings: 0
```

## 6. Docker

实际构建：

```text
deploy-relay: built
deploy-web: built
nginx gateway: running
```

实际 Compose：

```text
relay: healthy
web: healthy
gateway: running
HTTPS /healthz: 200
HTTPS /: 200
manifest: 200
CSP: present
WSS /ws/controller: Open
```

SQLite volume 在 Relay restart 前后 SHA-256 一致，证明持久化 volume 生效。

本地使用自签名证书，仅诊断客户端跳过证书校验；产品 Agent/PWA 未加入证书绕过。

## 7. MVP 验收矩阵

原设计中的 Windows 入口已按用户确认改为独立 Agent，不再包含其他桌面插件。

| 标准 | 结果 | 证据 |
|---|---|---|
| A Agent Idle | PASS | app-server initialize 后 Snapshot Idle |
| B 打开 Managed Codex | PASS | 真实 `codex --remote` 进入交互界面 |
| C 运行状态 | PASS | Turn/Item 状态机与 Snapshot 测试 |
| D PWA 实时状态 | PASS | 真实 Relay Playwright 系统测试 |
| E 手机 Steer | PASS | 浏览器系统测试 + 真实 Codex `turn/steer` |
| F 手机 Interrupt | PASS | 浏览器系统测试 + 真实 `turn/completed/interrupted` |
| G 手机 Approval | PASS | 真实 Command Approval + PWA Decision |
| H PWA 断线恢复 | PASS | 页面 reload 后 Challenge Auth + Device List/Snapshot |
| I Agent 断线恢复 | PASS | Agent Relay reconnect/Presence 测试 |
| J Relay restart | PASS | Docker restart + SQLite volume hash |
| K 一个 Controller 三台 PC | PASS | Relay three-device pairing/list 测试 |
| L 未配对 Controller 隔离 | PASS | zero-device isolation + permission test |
| M 手机查看真实历史 | PASS | `thread/list.recencyAt`、项目/最近分区、能力探测与当前 Desktop cwd 兼容 + 浏览器/真实 Codex 测试 |
| N 手机新建/恢复会话 | PASS | 项目内预填 cwd、`thread/start/resume -> turn/start` + 浏览器/真实 Codex 测试 |
| O Agent 自恢复 | PASS | app-server 子进程故障注入后 Agent PID 保持、proxy/Relay 自动恢复 |
| P 实时回复 | PASS | `AgentMessageDelta` 合并、流式光标、Completed 收口与后台历史刷新测试 |

## 8. 安全扫描

```text
dotnet list package --vulnerable --include-transitive: 0 findings
npm audit --audit-level=high: 0 vulnerabilities
```

## 9. 长稳

```text
短预跑: 120 秒 / 119 iterations / 2 restarts / PASS
一小时: 3600 秒 / 3558 iterations / 60 restarts / PASS
```

一小时测试每秒执行 TUI connect/initialize/list/close + Steer + Interrupt，每 60 轮重启 app-server/Proxy；全程 0 failure。

## 10. Release

`tools/Build-Release.ps1` 执行：

- Release build；
- Agent tests；
- Relay tests；
- PWA build/Playwright；
- Agent win-x64 single-file publish；
- Relay publish；
- PWA/Deploy copy；
- SHA256SUMS。

## 11. 现场边界

当前环境无法证明：

- 正式公网 DNS/CA/防火墙配置；
- 三台真实 Windows PC 同时在线；
- Android Chrome 与 iOS Safari 实机后台挂起行为；
- 企业代理/VPN/弱网；
- 生产用户负载、渗透测试和运维告警；
- 目标电脑上的 Codex 版本与本机完全一致。

这些属于部署 FAT/SAT，不应由本机自动化结果替代。
