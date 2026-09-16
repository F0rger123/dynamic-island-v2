from pathlib import Path

root = Path(__file__).parents[1]
source = (root / "sourcecode").read_text(encoding="utf-8")
bridge = (root / "bridge" / "Program.cs").read_text(encoding="utf-8")
readme = (root / "README.md").read_text(encoding="utf-8")

# Regression checks for the requested contracts that can run on Linux/Windows.
assert "kRenderPadY) + 1" in source
assert "artChangedAt" in source and "recentArtChange" not in source
assert "GetMonitorDpiScale(next.targetMonitor)" in source
assert "WaitForSingleObject(g_stopEvent, 16);" not in source
assert "DwmFlush()" in source and "WaitForSingleObject(g_stopEvent, 100)" in source
assert "DynamicIslandBridge-v2" in source
assert "BridgeThreadProc" in source and "QueueBridgeRequest" in source
assert "DrawBridgeCalendarDashboard" in source and "DrawAgentsDashboard" in source
assert "AgentInputWndProc" in source
assert 'NamedPipeServerStreamAcl.Create' in bridge
assert 'DYNAMIC_ISLAND_GOOGLE_CLIENT_SECRET' in bridge
assert 'ProtectedData.Protect' in bridge and 'ProtectedData.Unprotect' in bridge
assert 'psi.ArgumentList.Add' in bridge
assert 'NormalizeEvent' in bridge and 'agent.resume' in bridge
assert 'resume_not_available' not in bridge
for agent in ("claude", "codex", "gemini"):
    assert f'"{agent}"' in bridge
assert "offline" in readme.lower() and "DPAPI" in readme
print("static contract checks passed")
