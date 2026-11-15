"""Offline MCP adapter tests; no DD2 installation or Named Pipe required."""
import json
import queue
import unittest

from ddcli_mcp import GameSession


class FakePipe:
    def __init__(self, inbox):
        self.inbox = inbox
        self.writes = []
        self.closed = False

    def write(self, data):
        value = json.loads(data.decode("utf-8"))
        self.writes.append(value)
        self.inbox.put(("message", {
            "type": "state",
            "frontend_state": "main_menu",
            "available_actions": [{"action": "new_expedition"}],
        }))
        return len(data)

    def close(self):
        self.closed = True


class BridgeSessionTests(unittest.TestCase):
    def setUp(self):
        self.game = GameSession("fake-pipe", timeout=1.0)
        self.game.pipe = FakePipe(self.game.inbox)

    def test_state_request_uses_native_command(self):
        result = self.game._exchange("state")
        self.assertEqual(
            self.game.pipe.writes,
            [{"type": "command", "action": "state"}],
        )
        self.assertEqual(result["messages"][-1]["frontend_state"], "main_menu")

    def test_action_arguments_pass_through_unmodified(self):
        self.game._exchange("skill", {
            "performer_guid": 12, "skill_id": "Strike", "target_guid": 23,
        })
        self.assertEqual(self.game.pipe.writes[-1], {
            "type": "command", "action": "skill",
            "performer_guid": 12, "skill_id": "Strike", "target_guid": 23,
        })

    def test_cannot_override_command_envelope(self):
        with self.assertRaises(ValueError):
            self.game._exchange("state", {"type": "debug"})

    def test_event_cursor_and_history(self):
        self.game.inbox.put(("message", {
            "type": "event", "event": "actor_killed", "target_guid": 23,
        }))
        latest = self.game.events_since()
        self.assertEqual(len(latest["messages"]), 1)
        self.assertEqual(latest["latest_sequence"], 1)
        self.assertEqual(self.game.events_since(since=1)["messages"], [])

    def test_disconnection_exposed(self):
        self.game.inbox.put(("disconnect", "broken pipe"))
        with self.assertRaises(RuntimeError):
            self.game.status()
        self.assertIsNone(self.game.pipe)


if __name__ == "__main__":
    unittest.main()
