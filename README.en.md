# Codex Control

[简体中文](README.md) | [English](README.en.md)

View, create, and control Codex sessions on Windows from your phone or browser. Self-host the Windows Agent, Relay, and web app.

- Live responses, session history, and concurrent sessions.
- Send instructions, interrupt tasks, and handle approvals.
- Windows system tray, QR pairing, and mobile / desktop browsers.
- Optional shared mode: use the same session from official Codex Desktop and the web app.

## Screenshots

**Windows desktop Agent**

![Windows desktop Agent](docs/images/agent-desktop.png)

<table>
  <tr><th>Desktop browser</th><th>Mobile browser</th></tr>
  <tr>
    <td><img src="docs/images/web-desktop.png" alt="Desktop web interface" width="900"></td>
    <td><img src="docs/images/web-mobile.png" alt="Mobile web interface" width="260"></td>
  </tr>
</table>

The desktop screenshot shows the running Agent with its server address hidden. Web screenshots use sample session data.

## Build the Windows Agent

Requires Windows 10/11 x64, PowerShell 7, .NET 8 SDK, Node.js 24, and Inno Setup 6. Install and configure Codex CLI or Codex Desktop on the computer.

```powershell
git clone https://github.com/yuanx0227/CodexControl.git
Set-Location '.\CodexControl'
& '.\tools\Build-Release.ps1'
```

Installer: `out/release/installer/CodexControl-Setup-UNSIGNED.exe`. Running the Agent requires .NET 8 Desktop Runtime and ASP.NET Core Runtime.

## Deploy the Relay and web app

The server needs Docker Compose, a domain, and a trusted TLS certificate.

1. Copy `deploy/.env.example` to `deploy/.env`. Set `CODEX_CONTROL_PAIRING_SECRET` to at least 32 random characters and `CODEX_CONTROL_ALLOWED_ORIGINS` to your web app origin.
2. Place certificates at `deploy/certs/fullchain.pem` and `deploy/certs/privkey.pem`.
3. Run from the repository root:

```powershell
docker compose -f '.\deploy\docker-compose.yml' up -d --build
```

Preserve `.env`, certificates, and the `relay-data` volume across upgrades.

## Pair and use

1. Open the Agent and enter the Relay's HTTPS root URL and a computer name.
2. Generate a pairing code. Scan the QR code on your phone or enter the six-digit code in the web app.
3. Confirm full control or view only on the computer.
4. Select a session in the web app or create a task in an existing project directory.

In the default independent mode, Agent-managed sessions support live control. External sessions opened by the native Codex Desktop launcher support history reads on demand.

## Shared session mode (experimental)

Connect official Codex Desktop and the Agent to the same local `codex app-server` WebSocket service to use one session from both clients: live messages in both directions, additional instructions during a turn (Steer), manual approvals, and task interruption.

1. Follow the [shared mode setup guide](docs/shared-session-implementation.md#当前账户的使用入口) to prepare the service and its identity manifest, then open official Desktop through the shared launcher.
2. In the Agent's advanced settings, enter the shared service URL and identity manifest path. Add absolute project directories that the web app may control, separated by semicolons.
3. Save and reconnect, then select the session in the web app.

The shared URL must use `ws://127.0.0.1:<port>`. Project directories require explicit authorization on the computer; new remote work is disabled by default. Existing sessions retain their model and permissions, and approvals require a human decision. Closing the web app, Agent, or Desktop disconnects that client while the shared service continues the task. Use the session's stop button to interrupt it.

Installing an update does not switch to shared mode automatically. Exit Desktop's windows and system tray before changing its launcher. See the [implementation record](docs/shared-session-implementation.md) for setup, rollback, tested versions, and remaining acceptance checks.

[Development and local setup](docs/development.md) · [Architecture](docs/architecture.md) · [Security](docs/security.md) · [Changelog](CHANGELOG.md)
