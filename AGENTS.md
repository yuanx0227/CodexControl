# Codex Control repository rules

## Scope

- Target framework is `.NET 8`; do not upgrade Agent or Relay without approval.
- Windows endpoint is only `CodexControlAgent.exe`. Do not reintroduce a desktop plugin or DLL host.
- Codex integration must use public `codex app-server` JSON-RPC; no UI scraping, OCR, input simulation, private hooks, or Desktop attach hacks.
- Never read, upload, persist, or log the user's OpenAI API Key/private endpoint credentials.

## Tools

- When `.codegraph/` exists, inspect code/call paths with CodeGraph before `rg`.
- Web UI and WebSocket UI tests use Playwright.
- PowerShell commands/scripts must use PowerShell 7, never Windows PowerShell 5.1.

## Git publishing

- Publish and push this project only to `https://github.com/yuanx0227/CodexControl.git` on GitHub.com.
- Keep `origin` pointed at that GitHub.com repository. Do not push to the former private Git service or restore it as a remote unless the user explicitly changes this instruction.
- Never commit credentials, runtime identity/data, local browser automation output, or build artifacts.

## Project map

```text
src/agent/   Windows Agent, app-server bridge, local proxy, DPAPI identity, Relay client
src/shared/  Relay protocol constants/contracts and P-256 encoding
src/relay/   ASP.NET Core Relay, EF Core SQLite, auth/pairing/routing
src/web/     React/Vite PWA and Playwright tests
tests/       Agent, Relay, browser system host, soak
deploy/      Docker Compose, nginx HTTPS/WSS, certificate helper
docs/        Architecture, protocol, security, development, acceptance evidence
```

## Hard boundaries

- Local Codex WebSocket binds only `127.0.0.1`.
- Relay production transport is HTTPS/WSS behind nginx; insecure Relay is localhost-development only.
- Device private key is DPAPI protected; Controller private key is a non-exportable IndexedDB CryptoKey.
- Pairing Code is one-time, 180-second TTL, five invalid proof attempts, and HMAC-only in SQLite.
- All remote Control checks current Pairing Permission.
- Session history and session options require `view`; Thread create/resume requires `steer`, an existing absolute cwd, and no active Turn on that same Thread. Different Threads may run concurrently.
- Remote Thread create/resume must stay behind `RemoteControlDispatcher`; Web and Relay never parse raw app-server Thread objects.
- Remote-created/resumed work enforces `workspace-write`; model comes from normalized `model/list`, and approval policy is one of `untrusted`, `on-request`, or `never` with `untrusted` as the default.
- Approval is explicit first-valid-response-wins; never auto-approve.
- Relay does not persist full Prompt, Agent Response, Shell Output, Diff, source code, or raw JSON-RPC history.
- PWA parses only Codex Control Domain Events, never raw app-server messages.

## Protocol changes

- Message type constants belong in `src/shared/CodexControl.Protocol/RelayMessageTypes.cs`.
- Update `docs/protocol.md`, Agent, Relay, PWA types, and cross-component tests together.
- Control requests require idempotent `requestId`; Device Result must match Relay tracking.
- Database model changes require checked-in EF Migration; do not use `EnsureCreated`.

## Required verification

```powershell
dotnet run --project '.\tests\CodexControl.Agent.Tests\CodexControl.Agent.Tests.csproj'
dotnet run --project '.\tests\CodexControl.Relay.Tests\CodexControl.Relay.Tests.csproj'

Set-Location '.\src\web'
npm run build
npm test
```

For release, run `tools/Build-Release.ps1`. For current Codex compatibility, also run the explicit real-Codex tests described in `docs/development.md`. Keep build/simulation/container evidence separate from real Codex, real mobile device, and production deployment acceptance.

## Documentation pointers

- `docs/architecture.md` — components and flows
- `docs/protocol.md` — wire contract
- `docs/security.md` — trust model and production checklist
- `docs/development.md` — build/test/release/runbook
- `docs/feasibility.md` — acceptance evidence and residual field boundaries
