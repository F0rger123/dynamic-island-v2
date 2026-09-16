from pathlib import Path

root = Path(__file__).parents[1]
wizard = (root / "bridge" / "SetupWizard.cs").read_text()
program = (root / "bridge" / "Program.cs").read_text()
project = (root / "bridge" / "DynamicIslandBridge.csproj").read_text()

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
for forbidden in ("AIza", "sk-", "client_secret = \"", "access_token = \""):
    assert forbidden not in wizard + program
print("setup contract tests passed")
