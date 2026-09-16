from pathlib import Path

root = Path(__file__).parents[1]
source = (root / "sourcecode").read_text()
bridge = (root / "bridge" / "Program.cs").read_text()
readme = (root / "README.md").read_text()

# Regression checks for the requested contracts that can run on Linux.
assert "int y = work.top + 1;" in source
assert "GetMonitorDpiScale(next.targetMonitor)" in source
assert "WaitForSingleObject(g_stopEvent, 16);" not in source
assert "DwmFlush()" in source and "WaitForSingleObject(g_stopEvent, 100)" in source
assert 'NamedPipeServerStreamAcl.Create' in bridge
assert 'DYNAMIC_ISLAND_GOOGLE_CLIENT_SECRET' in bridge
assert 'ProtectedData.Protect' in bridge and 'ProtectedData.Unprotect' in bridge
assert 'psi.ArgumentList.Add' in bridge
for agent in ("claude", "codex", "gemini"):
    assert f'"{agent}"' in bridge
assert "offline" in readme.lower() and "DPAPI" in readme
print("static contract checks passed")
