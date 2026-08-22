# Changelog

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
