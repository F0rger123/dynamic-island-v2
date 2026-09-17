from pathlib import Path

root = Path(__file__).parents[1]
source = (root / "sourcecode").read_text(encoding="utf-8")
readme = (root / "README.md").read_text(encoding="utf-8")
bridge_files = sorted((root / "bridge").glob("*.cs"))
bridge = "".join(p.read_text(encoding="utf-8") for p in bridge_files)
program = (root / "bridge" / "Program.cs").read_text(encoding="utf-8")
discovery = (root / "bridge" / "AgentDiscovery.cs").read_text(encoding="utf-8")
gemini_agent = (root / "bridge" / "GeminiApiAgent.cs").read_text(encoding="utf-8")
wizard = (root / "bridge" / "SetupWizard.cs").read_text(encoding="utf-8")

# ---------------------------------------------------------------------------
# Regression checks for the pre-existing contracts (must keep passing).
# ---------------------------------------------------------------------------
assert "kRenderPadY) + 1" in source
assert "artChangedAt" in source and "recentArtChange" not in source
assert "GetMonitorDpiScale(next.targetMonitor)" in source
assert "WaitForSingleObject(g_stopEvent, 16);" not in source
assert "DwmFlush()" in source and "WaitForSingleObject(g_stopEvent, 100)" in source
assert "DynamicIslandBridge-v2" in source
assert "BridgeThreadProc" in source and "QueueBridgeRequest" in source
assert "DrawBridgeCalendarDashboard" in source and "DrawAgentsDashboard" in source
assert "AgentInputWndProc" in source
assert "NamedPipeServerStreamAcl.Create" in bridge
assert "DYNAMIC_ISLAND_GOOGLE_CLIENT_SECRET" in bridge
assert "ProtectedData.Protect" in bridge and "ProtectedData.Unprotect" in bridge
assert "psi.ArgumentList.Add" in bridge
assert "NormalizeEvent" in program and "agent.resume" in program
assert "resume_not_available" not in bridge
for agent in ("claude", "codex", "gemini"):
    assert f'"{agent}"' in bridge
assert "offline" in readme.lower() and "DPAPI" in readme

# ---------------------------------------------------------------------------
# (5) Old local month calendar removed; bridge calendar dashboard used in the
# media view instead.
# ---------------------------------------------------------------------------
assert "void DrawCalendarDashboard" not in source
assert "GetDaysInMonth" not in source
assert "GetDayOfWeek" not in source
assert "DrawBridgeCalendarDashboard(state, rect, 1.0f);" in source
assert "DrawAgentsDashboard(state, rect, 1.0f);" in source
# The bridge dashboard itself still shows connection/auth + next event details.
assert "Google Calendar" in source and "NEXT" in source and "TODAY" in source

# ---------------------------------------------------------------------------
# (6) Unified page routing: media active => Media/Calendar/Agents/Weather (4),
# media inactive => Calendar/Agents/Weather (3).
# ---------------------------------------------------------------------------
assert "int tab = g_idleTab % 4;" in source            # DrawMedia routing
assert "int tab = g_idleTab % 3;" in source            # DrawIdleDashboard routing
assert "mediaActive ? 4 : 3" in source                 # mouse wheel tab count
assert source.count("(g_idleTab % 4) == 0") >= 2       # media click + release hit tests
assert "dotY - 1.5f * spacing" in source               # 4 pagination dots
assert "dotY + 1.5f * spacing" in source

# ---------------------------------------------------------------------------
# (7) Spotify: track changes update in place; only hover/pin/click expand.
# ---------------------------------------------------------------------------
assert "Track changes update artwork/title/waveform in place" in source
assert "recentArtChange" not in source
assert "isHoverExpanded || pinned" in source

# ---------------------------------------------------------------------------
# (8) Safe unused symbols removed (render-thread privacyActive stays).
# ---------------------------------------------------------------------------
assert "accessLogged" not in source
assert "SendMediaCommandAtPoint" not in source
assert "bool privacyActive = state.system.micActive" not in source
assert "bool privacyActive = snapshot.system.micActive" in source

# ---------------------------------------------------------------------------
# (10) Windhawk CALLBACK fix: no inline lambda may be passed to
# EnumDisplayMonitors (MONITORENUMPROC needs the CALLBACK/__stdcall convention;
# Windhawk/MinGW fails to convert a lambda). Both call sites must use the named
# MonitorEnumProc, defined exactly once and before its first use.
# ---------------------------------------------------------------------------
import re as _re
assert not _re.search(r"EnumDisplayMonitors\([^;]*\[\]\(", source), "inline lambda passed to EnumDisplayMonitors"
assert source.count("EnumDisplayMonitors(nullptr, nullptr, MonitorEnumProc") == 2, "both call sites must use MonitorEnumProc"
assert source.count("BOOL CALLBACK MonitorEnumProc") == 1, "MonitorEnumProc must be defined exactly once"
assert source.count("struct MonitorEnumData") == 1
assert source.index("BOOL CALLBACK MonitorEnumProc") < source.index("EnumDisplayMonitors(nullptr, nullptr, MonitorEnumProc"), \
    "MonitorEnumProc must be declared before its first use"
assert "data.monitors" in source and "data.values" not in source
# No other Win32 enumeration callback may be an inline lambda.
assert not _re.search(r"Enum(Windows|DisplayDevices|ChildWindows|Processes|DisplayMonitors)\s*\([^;]*\[\]\(", source)

# ---------------------------------------------------------------------------
# (3) Gemini CLI login-unavailable surfacing + Antigravity recovery actions.
# ---------------------------------------------------------------------------
assert "geminiLoginUnavailable" in source
assert 'L"Gemini CLI individual login is no longer supported."' in source
assert 'L"Use Google Antigravity or the Gemini API instead."' in source
assert "Use Gemini API Instead" in source
assert "Use Antigravity Instead" in source
assert "https://aistudio.google.com/apikey" in source
assert "ShowAgentInput(2, 1)" in source               # Gemini API pref
assert "ShowAgentInput(2, 0)" in source               # Antigravity pref

# ---------------------------------------------------------------------------
# (4) Agent input provider selection -> agent.start wiring per provider.
# ---------------------------------------------------------------------------
assert "reinterpret_cast<HMENU>(107)" in source       # provider combo id
assert 'L"Gemini API"' in source
assert 'L"Antigravity CLI"' in source
assert 'L"Gemini CLI (legacy)"' in source
assert 'L"CLI (login)"' not in source                 # old Gemini-only combo item gone
assert "ApplyAgentInputProviderSelection" in source
assert 'L"Google (Antigravity / API)"' in source      # agent combo label
assert '\\"provider\\":\\"api' in source              # request builder (Gemini API)
assert "request += \"antigravity\"" in source         # request builder (Antigravity)
assert "agent_state_claude" in source                 # C++ parse of readiness
assert "agent_provider_gemini" in source
assert "googleProvider" in source                     # int slot: 1 antigravity / 2 api / 3 legacy
assert "gemini_login_unavailable" in source
assert 'L"cancelled"' in source                       # phase map

# ---------------------------------------------------------------------------
# (1) Windows CLI discovery variants (bridge/AgentDiscovery.cs).
# ---------------------------------------------------------------------------
assert "SpecialFolder.ApplicationData" in discovery    # %APPDATA%
assert "SpecialFolder.LocalApplicationData" in discovery  # %LOCALAPPDATA%
assert '".npm-global"' in discovery
assert '"Programs"' in discovery
assert '"prefix", "-g"' in discovery and '"root", "-g"' in discovery  # npm prefix/root -g
assert '"where.exe"' in discovery                     # where.exe fallback
assert '".exe", ".cmd", ".bat", ".ps1", ""' in discovery
assert "cli-overrides.json" in discovery              # manual exe selection
assert "SaveManualPath" in discovery and "GetManualPath" in discovery
assert '"/d", "/s", "/c"' in discovery                # cmd.exe /d /s /c for .cmd/.bat
assert '"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"' in discovery
# Antigravity CLI (agy): official install folder probed explicitly so a fresh
# install is found without waiting for the user PATH to refresh.
assert '"agy", "bin"' in discovery                    # %LOCALAPPDATA%\agy\bin
assert '"Antigravity"' in discovery

# ---------------------------------------------------------------------------
# (2) CLI readiness states + harmless real tests.
# ---------------------------------------------------------------------------
assert '"not_installed"' in discovery and '"auth_required"' in discovery
assert '"ready"' in discovery and '"error"' in discovery
assert '"--version"' in discovery                     # real harmless CLI test
assert "CheckAntigravity" in discovery                # agy readiness (version + models probe)
assert "CheckGoogle" in discovery                     # aggregated Google slot
# Google slot preference: Antigravity -> Gemini API -> legacy Gemini CLI.
assert 'new AgentResult(Ready, antigravity.Path, "Ready \\u00b7 Gemini API", "api")' in discovery
assert '"antigravity"' in discovery
assert 'detail, "api"' not in discovery               # old single-CLI gemini check removed

# ---------------------------------------------------------------------------
# (4) Gemini API agent: SSE streaming, sandbox, allowlist, cancellation.
# ---------------------------------------------------------------------------
assert "streamGenerateContent?alt=sse" in gemini_agent
assert "x-goog-api-key" in gemini_agent
assert "GetFileAttributesW" in gemini_agent           # reparse-point escape check
assert "0x00000040" in gemini_agent
assert '"UNC paths are not allowed"' in gemini_agent
assert '"reparse point escape is not allowed"' in gemini_agent
assert '"path escapes the project root"' in gemini_agent
assert '{ "git", "dotnet", "npm", "node", "python", "pytest" }' in gemini_agent
assert "ShellMetacharacters" in gemini_agent
assert "CancellationTokenSource" in gemini_agent
assert '"cancelled"' in gemini_agent
assert "MaxTurns" in gemini_agent
assert "list_files" in gemini_agent and "write_file" in gemini_agent and "edit_file" in gemini_agent
assert "run_command" in gemini_agent and "git_status" in gemini_agent and "git_diff" in gemini_agent

# ---------------------------------------------------------------------------
# (4)/(3) Program.cs wiring: provider=api, CTS cancel, login-unavailable.
# ---------------------------------------------------------------------------
assert "ApiJobs" in program and "CancellationTokenSource" in program
assert 'provider == "api"' in program
assert '"gemini_login_unavailable"' in program
assert "LooksLikeGeminiLoginError" in program
assert "agent_state_claude" in program and "agent_provider_gemini" in program

# ---------------------------------------------------------------------------
# (9) Setup wizard provider rows + install behavior.
# ---------------------------------------------------------------------------
for label in ("Rescan", "Test", "Login", "Locate manually", "Install"):
    assert f'"{label}"' in wizard
assert "SaveManualPath" in wizard                     # manual executable selection
assert "MessageBox.Show" in wizard                    # ask before installing
assert '"install", "-g", package' in wizard           # npm install -g <package>
assert '"@openai/codex"' in wizard and '"@google/gemini-cli"' in wizard
assert "RefreshAgentRow(index, force: true)" in wizard  # auto-rescan after install
assert "https://aistudio.google.com/apikey" in wizard
assert "Open Google AI Studio" in wizard
# Antigravity is the recommended individual Google agent: official installer.
assert "irm https://antigravity.google/cli/install.ps1 | iex" in wizard
assert "Antigravity" in wizard
assert '"claude", "codex", "antigravity", "gemini"' in wizard  # row order
# The legacy Gemini CLI is de-emphasized and clearly labeled.
assert "legacy" in wizard
assert "Gemini CLI \\u2014 legacy (enterprise/Cloud)" in wizard
assert "recommended" in wizard

# No secrets/keys may be hardcoded anywhere in the bridge.
for forbidden in ("AIza", "sk-", 'client_secret = "', 'access_token = "'):
    assert forbidden not in bridge, forbidden

# ---------------------------------------------------------------------------
# (11) README: a brand-new Windows user must be able to install and configure
# everything from it.
# ---------------------------------------------------------------------------
assert "DynamicIslandBridge.exe --setup" in readme, "README must document the --setup flag"
assert "double-click" in readme.lower() or "double click" in readme.lower(), "README must explain the silent double-click behavior"
for heading in ("Overview", "Requirements", "Installation", "Windhawk", "Troubleshooting", "Security", "Architecture", "Building from source"):
    assert heading in readme, f"README missing section: {heading}"
# Windhawk installation steps.
for step in ("Create new mod", "Compile Mod"):
    assert step in readme, f"README missing Windhawk step: {step}"
# Antigravity setup with the verified official installer.
assert "Antigravity" in readme
assert "irm https://antigravity.google/cli/install.ps1 | iex" in readme
assert "%LOCALAPPDATA%" in readme and "agy" in readme
# Gemini API fallback + DPAPI.
assert "Gemini API" in readme and "DPAPI" in readme
assert "aistudio.google.com" in readme
# Legacy Gemini CLI clearly marked, with the observed deprecation message.
assert "no longer supported" in readme
assert "Legacy" in readme
# Agent sections for every provider.
for provider in ("Claude Code", "Codex", "Antigravity", "Gemini API"):
    assert provider in readme, f"README missing provider section: {provider}"
# Google Calendar setup steps.
for step in ("OAuth client ID", "Desktop app", "credentials JSON", "Connect Google Calendar"):
    assert step in readme, f"README missing calendar step: {step}"
# Usage: media-active page order + Spotify non-expansion.
assert "Weather/System" in readme
assert "Spotify" in readme
# Architecture diagram includes the named pipe and the Antigravity leg.
assert "named pipe" in readme.lower()
# Building from source notes that Windhawk compiles the C++ itself.
assert "build.ps1" in readme

print("static contract checks passed")
