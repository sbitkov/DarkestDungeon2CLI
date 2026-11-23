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

    def test_rejected_refresh_is_not_falsely_fresh(self):
        self.game.snapshot = {
            "type": "ready", "frontend_state": "inn",
            "available_actions": [{"action": "hire"}],
        }
        self.game.snapshot_sequence = 5

        class RejectingPipe(FakePipe):
            def write(self, data):
                self.writes.append(json.loads(data.decode("utf-8")))
                self.inbox.put(("message", {
                    "type": "error", "code": "command_rejected",
                    "message": "no stable decision boundary is available",
                }))
                return len(data)

        self.game.pipe = RejectingPipe(self.game.inbox)
        result = self.game._exchange("state")
        self.assertFalse(result["refresh_succeeded"])
        self.assertEqual(result["frontend_state_source"], "last_observed")
        self.assertEqual(result["frontend_state"], "inn")
        self.assertEqual(result["error"]["code"], "command_rejected")
        cached = self.game.snapshot_status()
        self.assertEqual(cached["snapshot_sequence"], 5)
        self.assertFalse(cached["fresh_query"])
        self.assertEqual(cached["snapshot"]["available_actions"][0]["action"], "hire")

    def test_travelogue_query_does_not_replace_inn_ready(self):
        self.game.snapshot = {
            "type": "ready", "frontend_state": "inn",
            "available_actions": [{"action": "store"}, {"action": "travelogue"}],
        }
        self.game.snapshot_sequence = 2

        class TraveloguePipe(FakePipe):
            def write(self, data):
                self.writes.append(json.loads(data.decode("utf-8")))
                self.inbox.put(("message", {
                    "type": "inn_travelogue_state",
                    "inn_name": "The Inn", "journal": [],
                }))
                return len(data)

        self.game.pipe = TraveloguePipe(self.game.inbox)
        response = self.game._exchange("travelogue")
        self.assertEqual(response["messages"][0]["type"], "inn_travelogue_state")
        self.assertTrue(response["read_only_query"])
        self.assertEqual(response["frontend_transition"], "none")
        self.assertEqual(response["frontend_state"], "inn")
        self.assertEqual(response["frontend_state_source"], "last_observed")
        self.assertEqual(self.game.snapshot_status()["snapshot_sequence"], 2)
        self.assertEqual(
            self.game.snapshot_status()["snapshot"]["available_actions"][0]["action"],
            "store",
        )

    def test_timeout_after_side_effect_preserves_events_and_unknown_outcome(self):
        class UnconfirmedPipe(FakePipe):
            def write(self, data):
                self.writes.append(json.loads(data.decode("utf-8")))
                self.inbox.put(("message", {
                    "type": "event", "event": "skill_used",
                    "skill_id": "pass_heal",
                }))
                # Native action may have executed; no READY follows.
                return len(data)

        self.game.pipe = UnconfirmedPipe(self.game.inbox)
        self.game.timeout = 0.01
        response = self.game._exchange("skill", {
            "performer_guid": 248,
            "skill_id": "pass_heal",
            "target_guid": 248,
        })
        self.assertEqual(response["response_kind"], "unknown")
        self.assertEqual(response["error"]["code"], "command_outcome_unknown")
        self.assertFalse(response["retry_safe"])
        self.assertFalse(response["command_completion_confirmed"])
        self.assertTrue(response["snapshot_stale"])
        self.assertEqual(response["latest_sequence"], 1)
        self.assertEqual(response["messages"][0]["event"], "skill_used")
        self.assertFalse(self.game.events_since(0)["history_gap"])
        self.assertEqual(self.game.status()["last_exchange"]["response_kind"], "unknown")

    def test_ready_after_action_restores_nonstale_boundary(self):
        self.game.snapshot = {
            "type": "ready", "frontend_state": "combat",
            "available_actions": [{"skill_id": "pass_heal"}],
        }
        self.game.snapshot_sequence = 7
        response = self.game._exchange("skill", {
            "performer_guid": 248,
            "skill_id": "pass_heal",
            "target_guid": 248,
        })
        self.assertEqual(response["response_kind"], "state_observed")
        self.assertFalse(response["command_completion_confirmed"])
        self.assertFalse(response["snapshot_stale"])
        self.assertEqual(response["frontend_state"], "main_menu")

    def test_snapshot_marked_stale_after_unconfirmed_command(self):
        class SilentPipe(FakePipe):
            def write(self, data):
                self.writes.append(json.loads(data.decode("utf-8")))
                return len(data)

        self.game.snapshot = {
            "type": "ready", "frontend_state": "combat",
            "available_actions": [{"skill_id": "pass_heal"}],
        }
        self.game.snapshot_sequence = 7
        self.game.pipe = SilentPipe(self.game.inbox)
        self.game.timeout = 0.01
        response = self.game._exchange("skill", {
            "performer_guid": 248,
            "skill_id": "pass_heal",
            "target_guid": 248,
        })
        cached = self.game.snapshot_status()
        self.assertEqual(response["response_kind"], "unknown")
        self.assertTrue(cached["snapshot_stale"])
        self.assertEqual(cached["snapshot_sequence"], 7)
        self.assertEqual(cached["snapshot"]["frontend_state"], "combat")

    def test_bridge_rejection_is_not_transport_unknown(self):
        class RejectingPipe(FakePipe):
            def write(self, data):
                self.writes.append(json.loads(data.decode("utf-8")))
                self.inbox.put(("message", {
                    "type": "error",
                    "code": "unknown_action",
                    "message": "Unknown action",
                }))
                return len(data)

        self.game.pipe = RejectingPipe(self.game.inbox)
        response = self.game._exchange("not_an_action")
        self.assertEqual(response["response_kind"], "bridge_rejected")
        self.assertEqual(response["error"]["code"], "unknown_action")
        self.assertFalse(response["refresh_succeeded"])

    def test_read_only_refresh_preserves_boundary_staleness_until_state(self):
        class QueryPipe(FakePipe):
            def write(self, data):
                value = json.loads(data.decode("utf-8"))
                self.writes.append(value)
                if value["action"] == "travelogue":
                    self.inbox.put(("message", {
                        "type": "inn_travelogue_state",
                        "journal": [],
                    }))
                else:
                    self.inbox.put(("message", {
                        "type": "state", "frontend_state": "expedition_results",
                    }))
                return len(data)

        self.game.pipe = QueryPipe(self.game.inbox)
        self.game.snapshot_stale = True
        self.game.snapshot = {"type": "ready", "frontend_state": "combat"}
        self.game.snapshot_sequence = 4
        result = self.game._exchange("travelogue")
        self.assertEqual(result["response_kind"], "state_observed")
        self.assertTrue(result["snapshot_stale"])
        self.assertEqual(self.game.snapshot_sequence, 4)
        refreshed = self.game._exchange("state")
        self.assertFalse(refreshed["snapshot_stale"])
        self.assertEqual(refreshed["frontend_state"], "expedition_results")

    def test_disconnection_exposed(self):
        self.game.inbox.put(("disconnect", "broken pipe"))
        with self.assertRaises(RuntimeError):
            self.game.status()
        self.assertIsNone(self.game.pipe)


if __name__ == "__main__":
    unittest.main()
