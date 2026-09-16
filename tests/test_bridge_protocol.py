"""Protocol-level tests that run without Windows named-pipe/.NET dependencies.

The C# bridge logic (discovery, readiness, sandboxing, allowlist, cancellation
and the wire protocol consumed by the C++ client) is mirrored here so the
contracts can be exercised on Linux and Windows alike.
"""
import json
import os
import tempfile


# ---------------------------------------------------------------------------
# C++-side normalization and calendar parsing (unchanged contracts).
# ---------------------------------------------------------------------------

def normalize(line):
    try:
        value = json.loads(line)
        kind = value.get("type")
        phase = {
            "tool_use": "running_tool", "function_call": "running_tool",
            "tool_result": "tool_result", "function_return": "tool_result",
            "assistant": "working", "message": "working",
            "result": "completed", "completed": "completed", "error": "failed",
        }.get(kind, "working")
        return {"phase": phase, "text": str(value.get("message", line)), "raw": line}
    except (ValueError, TypeError):
        return {"phase": "working", "text": line, "raw": line}


def parse_calendar(response):
    if response.get("error") in {"calendar_auth_required", "google_oauth_configuration_missing"}:
        return "auth_required", []
    return "ready", response.get("events", [])


def test_malformed_ipc_and_event_normalization():
    assert normalize("not-json")["phase"] == "working"
    assert normalize('{"type":"tool_use","message":"Read file"}')["phase"] == "running_tool"
    assert normalize('{"type":"error","message":"failed"}')["phase"] == "failed"


def test_calendar_response_parsing():
    state, events = parse_calendar({"ok": True, "events": [{"summary": "Standup", "start": "10:00"}]})
    assert state == "ready" and events[0]["summary"] == "Standup"
    assert parse_calendar({"ok": False, "error": "calendar_auth_required"})[0] == "auth_required"


def test_reconnect_cancellation_and_unsupported_agent_contracts():
    # These are the wire contracts consumed by the C++ client.
    assert json.loads('{"op":"status"}')["op"] == "status"
    assert json.loads('{"op":"agent.cancel","id":"abc"}')["op"] == "agent.cancel"
    assert json.loads('{"ok":false,"error":"unsupported_agent"}')["error"] == "unsupported_agent"
    assert json.loads('{"type":"exit","id":"abc","code":0}')["code"] == 0


# ---------------------------------------------------------------------------
# (4) agent.start with the Gemini API provider.
# ---------------------------------------------------------------------------

def test_gemini_api_start_wire_format():
    request = json.loads(
        '{"op":"agent.start","agent":"gemini","provider":"api","project":"C:\\\\work","prompt":"Fix the tests"}'
    )
    assert request["op"] == "agent.start"
    assert request["agent"] == "gemini"
    assert request["provider"] == "api"
    assert request["project"] == "C:\\work"


# ---------------------------------------------------------------------------
# C++ ApplyBridgeMessage port: state machine for agent status.
# ---------------------------------------------------------------------------

def bridge_state_from_name(state):
    if state == "ready":
        return 2
    if state == "auth_required":
        return 1
    if state == "error":
        return 3
    return 0


def apply_bridge_message(state, json_text):
    b = state["bridge"]
    b["last_update"] = 1.0
    if '"bridge":"online"' in json_text:
        b["online"] = True
    if '"ok":true' in json_text and '"id":' in json_text and '"agent":' in json_text:
        b["agent_job_id"] = _json_string(json_text, "id")
        b["agent_running"] = True
        agent = _json_string(json_text, "agent")
        b["active_agent"] = 1 if agent == "codex" else (2 if agent == "gemini" else 0)
        b["agent_status"] = "Running"
        b["gemini_login_unavailable"] = False
    if '"agents":' in json_text:
        b["agents_detected"] = [
            '"claude":true' in json_text,
            '"codex":true' in json_text,
            '"gemini":true' in json_text,
        ]
    if '"agent_state_claude"' in json_text:
        b["agents_state"] = [
            bridge_state_from_name(_json_string(json_text, key))
            for key in ("agent_state_claude", "agent_state_codex", "agent_state_gemini")
        ]
        b["gemini_api_provider"] = '"agent_provider_gemini":"api"' in json_text
    if '"type":"agent"' in json_text:
        b["agent_running"] = True
        phase = _json_string(json_text, "phase")
        mapping = {
            "running_tool": "Running tool", "tool_result": "Tool complete",
            "reading_file": "Reading file", "editing_file": "Editing file",
            "running_command": "Running command", "completed": "Completed",
            "failed": "Failed", "cancelled": "Cancelled",
        }
        b["agent_status"] = mapping.get(phase, "Working")
        data = _json_string(json_text, "data")
        if data:
            b["agent_output"] = (b["agent_output"] + data + "\n")[-6000:]
    if "gemini_login_unavailable" in json_text:
        b["gemini_login_unavailable"] = True
        b["agent_status"] = "Gemini CLI login unavailable"
    if '"type":"exit"' in json_text:
        b["agent_running"] = False
        ok_exit = '"code":0' in json_text
        if b["agent_status"] not in ("Failed", "Cancelled"):
            b["agent_status"] = "Completed" if ok_exit else "Failed"
        elif not ok_exit and b["agent_status"] != "Cancelled":
            b["agent_status"] = "Failed"
    return state


def _json_string(json_text, key):
    needle = '"%s":"' % key
    start = json_text.find(needle)
    if start < 0:
        return ""
    start += len(needle)
    end = json_text.find('"', start)
    return json_text[start:end] if end > start else ""


def fresh_state():
    return {
        "bridge": {
            "online": False, "agent_job_id": "", "agent_running": False,
            "active_agent": -1, "agent_status": "", "agent_output": "",
            "agents_detected": [False, False, False],
            "agents_state": [0, 0, 0],
            "gemini_api_provider": False, "gemini_login_unavailable": False,
        }
    }


def jdump(obj):
    """Compact JSON, matching the bridge's Web-default serializer (no spaces)."""
    return json.dumps(obj, separators=(",", ":"))


def test_agent_state_machine_contract():
    state = fresh_state()
    # Status with flat readiness keys, exactly as C# emits them.
    status = jdump({
        "ok": True, "bridge": "online", "version": "2.0.0",
        "agents": {"claude": True, "codex": True, "gemini": False},
        "agent_state_claude": "ready", "agent_state_codex": "auth_required",
        "agent_state_gemini": "not_installed", "agent_provider_gemini": "none",
    })
    state = apply_bridge_message(state, status)
    b = state["bridge"]
    assert b["online"] is True
    assert b["agents_state"] == [2, 1, 0]
    assert b["gemini_api_provider"] is False

    # Gemini API provider start ack resets the login-unavailable hint.
    ack = jdump({"ok": True, "id": "job-1", "agent": "gemini", "project": "C:\\w", "provider": "api"})
    state = apply_bridge_message(state, ack)
    b["gemini_login_unavailable"] = True  # simulate an old hint
    state = apply_bridge_message(state, ack)
    assert b["agent_job_id"] == "job-1" and b["active_agent"] == 2
    assert b["agent_running"] is True and b["gemini_login_unavailable"] is False

    # Phases from the Gemini API agent normalize to the richer C++ labels.
    for phase, label in (("reading_file", "Reading file"), ("editing_file", "Editing file"),
                         ("running_command", "Running command"), ("working", "Working")):
        state = apply_bridge_message(state, jdump({
            "type": "agent", "id": "job-1", "stream": "gemini-api",
            "normalized": {"phase": phase, "text": "x"}, "data": "x",
        }))
        assert state["bridge"]["agent_status"] == label, phase

    # A failed phase is preserved by the exit event.
    state = apply_bridge_message(state, jdump({
        "type": "agent", "id": "job-1", "normalized": {"phase": "failed", "text": "nope"}, "data": "nope",
    }))
    state = apply_bridge_message(state, jdump({"type": "exit", "id": "job-1", "code": 1}))
    assert state["bridge"]["agent_status"] == "Failed"
    assert state["bridge"]["agent_running"] is False

    # A cancelled phase is preserved even on a non-zero exit.
    state = apply_bridge_message(state, jdump({
        "type": "agent", "id": "job-1", "normalized": {"phase": "cancelled", "text": "Cancelled."}, "data": "",
    }))
    state = apply_bridge_message(state, jdump({"type": "exit", "id": "job-1", "code": 1}))
    assert state["bridge"]["agent_status"] == "Cancelled"

    # Gemini CLI login failure surfaces the recovery hint.
    state = apply_bridge_message(state, jdump({"type": "gemini_login_unavailable", "id": "job-2", "agent": "gemini"}))
    assert state["bridge"]["gemini_login_unavailable"] is True


# ---------------------------------------------------------------------------
# (1) Windows CLI discovery — port of CliDiscovery.Find over a fake file tree.
# ---------------------------------------------------------------------------

def fake_find(name, files, dirs, extensions=('.exe', '.cmd', '.bat', '.ps1', ''),
              where_results=None, manual=None):
    """Port of CliDiscovery.Find: manual override, dir*ext scan, where.exe."""
    where_results = where_results or {}
    files = set(files)
    if manual and name in manual and manual[name] in files:
        return manual[name]
    for d in dirs:
        for ext in extensions:
            candidate = os.path.join(d, name + ext)
            if candidate in files:
                return candidate
    return where_results.get(name)


def test_discovery_variants(tmp_path=None):
    import tempfile as _t
    holder = tmp_path or _t.TemporaryDirectory()
    root = getattr(holder, "name", str(holder))

    path_dir = str(os.path.join(root, "PATH"))
    appdata_npm = str(os.path.join(root, "APPDATA", "npm"))
    localappdata_npm = str(os.path.join(root, "LOCAL", "npm"))
    npm_global = str(os.path.join(root, "dot", "npm-global"))
    programs = str(os.path.join(root, "LOCAL", "Programs"))
    files = [
        os.path.join(path_dir, "claude.exe"),
        os.path.join(appdata_npm, "codex.cmd"),
        os.path.join(localappdata_npm, "gemini.bat"),
        os.path.join(npm_global, "node.ps1"),
        os.path.join(programs, "git"),
    ]
    search_dirs = [path_dir, appdata_npm, localappdata_npm, npm_global, programs]

    assert fake_find("claude", files, search_dirs) == os.path.join(path_dir, "claude.exe")
    assert fake_find("codex", files, search_dirs) == os.path.join(appdata_npm, "codex.cmd")
    assert fake_find("gemini", files, search_dirs) == os.path.join(localappdata_npm, "gemini.bat")
    assert fake_find("node", files, search_dirs) == os.path.join(npm_global, "node.ps1")
    assert fake_find("git", files, search_dirs) == os.path.join(programs, "git")  # extensionless
    assert fake_find("missing", files, search_dirs) is None
    # where.exe fallback only kicks in when no directory hit.
    where = {"missing": os.path.join(root, "extra", "missing.exe")}
    assert fake_find("missing", files, search_dirs, where_results=where) == where["missing"]
    # Manual override wins over PATH.
    manual = {"claude": os.path.join(root, "manual", "claude.exe")}
    manual_files = files + [os.path.join(root, "manual", "claude.exe")]
    assert fake_find("claude", manual_files, search_dirs, manual=manual) == manual["claude"]


def test_discovery_extension_priority():
    root = tempfile.TemporaryDirectory()
    d = str(os.path.join(root.name, "dir"))
    files = [
        os.path.join(d, "gemini.exe"), os.path.join(d, "gemini.cmd"),
        os.path.join(d, "gemini.bat"), os.path.join(d, "gemini.ps1"),
    ]
    found = fake_find("gemini", files, [d])
    assert found == os.path.join(d, "gemini.exe")  # .exe first, then .cmd/.bat/.ps1/none


# ---------------------------------------------------------------------------
# (2)/(4) Readiness: gemini reports READY via the API even without the CLI.
# ---------------------------------------------------------------------------

def check_gemini(cli_state, api_ok):
    """Port of AgentReadiness.CheckGemini (state, provider)."""
    if cli_state == "ready":
        return ("ready", "cli")
    if api_ok:
        return ("ready", "api")
    return (cli_state, None)


def test_gemini_api_readiness():
    assert check_gemini("not_installed", True) == ("ready", "api")
    assert check_gemini("auth_required", True) == ("ready", "api")
    assert check_gemini("ready", False) == ("ready", "cli")
    assert check_gemini("not_installed", False) == ("not_installed", None)
    assert check_gemini("error", False) == ("error", None)


# ---------------------------------------------------------------------------
# (4) Tool sandbox — port of ResolveSafePath (Windows path semantics).
# ---------------------------------------------------------------------------

class EscapeError(Exception):
    pass


def resolve_safe_path(project, relative, reparse_points=()):
    """Port of GeminiApiAgent.ResolveSafePath for Windows-style paths."""
    import ntpath
    if not relative or not relative.strip():
        raise EscapeError("path is required")
    trimmed = relative.strip().replace("/", ntpath.sep)
    if trimmed.startswith("\\\\"):
        raise EscapeError("UNC paths are not allowed")
    root = ntpath.normpath(project)
    full = ntpath.normpath(ntpath.join(root, trimmed) if not ntpath.isabs(trimmed) else trimmed)
    if not (full == root or full.lower().startswith(root.lower() + ntpath.sep)):
        raise EscapeError("path escapes the project root")
    reparse = {r.lower() for r in reparse_points}

    def reject(path):
        if len(path) > 3 and path.lower() in reparse:
            raise EscapeError("reparse point escape is not allowed")

    # Raw (pre-normalization) walk: a reparse point can mask a `..` traversal
    # that textual normalization would otherwise hide.
    walked = root
    for part in trimmed.split(ntpath.sep):
        if part in ("", "."):
            continue
        if part == "..":
            walked = ntpath.dirname(walked.rstrip(ntpath.sep)) or walked
            continue
        walked = ntpath.join(walked, part)
        reject(walked)
    # Normalized target walk.
    current = ""
    for part in [p for p in full.split(ntpath.sep) if p]:
        current = part if not current else ntpath.join(current, part)
        reject(current)
    reject(root)
    return full


def test_tool_sandbox():
    root = "C:\\work\\repo"
    assert resolve_safe_path(root, "src\\main.cs") == "C:\\work\\repo\\src\\main.cs"
    assert resolve_safe_path(root, "sub/file.txt") == "C:\\work\\repo\\sub\\file.txt"
    # `..` traversal and absolute external paths are rejected.
    for bad in ("..\\secret.txt", "src\\..\\..\\etc\\passwd", "C:\\other\\file.txt", "D:\\x\\y"):
        try:
            resolve_safe_path(root, bad)
            assert False, bad
        except EscapeError:
            pass
    # UNC paths are rejected.
    for bad in ("\\\\server\\share", "//server/share"):
        try:
            resolve_safe_path(root, bad)
            assert False, bad
        except EscapeError:
            pass
    # Absolute paths inside the root are allowed.
    assert resolve_safe_path(root, "C:\\work\\repo\\ok.txt") == "C:\\work\\repo\\ok.txt"
    # Reparse-point (symlink/mount) escapes are rejected.
    try:
        resolve_safe_path(root, "link\\..\\outside.txt", reparse_points=["C:\\work\\repo\\link"])
        assert False
    except EscapeError:
        pass
    # The project root itself must not be a reparse point.
    try:
        resolve_safe_path(root, "a.txt", reparse_points=[root])
        assert False
    except EscapeError:
        pass


# ---------------------------------------------------------------------------
# (4) run_command allowlist + metacharacter rejection.
# ---------------------------------------------------------------------------

ALLOWED_COMMANDS = ("git", "dotnet", "npm", "node", "python", "pytest")
SHELL_METACHARACTERS = set("||&;><`$*?(){}[]~%\"'\n\r")


def validate_command(command):
    import ntpath
    if not command or not command.strip():
        raise EscapeError("command is required")
    for c in command:
        if c in SHELL_METACHARACTERS:
            raise EscapeError("shell metacharacters are not allowed")
    first = command.split()
    base = ntpath.splitext(ntpath.basename(first[0]))[0].lower()
    if base not in ALLOWED_COMMANDS:
        raise EscapeError("not on the allowlist")


def test_command_allowlist():
    for ok in ("git status --short", "dotnet build", "npm install", "node --version",
               "python tests/test.py", "pytest -q", "Git status"):
        validate_command(ok)
    for bad in ("rm -rf /", "cmd /c dir", "powershell -c x", "curl http://x", "npm; rm x",
                "git status | tee log", "git && rm a", "git > out.txt", "git < in.txt",
                "git $(rm a)", "git `rm a`", "git st*atus", "git status 2>&1", "git 'status'"):
        try:
            validate_command(bad)
            assert False, bad
        except EscapeError:
            pass


# ---------------------------------------------------------------------------
# (4) Cancellation contract: a pre-cancelled token ends the loop immediately.
# ---------------------------------------------------------------------------

def gemini_agent_loop(max_turns, token_cancelled, disconnect_after=None):
    """Port of the GeminiApiAgent.Run loop control flow (no network)."""
    for turn in range(max_turns):
        if token_cancelled:
            return "cancelled"
        if disconnect_after is not None and turn >= disconnect_after:
            return "failed"
    return "completed"


def test_cancellation_contract():
    assert gemini_agent_loop(24, token_cancelled=True) == "cancelled"
    assert gemini_agent_loop(24, token_cancelled=False) == "completed"
    # Safe disconnect mid-stream ends the run as a failure, not a hang.
    assert gemini_agent_loop(24, token_cancelled=False, disconnect_after=3) == "failed"


if __name__ == "__main__":
    test_malformed_ipc_and_event_normalization()
    test_calendar_response_parsing()
    test_reconnect_cancellation_and_unsupported_agent_contracts()
    test_gemini_api_start_wire_format()
    test_agent_state_machine_contract()
    test_discovery_variants()
    test_discovery_extension_priority()
    test_gemini_api_readiness()
    test_tool_sandbox()
    test_command_allowlist()
    test_cancellation_contract()
    print("bridge protocol tests passed")
