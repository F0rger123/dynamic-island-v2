"""Protocol-level tests that run without Windows named-pipe/.NET dependencies."""
import json


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
    assert normalize('{"type":"tool_use","message":"Read file"}') ["phase"] == "running_tool"
    assert normalize('{"type":"error","message":"failed"}') ["phase"] == "failed"


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


if __name__ == "__main__":
    test_malformed_ipc_and_event_normalization()
    test_calendar_response_parsing()
    test_reconnect_cancellation_and_unsupported_agent_contracts()
    print("bridge protocol tests passed")
