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
        self.bridge_version = None
        self.activity = None
        self.disconnected = None
        self.reader = None

    def _reader(self):
        try:
            while True:
                line = self.pipe.readline()
                if not line:
                    raise BridgeDisconnected("DD2Bridge closed the Named Pipe")
                message = json.loads(line.decode("utf-8"))
                self.inbox.put(("message", message))
        except Exception as exc:
            self.inbox.put(("disconnect", str(exc)))

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
        if isinstance(kind, str) and (kind in ("ready", "state") or kind.endswith("_state")):
            # Preserve *exactly* the bridge's player-facing data, not an
            # invented or expanded interpretation of game internals.
            self.snapshot = message
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
        if kind == "disconnect":
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
            if kind == "disconnect":
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
        with self.lock:
            self._connect()
            self._drain()
            self._send(action, arguments)
            deadline = time.monotonic() + self.timeout
            messages = []
            while True:
                message = self._next(deadline)
                messages.append(message)
                kind = message.get("type")
                if kind == "error":
                    break
                if kind == "ready" or kind in (
                    "state", "map_state", "hero_inspection", "inventory_state",
                    "equipment_state", "inn_hire_state", "inn_items_state",
                    "inn_route_choice_state", "inn_wainwright_state",
                    "inn_mastery_state", "inn_store_state",
                    "inn_travelogue_state", "inn_end_expedition_state",
                    "hospital_state", "store_state",
                ):
                    break
                # Driving remains interruptible. The 'status' event gives
                # control back rather than waiting for a distant READY.
                if kind == "status" and message.get("activity") == "driving":
                    break
            return {
                "messages": messages,
                "latest_sequence": self.sequence,
                "frontend_state": (self.snapshot or {}).get("frontend_state"),
                "activity": self.activity,
            }

    def status(self):
        with self.lock:
            if self.pipe is not None:
                self._drain()
            return {
                "connected": self.pipe is not None and self.disconnected is None,
                "pipe_path": self.pipe_path,
                "bridge_version": self.bridge_version,
                "frontend_state": (self.snapshot or {}).get("frontend_state"),
                "activity": self.activity,
                "latest_sequence": self.sequence,
                "last_disconnect": self.disconnected,
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
        "available_actions with ddcli_state. Do not infer hidden game state. "
        "Only one agent may control the game at a time."
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
        """Execute a native DD2Bridge action. Pass argument fields from available_actions."""
        if not action or not isinstance(action, str):
            raise ValueError("action must be a nonempty string")
        if action in ("quit", "raw_actions", "help"):
            raise ValueError("This is a local CLI command, not a bridge action")
        return game._exchange(action, arguments)

    @mcp.tool()
    def ddcli_events(since: int = 0, limit: int = 100) -> dict:
        """Fetch recorded player-visible bridge messages after a sequence cursor."""
        return game.events_since(since, limit)

    mcp.run(transport="stdio")
