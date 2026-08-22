# Changelog

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
