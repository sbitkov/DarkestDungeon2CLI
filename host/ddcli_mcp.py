"""MCP stdio transport for the existing DDCLI host and Named Pipe protocol.

Only the game-facing wire protocol is reused; no extra game observations are
collected. stdout is reserved exclusively for the MCP SDK.
"""

from collections import deque
from contextlib import suppress
from threading import Lock, Thread
import json
import queue
import time

from mcp.server import MCPServer


class BridgeDisconnected(RuntimeError):
    pass


class GameSession:
    """One client connection and one serialized command stream."""

    def __init__(self, pipe_path, timeout=45.0):
        self.pipe_path = pipe_path
        self.timeout = timeout
        self.pipe = None
        self.inbox = queue.Queue(maxsize=512)
        self.lock = Lock()
        self.events = deque(maxlen=2048)
        self.sequence = 0
        self.snapshot = None
        self.snapshot_sequence = 0
        # A cached boundary is historical after a state-changing command is sent.
        # Only a subsequent bridge-emitted READY/state can restore its freshness.
        self.snapshot_stale = False
        self.last_exchange = None
        self.bridge_version = None
        self.activity = None
        self.disconnected = None
        self.reader = None

    def _reader(self):
        # Reuse the RC1 host's Windows PeekNamedPipe reader.  A blocking
        # unbuffered readline() worked for the initial READY but can stall
        # subsequent messages on Windows named pipes.
        from host import _pipe_reader_worker
        _pipe_reader_worker(self.pipe, self.inbox)

    def _connect(self):
        if self.pipe is not None:
            return
        # Never log to stdout: it is the MCP protocol channel.
        try:
            self.pipe = open(self.pipe_path, "r+b", buffering=0)
        except OSError as exc:
            raise BridgeDisconnected(
                f"Cannot open DDCLI Named Pipe {self.pipe_path}: {exc}"
            ) from exc
        self.disconnected = None
        self.reader = Thread(target=self._reader, name="ddcli-mcp-pipe", daemon=True)
        self.reader.start()
        # The bridge initially reports its authoritative READY boundary.
        # Do not dispatch game commands before the initial snapshot arrives.
        deadline = time.monotonic() + self.timeout
        while True:
            message = self._next(deadline)
            if message.get("type") in ("ready", "state"):
                break
            if message.get("type") == "status" and message.get("activity") == "driving":
                break

    def _record(self, message):
        kind = message.get("type")
        if kind == "bridge_info":
            self.bridge_version = message.get("version")
        if kind == "status":
            self.activity = message.get("activity")
        if kind == "ready":
            self.activity = None
        # An inn travelogue query is read-only. Its inn_travelogue_state
        # payload contains journal entries, NOT a frontend decision boundary.
        # It must not displace the last authoritative inn READY.
        if isinstance(kind, str) and (
            kind in ("ready", "state")
            or (kind.endswith("_state") and kind != "inn_travelogue_state")
        ):
            self.snapshot = message
            self.snapshot_sequence = self.sequence + 1
            self.snapshot_stale = False
        self.sequence += 1
        self.events.append({"sequence": self.sequence, "message": message})

    def _next(self, deadline):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("No DDCLI response within the command timeout")
        try:
            kind, value = self.inbox.get(timeout=remaining)
        except queue.Empty as exc:
            raise TimeoutError("No DDCLI response within the command timeout") from exc
        if kind in ("disconnect", "reader_error"):
            self.disconnected = value
            with suppress(Exception):
                self.pipe.close()
            self.pipe = None
            raise BridgeDisconnected(value)
        self._record(value)
        return value

    def _drain(self):
        while True:
            try:
                kind, value = self.inbox.get_nowait()
            except queue.Empty:
                return
            if kind in ("disconnect", "reader_error"):
                self.disconnected = value
                with suppress(Exception):
                    self.pipe.close()
                self.pipe = None
                raise BridgeDisconnected(value)
            self._record(value)

    def _send(self, action, arguments=None):
        arguments = arguments or {}
        if not isinstance(arguments, dict):
            raise ValueError("arguments must be a JSON object")
        if "type" in arguments or "action" in arguments:
            raise ValueError("arguments cannot override type or action")
        payload = {"type": "command", "action": action, **arguments}
        data = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
        try:
            self.pipe.write((data + "\n").encode("utf-8"))
        except OSError as exc:
            raise BridgeDisconnected(f"DDCLI send failed: {exc}") from exc

    def _exchange(self, action, arguments=None):
        """Return observed evidence, never infer exactly-once execution from a READY.

        The v0.0.56 bridge does not echo a command ID. A timeout after sending
        can mean the game executed the action but did not produce a new READY.
        Return an explicit unknown result with all observed events instead of
        raising and losing the event cursor. Do not automatically retry.
        """
        with self.lock:
            self._connect()
            self._drain()
            start_sequence = self.sequence
            read_only = action in ("state", "travelogue")
            # A write failure can occur after some bytes reached the pipe.
            # Treat it as an uncertain outcome, not as a safe failure.
            transport_failure = None
            try:
                self._send(action, arguments)
            except BridgeDisconnected as exc:
                transport_failure = exc
            if not read_only:
                self.snapshot_stale = True

            deadline = time.monotonic() + self.timeout
            messages = []
            response = None
            while transport_failure is None:
                try:
                    message = self._next(deadline)
                except (TimeoutError, BridgeDisconnected) as exc:
                    transport_failure = exc
                    break
                messages.append(message)
                kind = message.get("type")
                if kind == "error":
                    response = "bridge_rejected"
                    break
                if kind == "ready":
                    response = "ready_observed"
                    break
                if kind in (
                    "state", "map_state", "hero_inspection", "inventory_state",
                    "equipment_state", "inn_hire_state", "inn_items_state",
                    "inn_route_choice_state", "inn_wainwright_state",
                    "inn_mastery_state", "inn_store_state",
                    "inn_travelogue_state", "inn_end_expedition_state",
                    "hospital_state", "store_state",
                ):
                    response = "state_observed"
                    break
                # Driving remains interruptible and has no terminal READY.
                if kind == "status" and message.get("activity") == "driving":
                    response = "activity_observed"
                    break

            bridge_error = next(
                (m for m in messages if m.get("type") == "error"), None
            )
            error = bridge_error
            if transport_failure is not None:
                # The pipe write has returned, but completion is unconfirmed.
                # Crucially, this is not a bridge rejection or a safe retry.
                error = {
                    "type": "transport_error",
                    "code": "command_outcome_unknown",
                    "message": str(transport_failure),
                }
                response = "unknown"

            fresh_types = {
                "ready", "state", "map_state", "hero_inspection",
                "inventory_state", "equipment_state", "inn_hire_state",
                "inn_items_state", "inn_route_choice_state",
                "inn_wainwright_state", "inn_mastery_state",
                "inn_store_state", "inn_travelogue_state",
                "inn_end_expedition_state", "hospital_state", "store_state",
            }
            refresh_succeeded = error is None and any(
                m.get("type") in fresh_types for m in messages
            )
            current_frontend_observed = any(
                m.get("type") in ("ready", "state")
                or (
                    str(m.get("type", "")).endswith("_state")
                    and m.get("type") != "inn_travelogue_state"
                )
                for m in messages
            )
            result = {
                "messages": messages,
                "start_sequence": start_sequence,
                "latest_sequence": self.sequence,
                "response_kind": response,
                # A bridge READY is evidence of a boundary, not a correlated
                # completion ACK for this specific action.
                "command_completion_confirmed": False,
                "retry_safe": read_only,
                "frontend_state": (self.snapshot or {}).get("frontend_state"),
                "frontend_state_source": (
                    "fresh" if refresh_succeeded and current_frontend_observed
                    else "last_observed"
                ),
                "read_only_query": action == "travelogue",
                "frontend_transition": (
                    "none" if action == "travelogue" and error is None
                    else "not_determined"
                ),
                "snapshot_sequence": self.snapshot_sequence,
                "snapshot_stale": self.snapshot_stale,
                "refresh_succeeded": refresh_succeeded,
                "error": error,
                "activity": self.activity,
            }
            if response == "unknown":
                result["recovery_note"] = (
                    "Command was sent but its outcome is unknown. Do not "
                    "blindly retry. Inspect ddcli_events(since=start_sequence) "
                    "and request ddcli_state where supported before deciding."
                )
            self.last_exchange = {
                "action": action,
                "start_sequence": start_sequence,
                "latest_sequence": self.sequence,
                "response_kind": response,
            }
            return result

    def status(self):
        with self.lock:
            self._connect()
            self._drain()
            return {
                "connected": self.pipe is not None and self.disconnected is None,
                "pipe_path": self.pipe_path,
                "bridge_version": self.bridge_version,
                "frontend_state": (self.snapshot or {}).get("frontend_state"),
                "activity": self.activity,
                "latest_sequence": self.sequence,
                "snapshot_sequence": self.snapshot_sequence,
                "snapshot_stale": self.snapshot_stale,
                "last_exchange": self.last_exchange,
                "last_disconnect": self.disconnected,
            }

    def snapshot_status(self):
        """Return a cached player-visible boundary without sending a command."""
        with self.lock:
            self._connect()
            self._drain()
            return {
                "snapshot": self.snapshot,
                "snapshot_sequence": self.snapshot_sequence,
                "latest_sequence": self.sequence,
                "snapshot_stale": self.snapshot_stale,
                "source": "last_observed",
                "fresh_query": False,
                "note": (
                    "This is the most recent bridge-emitted snapshot. It is not "
                    "a new authoritative query; call ddcli_state for a refresh "
                    "where the bridge supports one. After sending an action, "
                    "a cached READY is not proof that the same boundary is "
                    "still active."
                ),
            }

    def events_since(self, since=0, limit=100):
        with self.lock:
            if self.pipe is not None:
                self._drain()
            limit = max(1, min(int(limit), 300))
            if since < 0:
                raise ValueError("since must be nonnegative")
            oldest = self.events[0]["sequence"] if self.events else self.sequence + 1
            found = [entry for entry in self.events if entry["sequence"] > since]
            return {
                "messages": found[:limit],
                "latest_sequence": self.sequence,
                "oldest_sequence": oldest,
                "history_gap": since > 0 and since < oldest - 1,
                "has_more": len(found) > limit,
            }


def run_mcp(pipe_path):
    game = GameSession(pipe_path)
    mcp = MCPServer("ddcli", instructions=(
        "Use DDCLI as your only interface for live Darkest Dungeon II game state "
        "and actions. ddcli_command sends bridge-native commands; obtain "
        "available_actions from READY or ddcli_state. Combat uses action='skill' "
        "with performer_guid, skill_id, target_guid (choose a target from "
        "the current skill's targets). A timeout means UNKNOWN execution, "
        "not a failed action: inspect ddcli_events and fresh state before "
        "considering another command. Cached snapshots may be stale. "
        "Do not infer hidden game state. Only one agent may control the game."
    ))

    @mcp.tool()
    def ddcli_status() -> dict:
        """Return bridge connection status and last observed frontend (no gameplay action)."""
        return game.status()

    @mcp.tool()
    def ddcli_state() -> dict:
        """Request an authoritative live player-visible frontend state from DD2Bridge."""
        return game._exchange("state")

    @mcp.tool()
    def ddcli_command(action: str, arguments: dict | None = None) -> dict:
        """Send a bridge action. Combat: skill with performer_guid, skill_id, target_guid. A timeout returns unknown; do not retry blindly."""
        if not action or not isinstance(action, str):
            raise ValueError("action must be a nonempty string")
        if action in ("quit", "raw_actions", "help"):
            raise ValueError("This is a local CLI command, not a bridge action")
        return game._exchange(action, arguments)

    @mcp.tool()
    def ddcli_snapshot() -> dict:
        """Get the last observed READY/state, explicitly labeled cached, without querying unsupported frontends."""
        return game.snapshot_status()

    @mcp.tool()
    def ddcli_events(since: int = 0, limit: int = 100) -> dict:
        """Fetch recorded player-visible bridge messages after a sequence cursor."""
        return game.events_since(since, limit)

    mcp.run(transport="stdio")
