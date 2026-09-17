from pathlib import Path

root = Path(__file__).parents[1]
bridge_dir = root / "bridge"
wizard = (bridge_dir / "SetupWizard.cs").read_text(encoding="utf-8")
program = (bridge_dir / "Program.cs").read_text(encoding="utf-8")
discovery = (bridge_dir / "AgentDiscovery.cs").read_text(encoding="utf-8")
gemini_agent = (bridge_dir / "GeminiApiAgent.cs").read_text(encoding="utf-8")
project = (bridge_dir / "DynamicIslandBridge.csproj").read_text(encoding="utf-8")
all_bridge = "".join(p.read_text(encoding="utf-8") for p in sorted(bridge_dir.glob("*.cs")))

# Pre-existing wizard/bridge contracts.
assert "ShowIfFirstRun" in program and "--setup" in program
assert "Select credentials JSON" in wizard
assert "ProtectedData.Protect" in wizard
assert "Open Google Cloud Console" in wizard
assert "Copy" in wizard and "Open Google AI Studio" in wizard
assert "ConfigureStartup" in wizard
assert "diagnostics" in program
assert "gemini.api.test" in program
assert "System.IO.Pipes.AccessControl" in project
assert "System.Security.Cryptography.ProtectedData" in project

# (9) Setup wizard provider rows: every provider gets its button set, asks
# before installing, and auto-rescans after the install finishes.
for label in ("Rescan", "Test", "Login", "Locate manually", "Install"):
    assert f'"{label}"' in wizard, label
assert "@openai/codex" in wizard and "@google/gemini-cli" in wizard
assert '"install", "-g", package' in wizard
assert "MessageBox.Show" in wizard  # confirmation before installing software
assert "RefreshAgentRow(index, force: true)" in wizard  # auto-rescan after install

# (12) Antigravity: the recommended individual Google agent, installed with
# the verified official one-liner and discovered at %LOCALAPPDATA%\agy\bin.
assert "irm https://antigravity.google/cli/install.ps1 | iex" in wizard
assert "antigravity" in wizard
assert '"claude", "codex", "antigravity", "gemini"' in wizard  # row order
assert "agy" in wizard and "agy" in discovery
# The legacy Gemini CLI row is kept but clearly de-emphasized.
assert "legacy" in wizard
assert "Gemini CLI \\u2014 legacy (enterprise/Cloud)" in wizard
assert "obsolete" in wizard.lower()
# Login launches the discovered CLI (agy.exe for Antigravity) interactively.
assert 'CliDiscovery.Find(agent == "antigravity" ? "agy" : agent)' in wizard

# (1) Manual executable selection persisted per user and honored first.
assert "SaveManualPath" in wizard and "cli-overrides.json" in discovery
assert "GetManualPath" in discovery

# (1) npm install locations probed (no reinstall needed for npm users).
for probe in (".npm-global", "Roaming", "Programs", "npm"):
    assert probe in discovery, probe
assert '"prefix", "-g"' in discovery and '"root", "-g"' in discovery and "where.exe" in discovery

# (2) Readiness states and real harmless CLI tests.
for state in ("not_installed", "auth_required", "ready", "error"):
    assert state in discovery, state
assert '"--version"' in discovery

# (3) Wizard deep-link into the Gemini API recovery path.
assert "aistudio.google.com/apikey" in wizard

# (4) Gemini API agent is a first-class provider in the bridge.
assert 'provider == "api"' in program
assert "streamGenerateContent" in gemini_agent and "x-goog-api-key" in gemini_agent
assert "list_files" in gemini_agent and "run_command" in gemini_agent
assert "CancellationTokenSource" in gemini_agent  # agent.cancel wired via CTS

# (4) The API key is header-only: never logged, never in a URL.
assert "x-goog-api-key" in all_bridge
assert "DefaultRequestHeaders.TryAddWithoutValidation(\"x-goog-api-key\"" in all_bridge
# SafeLog only records the operation name, never payloads or keys.
assert "SafeLog(op" in program

# No hardcoded secrets or keys anywhere in the bridge.
for forbidden in ("AIza", "sk-", 'client_secret = "', 'access_token = "'):
    assert forbidden not in all_bridge, forbidden

print("setup contract tests passed")
