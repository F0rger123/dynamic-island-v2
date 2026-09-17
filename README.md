# Dynamic Island v2

A macOS-style Dynamic Island for Windows: a small pill floating at the top of
your screen that shows media (Spotify), Google Calendar, AI coding agents, and
weather/system status in one expandable surface.

It is made of two parts that work together — and the overlay keeps working even
when the bridge is offline:

- **Windhawk mod** (`sourcecode`) — the overlay itself. Renders the pill,
  reacts to media, calendar, agent and weather state, and talks to the bridge
  over a local named pipe.
- **DynamicIslandBridge.exe** (`bridge/`) — a local, per-user .NET 8 service
  that connects to the real integrations: Google Calendar (read-only OAuth),
  Claude Code, OpenAI Codex, Google Antigravity, and the Gemini API.

Everything is **local-only**: the bridge listens on a named pipe owned by the
current Windows user (no TCP port, no LAN endpoint), secrets are stored with
Windows DPAPI, and nothing in this repository contains credentials.

---

## Overview

| Page (media active) | What it shows |
| --- | --- |
| 1. Media | Spotify (or the system media session): artwork, title, artist, play/pause/skip controls |
| 2. Google Calendar | Bridge connection + auth state, next event (title, start–end, countdown, location), today's upcoming events |
| 3. AI Agents | Claude Code, Codex, and the Google slot (Antigravity / Gemini API / legacy Gemini CLI) with live Ready / auth-required / not-installed / error states |
| 4. Weather/System | Temperature + weather text, and system status (mic/camera indicators) |

When no media is active the island keeps the Calendar / Agents / Weather pages
so bridge content is always reachable.

Agent providers supported by the bridge:

- **Claude Code** (`claude`)
- **OpenAI Codex** (`codex`)
- **Google Antigravity** (`agy`) — Google's current coding agent for individual accounts
- **Gemini API** — a real, in-bridge coding agent that works with just an API key (no CLI, no sign-in)
- **Gemini CLI** (`gemini`) — legacy, enterprise/Cloud Code Assist compatibility only (see below)

---

## Requirements

- **Windows 10 1809 or newer** (Windows 11 recommended).
- **Windhawk** — [windhawk.net](https://windhawk.net). The overlay is a
  Windhawk mod compiled inside the Windhawk app.
- **.NET 8 Windows Desktop Runtime** — only needed to run the bridge:
  <https://dotnet.microsoft.com/download/dotnet/8.0> (choose
  "Windows Desktop Runtime x64"). The overlay alone does not need .NET.
- **Git** — optional, recommended (the agent projects usually are Git
  repositories; the Gemini API agent's command allowlist includes `git`).
- Node.js 18+ — only needed if you install Claude Code or Codex via npm.
- Any coding agent you want to use (all optional): Claude Code, Codex,
  Antigravity, or just a Gemini API key.

---

## Installation

### 1. Download the bridge

Download the latest **`DynamicIslandBridge-win-x64`** artifact/release from the
repository's [Windows Actions runs](https://github.com/F0rger123/dynamic-island-v2/actions/workflows/windows-release.yml)
(it is produced on every push to `main` and for `v*` tags).

### 2. Extract it to a permanent folder

Unzip the archive into a folder you will keep, e.g.
`C:\Tools\DynamicIslandBridge`. The folder contains
`DynamicIslandBridge.exe`, the `sourcecode` file for Windhawk, and the bridge's
configuration (created on first run).

### 3. Open PowerShell in that folder and run the setup wizard

```powershell
cd C:\Tools\DynamicIslandBridge
.\DynamicIslandBridge.exe --setup
```

> **Important:** double-clicking `DynamicIslandBridge.exe` may appear to do
> nothing. That is expected: the bridge is a silent background process, and
> after the first run it remembers that setup was completed
> (`%LOCALAPPDATA%\DynamicIslandBridge\setup.json`) and does not open the
> wizard again. **The reliable way to open — or reopen — setup is always:**
>
> ```powershell
> .\DynamicIslandBridge.exe --setup
> ```
>
> A running instance is enough; the flag reopens the wizard.

### 4. First-run setup

The wizard walks you through:

1. **System check** — Windows/.NET/Windhawk/agent states, with **Test All**.
2. **Google Calendar** — optional; see [Google Calendar setup](#google-calendar-setup).
3. **Local coding agents** — per-provider rows with **Rescan / Test / Login /
   Locate manually** (plus **Install** where an official one-line install
   exists); an optional **Gemini API key** field; and a
   **Start with Windows** checkbox.
4. **Finish** — saves your choices.

You can **Skip For Now** at any point; the island works without any of the
integrations. Reopen the wizard any time with `.\DynamicIslandBridge.exe --setup`.

---

## Windhawk installation

1. Install [Windhawk](https://windhawk.net) if you haven't already.
2. Open Windhawk and choose **Create new mod**.
3. Open the **`sourcecode`** file included in the bridge package
   (or in this repository), select **all** of its contents, and copy.
4. Paste it into Windhawk's source editor, **replacing** the default sample
   source completely.
5. Click **Compile Mod**, then **enable** the mod for `windhawk.exe`.
6. The Dynamic Island appears at the top edge of your chosen monitor (monitor,
   size and offsets are available in the mod's settings).

> **The included `sourcecode` must match the bridge build you downloaded.**
> Bridge and overlay share a JSON protocol; if you upgrade the bridge, paste
> the newest `sourcecode` from that same package into the mod and recompile.

---

## Claude Code setup

Official install (Node.js 18+ required):

```powershell
npm install -g @anthropic-ai/claude-code
```

Then authenticate with Claude's own flow — in the wizard press **Login** on the
Claude Code row (or run `claude` in a terminal once) and complete the sign-in.
After that the wizard's **Rescan** finds `claude` and **Test** runs a real,
harmless `claude --version` check.

- **Rescan** — re-probe PATH, the npm global folders, and any manual override.
- **Test** — runs the CLI's own harmless version check (no project access).
- **Login** — opens a console where Claude Code's sign-in runs interactively.
- **Locate manually** — pick the `claude` executable if detection can't find
  it; the choice is stored per user and preferred on the next scan.

---

## Codex setup

Official install:

```powershell
npm install -g @openai/codex
```

The wizard has an **Install** button that asks first, runs exactly that command,
and **auto-rescans** when the install finishes. Then:

- **Test** — runs `codex --version` for a real harmless check.
- **Login** — opens Codex's own sign-in console.
- **Rescan / Locate manually** — same as Claude.

---

## Google Antigravity setup (recommended for individual accounts)

**Antigravity CLI (`agy`) is Google's current coding agent for individual
Google accounts.** The old Gemini CLI individual sign-in ("Gemini Code Assist
for individuals") is **no longer supported** — see
[Legacy Gemini CLI](#legacy-gemini-cli) for the details.

### Install (official)

PowerShell:

```powershell
irm https://antigravity.google/cli/install.ps1 | iex
```

The installer puts the single `agy.exe` binary in
`%LOCALAPPDATA%\agy\bin\agy.exe` and adds that folder to your user PATH.
(No Node.js or npm needed — it is a self-contained binary.)

### Sign in

Open the wizard's **Google · Antigravity** row and press **Login** — or just run
`agy` in a terminal. On first run Antigravity opens your default browser for
Google sign-in (it keeps the session in the Windows Credential Manager, so
later runs sign in silently). Close the console and press **Rescan**.

### Wizard controls

- **Install** — asks first, runs the official one-liner above, then
  auto-rescans. The bridge also probes `%LOCALAPPDATA%\agy\bin` directly, so a
  fresh install is detected immediately even before your PATH refreshes.
- **Rescan / Test** — Test runs `agy --version` plus a session probe.
- **Login** — launches `agy` interactively for the browser sign-in flow.
- **Locate manually** — pick `agy.exe` anywhere if it lives in a custom folder.

When Antigravity is installed and signed in, the island's Agents page shows
**`G  Google — ready · antigravity`**, and "Run AI agent" defaults to it.

---

## Gemini API fallback

The bridge also runs a **real Gemini API coding agent** that needs only an API
key — no CLI, no account sign-in, no browser. This is the recommended fallback
when Antigravity is unavailable:

1. Create a key in [Google AI Studio](https://aistudio.google.com/apikey).
2. In the setup wizard, paste it into the **Gemini API** field and press
   **Save Gemini key**. The key is encrypted with Windows **DPAPI** for the
   current user (stored at `%LOCALAPPDATA%\DynamicIslandBridge`); it is never
   written to logs and is only ever sent to Google as the `x-goog-api-key`
   header. Press **Open Google AI Studio** for the official key page and
   **Test** to verify the key.
3. Launch it from the island's agent dialog (Agent: *Google (Antigravity /
   API)*, Provider: *Gemini API*) or right-click the island →
   **Use Gemini API Instead**.

The API agent streams `streamGenerateContent` (SSE), executes the model's tool
calls (`list_files`, `read_file`, `search`, `write_file`, `edit_file`,
`git_status`, `git_diff`, `run_command`) **inside the project root only** —
no `..`, no absolute external paths, no UNC, no symlink/reparse escapes — and
`run_command` is allowlisted to `git`, `dotnet`, `npm`, `node`, `python`,
`pytest` with shell metacharacters rejected. `agent.cancel` stops it through a
cancellation token. With a working key the bridge reports the Google slot as
**Ready · Gemini API** even when no CLI is installed at all.

---

## Legacy Gemini CLI

The original `gemini` CLI remains supported **for enterprise / Cloud Code
Assist users only**. It is **not** the recommended sign-in path for individual
Google accounts: Gemini CLI individual login is deprecated, and the CLI now
reports:

> “Failed to sign in. This client is no longer supported for Gemini Code Assist
> for individuals. To continue using Gemini, please migrate to the Antigravity
> suite of products.”

If you see that message, use **Google Antigravity** (the individual path) or
the **Gemini API** instead — both are one click away from the island's right-
click menu (**Use Antigravity Instead** / **Use Gemini API Instead**).
The wizard keeps a clearly labeled *Gemini CLI — legacy (enterprise/Cloud)*
row with the same Install/Rescan/Test/Login/Locate controls, and the agent
dialog lists it as *Gemini CLI (legacy)*.

---

## Google Calendar setup

This project does **not** supply Google credentials — you create your own
(read-only) OAuth client:

1. In [Google Cloud Console](https://console.cloud.google.com/apis/credentials)
   create (or pick) a project and enable the
   [Calendar API](https://console.cloud.google.com/apis/library/calendar-json.googleapis.com).
2. Configure the OAuth consent screen (**External** is fine for personal use)
   and add yourself as a test user.
3. Create an **OAuth client ID → Desktop app** and **download the
   credentials JSON**. Do not commit that file or any secret.
4. In the wizard's Google Calendar page press **Select credentials JSON**, pick
   the downloaded file (stored with DPAPI for this user), then press
   **Connect Google Calendar**.
5. Your browser opens the official Google sign-in page; after authorizing, the
   refresh/access tokens are encrypted with Windows DPAPI CurrentUser at
   `%LOCALAPPDATA%\DynamicIslandBridge\google-token.bin`.

The Calendar page then shows connection/auth state, the next event (title,
start–end, countdown, location) and today's remaining events. Events are read
with the `calendar.readonly` scope only.

---

## Startup

The wizard's **Start with Windows** checkbox adds a per-user registry entry
(`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) that starts
`DynamicIslandBridge.exe` at logon — no administrator rights, no global
Task Scheduler entry needed. Uncheck it (and Finish) to remove the entry.
The Windhawk mod itself loads with Windhawk, which is normally enabled at
startup.

---

## Usage

- **Expand/collapse** — hover (or click) the pill to expand it; a pinned
  island stays open.
- **Pages while media is active:** 1. Media, 2. Google Calendar, 3. AI Agents,
  4. Weather/System. Scroll the mouse wheel over the island (or use the
  pagination dots) to switch pages; without media the Calendar/Agents/Weather
  pages remain.
- **Spotify** — track changes update the artwork, title, and waveform **in
  place without auto-expanding**; the island expands only when you hover,
  click, or pin it. Play/pause/skip works by clicking the pill's controls
  (or hovering the pill and using the media keys).
- **Running an agent** — right-click the island → **Run AI agent...** and pick
  the agent (Claude / Codex / Google), the provider for the Google slot, a
  project folder, and a prompt. The Agents page then streams the normalized
  progress (`working`, `reading_file`, `editing_file`, `running_command`,
  `tool_result`, `completed`, `failed`, `cancelled`) with the raw CLI output
  beneath. Right-click → **Stop running agent** cancels the job.
- **Calendar** — the page reflects the bridge's live state; when OAuth is not
  connected it tells you to connect (wizard → Google Calendar).

---

## Troubleshooting

### Setup wizard doesn't open

Double-clicking the EXE often *appears* to do nothing (it runs silently in the
background, and skips setup once setup was completed). Always use:

```powershell
.\DynamicIslandBridge.exe --setup
```

### Bridge seems to do nothing

That is normal — it is a background service with no window. Confirm it is
running in Task Manager (`DynamicIslandBridge.exe`); the island's Calendar and
Agents pages show **Bridge offline** when it is not. Its log is at
`%LOCALAPPDATA%\DynamicIslandBridge\bridge.log` (operation names only — never
prompts or secrets).

### Claude / Codex / Antigravity not detected

- Press **Rescan** on the provider row (the bridge probes PATH, the npm global
  folders, `npm prefix -g` / `npm root -g`, `%LOCALAPPDATA%\agy\bin`, and
  `where.exe`).
- If PATH changed since the bridge started, **restart the bridge** (end the
  task and start it again) — processes keep the PATH they were launched with.
- If detection still fails, use **Locate manually** and pick the executable
  directly (`claude`/`codex` shims, `agy.exe`, ...). The manual path is stored
  per user and wins on the next scan.

### Gemini CLI says “client is no longer supported”

That is the deprecated **individual** Gemini CLI login path. Migrate to
**Antigravity** (`irm https://antigravity.google/cli/install.ps1 | iex`, then
sign in with `agy`) or use the **Gemini API** key flow — the island offers
both via its right-click menu. The legacy `gemini` CLI still works for
enterprise/Cloud Code Assist accounts.

### Windhawk compile error (e.g. `no matching function for call to 'EnumDisplayMonitors'`)

Make sure you pasted the **newest** `sourcecode` from the matching package —
old sources used an inline lambda where Windows requires a `CALLBACK`
(`__stdcall`) `MONITORENUMPROC`. That issue is fixed in this release (both
`EnumDisplayMonitors` call sites use the named `MonitorEnumProc`); if you see
any other callback-related compile error, paste the current `sourcecode`
again, **Compile Mod**, and enable.

### Google Calendar not showing events

- The bridge must be running (see above).
- OAuth must be connected: wizard → Google Calendar → **Connect Google
  Calendar** (the page shows the auth state).
- If the consent screen expired or you removed yourself as a test user, the
  connect step will fail — re-add the test user in Google Cloud Console and
  connect again.

---

## Security

- **Local named pipe only.** The bridge listens on
  `\\.\pipe\DynamicIslandBridge-v2` with an ACL restricted to the current
  Windows user. It binds no TCP/LAN port and has no remote command endpoint.
- **No shell.** Agent commands are launched through `ProcessStartInfo`
  `ArgumentList` (never a shell string); `.cmd`/`.bat` npm shims are invoked
  via `cmd.exe /d /s /c` and `.ps1` via `powershell.exe -File` with fixed
  arguments.
- **DPAPI for secrets.** Google OAuth client credentials, the Google token,
  and the Gemini API key are encrypted with Windows DPAPI (`CurrentUser`)
  under `%LOCALAPPDATA%\DynamicIslandBridge`.
- **Project-root restrictions.** Agents only run inside an existing local
  directory you select; the Gemini API tool sandbox additionally rejects
  `..`, absolute external paths, UNC paths, and symlink/reparse escapes, and
  allowlists `run_command` to `git`, `dotnet`, `npm`, `node`, `python`,
  `pytest` (shell metacharacters rejected).
- **Keys are never logged.** The Gemini API key travels only as the
  `x-goog-api-key` header; diagnostics contain paths and states, never
  payloads, prompts, or secrets. No secrets are embedded in this repository.

---

## Architecture

```
Windhawk Mod (overlay)
        ↕  named pipe  \\.\pipe\DynamicIslandBridge-v2  (per-user ACL, JSON lines)
DynamicIslandBridge.exe
   ├── Google Calendar   (read-only OAuth, DPAPI tokens)
   ├── Claude Code       (claude, stream-json)
   ├── Codex             (codex exec, --json)
   ├── Antigravity       (agy headless, stream-json)
   └── Gemini API        (in-bridge SSE agent, sandboxed tools)
```

The overlay is non-blocking: it consumes bridge messages from a worker thread,
reconnects when the bridge restarts, and stays fully functional (media,
weather, system) when the bridge is offline.

---

## Building from source

On a Windows machine with the .NET 8 SDK and Python 3:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` restores and builds `bridge/DynamicIslandBridge.csproj` (Release),
runs the three protocol/setup test suites, publishes a framework-dependent
`win-x64` build to `artifacts\DynamicIslandBridge`, and copies the `sourcecode`
file next to it — that folder is the contents of the
`DynamicIslandBridge-win-x64` release artifact.

A Windows GitHub Actions workflow
(`.github/workflows/windows-release.yml`) runs the same build on every push to
`main` and for `v*` tags and uploads the artifact. **Note:** the .NET CI build
does *not* compile the Windhawk C++ source — the `sourcecode` file is compiled
by Windhawk itself on the user's machine (see
[Windhawk installation](#windhawk-installation)).

## Releasing

1. Push the change to `main` (or tag a `v*` ref) — Windows Actions builds and
   uploads `DynamicIslandBridge-win-x64`.
2. Download the artifact from the run, verify the version string, and attach it
   to a GitHub release (the tag is optional but conventional).
3. Tell users to re-paste the new `sourcecode` into Windhawk if the overlay
   protocol changed.
