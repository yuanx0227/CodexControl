# Changelog

## 0.6.0 - 2026-08-23

- Removed periodic full `control.thread.read` polling. Agent-managed sessions now update through app-server Domain Events over Relay WebSocket; completion performs one bounded reconciliation read.
- Desktop-owned sessions are explicitly labeled as external/read-only because a standalone Agent cannot subscribe to another Desktop app-server process without unsupported attachment.
- Added sidebar running, unread, completed-unread and failed-unread markers with timestamp-only read receipts.
- Preserved scroll position while reading older messages and added an explicit new-message jump button.
- Added Web, Relay, Agent and Protocol version information.

## 0.5.4 - 2026-08-23

- 修复页面先打开、Desktop Turn 后启动时的状态推断时钟死锁：活动判断改用当前墙钟，不再依赖尚未启动的运行计时器。
- 增加“工作区已打开后再启动 Desktop Turn”回归测试，覆盖 Idle 到运行中、已运行时间和当前输出同步。

## 0.5.3 - 2026-08-23

- 选中会话不再依赖独立 Agent 自己的活动 Turn 才追平；空闲时每 2 秒、运行时每 0.8 秒读取公共 app-server 历史。
- 历史 Turn DTO 保留 `status`，可识别由 Codex Desktop 拥有的活动 Turn，并显示“任务正在运行”。
- 使用 Turn `startedAt` 每秒更新“已运行”时间；Desktop 所有的 Turn 保持只读，不错误开放 Steer 或停止。
- 兼容跨 app-server 活动 Turn 暂时报 `interrupted`：最近开始、无 final_answer 且历史仍增长时按只读活动状态显示，45 秒无更新后自动过期。
- 增加 Desktop 所有活动 Turn 的 Chromium/WebKit 回归测试，覆盖状态、计时和无刷新输出同步。

## 0.5.2 - 2026-08-23

- 文件变更摘要现在保留并显示每个影响文件的新增、删除行数和 Turn 汇总。
- 活动 Turn 保持 WebSocket 实时增量，并每 1.2 秒从 app-server 轻量追平已完成条目，断线抖动或漏事件后无需手工刷新会话。
- 增加 Agent diff 统计和 PWA 无刷新追平的回归测试。

## 0.5.1 - 2026-08-23

- Rendered assistant and user messages as safe GitHub-flavored Markdown without enabling raw HTML.
- Added bounded local-image thumbnails to live thread reads without exposing absolute paths or persisting images in Relay.
- Added historical and live Turn elapsed time from `thread/turns/list` and normalized Turn events.
- Folded command and file activity into a default-collapsed per-Turn process summary.
- Collapsed injected file/context metadata in user prompts while keeping the actual request visible.
- Allowed an authenticated Controller with a removed Pairing to re-pair through a dedicated unauthenticated claim connection.
- Rotated the PWA service-worker cache for immediate delivery of the rendering fixes.

## 0.5.0 - 2026-08-23

- Added the single-instance WinForms tray and four-page settings shell inside `CodexControlAgent.exe`.
- Added schema-versioned atomic settings, DPAPI identity metadata updates, offline revocation state and a unified install-relative `data` directory.
- Added the Runtime Coordinator, automatic loopback ports, one-click local TUI, remote pause and active-Turn-aware restart/exit behavior.
- Upgraded Relay Protocol to v2 with QR pairing, signed Controller names, Pending requests, 60-second Device confirmation and Full/ViewOnly profiles.
- Added Device Ready synchronization, Pairing aliases, permission updates, Device-side revoke and an EF Core v2 Migration that clears confirmed test Pairings.
- Added QR Relay summaries and editable Controller names to the PWA while preserving the existing project/history workspace.
- Added framework-dependent win-x64 Inno Setup delivery, .NET 8 Runtime detection, upgrade data preservation, optional uninstall retention and explicit `UNSIGNED` artifacts.

## 0.4.1 - 2026-08-23

- Split first-time device enrollment throttling from idempotent registration by an already-known device identity.
- Added a reconnect-burst regression covering twelve complete register-and-authenticate cycles from one Agent identity.
- Prevented normal Agent backoff from exhausting the hourly new-device enrollment allowance and staying offline.

## 0.4.0 - 2026-08-23

- Added Desktop-compatible task ordering through `recencyAt` instead of stale `updatedAt` timestamps.
- Added `project/list` and `thread.projectId` support, with a safe cwd fallback while current Desktop returns no persisted projects.
- Split the sidebar into real project folders and a direct “Recent” task list; Recent items never render project-new actions.
- Restyled project rows with folder icons, quieter hover-only desktop actions and compact child tasks.
- Coalesced `item/agentMessage/delta` events into the active assistant message for immediate live updates.
- Added a streaming typewriter caret, automatic near-bottom follow and final background history refresh.

## 0.3.2 - 2026-08-23

- Preferred metadata plus paged `thread/items/list` reads, with a bounded high-capacity `thread/read` fallback for current Desktop builds that report the paged method as unsupported.
- Kept the standalone Agent alive and rebuilt its bridge, proxy and Relay session after an unexpected app-server exit.
- Rendered one-session projects directly while retaining folding only for projects with multiple sessions.
- Made sidebar and message scrolling independent and kept the Composer inside the viewport for long histories.
- Added per-project new-session actions that prefill the project's absolute working directory.
- Deferred history reads while the Agent is offline and resumed them automatically when the device returns.

## 0.3.1 - 2026-08-23

- Fixed same-controller browser tabs repeatedly replacing and aborting each other's Relay connections.
- Broadcast device presence, snapshots and control results to every live tab for the authenticated Controller identity.
- Added automatic recovery for read-only history requests interrupted by Relay or Agent reconnects.
- Increased the browser-friendly heartbeat tolerance and shortened Controller/Agent reconnect backoff.
- Added multi-tab Relay and list/read disconnect recovery coverage.

## 0.3.0 - 2026-08-23

- Added permission-checked `thread/read` history loading so selecting a session renders its persisted user and assistant messages.
- Grouped sessions by normalized project working directory with collapsible project sections on desktop and mobile.
- Added stale-response protection, explicit loading/retry states and bounded history mapping below the Relay message limit.
- Extended Agent, Relay, system-host and Chromium/WebKit coverage for real history reads and project folding.

## 0.2.1 - 2026-08-22

- Reworked the PWA into a familiar chat workspace with a conversation sidebar, central message stream and bottom composer.
- Added a mobile drawer for history, a compact runtime strip, inline approvals and context-aware create/resume/Steer input modes.
- Removed the metric-card dashboard visual language while preserving all real Codex controls.
- Rotated the Service Worker cache so deployed clients receive the new shell immediately.

## 0.2.0 - 2026-08-22

- Added real Codex history from `thread/list`, including CLI, VS Code, app-server and exec sessions.
- Added remote thread creation plus first `turn/start`, and historical thread resume plus a new turn.
- Routed session controls through existing view/steer permissions and Relay request idempotency.
- Rebuilt the mobile device detail page around real session control instead of snapshot-only controls.
- Fixed persistent pairing/action toasts with timed state cleanup and a non-blocking visual lifecycle.
- Added real Codex create/list/resume/delete acceptance and 12 Chromium/WebKit system tests.

## 0.1.2 - 2026-08-22

- Added AppX package repository discovery when an independent PowerShell PATH cannot resolve Codex Desktop.
- Ignored unresolved Windows App Execution Alias entries and selected the highest installed `OpenAI.Codex` package.
- Verified first-stage and reuse behavior with Codex removed from PATH.

## 0.1.1 - 2026-08-22

- Added automatic discovery of the Codex Desktop runtime on Windows.
- Added atomic per-version staging of the four Desktop runtime executables outside WindowsApps.
- Added concurrent staging, reuse, real Desktop capability and hash verification.

## 0.1.0 - 2026-08-22

- Added standalone `.NET 8` Windows Agent and managed `codex app-server` bridge.
- Added localhost TUI WebSocket proxy, state machine, Steer, Interrupt and Approval arbitration.
- Added DPAPI Device identity and Relay WSS client with reconnect/heartbeat.
- Added `.NET 8` Relay with ECDSA auth, six-digit pairing, permissions, Presence, routing, SQLite migrations and audit metadata.
- Added React/Vite PWA with Web Crypto/IndexedDB identity, multi-device UI and remote controls.
- Added Docker/nginx HTTPS/WSS deployment, Playwright Edge/WebKit system tests, real Codex tests and soak tooling.
