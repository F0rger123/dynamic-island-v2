# Dynamic Island v2

This repository contains the Windhawk overlay (`sourcecode`) and the optional
local bridge (`bridge/DynamicIslandBridge.exe`). The overlay remains functional
when the bridge is stopped; bridge-backed Calendar and agent cards simply report
offline/auth-required state.

## Build and install

### Windhawk
1. Install Windhawk on Windows 10 1809 or newer.
2. Open **Create new mod**, paste the complete `sourcecode` file, compile and
   enable it for `windhawk.exe`.
3. In the mod settings select the monitor (`primary`, a 1-based monitor index,
   or `follow`), offsets, DPI scaling and modules. The top anchor is the
   selected display top edge + 1 logical pixel (the render padding is placed above the glass); `OffsetY` remains applied after
   monitor selection.

The source requires the Windows SDK/Windows App SDK headers already supplied by
Windhawk. This Linux checkout cannot produce the Windows Windhawk DLL.

### Bridge
Install the .NET 8 Windows Desktop Runtime, then from `bridge/` run:

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false
.\bin\Release\net8.0-windows\win-x64\publish\DynamicIslandBridge.exe
```

The bridge listens only on the current user's named pipe
`\\.\pipe\DynamicIslandBridge-v2`; it does not bind a TCP/LAN port. Messages
are newline-delimited JSON. Example status request:

```powershell
'{"op":"status"}' | # use a named-pipe client; this is intentionally not TCP
```

The Windhawk source includes a non-blocking named-pipe client. It reconnects
from a worker thread, consumes status/calendar/agent events, drives the Calendar
and Agents expanded dashboards, and leaves the overlay functional when the
bridge is offline. While media is active the expanded island has four pages —
Media, Google Calendar, AI Agents, Weather/System — and without media it keeps
the Calendar/Agents/Weather pages, so bridge content is reachable either way.
The local month calendar is gone; the Calendar page is the bridge's dashboard
(connection + auth state, next event with title/start/end/countdown/location,
and today's upcoming events). Clicking the Agents page opens a native input window for
agent, project folder, multiline prompt, and Run. A production installer should
start the bridge at logon using a per-user Task Scheduler entry, not administrator
privileges.

## Google Calendar setup (read-only)

1. In Google Cloud Console create a project and enable **Google Calendar API**.
2. Configure the OAuth consent screen (External is fine for personal use), add
   yourself as a test user, and request only the
   `https://www.googleapis.com/auth/calendar.readonly` scope.
3. Create an **OAuth client ID → Desktop app**. Do not commit the downloaded
   JSON or any secret.
4. Before starting the bridge, set the client values in the current user's
   environment (PowerShell):

```powershell
[Environment]::SetEnvironmentVariable('DYNAMIC_ISLAND_GOOGLE_CLIENT_ID','...','User')
[Environment]::SetEnvironmentVariable('DYNAMIC_ISLAND_GOOGLE_CLIENT_SECRET','...','User')
```

5. Send `{"op":"calendar.auth"}` through the named pipe. The bridge opens the
   official Google OAuth page and uses the loopback redirect
   `http://127.0.0.1:43871/`. The refresh/access token is encrypted with
   Windows DPAPI CurrentUser at `%LOCALAPPDATA%\DynamicIslandBridge\google-token.bin`.
   No token or client secret is embedded in the repository.
6. Send `{"op":"calendar.today"}` to retrieve today's events. Offline, revoked, and missing-auth responses are explicit errors; expired access
   tokens are refreshed using the DPAPI-protected refresh token.

## Agent verification

The bridge discovers the real `claude`, `codex`, and `gemini` executables from
`PATH` and never fabricates output. Install/login to each CLI using its own
supported flow, then send:

```json
{"op":"agent.start","agent":"claude","project":"C:\\work\\repo","prompt":"Inspect the tests and summarize failures."}
```

Use `codex` or `gemini` for the `agent` field. Output is streamed as JSON lines
with `stdout`/`stderr` and an exit event. `agent.cancel` accepts the returned
job id. Project paths must be local existing directories; no shell is used and
there is no remote command endpoint. Resume accepts an explicit CLI session id and uses the documented command
forms for Claude (`--resume`), Codex (`exec resume`), and Gemini (`--resume`).
The bridge does not invent session ids; the host must retain the id emitted by a
CLI or selected by the user. Full-terminal handoff is intentionally left to the
host UI.

### Gemini API provider

Gemini can also run as a real coding agent through the Gemini API instead of
the CLI. Store a key in the setup wizard (DPAPI), then send:

```json
{"op":"agent.start","agent":"gemini","provider":"api","project":"C:\\\\work\\\\repo","prompt":"Fix the failing tests."}
```

The agent streams `streamGenerateContent` SSE responses, executes the model's
tool calls (list_files, read_file, search, write_file, edit_file, git_status,
git_diff, run_command) inside the project root, and reports the same
normalized phases (`working`, `reading_file`, `editing_file`, `running_tool`,
`running_command`, `tool_result`, `completed`, `failed`, `cancelled`). Tool
paths may not leave the project root (no `..`, no absolute external paths, no
UNC, no symlink/reparse escapes) and `run_command` is allowlisted to
git, dotnet, npm, node, python, pytest with shell metacharacters rejected.
`agent.cancel` works for API jobs through a cancellation token. The API key is
sent only as the `x-goog-api-key` header, is never logged, and if the Gemini
CLI itself cannot log in the island offers **Use Gemini API Instead** plus a
shortcut to <https://aistudio.google.com/apikey>. With a working API key the
bridge reports Gemini as ready even when no CLI is installed.

## Verification performed

- Reviewed the complete 6,085-line Windhawk source and confirmed the checkout
  already matched the supplied `sourcecode` baseline.
- Source changes remove the render thread's fixed 16 ms sleep, use compositor
  synchronization while animating and event waiting while idle, make the top
  anchor 1 px, and request DWM acrylic/backdrop where supported.
- Added a compileable .NET bridge implementation with named-pipe ACL, message
  size/field/path validation, real CLI process streaming/cancellation, OAuth
  loopback, Google Calendar REST read, and DPAPI token storage.

Final Windows verification still requires a Windows SDK/Windhawk build and
real local CLI installations plus Google OAuth credentials. This environment is
Linux, so those end-to-end steps cannot honestly be claimed as run here.

## Easy setup and release packaging

The bridge is now a Windows GUI executable (`WinExe`). On first launch it opens
`Dynamic Island v2 Setup` automatically. It can also be reopened with:

```powershell
DynamicIslandBridge.exe --setup
```

The wizard provides:

- Windows/.NET/Windhawk/CLI checks, refreshed live per provider (Rescan/Test).
- Claude, Codex, Gemini, and Git discovery across PATH, `%APPDATA%\npm`,
  `%LOCALAPPDATA%\npm`, `%USERPROFILE%\.npm-global`, `%LOCALAPPDATA%\Programs`,
  `npm prefix -g` / `npm root -g`, and a `where.exe` fallback. Executables may
  be `.exe`, `.cmd`, `.bat`, `.ps1`, or extensionless; npm-installed CLIs are
  detected in place, so a reinstall is never required.
- Per-provider controls: Rescan, Test (runs a real harmless `--version`),
  Login (opens the CLI's own sign-in console), Locate manually (pick the
  executable; stored per user and preferred on the next scan), and for Codex
  and Gemini an Install button that asks first, runs
  `npm install -g @openai/codex` / `@google/gemini-cli`, and auto-rescans.
- Ready / Installed·authentication required / Not installed / Error states per
  provider, so a missing login is never mistaken for a missing install.
- Copyable official install commands.
- Official setup links opened directly in the browser.
- Google Desktop OAuth JSON selection and DPAPI-protected client credentials.
- Browser OAuth connection from the wizard.
- Optional DPAPI-protected Gemini API-key storage (the Gemini API is a real,
  selectable agent provider — not just a key test).
- Per-user Windows startup configuration.
- Skip/setup-later behavior without blocking the base Dynamic Island.

Build and package on Windows with:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The script restores dependencies, builds Release, runs the protocol/setup tests,
and publishes `artifacts\DynamicIslandBridge`. `install.ps1` can copy that
published bridge into the current user's LocalAppData and launch the wizard.
There is also a Windows GitHub Actions workflow at
`.github/workflows/windows-release.yml` that publishes a downloadable artifact
for tags or manual runs.

Google credentials selected by the wizard are stored with DPAPI under the
current user's LocalAppData. Environment variables remain an advanced fallback,
not the normal setup path. Diagnostic bridge requests contain paths/status only;
secrets and prompts are not logged.
