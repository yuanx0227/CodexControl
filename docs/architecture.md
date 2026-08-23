# Codex Control 架构

状态：MVP 实现基线。

## 1. 系统边界

Codex Control 是 Codex Session 的控制面，不是 OpenAI 身份或模型代理。

它负责：

- 启动并持有 Managed Codex；
- 观察 Thread、Turn、Item、Command、File Change 和 Approval；
- 将 Codex JSON-RPC 规范化为 Snapshot/Domain Event；
- 通过自托管 Relay 连接多个 Device 和 Controller；
- 执行 Steer、Interrupt、Approval 和状态恢复。
- 列出本机 Codex 历史、按项目/最近分区和 `recencyAt` 排序、读取历史消息、创建 Thread、恢复历史 Thread 并启动真实 Turn。

它不负责：

- 读取、迁移或托管 OpenAI API Key；
- 修改用户的 Model Provider、Model 或 Endpoint；
- 抓取 UI、OCR、模拟输入或 Hook 私有函数；
- 附加到 Codex Desktop 已经创建的当前会话；
- 在 Relay 保存完整代码、Prompt、Response、Shell Output 或 Diff。

## 2. 总体拓扑

```text
Codex TUI
  │ ws://127.0.0.1:<port>
  ▼
CodexControlAgent.exe (.NET 8 / Windows)
  ├─ WinForms Tray / Settings Adapter
  ├─ AgentSettingsStore / AgentStateStore (`data\`)
  ├─ AgentRuntimeCoordinator
  ├─ CodexRuntimeResolver (CLI / Desktop bundle staging)
  ├─ CodexExecutableProbe
  ├─ AppServerBridge
  ├─ LocalCodexProxyServer
  ├─ CodexStateManager / DomainEventNormalizer
  ├─ ApprovalCoordinator / RemoteControlDispatcher
  ├─ DeviceIdentity (ECDSA P-256 + DPAPI)
  └─ RelayClient ───────────────┐
                                │ WSS
                                ▼
                        CodexControlRelay
                        ├─ Challenge Auth
                        ├─ Pairing
                        ├─ Presence / Cache
                        ├─ Permission / Routing
                        └─ EF Core / SQLite
                                ▲
                                │ WSS
                                │
                         React/Vite PWA
                         ├─ Web Crypto P-256
                         ├─ IndexedDB Identity
                         └─ Device/Control UI

Agent ──stdio JSONL──> codex app-server
```

## 3. Agent

### 3.1 CodexExecutableProbe

启动前实际执行：

```text
codex --version
codex --help
codex app-server --help
```

只有同时具备 `--remote` 和 `app-server --listen stdio://` 才启动。

### 3.2 AppServerBridge

- 独占 app-server stdin/stdout；
- stdout 只处理 JSONL，stderr 独立排空且默认不记录正文；
- 单 Writer Queue 防止 JSON 交错；
- 内部 Request ID 使用 `bridge:<UUID>`；
- TUI ID 保持原样；
- 当前 TUI 依赖 `runtimeWorkspaceRoots`，Bridge 显式启用 `experimentalApi=true`；
- 消息、队列、初始化和关停均有上限；
- stdin 关闭后有界等待，超时才终止进程树。

### 3.3 TUI 握手代理

同一条 stdio app-server 连接只能初始化一次，而 `codex --remote` 也会执行连接级握手：

```text
Agent -> app-server initialize/initialized
TUI   -> Agent WebSocket initialize/initialized
Agent -> TUI 返回缓存 Initialize Result
```

TUI 的第二次握手不转发到 app-server。当前 Agent 要求 TUI 声明 `experimentalApi=true`，并对尚未支持的 Initialize Extension fail closed。

### 3.4 状态与事件

集中状态：

```text
Offline / Starting / Idle / Thinking / Reading / Editing
RunningCommand / RunningTests / WaitingApproval / WaitingUserInput
Completed / Interrupted / Failed
```

Snapshot 是不可变对象并带单调 `revision`。Domain Event 只包含移动端需要的摘要，不把 Codex 原始协议暴露给 PWA。

### 3.5 远程控制

`RemoteControlDispatcher` 强制校验：

- `threadId`；
- 当前 `activeTurnId`；
- `expectedTurnId`；
- 新 Thread 的本机绝对 `cwd` 是否存在；
- 创建/恢复会话时是否已有其他活动 Turn；
- Approval ID 与 Decision；
- app-server RPC 错误/超时。

手机会话控制不是前端伪状态：

```text
历史: control.thread.list -> thread/list
读取: control.thread.read -> thread/read(includeTurns=false)
                            + thread/items/list(desc, paged)
                            + thread/turns/list(summary, duration)
                            -> 当前 Desktop 不支持时回退 thread/read(includeTurns=true)
                            -> 受限用户/助手/过程摘要与图片缩略图
新建: control.thread.start -> thread/start -> turn/start
恢复: control.thread.resume -> thread/resume -> turn/start
```

历史列表与历史消息读取使用 `view` 权限；创建/恢复使用 `steer` 权限。远程创建/恢复强制 `approvalPolicy=untrusted` 与 `sandbox=workspace-write`，且同一 Agent 同时只允许一个远程活动 Turn。PWA 按规范化 `cwd` 分组，历史正文只在选中 Thread 时按需读取。

Interrupt RPC 成功只表示接受请求。UI 只有收到 `turn/completed status=interrupted` 后才显示 Interrupted。

### 3.6 Approval

`ApprovalCoordinator` 保存 Server Request ID 与对外 `apr_<uuid>` 映射：

```text
Pending -> Resolving -> Resolved
```

本地 TUI 与远程 PWA first-valid-response-wins。Decision 优先严格匹配 app-server 的 `availableDecisions`；第二份响应返回 `APPROVAL_ALREADY_RESOLVED`，未知或非法 Decision 被抑制且不改变状态机。

## 4. 身份与认证

### 4.1 Device

- Agent 首次运行生成 ECDSA P-256；
- public key 使用未压缩 SEC1 点；
- private key 导出 PKCS#8 后用 Current User DPAPI 保护；
- 磁盘只保存 DPAPI 密文；
- Device ID 使用不可猜测 `dev_<uuid>`。

### 4.2 Controller

- PWA 使用 Web Crypto 生成 P-256；
- private key 重新导入为不可导出 `CryptoKey`；
- CryptoKey 通过 structured clone 保存到 IndexedDB；
- Controller ID 使用 `ctl_<uuid>`；
- 清除浏览器数据后重新配对。

### 4.3 长期 Challenge

```text
auth.hello
  -> auth.challenge (256-bit nonce, 15s TTL, connection-bound)
  -> ECDSA P1363 signature
  -> auth.ok
```

Challenge 成功、过期或使用后立即移除，重复响应失败。

## 5. 二维码与六位码配对 v2

固定规则：

```text
TTL: 180 秒
Proof 失败上限: 5
同 Device 有效 Session: 1
首个有效 Claim: PairingSession 立即 consumed
本机确认: 60 秒
重新生成: 旧 Session 失效
```

数据库保存两种服务端 HMAC：

- `CodeHash = HMAC(secret, sessionId + code)`：最终等值验证；
- `CodeLookupHash = HMAC(secret, code)`：安全查找并避免活动码碰撞。

两者都不是明文码。Controller Claim 的 v2 proof 同时签名 Controller ID、Controller Name、
public key、code 和随机 nonce。Claim 只创建 Pending Request；电脑本机选择 Full、ViewOnly
或 Deny 后，Relay 才创建 Pairing。Device 认证后先同步离线撤销项，发送 `device.ready`
后才发布 Online 并接受 Control。

## 6. Relay

### 6.1 职责

- Device/Controller 认证；
- Pairing 与权限；
- 在线连接注册；
- 15 秒 Heartbeat / 120 秒失活回收；
- 最新 Snapshot 缓存；
- Control/Result 路由；
- 同一 Controller 身份的多标签连接注册与扇出；
- 审计元数据；
- 速率限制。

### 6.2 Control 幂等

Relay 跟踪：

```text
requestId -> deviceId + controllerId + messageType + result
```

- 相同 Request ID/同一操作：Pending 时不重复执行，完成后返回缓存 Result；
- 相同 Request ID/不同操作：`REQUEST_ID_CONFLICT`；
- Device 伪造或不匹配的 Result：`CONTROL_RESULT_UNEXPECTED`。

### 6.3 数据库

EF Core SQLite Migration 管理：

```text
Devices
Controllers
Pairings
PairingSessions
PairingRequests
AuditEvents
```

Relay 不持久化完整事件流，只保存最新 Snapshot 和必要审计元数据。

## 7. PWA

移动优先 React UI：

- 二维码与六位码备用配对；
- Relay 摘要确认、电脑二次确认和 Pending 状态；
- 多设备列表与在线状态；
- 桌面固定会话侧栏 / 移动端抽屉；
- 中央对话消息流与底部固定 Composer；
- Snapshot 恢复；
- 活动时间线；
- 真实历史会话列表；
- 新建会话并启动首个 Turn；
- 恢复历史会话并启动后续 Turn；
- Steer；
- Interrupting/Interrupted 两阶段语义；
- app-server 原始 Decision 审批；
- 安全 GFM Markdown、受控图片缩略图、Turn 耗时和默认折叠过程摘要；
- 解除配对；
- Service Worker 与 Manifest。

侧栏优先使用 `project/list.position` 与 `thread.projectId`；当前 Desktop 返回空项目目录时，真实工作 `cwd` 兼容为项目，`Documents\Codex\YYYY-MM-DD` 自动会话目录归入“最近”。项目行有文件夹图标和项目内新建入口；“最近”任务直接显示且没有“＋”。项目内与最近任务均按 `recencyAt` 降序。侧栏、消息流和 Composer 分属独立布局行。

`item/agentMessage/delta` 被规范化为 `AgentMessageDelta`，PWA 按 `itemId` 合并到同一助手消息并立即显示；闪烁光标提供打字机反馈。0.6.0 起不再周期读取完整历史：Agent 托管会话只消费同一 app-server 推送的 Thread、Turn、Item 和 Approval Domain Events，Turn 完成后仅执行一次有界历史核对。最终正文使用不执行原始 HTML 的 GFM Markdown。`Command/File` 过程按 Turn 合并进默认关闭的摘要；文件 diff 只统计新增/删除行并显示每文件及总计，不传完整 diff。

独立 Agent 无法订阅另一个 Codex Desktop app-server 进程内部的事件。此类 Thread 显示为“Desktop 外部会话（状态不可订阅）”，只在用户明确打开或手动刷新时读取历史；用户从 PWA 恢复后，它才成为 Agent 托管会话并进入事件驱动链路。PWA 按 Thread 保存无正文的活动时间/状态和已读时间，派生运行、未读、完成未读与失败未读标志。

消息流首次打开时定位到底部；后续事件仅在用户原本位于底部时自动跟随。用户向上阅读时保存滚动锚点，显示“新消息”按钮，不再因历史对象替换而重置位置。

同一浏览器身份可以同时打开多个标签页。Relay 为 Device 保留单连接替换语义，但按 `controllerId + connectionId` 保存全部 Controller 页面；Presence、Snapshot、Event 和 Control Result 会发送给该 Controller 身份的所有活动页面，未发起请求的页面会忽略不匹配的 `requestId`。

重载或手机挂起恢复：

```text
IndexedDB Identity
  -> auth challenge
  -> device.list
  -> cached snapshot
  -> live event
```

## 8. 重连与 Presence

Agent backoff：

```text
1s, 2s, 3s, 5s, 10s, 10s...
```

Controller backoff 为 `0.5s, 1s, 2s, 5s, 10s...`。两者都带 0–20% jitter。重连后先认证并恢复设备列表；只读历史列表/正文请求会等待 Controller 与 Agent 都恢复在线后自动重试。Device 使用 connection replacement fencing，Controller 多标签互不替换。

若 Codex app-server 异常退出，Agent 进程不退出；它按 `1s, 2s, 5s, 10s, 30s...` 重建 app-server bridge、localhost proxy 与 Relay session。历史正文优先使用 `thread/items/list` 分页并以 `thread/turns/list` 补齐耗时；当前 Desktop 返回 `not supported yet` 时回退完整 `thread/read`，但 app-server 内部消息上限提升到 128MB，随后仍只映射受限的最近消息、过程摘要和有界图片缩略图。

## 9. 部署

Docker Compose：

```text
gateway nginx : HTTPS/WSS
web nginx     : PWA static/SPА fallback
relay         : ASP.NET Core 8
relay-data    : SQLite volume
```

Relay 容器不发布外部端口，只由 gateway 访问；`X-Forwarded-Proto=https` 进入 Relay。生产 Origin 白名单和 Pairing Secret 必须显式配置。

## 10. 验证分层

- 单元/进程：Fake app-server、状态、路由、Approval；
- Relay：真实 WebSocket + 临时 SQLite；
- Browser：Playwright Edge/Chromium + iPhone WebKit + Web Crypto + 真实 Relay；
- Codex：真实 TUI、历史列表、Thread 创建/恢复、真实 Turn、Steer、Approval Decline、Interrupt；
- Docker：镜像、Compose、HTTPS/WSS、Health、volume restart；
- Soak：持续连接/断开、Steer/Interrupt、app-server/Proxy 重启。

这些证据不替代真实公网证书、多台实体 PC、Android/iOS 实机和生产网络 FAT/SAT。
