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
   selected work-area top + 1 logical pixel; `OffsetY` remains applied after
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

The Windhawk source currently contains the visual/UI side and the bridge is a
separately deployable integration boundary. A production installer should start
the bridge at logon using a per-user Task Scheduler entry, not administrator
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
there is no remote command endpoint. The current bridge reports resume as
unsupported until each CLI's session store can be safely and consistently
mapped; full-terminal handoff is also intentionally left to the host UI.

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
