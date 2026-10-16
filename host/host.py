import json
import os
import queue
import re
import sys
import threading
import time

import ctypes
import msvcrt

from datetime import datetime
from pathlib import Path


PIPE_PATH = r"\\.\pipe\dd2-agent"


class TeeTextIO:
    """Mirror host output to the console and a line-buffered transcript file."""

    def __init__(self, console_stream, transcript_stream):
        self._console_stream = console_stream
        self._transcript_stream = transcript_stream

    def write(self, text):
        self._console_stream.write(text)
        self._transcript_stream.write(text)
        # The bridge/agent interaction is diagnostic data: persist every write
        # immediately so a crash or hung transition does not lose the tail.
        self._transcript_stream.flush()
        return len(text)

    def flush(self):
        self._console_stream.flush()
        self._transcript_stream.flush()

    def isatty(self):
        return self._console_stream.isatty()

    @property
    def encoding(self):
        return getattr(self._console_stream, "encoding", "utf-8")

    def log_input(self, text):
        # input() writes the prompt through stdout, but terminal echo is not a
        # Python stdout write. Append only the entered text to the transcript so
        # the file contains the exact command line without duplicating it on
        # screen.
        self._transcript_stream.write(text + "\n")
        self._transcript_stream.flush()


def start_transcript():
    user_profile = os.environ.get("USERPROFILE")
    if user_profile:
        documents = Path(user_profile) / "Documents"
    else:
        documents = Path.home() / "Documents"

    documents.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    path = documents / f"DD2-host-{stamp}.txt"

    # utf-8-sig keeps the Russian transcript pleasant in Windows editors while
    # remaining normal UTF-8 for tooling. Line buffering plus explicit flush in
    # TeeTextIO makes the file useful even if the game/host later hangs.
    transcript = path.open(
        "w",
        encoding="utf-8-sig",
        buffering=1,
        newline="",
    )

    original_stdout = sys.stdout
    original_stderr = sys.stderr
    sys.stdout = TeeTextIO(original_stdout, transcript)
    sys.stderr = TeeTextIO(original_stderr, transcript)

    return path, transcript, original_stdout, original_stderr


def connect():
    print(f"Connecting to {PIPE_PATH}...")

    while True:
        try:
            pipe = open(PIPE_PATH, "r+b", buffering=0)
            print("Connected to DD2Bridge.")
            return pipe
        except OSError as e:
            print(
                f"Pipe connect failed: {type(e).__name__}: {e!r} "
                f"(winerror={getattr(e, 'winerror', None)})"
            )
            time.sleep(1)


def read_message(pipe):
    line = pipe.readline()

    if not line:
        raise ConnectionError("DD2Bridge disconnected")

    text = line.decode("utf-8").strip()

    if not text:
        return None

    return json.loads(text)


def send_message(pipe, message):
    text = json.dumps(
        message,
        ensure_ascii=False,
        separators=(",", ":"),
    )

    #print(f"HOST TX before write: {text}", flush=True)

    written = pipe.write((text + "\n").encode("utf-8"))

    #print(f"HOST TX after write: bytes={written}", flush=True)


class ActivityIndicator:
    """Console-only reusable activity spinner.

    The transcript gets one stable STATUS line when an activity starts; animated
    frames bypass TeeTextIO so control characters / redraws do not pollute logs.
    A READY boundary stops the activity. Non-boundary messages can temporarily
    clear the rendered frame and the spinner will redraw itself on the next tick.
    """

    _FRAMES = ("в ‹", "в ™", "в №", "в ё", "в ј", "в ґ", "в ¦", "в §", "в ‡", "в Џ")

    def __init__(self):
        self._lock = threading.Lock()
        self._stop_event = None
        self._thread = None
        self._label = None
        self._render_width = 0

    @staticmethod
    def _console_stream():
        stream = sys.stdout
        return getattr(stream, "_console_stream", stream)

    def _clear_line_locked(self):
        if self._render_width <= 0:
            return

        console = self._console_stream()
        console.write("\r" + (" " * self._render_width) + "\r")
        console.flush()
        self._render_width = 0

    def clear_line(self):
        with self._lock:
            self._clear_line_locked()

    def start(self, label):
        label = str(label or "").strip()
        if not label:
            return

        self.stop()

        # Keep one non-animated record in the transcript.
        print()
        print(f"STATUS: {label}")

        stop_event = threading.Event()
        self._stop_event = stop_event
        self._label = label

        def worker():
            frame_index = 0

            while not stop_event.wait(0.10):
                frame = self._FRAMES[frame_index % len(self._FRAMES)]
                frame_index += 1
                text = f"{frame} {label}"

                with self._lock:
                    if stop_event.is_set():
                        break

                    console = self._console_stream()
                    width = max(self._render_width, len(text))
                    console.write("\r" + text.ljust(width))
                    console.flush()
                    self._render_width = width

        self._thread = threading.Thread(
            target=worker,
            name="dd2-host-activity-indicator",
            daemon=True,
        )
        self._thread.start()

    def stop(self):
        stop_event = self._stop_event
        thread = self._thread

        self._stop_event = None
        self._thread = None
        self._label = None

        if stop_event is not None:
            stop_event.set()

        if thread is not None and thread.is_alive():
            thread.join(timeout=0.5)

        with self._lock:
            self._clear_line_locked()


def _hero_select_hero_lookup(snapshot):
    heroes = {}
    if not isinstance(snapshot, dict):
        return heroes

    for hero in snapshot.get("available_heroes", []) or []:
        if not isinstance(hero, dict):
            continue
        guid = hero.get("actor_guid")
        if guid is not None:
            heroes[guid] = hero

    return heroes


def _hero_select_label(hero):
    if not isinstance(hero, dict):
        return "<unknown>"

    guid = hero.get("actor_guid")
    name = hero.get("actor_name") or ""
    actor_id = hero.get("actor_data_id") or ""

    label = f"[{guid}]"
    if name:
        label += f" {name}"
    if actor_id:
        label += f" ({actor_id})"

    return label


def _print_hero_select_party(snapshot):
    heroes = _hero_select_hero_lookup(snapshot)
    party = snapshot.get("party", []) if isinstance(snapshot, dict) else []

    print()
    print("Party:")

    if not party:
        print("  none")
        return

    for slot in party:
        if not isinstance(slot, dict):
            print(f"  {slot}")
            continue

        slot_index = slot.get("slot")
        actor_guid = slot.get("actor_guid")

        if not actor_guid:
            print(f"  slot {slot_index}: empty")
            continue

        hero = heroes.get(actor_guid)
        label = _hero_select_label(hero) if hero else f"[{actor_guid}]"
        print(f"  slot {slot_index}: {label}")


def _print_raw_actions(snapshot):
    actions = snapshot.get("available_actions", []) if isinstance(snapshot, dict) else []

    print()
    print("RAW ACTIONS:")

    if not actions:
        print("  none")
        print()
        return

    for action in actions:
        if not isinstance(action, dict):
            print(f"  {action}")
            continue

        name = action.get("action", "<unknown>")
        parts = [name]

        for key in (
            "actor_guid",
            "path_id",
            "boss_id",
            "slot",
            "skill_id",
            "target_guid",
            "option_index",
            "direction",
            "player_slot",
            "trinket_slot",
            "parameters",
        ):
            if action.get(key) is not None:
                parts.append(f"{key}={action[key]}")

        print("  " + " ".join(parts))

    print()


def _path_actions_for_actor(snapshot, actor_guid):
    result = []

    if not isinstance(snapshot, dict):
        return result

    for action in snapshot.get("available_actions", []) or []:
        if (
            isinstance(action, dict)
            and action.get("action") == "set_hero_path"
            and action.get("actor_guid") == actor_guid
            and action.get("path_id")
        ):
            result.append(action)

    return result


def _read_paths_submenu(snapshot, hero_inspections, actor_guid):
    heroes = _hero_select_hero_lookup(snapshot)
    hero = heroes.get(actor_guid)

    if hero is None:
        print(f"No hero with actor_guid={actor_guid} is available here.")
        return "__back__"

    actions = _path_actions_for_actor(snapshot, actor_guid)

    if not actions:
        print(f"No path actions are available for {_hero_select_label(hero)}.")
        return "__back__"

    inspection = hero_inspections.get(actor_guid) or {}
    path_info = inspection.get("path") if isinstance(inspection, dict) else {}
    current_path = (
        path_info.get("current_path_id")
        if isinstance(path_info, dict)
        else None
    )

    print()
    print(f"PATHS вЂ” {_hero_select_label(hero)}")
    print()
    print(f"Current: {current_path or '<unknown; use inspect first>'}")
    print()

    for index, action in enumerate(actions, start=1):
        path_id = action["path_id"]
        marker = "  [CURRENT]" if path_id == current_path else ""
        print(f"  {index}. {path_id}{marker}")

    print()
    print("Enter number or path id; 'back' returns to HERO_SELECT.")

    while True:
        text = input("paths> ").strip()

        log_input = getattr(sys.stdout, "log_input", None)
        if callable(log_input):
            log_input(text)

        if not text:
            continue

        if text in {"back", "b", ".."}:
            return "__back__"

        selected = None

        if text.isdigit():
            index = int(text)
            if 1 <= index <= len(actions):
                selected = actions[index - 1]
        else:
            selected = next(
                (
                    action for action in actions
                    if action.get("path_id") == text
                ),
                None,
            )

        if selected is None:
            print("Unknown path. Enter its number/id or 'back'.")
            continue

        return {
            "type": "command",
            "action": "set_hero_path",
            "actor_guid": actor_guid,
            "path_id": selected["path_id"],
        }


def _print_skills_submenu(snapshot, inspection, actor_guid):
    heroes = _hero_select_hero_lookup(snapshot)
    hero = heroes.get(actor_guid)

    print()
    print(
        "SKILLS вЂ” "
        + (
            _hero_select_label(hero)
            if hero is not None
            else f"[{actor_guid}]"
        )
    )

    skills = inspection.get("skills") if isinstance(inspection, dict) else {}
    if not isinstance(skills, dict):
        skills = {}

    equipped = skills.get("equipped") or []
    available_base = skills.get("available_base") or []
    equipped_limit = skills.get("equipped_limit")

    print()
    if equipped_limit is not None:
        print(f"Equipped limit: {equipped_limit}")

    print()
    print("Equipped:")
    if equipped:
        for index, skill_id in enumerate(equipped, start=1):
            print(f"  {index}. {skill_id}")
    else:
        print("  none")

    print()
    print("Available base skills:")
    if available_base:
        equipped_set = set(equipped)
        for index, skill_id in enumerate(available_base, start=1):
            marker = "  [EQUIPPED]" if skill_id in equipped_set else ""
            print(f"  {index}. {skill_id}{marker}")
    else:
        print("  none")

    print()
    print(
        "Commands:\n"
        "  set <numbers or base_skill_ids...>\n"
        "  show\n"
        "  debug\n"
        "  back"
    )


def _resolve_skill_selection(inspection, tokens):
    skills = inspection.get("skills") if isinstance(inspection, dict) else {}
    if not isinstance(skills, dict):
        return None, "No skill inspection data is available."

    available_base = skills.get("available_base") or []
    if not isinstance(available_base, list):
        return None, "No canonical skill list is available."

    selected = []

    for token in tokens:
        if token.isdigit():
            index = int(token)
            if not 1 <= index <= len(available_base):
                return None, f"Skill number {index} is out of range."
            skill_id = available_base[index - 1]
        else:
            skill_id = token
            if skill_id not in available_base:
                return None, f"Unknown/unavailable base skill: {skill_id}"

        if skill_id in selected:
            return None, f"Duplicate skill: {skill_id}"

        selected.append(skill_id)

    equipped_limit = skills.get("equipped_limit")
    if (
        isinstance(equipped_limit, int)
        and equipped_limit > 0
        and len(selected) > equipped_limit
    ):
        return (
            None,
            f"Selected {len(selected)} skills; equip limit is {equipped_limit}.",
        )

    return selected, None


def _print_skill_diagnostics(inspection):
    skills = inspection.get("skills") if isinstance(inspection, dict) else {}
    if not isinstance(skills, dict):
        skills = {}

    debug_equipped = skills.get("debug_equipped") or []

    print()
    print("Equipped diagnostics:")

    if not debug_equipped:
        print("  none")
        print()
        return

    for item in debug_equipped:
        if not isinstance(item, dict):
            continue

        current_id = item.get("current_skill_id") or "<unknown>"
        base_id = item.get("base_skill_id") or "<unknown>"
        always = item.get("is_always_equipped")
        active_modifier = item.get("has_active_modifier")
        force = item.get("force_equip")
        can_unequip = item.get("can_unequip")

        print(
            f"  {base_id} "
            f"(current={current_id}) | "
            f"always={always} "
            f"active_modifier={active_modifier} "
            f"force={force} "
            f"can_unequip={can_unequip}"
        )

    print()


def _read_skills_context(session_state):
    actor_guid = session_state.get("skills_context")
    if actor_guid is None:
        return "__no_context__"

    snapshot = session_state.get("frontend_snapshot") or {}
    inspection = (
        session_state.get("hero_inspections", {}).get(actor_guid)
        or {}
    )

    text = input("skills> ").strip()

    log_input = getattr(sys.stdout, "log_input", None)
    if callable(log_input):
        log_input(text)

    if not text:
        return "__continue__"

    if text in {"back", "b", ".."}:
        session_state["skills_context"] = None
        return "__continue__"

    if text == "show":
        _print_skills_submenu(snapshot, inspection, actor_guid)
        return "__continue__"

    if text == "debug":
        _print_skill_diagnostics(inspection)
        return "__continue__"

    if text.startswith("set"):
        parts = text.split()
        if len(parts) < 2:
            print("Use: set <numbers or base_skill_ids...>")
            return "__continue__"

        selected, error = _resolve_skill_selection(
            inspection,
            parts[1:],
        )

        if error:
            print(error)
            return "__continue__"

        return {
            "type": "command",
            "action": "set_hero_skills",
            "actor_guid": actor_guid,
            "skill_ids": selected,
        }

    print("Skills commands: set <...>, show, debug, back")
    return "__continue__"


def _print_party_submenu(snapshot):
    print()
    print("PARTY")
    _print_hero_select_party(snapshot)

    print()
    print("Commands:")
    print("  set <slot> <actor_guid>")
    print("  clear <slot>")
    print("  heroes")
    print("  show")
    if snapshot.get("party_complete"):
        print("  confirm")
    print("  back")


def _print_party_heroes(snapshot):
    heroes = _hero_select_hero_lookup(snapshot)

    print()
    print("Heroes:")

    if not heroes:
        print("  none")
        print()
        return

    party = snapshot.get("party", []) if isinstance(snapshot, dict) else []
    slot_by_actor = {}

    for slot in party or []:
        if not isinstance(slot, dict):
            continue
        actor_guid = slot.get("actor_guid")
        slot_index = slot.get("slot")
        if actor_guid:
            slot_by_actor[actor_guid] = slot_index

    for actor_guid, hero in heroes.items():
        suffix = ""
        if actor_guid in slot_by_actor:
            suffix = f"  [slot {slot_by_actor[actor_guid]}]"
        print(f"  {_hero_select_label(hero)}{suffix}")

    print()


def _read_party_context(session_state):
    snapshot = session_state.get("frontend_snapshot") or {}

    text = input("party> ").strip()

    log_input = getattr(sys.stdout, "log_input", None)
    if callable(log_input):
        log_input(text)

    if not text:
        return "__continue__"

    if text in {"back", "b", ".."}:
        session_state["party_context"] = False
        return "__continue__"

    if text == "show":
        _print_party_submenu(snapshot)
        return "__continue__"

    if text == "heroes":
        _print_party_heroes(snapshot)
        return "__continue__"

    if text == "confirm":
        if not snapshot.get("party_complete"):
            print("Party is incomplete.")
            return "__continue__"

        session_state["party_context"] = False
        return {
            "type": "command",
            "action": "confirm_party",
        }

    parts = text.split()

    if len(parts) == 3 and parts[0] == "set":
        try:
            slot_index = int(parts[1])
            actor_guid = int(parts[2])
        except ValueError:
            print("Use: set <slot> <actor_guid>")
            return "__continue__"

        heroes = _hero_select_hero_lookup(snapshot)
        party = snapshot.get("party", []) if isinstance(snapshot, dict) else []
        valid_slots = {
            slot.get("slot")
            for slot in party
            if isinstance(slot, dict)
        }

        if slot_index not in valid_slots:
            print(f"Unknown party slot: {slot_index}")
            return "__continue__"

        if actor_guid not in heroes:
            print(f"Unknown/unavailable actor_guid: {actor_guid}")
            return "__continue__"

        return {
            "type": "command",
            "action": "set_party_member",
            "slot": slot_index,
            "actor_guid": actor_guid,
        }

    if len(parts) == 2 and parts[0] == "clear":
        try:
            slot_index = int(parts[1])
        except ValueError:
            print("Use: clear <slot>")
            return "__continue__"

        party = snapshot.get("party", []) if isinstance(snapshot, dict) else []
        valid_slots = {
            slot.get("slot")
            for slot in party
            if isinstance(slot, dict)
        }

        if slot_index not in valid_slots:
            print(f"Unknown party slot: {slot_index}")
            return "__continue__"

        return {
            "type": "command",
            "action": "clear_party_slot",
            "slot": slot_index,
        }

    print(
        "Party commands: set <slot> <actor_guid>, clear <slot>, "
        "heroes, show, confirm, back"
    )
    return "__continue__"


def _print_hero_select_commands(snapshot=None):
    print()
    print("Commands:")
    print("  inspect <actor_guid>")
    print("  paths <actor_guid>")
    print("  skills <actor_guid>")
    print("  party")
    if isinstance(snapshot, dict) and snapshot.get("party_complete"):
        print("  confirm_party")
    print("  state")
    print("  raw_actions")
    print("  quit")



def _equipment_item_text(item):
    if not isinstance(item, dict):
        return str(item)

    slot = item.get("slot")
    if item.get("empty"):
        return f"slot {slot}: empty"

    item_id = item.get("item_id") or "<unknown-id>"
    display_name = item.get("display_name") or item_id
    qty = item.get("qty")
    item_type = item.get("item_type") or ""
    locked = " [UNEQUIP INVALID]" if item.get("unequip_invalid") else ""

    suffix = ""
    if item_id and item_id != display_name:
        suffix += f" [{item_id}]"
    if qty is not None:
        suffix += f" x{qty}"
    if item_type:
        suffix += f" ({item_type})"

    return f"slot {slot}: {display_name}{suffix}{locked}"


def _print_equipment_snapshot(equipment):
    if not isinstance(equipment, dict):
        print()
        print("Equipment: unavailable")
        return

    print()
    print("Equipment:")

    player_items = equipment.get("player_inventory") or []
    print("  Player inventory (trinkets/combat items):")
    if not player_items:
        print("    none")
    else:
        for item in player_items:
            print("    " + _equipment_item_text(item))

    heroes = equipment.get("heroes") or []
    print("  Party:")
    if not heroes:
        print("    none")
        return

    for hero in heroes:
        if not isinstance(hero, dict):
            continue

        guid = hero.get("actor_guid")
        name = hero.get("actor_name") or ""
        actor_id = hero.get("actor_data_id") or ""

        label = f"[{guid}]"
        if name:
            label += f" {name}"
        if actor_id:
            label += f" ({actor_id})"

        print(f"    {label}")

        trinkets = hero.get("trinkets") or []
        print("      trinkets:")
        if not trinkets:
            print("        unavailable")
        else:
            for item in trinkets:
                print("        " + _equipment_item_text(item))

        combat_items = hero.get("combat_items") or []
        print("      combat item:")
        if not combat_items:
            print("        unavailable")
        else:
            for item in combat_items:
                print("        " + _equipment_item_text(item))


def read_command(session_state=None):
    if session_state is None:
        session_state = {}

    while True:
        if session_state.get("party_context"):
            party_command = _read_party_context(session_state)

            if party_command == "__continue__":
                continue

            return party_command

        if session_state.get("skills_context") is not None:
            skill_command = _read_skills_context(session_state)

            if skill_command == "__continue__":
                continue

            if skill_command != "__no_context__":
                return skill_command

        text = input("> ").strip()

        log_input = getattr(sys.stdout, "log_input", None)
        if callable(log_input):
            log_input(text)

        if not text:
            continue

        if text == "quit":
            return None

        activity_name = session_state.get("activity")
        if activity_name == "driving":
            if text == "stop_driving":
                return {
                    "type": "command",
                    "action": "stop_driving",
                }

            if text == "map":
                return {
                    "type": "command",
                    "action": "map",
                }

            print("DRIVING commands: map, stop_driving, quit")
            continue

        if text == "map":
            return {
                "type": "command",
                "action": "map",
            }

        snapshot = session_state.get("frontend_snapshot")
        frontend_state = (
            snapshot.get("frontend_state")
            if isinstance(snapshot, dict)
            else None
        )

        if frontend_state in {
            "hero_select_confirmation",
            "new_expedition_confirmation",
            "post_combat_loot_confirmation",
            "end_expedition_confirmation",
            "stagecoach_equip_confirmation",
        }:
            if text == "confirm":
                return {
                    "type": "command",
                    "action": "confirm",
                }

            if text == "cancel":
                return {
                    "type": "command",
                    "action": "cancel",
                }

            print("Confirmation commands: confirm, cancel")
            continue

        if frontend_state == "hero_select":
            if text == "confirm_party":
                if not snapshot.get("party_complete"):
                    print("Party is incomplete.")
                    continue

                return {
                    "type": "command",
                    "action": "confirm_party",
                }

            if text == "party":
                session_state["party_context"] = True
                _print_party_submenu(snapshot)
                continue

            if text == "raw_actions":
                _print_raw_actions(snapshot)
                continue

            match = re.fullmatch(r"inspect\s+(\d+)", text)
            if match:
                return {
                    "type": "command",
                    "action": "inspect_hero",
                    "actor_guid": int(match.group(1)),
                }

            match = re.fullmatch(r"paths\s+(\d+)", text)
            if match:
                command = _read_paths_submenu(
                    snapshot,
                    session_state.setdefault("hero_inspections", {}),
                    int(match.group(1)),
                )
                if command == "__back__":
                    continue
                return command

            match = re.fullmatch(r"skills\s+(\d+)", text)
            if match:
                actor_guid = int(match.group(1))
                heroes = _hero_select_hero_lookup(snapshot)

                if actor_guid not in heroes:
                    print(
                        f"No hero with actor_guid={actor_guid} "
                        "is available here."
                    )
                    continue

                inspection = session_state.setdefault(
                    "hero_inspections",
                    {},
                ).get(actor_guid)

                if inspection is None:
                    session_state["pending_skills_actor_guid"] = actor_guid
                    print(
                        f"Fetching skill data for "
                        f"{_hero_select_label(heroes[actor_guid])}..."
                    )
                    return {
                        "type": "command",
                        "action": "inspect_hero",
                        "actor_guid": actor_guid,
                    }

                session_state["skills_context"] = actor_guid
                _print_skills_submenu(
                    snapshot,
                    inspection,
                    actor_guid,
                )
                continue

        if text == "raw_actions":
            if isinstance(snapshot, dict):
                _print_raw_actions(snapshot)
            else:
                print("No frontend snapshot is available.")
            continue

        if text == "state" and frontend_state:
            # Frontend READY already is the authoritative snapshot for the
            # current decision boundary. Do not route this through the legacy
            # combat-only state request.
            print_frontend_snapshot(snapshot, "STATE (cached)")
            continue

        if text == "state":
            return {
                "type": "command",
                "action": "state",
            }

        if frontend_state == "story_choice":
            story_match = re.fullmatch(
                r"(?:choose|choose_story)\s+(\d+)",
                text,
            )

            if story_match:
                actor_guid = int(story_match.group(1))

                valid_actors = {
                    int(action.get("actor_guid"))
                    for action in (snapshot.get("available_actions") or [])
                    if isinstance(action, dict)
                    and action.get("action") == "choose_story"
                    and action.get("actor_guid") is not None
                }

                if actor_guid not in valid_actors:
                    print(
                        f"Actor {actor_guid} is not an available story choice. "
                        "Use raw_actions to inspect choices."
                    )
                    continue

                return {
                    "type": "command",
                    "action": "choose_story",
                    "actor_guid": actor_guid,
                }

        if frontend_state == "route_choice":
            route_match = re.fullmatch(
                r"(?:route|choose_route)\s+(\d+)",
                text,
            )

            if route_match:
                option_index = int(route_match.group(1))

                valid_options = {
                    int(action.get("option_index"))
                    for action in (snapshot.get("available_actions") or [])
                    if isinstance(action, dict)
                    and action.get("action") == "choose_route"
                    and action.get("option_index") is not None
                }

                if option_index not in valid_options:
                    print(
                        f"Route option {option_index} is not currently "
                        "available. Use raw_actions to inspect choices."
                    )
                    continue

                return {
                    "type": "command",
                    "action": "choose_route",
                    "option_index": option_index,
                }

        if frontend_state == "post_combat_loot":
            take_item_match = re.fullmatch(r"take_item\s+(\d+)", text)
            if take_item_match:
                slot = int(take_item_match.group(1))
                valid_slots = {
                    int(action.get("slot"))
                    for action in (snapshot.get("available_actions") or [])
                    if isinstance(action, dict)
                    and action.get("action") == "take_item"
                    and action.get("slot") is not None
                }

                if slot not in valid_slots:
                    print(
                        f"take_item {slot} is not currently available. "
                        "Use raw_actions to inspect valid slots."
                    )
                    continue

                return {
                    "type": "command",
                    "action": "take_item",
                    "slot": slot,
                }

            if text == "take_all":
                return {
                    "type": "command",
                    "action": "take_all",
                }

        if frontend_state == "store":
            if text in {"store", "store_state"}:
                return {
                    "type": "command",
                    "action": "store_state",
                }

            buy_match = re.fullmatch(r"buy\s+(\d+)", text)
            if buy_match:
                slot = int(buy_match.group(1))
                valid_slots = {
                    int(action.get("slot"))
                    for action in (snapshot.get("available_actions") or [])
                    if isinstance(action, dict)
                    and action.get("action") == "buy"
                    and action.get("slot") is not None
                }

                if slot not in valid_slots:
                    print(
                        f"buy {slot} is not currently available. "
                        "Use raw_actions to inspect purchasable slots."
                    )
                    continue

                return {
                    "type": "command",
                    "action": "buy",
                    "slot": slot,
                }

            if text == "leave":
                return {
                    "type": "command",
                    "action": "leave",
                }

        if frontend_state in {"inventory_overfilled", "inventory"}:
            if text == "inventory":
                return {
                    "type": "command",
                    "action": "inventory",
                }

            discard_match = re.fullmatch(r"discard_item\s+(\d+)", text)
            if discard_match:
                return {
                    "type": "command",
                    "action": "discard_item",
                    "slot": int(discard_match.group(1)),
                }

            if frontend_state == "inventory" and text == "close_inventory":
                return {
                    "type": "command",
                    "action": "close_inventory",
                }

        if frontend_state == "stagecoach_ready":
            if text == "inventory":
                return {
                    "type": "command",
                    "action": "inventory",
                }

            if text in {"equipment", "equipment_state"}:
                return {
                    "type": "command",
                    "action": "equipment_state",
                }

            equip_trinket_match = re.fullmatch(
                r"equip_trinket\s+(\d+)\s+(\d+)\s+(\d+)",
                text,
            )
            if equip_trinket_match:
                return {
                    "type": "command",
                    "action": "equip_trinket",
                    "actor_guid": int(equip_trinket_match.group(1)),
                    "player_slot": int(equip_trinket_match.group(2)),
                    "trinket_slot": int(equip_trinket_match.group(3)),
                }

            unequip_trinket_match = re.fullmatch(
                r"unequip_trinket\s+(\d+)\s+(\d+)",
                text,
            )
            if unequip_trinket_match:
                return {
                    "type": "command",
                    "action": "unequip_trinket",
                    "actor_guid": int(unequip_trinket_match.group(1)),
                    "trinket_slot": int(unequip_trinket_match.group(2)),
                }

            equip_combat_match = re.fullmatch(
                r"equip_combat_item\s+(\d+)\s+(\d+)",
                text,
            )
            if equip_combat_match:
                return {
                    "type": "command",
                    "action": "equip_combat_item",
                    "actor_guid": int(equip_combat_match.group(1)),
                    "player_slot": int(equip_combat_match.group(2)),
                }

            unequip_combat_match = re.fullmatch(
                r"unequip_combat_item\s+(\d+)",
                text,
            )
            if unequip_combat_match:
                return {
                    "type": "command",
                    "action": "unequip_combat_item",
                    "actor_guid": int(unequip_combat_match.group(1)),
                }

            move_match = re.fullmatch(
                r"(?:move|move_party_member)\s+(\d+)\s+(\d+)",
                text,
            )
            if move_match:
                actor_guid = int(move_match.group(1))
                slot = int(move_match.group(2))

                party = snapshot.get("party", []) if isinstance(snapshot, dict) else []
                party_guids = {
                    item.get("actor_guid")
                    for item in party
                    if isinstance(item, dict) and item.get("actor_guid")
                }
                valid_slots = {
                    item.get("slot")
                    for item in party
                    if isinstance(item, dict) and item.get("slot") is not None
                }

                if actor_guid not in party_guids:
                    print(f"Unknown stagecoach party actor_guid: {actor_guid}")
                    continue

                if slot not in valid_slots:
                    print(f"Unknown stagecoach party slot: {slot}")
                    continue

                return {
                    "type": "command",
                    "action": "move_party_member",
                    "actor_guid": actor_guid,
                    "slot": slot,
                }

        if frontend_state == "hospital":
            if text in {"hospital", "hospital_state"}:
                return {
                    "type": "command",
                    "action": "hospital_state",
                }

            actor_match = re.fullmatch(r"(?:actor|select_actor)\s+(\d+)", text)
            if actor_match:
                actor_guid = int(actor_match.group(1))
                valid = {
                    int(item.get("actor_guid"))
                    for item in (snapshot.get("party") or [])
                    if isinstance(item, dict)
                    and item.get("actor_guid") is not None
                }
                if actor_guid not in valid:
                    print(f"Actor {actor_guid} is not in the hospital party list.")
                    continue
                return {
                    "type": "command",
                    "action": "select_hospital_actor",
                    "actor_guid": actor_guid,
                }

            tab_match = re.fullmatch(r"(?:tab|hospital_tab)\s+(treatment|quirks|store)", text)
            if tab_match:
                return {
                    "type": "command",
                    "action": "hospital_tab",
                    "tab": tab_match.group(1),
                }

            quirk_match = re.fullmatch(r"(?:quirk|select_quirk)\s+(\S+)", text)
            if quirk_match:
                quirk_id = quirk_match.group(1)
                valid = {
                    str(item.get("quirk_id"))
                    for item in (snapshot.get("quirks") or [])
                    if isinstance(item, dict) and item.get("quirk_id")
                }
                if quirk_id not in valid:
                    print(f"Quirk '{quirk_id}' is not listed for the selected hero.")
                    continue
                return {
                    "type": "command",
                    "action": "select_hospital_quirk",
                    "quirk_id": quirk_id,
                }

            simple_hospital_commands = {
                "minor_heal": "hospital_minor_heal",
                "full_heal": "hospital_full_heal",
                "cure_disease": "hospital_cure_disease",
                "lock_quirk": "hospital_lock_quirk",
                "remove_quirk": "hospital_remove_quirk",
                "leave": "leave_hospital",
                "leave_hospital": "leave_hospital",
            }
            if text in simple_hospital_commands:
                return {
                    "type": "command",
                    "action": simple_hospital_commands[text],
                }

            buy_match = re.fullmatch(r"buy\s+(\d+)", text)
            if buy_match:
                slot = int(buy_match.group(1))
                valid_slots = {
                    int(action.get("slot"))
                    for action in (snapshot.get("available_actions") or [])
                    if isinstance(action, dict)
                    and action.get("action") == "hospital_buy"
                    and action.get("slot") is not None
                }
                if slot not in valid_slots:
                    print(f"buy {slot} is not currently available in the hospital store.")
                    continue
                return {
                    "type": "command",
                    "action": "hospital_buy",
                    "slot": slot,
                }

        if frontend_state == "inn" and text == "travelogue":
            return {
                "type": "command",
                "action": "travelogue",
            }

        if frontend_state == "inn" and text == "end_expedition":
            return {
                "type": "command",
                "action": "end_expedition",
            }

        if frontend_state == "inn" and text == "store":
            return {
                "type": "command",
                "action": "store",
            }

        if frontend_state == "inn" and text in {"mastery", "trainer"}:
            return {
                "type": "command",
                "action": "mastery",
            }

        if frontend_state == "inn" and text in {"wainwright", "stagecoach", "coach"}:
            return {
                "type": "command",
                "action": "wainwright",
            }

        if frontend_state == "inn" and text in {"route", "path", "choose_path"}:
            return {
                "type": "command",
                "action": "route",
            }

        if frontend_state == "inn" and text in {"inn_items", "items", "rest_items"}:
            return {"type": "command", "action": "inn_items"}

        if frontend_state == "inn_items":
            if text in {"inn_items", "items", "refresh"}:
                return {"type": "command", "action": "inn_items_state"}
            use_match = re.fullmatch(r"(?:use|use_inn_item)\s+(\d+)(?:\s+(.+))?", text)
            if use_match:
                slot = int(use_match.group(1))
                targets = []
                remainder = (use_match.group(2) or "").strip()
                if remainder:
                    try:
                        targets = [int(x) for x in remainder.split()]
                    except ValueError:
                        print("Use: use_inn_item <inventory_slot> [actor_guid ...]")
                        continue
                valid = {
                    (int(a.get("slot")), tuple(int(x) for x in (a.get("target_guids") or [])))
                    for a in (snapshot.get("available_actions") or [])
                    if isinstance(a, dict) and a.get("action") == "inn_item_use" and a.get("slot") is not None
                }
                if (slot, tuple(targets)) not in valid:
                    print("That inn-item/target combination is not currently available. See target restrictions or raw_actions.")
                    continue
                return {"type":"command", "action":"inn_item_use", "slot":slot, "target_guids":targets}
            if text == "back":
                return {"type":"command", "action":"inn_items_back"}

        if frontend_state == "inn_route_choice":
            if text in {"route", "path", "refresh"}:
                return {
                    "type": "command",
                    "action": "inn_route_state",
                }

            route_match = re.fullmatch(
                r"(?:choose_route|choose_path|route)\s+(\d+)",
                text,
            )
            if route_match:
                option_index = int(route_match.group(1))
                valid_options = {
                    int(action.get("option_index"))
                    for action in (snapshot.get("available_actions") or [])
                    if isinstance(action, dict)
                    and action.get("action") == "inn_route_choose"
                    and action.get("option_index") is not None
                }
                if option_index not in valid_options:
                    print(
                        f"Route option {option_index} is not currently selectable. "
                        "Use raw_actions to inspect valid choices."
                    )
                    continue
                return {
                    "type": "command",
                    "action": "inn_route_choose",
                    "option_index": option_index,
                }

            if text == "back":
                return {
                    "type": "command",
                    "action": "inn_route_back",
                }

        if frontend_state == "inn_wainwright":
            if text in {"wainwright", "stagecoach", "coach", "refresh"}:
                return {
                    "type": "command",
                    "action": "inn_wainwright_state",
                }

            equip_match = re.fullmatch(
                r"equip_stagecoach\s+(\d+)\s+(general|trophy|pet|flame)\s+(\d+)",
                text,
            )
            if equip_match:
                player_slot = int(equip_match.group(1))
                slot_type = equip_match.group(2)
                stagecoach_slot = int(equip_match.group(3))
                valid = {
                    (int(action.get("player_slot")), str(action.get("slot_type")), int(action.get("stagecoach_slot")))
                    for action in (snapshot.get("available_actions") or [])
                    if isinstance(action, dict)
                    and action.get("action") == "inn_wainwright_equip"
                    and action.get("player_slot") is not None
                    and action.get("slot_type")
                    and action.get("stagecoach_slot") is not None
                }
                if (player_slot, slot_type, stagecoach_slot) not in valid:
                    print("That stagecoach equip is not currently available. Use raw_actions to inspect valid destinations.")
                    continue
                return {
                    "type": "command",
                    "action": "inn_wainwright_equip",
                    "player_slot": player_slot,
                    "slot_type": slot_type,
                    "stagecoach_slot": stagecoach_slot,
                }

            unequip_match = re.fullmatch(
                r"unequip_stagecoach\s+(general|trophy|pet|flame)\s+(\d+)",
                text,
            )
            if unequip_match:
                slot_type = unequip_match.group(1)
                stagecoach_slot = int(unequip_match.group(2))
                valid = {
                    (str(action.get("slot_type")), int(action.get("stagecoach_slot")))
                    for action in (snapshot.get("available_actions") or [])
                    if isinstance(action, dict)
                    and action.get("action") == "inn_wainwright_unequip"
                    and action.get("slot_type")
                    and action.get("stagecoach_slot") is not None
                }
                if (slot_type, stagecoach_slot) not in valid:
                    print("That stagecoach item cannot currently be unequipped. Permanent items are intentionally excluded.")
                    continue
                return {
                    "type": "command",
                    "action": "inn_wainwright_unequip",
                    "slot_type": slot_type,
                    "stagecoach_slot": stagecoach_slot,
                }

            if text == "back":
                return {
                    "type": "command",
                    "action": "inn_wainwright_back",
                }

        if frontend_state == "inn_mastery":
            if text in {"mastery", "mastery_state", "refresh"}:
                return {
                    "type": "command",
                    "action": "inn_mastery_state",
                }

            upgrade_match = re.fullmatch(
                r"(?:upgrade|upgrade_skill)\s+(\d+)\s+(\S+)",
                text,
            )
            if upgrade_match:
                actor_guid = int(upgrade_match.group(1))
                skill_id = upgrade_match.group(2)
                valid = {
                    (int(action.get("actor_guid")), str(action.get("skill_id")))
                    for action in (snapshot.get("available_actions") or [])
                    if isinstance(action, dict)
                    and action.get("action") == "inn_mastery_upgrade_skill"
                    and action.get("actor_guid") is not None
                    and action.get("skill_id")
                }
                if (actor_guid, skill_id) not in valid:
                    print(
                        f"upgrade_skill {actor_guid} {skill_id} is not currently "
                        "available. Use raw_actions to inspect valid upgrades."
                    )
                    continue
                return {
                    "type": "command",
                    "action": "inn_mastery_upgrade_skill",
                    "actor_guid": actor_guid,
                    "skill_id": skill_id,
                }

            if text == "back":
                return {
                    "type": "command",
                    "action": "inn_mastery_back",
                }

        if frontend_state == "inn_store":
            if text in {"store", "store_state", "refresh"}:
                return {
                    "type": "command",
                    "action": "inn_store_state",
                }

            buy_match = re.fullmatch(r"buy\s+(\d+)", text)
            if buy_match:
                slot = int(buy_match.group(1))
                valid_slots = {
                    int(action.get("slot"))
                    for action in (snapshot.get("available_actions") or [])
                    if isinstance(action, dict)
                    and action.get("action") == "inn_store_buy"
                    and action.get("slot") is not None
                }
                if slot not in valid_slots:
                    print(
                        f"buy {slot} is not currently available. "
                        "Use raw_actions to inspect purchasable slots."
                    )
                    continue
                return {
                    "type": "command",
                    "action": "inn_store_buy",
                    "slot": slot,
                }

            discard_match = re.fullmatch(r"discard_item\s+(\d+)", text)
            if discard_match:
                slot = int(discard_match.group(1))
                valid_slots = {
                    int(action.get("slot"))
                    for action in (snapshot.get("available_actions") or [])
                    if isinstance(action, dict)
                    and action.get("action") == "inn_store_discard"
                    and action.get("slot") is not None
                }
                if slot not in valid_slots:
                    print(
                        f"discard_item {slot} is not currently available. "
                        "Use raw_actions to inspect discardable slots."
                    )
                    continue
                return {
                    "type": "command",
                    "action": "inn_store_discard",
                    "slot": slot,
                }

            if text == "back":
                return {
                    "type": "command",
                    "action": "inn_store_back",
                }

        # inn_end_expedition is a read-only summary layered over the
        # persistent INN boundary. Bridge still validates the real game state.
        if text == "take_hope":
            return {
                "type": "command",
                "action": "take_hope",
            }

        if frontend_state == "inn_end_expedition" and text == "back":
            return {
                "type": "command",
                "action": "back",
            }

        if frontend_state == "inn_travelogue" and text == "continue_travelogue":
            return {
                "type": "command",
                "action": "continue_travelogue",
            }

        if text == "continue":
            return {
                "type": "command",
                "action": "continue",
            }

        if text == "enter_node":
            return {
                "type": "command",
                "action": "enter_node",
            }

        if text == "leave_altar":
            return {
                "type": "command",
                "action": "leave_altar",
            }

        if text == "start_driving":
            return {
                "type": "command",
                "action": "start_driving",
            }

        if text == "new_expedition":
            return {
                "type": "command",
                "action": "new_expedition",
            }

        if text == "continue_expedition":
            return {
                "type": "command",
                "action": "continue_expedition",
            }

        # Backward-compatible expert aliases.
        inspect_match = re.match(
            r"^inspect_hero(?:\s+actor_guid=|\s+)(\d+)(?:\s+.*)?$",
            text,
        )
        if inspect_match:
            return {
                "type": "command",
                "action": "inspect_hero",
                "actor_guid": int(inspect_match.group(1)),
            }

        path_match = re.match(
            r"^set_hero_path(?:\s+actor_guid=|\s+)(\d+)"
            r"(?:\s+path_id=|\s+)(\S+)(?:\s+.*)?$",
            text,
        )
        if path_match:
            return {
                "type": "command",
                "action": "set_hero_path",
                "actor_guid": int(path_match.group(1)),
                "path_id": path_match.group(2),
            }

        skill_set_match = re.match(
            r"^set_hero_skills(?:\s+actor_guid=|\s+)(\d+)(?:\s+(.+))?$",
            text,
        )
        if skill_set_match:
            actor_guid = int(skill_set_match.group(1))
            remainder = (skill_set_match.group(2) or "").strip()
            skill_ids = remainder.split() if remainder else []
            return {
                "type": "command",
                "action": "set_hero_skills",
                "actor_guid": actor_guid,
                "skill_ids": skill_ids,
            }

        if text == "confirm_party":
            return {
                "type": "command",
                "action": "confirm_party",
            }

        party_set_match = re.fullmatch(
            r"set_party_member\s+(\d+)\s+(\d+)",
            text,
        )
        if party_set_match:
            return {
                "type": "command",
                "action": "set_party_member",
                "slot": int(party_set_match.group(1)),
                "actor_guid": int(party_set_match.group(2)),
            }

        party_clear_match = re.fullmatch(
            r"clear_party_slot\s+(\d+)",
            text,
        )
        if party_clear_match:
            return {
                "type": "command",
                "action": "clear_party_slot",
                "slot": int(party_clear_match.group(1)),
            }

        parts = text.split()

        if len(parts) == 2 and parts[0] == "select_boss":
            return {
                "type": "command",
                "action": "select_boss",
                "boss_id": parts[1],
            }

        if len(parts) == 4 and parts[0] == "skill":
            return {
                "type": "command",
                "action": "skill",
                "performer_guid": int(parts[1]),
                "skill_id": parts[2],
                "target_guid": int(parts[3]),
            }

        if frontend_state == "route_choice":
            print(
                "ROUTE_CHOICE commands:\n"
                "  route <option_index>\n"
                "  choose_route <option_index>\n"
                "  state\n"
                "  raw_actions\n"
                "  quit"
            )
        elif frontend_state == "inventory_overfilled":
            print(
                "INVENTORY_OVERFILLED commands:\n"
                "  inventory\n"
                "  discard_item <slot>\n"
                "  state\n"
                "  raw_actions\n"
                "  quit"
            )
        elif frontend_state == "inventory":
            print(
                "INVENTORY commands:\n"
                "  inventory\n"
                "  discard_item <slot>\n"
                "  close_inventory\n"
                "  state\n"
                "  raw_actions\n"
                "  quit"
            )
        elif frontend_state == "stagecoach_ready":
            print(
                "STAGECOACH_READY commands:\n"
                "  inventory\n"
                "  equipment\n"
                "  equip_trinket <actor_guid> <player_slot> <trinket_slot>\n"
                "  unequip_trinket <actor_guid> <trinket_slot>\n"
                "  equip_combat_item <actor_guid> <player_slot>\n"
                "  unequip_combat_item <actor_guid>\n"
                "  move <actor_guid> <slot>\n"
                "  start_driving\n"
                "  state\n"
                "  raw_actions\n"
                "  quit"
            )
        elif frontend_state == "post_combat_loot":
            print(
                "POST_COMBAT_LOOT commands:\n"
                "  take_item <slot>\n"
                "  take_all\n"
                "  continue\n"
                "  state\n"
                "  raw_actions\n"
                "  quit"
            )
        elif frontend_state == "hero_select":
            print(
                "HERO_SELECT commands:\n"
                "  inspect <actor_guid>\n"
                "  paths <actor_guid>\n"
                "  skills <actor_guid>\n"
                "  party\n"
                "  confirm_party\n"
                "  state\n"
                "  raw_actions\n"
                "  quit"
            )
        else:
            print(
                "Command format:\n"
                "  state\n"
                "  continue\n"
                "  enter_node\n"
                "  leave_altar\n"
                "  start_driving\n"
                "  new_expedition\n"
                "  continue_expedition\n"
                "  select_boss <boss_id>\n"
                "  skill <performer_guid> <skill_id> <target_guid>\n"
                "  quit"
            )


def actor_label(actor):
    if not isinstance(actor, dict):
        return "<unknown>"

    guid = actor.get("guid")
    name = actor.get("name") or ""
    actor_id = actor.get("actor_id") or ""

    if name and actor_id:
        return f"[{guid}] {name} ({actor_id})"

    if name:
        return f"[{guid}] {name}"

    if actor_id:
        return f"[{guid}] {actor_id}"

    return f"[{guid}]"


def format_actor_line(actor):
    label = actor_label(actor)

    rank = actor.get("rank")
    hp = actor.get("hp")
    hp_max = actor.get("hp_max")
    stress = actor.get("stress")
    stress_max = actor.get("stress_max")

    parts = [label]

    if rank is not None:
        parts.append(f"rank {rank}")

    if hp is not None and hp_max is not None:
        parts.append(f"hp {hp}/{hp_max}")

    if stress is not None and stress_max is not None:
        parts.append(f"stress {stress}/{stress_max}")

    dots = actor.get("dots") or []

    if isinstance(dots, list) and dots:
        dot_labels = []

        for dot in dots:
            if not isinstance(dot, dict):
                continue

            dot_id = dot.get("m_Definition") or "dot"
            duration = dot.get("m_DurationAmountRemaining")

            if duration is None:
                dot_labels.append(str(dot_id))
            else:
                dot_labels.append(f"{dot_id}({duration})")

        if dot_labels:
            parts.append("dots " + ",".join(dot_labels))

    return "   ".join(parts)


_TMP_SPRITE_TAG_RE = re.compile(
    r'<sprite\b(?=[^>]*\bname\s*=\s*(?:"([^"]+)"|\'([^\']+)\'|([^\s>]+)))[^>]*>',
    re.IGNORECASE,
)
_TMP_BR_TAG_RE = re.compile(r'<br\s*/?>', re.IGNORECASE)
_TMP_FORMATTING_TAG_RE = re.compile(
    r'</?(?:color|alpha|b|i|u|s|size|mark|nobr|font|line-height)(?:\s*=\s*[^>]*)?\s*>',
    re.IGNORECASE,
)


def clean_token_tooltip(value):
    text = str(value or "")
    text = _TMP_BR_TAG_RE.sub("\n", text)

    def replace_sprite(match):
        sprite_name = next(
            (group for group in match.groups() if group),
            "sprite",
        )
        return f"[{sprite_name}]"

    text = _TMP_SPRITE_TAG_RE.sub(replace_sprite, text)
    text = _TMP_FORMATTING_TAG_RE.sub("", text)
    text = text.replace("\u00a0", " ")

    # TMP sprite tags are sometimes glued directly to surrounding localized
    # text. Add presentation-only spacing around the bracketed placeholder.
    text = re.sub(r"(?<=\w)(?=\[)", " ", text)
    text = re.sub(r"(?<=\])(?=\w)", " ", text)
    return text


def format_token_summary(token):
    if not isinstance(token, dict):
        return None, []

    token_id = token.get("id") or "<unknown-token>"
    count = token.get("count")

    if isinstance(count, int) and count > 1:
        label = f"{token_id} x{count}"
    else:
        label = str(token_id)

    tooltip = clean_token_tooltip(token.get("tooltip"))
    tooltip_lines = [
        line.strip()
        for line in tooltip.splitlines()
        if line.strip()
    ]

    return label, tooltip_lines


def print_actor_block(actor, indent="  "):
    print(f"{indent}{format_actor_line(actor)}")

    tokens = actor.get("tokens") or []

    if not isinstance(tokens, list) or not tokens:
        return

    print(f"{indent}     tokens:")

    for token in tokens:
        label, tooltip_lines = format_token_summary(token)

        if label is None:
            continue

        if tooltip_lines:
            print(f"{indent}       {label} - {tooltip_lines[0]}")

            for line in tooltip_lines[1:]:
                print(f"{indent}         {line}")
        else:
            print(f"{indent}       {label}")


def build_actor_lookup(message):
    actors = {}

    for side in ("friendly", "enemy"):
        for actor in message.get(side, []):
            if isinstance(actor, dict):
                guid = actor.get("guid")
                if guid is not None:
                    actors[guid] = actor

    current_actor = message.get("current_actor")

    if isinstance(current_actor, dict):
        guid = current_actor.get("guid")
        if guid is not None:
            actors[guid] = current_actor

    return actors


def format_target(guid, actor_lookup):
    actor = actor_lookup.get(guid)

    if actor is None:
        return f"[{guid}]"

    return actor_label(actor)


def _format_number(value):
    try:
        number = float(value)
    except (TypeError, ValueError):
        return str(value)

    if number.is_integer():
        return str(int(number))

    return f"{number:.2f}".rstrip("0").rstrip(".")


def _format_probability(value):
    try:
        percent = float(value) * 100.0
    except (TypeError, ValueError):
        return str(value)

    if percent.is_integer():
        return f"{int(percent)}%"

    return f"{percent:.2f}".rstrip("0").rstrip(".") + "%"


def _clean_skill_text(value):
    # The same TMP cleanup rules are useful for skill tooltip text. Raw JSON
    # remains untouched; this is host presentation only.
    return clean_token_tooltip(value)


def _print_skill_info(skill_info, indent="    "):
    if not isinstance(skill_info, dict):
        return

    name = _clean_skill_text(skill_info.get("name"))
    top_bar = _clean_skill_text(skill_info.get("top_bar"))
    results = skill_info.get("results") or []

    if name:
        print(f"{indent}name: {name}")

    if top_bar:
        lines = [line.strip() for line in top_bar.splitlines() if line.strip()]
        if lines:
            print(f"{indent}info: {lines[0]}")
            for line in lines[1:]:
                print(f"{indent}      {line}")

    for value in results:
        text = _clean_skill_text(value)
        lines = [line.strip() for line in text.splitlines() if line.strip()]
        if not lines:
            continue
        print(f"{indent}effect: {lines[0]}")
        for line in lines[1:]:
            print(f"{indent}        {line}")


def _format_ui_preview(ui_preview):
    if not isinstance(ui_preview, dict):
        return ""

    parts = []

    if "hit_chance" in ui_preview:
        parts.append(f"hit {_format_probability(ui_preview['hit_chance'])}")

    damage = ui_preview.get("damage")
    crit_chance = ui_preview.get("crit_chance")

    if isinstance(damage, dict):
        # Native CombatInterfaceBarUiBhv displays guaranteed-crit damage as a
        # single value instead of the normal low-high range.
        try:
            guaranteed_crit = float(crit_chance) * 100.0 >= 99.5
        except (TypeError, ValueError):
            guaranteed_crit = False

        if guaranteed_crit and damage.get("crit") is not None:
            parts.append(f"dmg {_format_number(damage.get('crit'))} (crit)")
        elif damage.get("low") is not None and damage.get("high") is not None:
            low = _format_number(damage.get("low"))
            high = _format_number(damage.get("high"))
            parts.append(f"dmg {low}-{high}")

    if crit_chance is not None:
        parts.append(f"crit {_format_probability(crit_chance)}")

    heal = ui_preview.get("heal")
    if isinstance(heal, dict):
        base = heal.get("base")
        spread = heal.get("range")
        if base is not None and spread is not None:
            low = _format_number(base)
            high = _format_number(float(base) + float(spread))
            parts.append(f"heal {low}-{high}")

    guarding = ui_preview.get("guarding_actor_guid")
    if guarding:
        parts.append(f"guarding actor [{guarding}]")

    return " | ".join(parts)


def _print_action_targets(action, actor_lookup):
    targets = action.get("targets", [])
    target_previews = action.get("target_previews", [])

    preview_by_guid = {}
    if isinstance(target_previews, list):
        for item in target_previews:
            if isinstance(item, dict) and item.get("actor_guid") is not None:
                preview_by_guid[item.get("actor_guid")] = item

    if not targets:
        print("    targets: none")
        return

    print("    targets:")

    for guid in targets:
        line = f"      {format_target(guid, actor_lookup)}"
        detail = preview_by_guid.get(guid)

        if isinstance(detail, dict):
            preview_text = _format_ui_preview(detail.get("ui_preview"))
            if preview_text:
                line += f" | {preview_text}"

            affinity = detail.get("affinity_warning")
            if isinstance(affinity, dict):
                delta = affinity.get("leaning_change")
                is_positive = affinity.get("is_positive")
                with_guid = affinity.get("with_actor_guid")
                connection_guids = (
                    affinity.get("connection_actor_guids") or []
                )

                if is_positive is True:
                    verb = "increase"
                elif is_positive is False:
                    verb = "reduce"
                else:
                    verb = "change"

                if with_guid is not None:
                    warning_target = format_target(
                        with_guid,
                        actor_lookup,
                    )
                    warning = (
                        f"! Will {verb} affinity with "
                        f"{warning_target}"
                    )
                elif (
                    isinstance(connection_guids, list)
                    and len(connection_guids) == 2
                ):
                    left = format_target(
                        connection_guids[0],
                        actor_lookup,
                    )
                    right = format_target(
                        connection_guids[1],
                        actor_lookup,
                    )
                    warning = (
                        f"! Will {verb} affinity between "
                        f"{left} and {right}"
                    )
                else:
                    warning = f"! Will {verb} affinity"

                if delta is not None:
                    try:
                        delta_text = f"{int(delta):+d}"
                    except (TypeError, ValueError):
                        delta_text = str(delta)
                    warning += f" ({delta_text})"

                line += f" | {warning}"

            # Some native actions (notably the dedicated move action) do not
            # have a stat-preview entry. Keep that diagnostic in raw JSON, but
            # do not present it as an error to the agent/user.

        print(line)


def _print_inventory_state(message):
    filled = message.get("filled_slots")
    limit = message.get("inventory_limit")
    over = message.get("overfilled_count")
    items = message.get("items", []) or []

    print()
    print("INVENTORY")
    if filled is not None and limit is not None:
        suffix = f"  (overfilled by {over})" if over else ""
        print(f"Capacity: {filled} / {limit}{suffix}")
    print()

    if not items:
        print("  empty")
        print()
        return

    for item in items:
        if not isinstance(item, dict):
            print(f"  {item}")
            continue

        slot = item.get("slot")
        item_id = item.get("item_id") or "<unknown>"
        name = item.get("name") or ""
        qty = item.get("qty")
        can_discard = item.get("can_discard")
        overflow = item.get("overflow")

        label = f"  slot {slot}: {item_id}"
        if name and name != item_id:
            label += f" вЂ” {name}"
        if qty is not None:
            label += f" x{qty}"
        if overflow:
            label += " [OVERFLOW]"
        if can_discard is False:
            label += " [CANNOT DISCARD]"
        print(label)

    print()


def _print_map_state(message):
    print()
    print("MAP")

    biome_type = message.get("biome_type") or "unknown"
    biome_index = message.get("biome_index")
    if biome_index is None:
        print(f"Biome: {biome_type}")
    else:
        print(f"Biome: {biome_type} (index {biome_index})")

    rows = message.get("rows") or []
    links = message.get("links") or []
    outgoing = {}
    for link in links:
        if not isinstance(link, dict):
            continue
        outgoing.setdefault(link.get("from"), []).append(link)

    print()
    if not rows:
        print("  no map rows")
    else:
        for row in rows:
            if not isinstance(row, dict):
                continue
            row_index = row.get("row")
            current = " [CURRENT]" if row.get("current") else ""
            print(f"Row {row_index}{current}:")

            for node in row.get("nodes") or []:
                if not isinstance(node, dict):
                    continue
                node_id = node.get("id") or "?"
                node_type = node.get("node_type") or "unknown"
                subtype = node.get("node_subtype") or ""
                if subtype:
                    node_type += f"/{subtype}"
                flags = []
                chosen = node.get("chosen_state")
                if chosen and chosen != "none":
                    flags.append(str(chosen))
                if node.get("kill_contract"):
                    flags.append("kill_contract")
                suffix = f" [{' '.join(flags)}]" if flags else ""
                print(f"  [{node_id}] {node_type}{suffix}")

                for link in outgoing.get(node_id, []):
                    target = link.get("to") or "?"
                    route = link.get("route_type") or "unknown"
                    lflags = []
                    chosen_link = link.get("chosen_state")
                    if chosen_link and chosen_link != "none":
                        lflags.append(str(chosen_link))
                    suffix2 = f" [{' '.join(lflags)}]" if lflags else ""
                    print(f"      -> [{target}] via {route}{suffix2}")

    print()


def print_frontend_snapshot(message, heading):
    frontend_state = message.get("frontend_state")

    if not frontend_state:
        print_combat_snapshot(message, heading)
        return

    print()
    print(heading)
    print()
    print(f"State: {frontend_state}")

    current_mode = message.get("current_mode")
    if current_mode:
        print(f"Current mode: {current_mode}")

    if frontend_state == "inn_items":
        print()
        print("Inn items:")
        items = message.get("items") or []
        if not items:
            print("  none")
        for item in items:
            if not isinstance(item, dict):
                continue
            slot = item.get("slot")
            item_id = item.get("item_id") or "<unknown>"
            name = _clean_skill_text(item.get("display_name"))
            qty = item.get("quantity")
            line = f"  slot {slot}: {name or item_id}"
            if name and name != item_id:
                line += f" [{item_id}]"
            if qty is not None:
                line += f" x{qty}"
            if item.get("random_target"):
                line += " [RANDOM TARGET]"
            print(line)
            desc = _clean_skill_text(item.get("tooltip"))
            for dl in desc.splitlines():
                if dl.strip(): print(f"      {dl.strip()}")
            print(f"      targets required: {item.get('selectable_targets')}")
            for target in item.get("targets") or []:
                if not isinstance(target, dict): continue
                tline = f"        [{target.get('actor_guid')}] {target.get('actor_name') or ''}"
                if not target.get("available"):
                    tline += f" [BLOCKED: {target.get('blocked_reason') or 'blocked'}]"
                    if target.get("blocked_by_id"):
                        tline += f" by {target.get('blocked_by_id')}"
                print(tline)
        print()
        print("Commands:")
        print("  use_inn_item <inventory_slot> [actor_guid ...]")
        print("  inn_items")
        print("  back")
        print("  state")
        print("  raw_actions")
        print("  quit")
        print()
        return

    if frontend_state == "inn_route_choice":
        selected_index = message.get("selected_index")
        choices = message.get("choices") or []

        print()
        print("Routes:")
        if not choices:
            print("  none")
        else:
            for choice in choices:
                if not isinstance(choice, dict):
                    print(f"  {choice}")
                    continue

                option_index = choice.get("option_index")
                biome_id = choice.get("biome_id") or "<unknown>"
                biome_name = _clean_skill_text(choice.get("biome_name"))
                label = f"  {option_index}. {biome_name or biome_id}"
                if biome_name and biome_name != biome_id:
                    label += f" [{biome_id}]"
                if choice.get("selected") or option_index == selected_index:
                    label += " [SELECTED]"
                if choice.get("is_end_biome"):
                    label += " [END BIOME]"
                if not choice.get("selectable", False):
                    label += " [BLOCKED]"
                print(label)

                modifier_name = _clean_skill_text(choice.get("modifier_name"))
                modifier_description = _clean_skill_text(
                    choice.get("modifier_description")
                )
                if modifier_name:
                    print(f"      modifier: {modifier_name}")
                for line in modifier_description.splitlines():
                    line = line.strip()
                    if line:
                        print(f"        {line}")

                goal_name = _clean_skill_text(choice.get("goal_name"))
                goal_description = _clean_skill_text(choice.get("goal_description"))
                if goal_name:
                    print(f"      goal: {goal_name}")
                for line in goal_description.splitlines():
                    line = line.strip()
                    if line:
                        print(f"        {line}")

                rewards = choice.get("goal_rewards") or []
                for reward in rewards:
                    if not isinstance(reward, dict):
                        continue
                    reward_text = _clean_skill_text(reward.get("text"))
                    if reward_text:
                        print(f"      reward: {reward_text}")
                    else:
                        reward_id = reward.get("id") or "<unknown>"
                        qty = reward.get("qty")
                        suffix = f" x{qty}" if qty is not None else ""
                        print(f"      reward: {reward_id}{suffix}")

                if not choice.get("selectable", False):
                    reason = choice.get("blocked_reason") or "unavailable"
                    required = choice.get("required_stagecoach_slot_type")
                    if required:
                        print(f"      blocked: {reason} (requires equipped {required})")
                    else:
                        print(f"      blocked: {reason}")

        print()
        print("Commands:")
        print("  choose_route <option_index>")
        print("  route")
        print("  back")
        print("  state")
        print("  raw_actions")
        print("  quit")
        print()
        return

    if frontend_state == "inn_wainwright":
        coach_name = message.get("stagecoach_name") or ""
        print()
        if coach_name:
            print(f"Stagecoach: {coach_name}")

        def print_stagecoach_slots(title, items):
            print()
            print(f"{title}:")
            if not items:
                print("  none")
                return
            for item in items:
                if not isinstance(item, dict):
                    print(f"  {item}")
                    continue
                slot = item.get("slot")
                if item.get("empty"):
                    print(f"  slot {slot}: empty")
                    continue
                item_id = item.get("item_id") or "<unknown>"
                name = _clean_skill_text(item.get("name"))
                line = f"  slot {slot}: {name or item_id}"
                if name and name != item_id:
                    line += f" [{item_id}]"
                if item.get("unequip_invalid"):
                    line += " [PERMANENT]"
                print(line)
                description = _clean_skill_text(item.get("description"))
                for desc_line in description.splitlines():
                    desc_line = desc_line.strip()
                    if desc_line:
                        print(f"      {desc_line}")

        print_stagecoach_slots("Stagecoach equipment", message.get("general_slots") or [])
        print_stagecoach_slots("Trophy", message.get("trophy_slots") or [])
        print_stagecoach_slots("Pet", message.get("pet_slots") or [])
        print_stagecoach_slots("Flame", message.get("flame_slots") or [])

        print()
        print("Compatible items in player inventory:")
        inventory_items = message.get("inventory_items") or []
        if not inventory_items:
            print("  none")
        else:
            for item in inventory_items:
                if not isinstance(item, dict):
                    continue
                player_slot = item.get("player_slot")
                item_id = item.get("item_id") or "<unknown>"
                name = _clean_skill_text(item.get("name"))
                slot_type = item.get("slot_type") or "unknown"
                line = f"  player slot {player_slot}: {name or item_id}"
                if name and name != item_id:
                    line += f" [{item_id}]"
                line += f" -> {slot_type}"
                if item.get("permanent_when_equipped"):
                    line += " [PERMANENT WHEN EQUIPPED]"
                print(line)
                description = _clean_skill_text(item.get("description"))
                for desc_line in description.splitlines():
                    desc_line = desc_line.strip()
                    if desc_line:
                        print(f"      {desc_line}")

        print()
        print("Commands:")
        print("  equip_stagecoach <player_slot> <general|trophy|pet|flame> <stagecoach_slot>")
        print("  unequip_stagecoach <general|trophy|pet|flame> <stagecoach_slot>")
        print("  wainwright")
        print("  back")
        print("  state")
        print("  raw_actions")
        print("  quit")
        print()
        return

    if frontend_state == "inn_mastery":
        points = message.get("mastery_points")
        print()
        print(f"Mastery points: {points if points is not None else '<unknown>'}")

        heroes = message.get("heroes", []) or []
        if not heroes:
            print()
            print("Heroes: none")
        else:
            for hero in heroes:
                if not isinstance(hero, dict):
                    continue

                guid = hero.get("actor_guid")
                name = hero.get("actor_name") or ""
                actor_id = hero.get("actor_data_id") or ""
                path_id = hero.get("path_id") or ""

                label = f"[{guid}]"
                if name:
                    label += f" {name}"
                if actor_id:
                    label += f" ({actor_id})"
                if path_id:
                    label += f" | path {path_id}"
                if hero.get("is_hire_class"):
                    label += " [HIRE CLASS - NO MASTERY]"

                print()
                print(label)

                skills = hero.get("skills", []) or []
                if not skills:
                    print("  skills: none")
                    continue

                for index, skill in enumerate(skills, start=1):
                    if not isinstance(skill, dict):
                        continue

                    skill_id = skill.get("skill_id") or "<unknown>"
                    upgrade_id = skill.get("upgrade_skill_id") or ""
                    upgraded = bool(skill.get("upgraded"))
                    can_upgrade = bool(skill.get("can_upgrade"))
                    cost_text = _clean_skill_text(skill.get("cost_text"))

                    current_info = skill.get("current") or {}
                    upgrade_info = skill.get("upgrade") or {}
                    current_name = _clean_skill_text(
                        current_info.get("name") if isinstance(current_info, dict) else ""
                    )
                    upgrade_name = _clean_skill_text(
                        upgrade_info.get("name") if isinstance(upgrade_info, dict) else ""
                    )

                    line = f"  {index}. {current_name or skill_id} [{skill_id}]"
                    if upgraded:
                        line += " [MASTERED]"
                    elif upgrade_id:
                        line += f" -> {upgrade_name or upgrade_id} [{upgrade_id}]"
                        if cost_text:
                            line += f" | {cost_text}"
                        if not can_upgrade:
                            line += " [UNAVAILABLE]"
                    print(line)

                    if isinstance(current_info, dict):
                        print("      Current:")
                        _print_skill_info(current_info, indent="        ")

                    if not upgraded and isinstance(upgrade_info, dict) and upgrade_info:
                        print("      Mastery preview:")
                        _print_skill_info(upgrade_info, indent="        ")

        print()
        print("Commands:")
        print("  upgrade_skill <actor_guid> <skill_id>")
        print("  mastery")
        print("  back")
        print("  state")
        print("  raw_actions")
        print("  quit")
        print()
        return

    if frontend_state == "inn_store":
        store_items = message.get("store_items", []) or []

        print()
        print("Black Reliquary / Inn store:")
        if not store_items:
            print("  out of stock")
        else:
            for item in store_items:
                if not isinstance(item, dict):
                    print(f"  {item}")
                    continue

                slot = item.get("slot")
                item_id = item.get("item_id") or "<unknown>"
                name = _clean_skill_text(item.get("name"))
                qty = item.get("qty")
                price_text = _clean_skill_text(item.get("price_text"))
                can_afford = item.get("can_afford")

                label = f"  slot {slot}: {name or item_id}"
                if name and name != item_id:
                    label += f" [{item_id}]"
                if qty is not None:
                    label += f" x{qty}"
                if price_text:
                    label += f" | {price_text}"
                if can_afford is False:
                    label += " [CAN'T AFFORD]"
                print(label)

                description = _clean_skill_text(item.get("description"))
                for line in description.splitlines():
                    line = line.strip()
                    if line:
                        print(f"      {line}")

        print()
        print(
            "Inventory: "
            f"{message.get('filled_slots', 0)} / "
            f"{message.get('inventory_limit', 0)}"
        )
        if message.get("is_overfilled"):
            print(
                f"  OVERFILLED by {message.get('overfilled_count', 0)}"
            )

        inventory_items = message.get("inventory_items", []) or []
        if not inventory_items:
            print("  empty")
        else:
            for item in inventory_items:
                if not isinstance(item, dict):
                    continue
                slot = item.get("slot")
                item_id = item.get("item_id") or "<unknown>"
                name = _clean_skill_text(item.get("name"))
                qty = item.get("qty")
                label = f"  slot {slot}: {name or item_id}"
                if name and name != item_id:
                    label += f" [{item_id}]"
                if qty is not None:
                    label += f" x{qty}"
                if item.get("overflow"):
                    label += " [OVERFLOW]"
                if item.get("can_discard") is False:
                    label += " [CANNOT DISCARD]"
                print(label)

        print()
        print("Commands:")
        print("  buy <store_slot>")
        print("  discard_item <inventory_slot>")
        print("  store")
        print("  back")
        print("  state")
        print("  raw_actions")
        print("  quit")
        print()
        return

    if frontend_state == "inn_travelogue":
        inn_name = _clean_skill_text(message.get("inn_name"))
        if inn_name:
            print(f"Inn: {inn_name}")

        journal = message.get("journal", []) or []

        print()
        print("Travelogue:")

        if not journal:
            print("  none")
        else:
            for entry in journal:
                if not isinstance(entry, dict):
                    print(f"  - {entry}")
                    continue

                entry_type = entry.get("entry_type") or "unknown"
                body = _clean_skill_text(entry.get("text"))
                bonus = _clean_skill_text(entry.get("bonus_text"))

                if body:
                    body_lines = [
                        line.strip()
                        for line in body.splitlines()
                        if line.strip()
                    ]
                else:
                    body_lines = []

                if body_lines:
                    print(f"  - [{entry_type}] {body_lines[0]}")
                    for line in body_lines[1:]:
                        print(f"      {line}")
                else:
                    print(f"  - [{entry_type}]")

                if bonus:
                    for line in bonus.splitlines():
                        line = line.strip()
                        if line:
                            print(f"      reward: {line}")

                quirk = entry.get("quirk")
                if isinstance(quirk, dict):
                    quirk_label = _clean_skill_text(
                        quirk.get("quirk_label")
                    )
                    gained_label = _clean_skill_text(
                        quirk.get("gained_label")
                    )
                    source_label = _clean_skill_text(
                        quirk.get("source_label")
                    )
                    description = _clean_skill_text(
                        quirk.get("description")
                    )
                    category = quirk.get("category")

                    if gained_label:
                        print(f"      hero: {gained_label}")
                    if quirk_label:
                        suffix = f" ({category})" if category else ""
                        print(f"      quirk: {quirk_label}{suffix}")
                    if source_label:
                        print(f"      {source_label}")
                    if description:
                        for line in description.splitlines():
                            line = line.strip()
                            if line:
                                print(f"      tooltip: {line}")

                for link in entry.get("item_links", []) or []:
                    if not isinstance(link, dict):
                        continue

                    label = _clean_skill_text(link.get("label"))
                    item_id = link.get("item_id") or "<unknown>"
                    qty = link.get("qty")
                    tooltip = _clean_skill_text(link.get("tooltip"))

                    print(
                        f"      hover: {label or item_id} "
                        f"x{qty}"
                    )

                    if tooltip:
                        for line in tooltip.splitlines():
                            line = line.strip()
                            if line:
                                print(f"        {line}")

    if frontend_state == "inn_end_expedition":
        forced = message.get("forced")
        total_hope = message.get("total_hope")
        scores = message.get("scores", []) or []

        print()
        print("Expedition results:")

        if not scores:
            print("  none")
        else:
            for row in scores:
                if not isinstance(row, dict):
                    print(f"  {row}")
                    continue

                label = _clean_skill_text(row.get("label"))
                score_type = row.get("score_type") or "unknown"
                value = row.get("display_value")
                modifier = row.get("score_modifier")
                individual_id = row.get("individual_id")

                name = label or score_type
                if individual_id:
                    name += f" [{individual_id}]"

                value_text = "X" if value == 0 else str(value)
                line = f"  {name}: {value_text}"
                if modifier not in (None, 0):
                    line += f" (modifier {modifier:+d})"
                print(line)

        print()
        if total_hope is not None:
            print(f"Hope to take: {total_hope}")
        if forced is True:
            print("Run end is forced at this Inn.")

        print()
        print("Commands:")
        print("  take_hope")
        print("  back")
        print("  state")
        print("  quit")
        print()
        return

    if frontend_state in {"inventory_overfilled", "inventory"}:
        filled = message.get("filled_slots")
        limit = message.get("inventory_limit")
        over = message.get("overfilled_count")
        ui_open = message.get("inventory_ui_open")

        if filled is not None and limit is not None:
            print(f"Inventory capacity: {filled} / {limit}")

        if frontend_state == "inventory_overfilled":
            print(f"Movement impossible: player inventory is overfilled by {over} slot(s).")
            print("Use `inventory` to inspect items, then `discard_item <slot>`.")
            if ui_open is False:
                print("Native inventory UI is still opening; discard will become available when it is open.")
        else:
            print("Player inventory is open.")
            print("Use `inventory` to inspect items.")

    if frontend_state == "expedition_results":
        outcome = message.get("outcome")
        if outcome:
            print(f"Outcome: {outcome}")

    if frontend_state == "node_entry":
        node_type = message.get("node_type")
        button_key = message.get("button_key")
        if node_type:
            print(f"Node: {node_type}")
        if button_key:
            print(f"Button key: {button_key}")
        if "has_candle_reward" in message:
            print(
                "Candle reward: "
                + ("yes" if message.get("has_candle_reward") else "no")
            )

    if frontend_state in {
        "hero_select_confirmation",
        "new_expedition_confirmation",
        "post_combat_loot_confirmation",
        "end_expedition_confirmation",
                "stagecoach_equip_confirmation",
    }:
        reason = message.get("reason")

        print()
        if reason == "incomplete_skill_loadout":
            print(
                "At least one hero has free combat-skill slots "
                "and additional skills available."
            )
        elif reason == "abandon_existing_expedition":
            print(
                "A saved expedition already exists. Starting a new one "
                "will abandon it."
            )
        elif reason == "leave_items":
            print("Loot remains. Continuing will leave those items behind.")
        elif reason == "take_hope_and_end_expedition":
            print("Taking the accumulated Hope will end the current expedition.")
        elif reason == "permanent_stagecoach_item":
            print("This stagecoach item cannot be unequipped after installation.")
        elif reason:
            print(f"Reason: {reason}")

        dialog_type = message.get("dialog_type")
        if dialog_type:
            print(f"Dialog type: {dialog_type}")

        print()
        print("Commands:")
        print("  confirm")
        print("  cancel")
        print()
        return

    if frontend_state == "hospital":
        active_tab = message.get("active_tab") or ""
        actor_guid = message.get("selected_actor_guid")
        actor_name = message.get("selected_actor_name") or ""

        print()
        print(f"Hospital tab: {active_tab or '<unknown>'}")
        print(f"Selected hero: [{actor_guid}] {actor_name}".rstrip())

        treatment = message.get("treatment") or {}
        if isinstance(treatment, dict):
            print()
            print("Treatment:")
            print(
                f"  HP: {treatment.get('hp_current')} / {treatment.get('hp_max')}"
                + (" [WOUNDED]" if treatment.get("is_wounded") else "")
            )
            print(
                f"  minor heal: {treatment.get('minor_heal_amount_text') or ''} | "
                f"{treatment.get('minor_heal_cost_text') or ''}"
                + ("" if treatment.get("minor_heal_available") else " [UNAVAILABLE]")
            )
            print(
                f"  full heal: {treatment.get('full_heal_description') or ''} | "
                f"{treatment.get('full_heal_cost_text') or ''}"
                + ("" if treatment.get("full_heal_available") else " [UNAVAILABLE]")
            )
            disease_id = treatment.get("disease_id") or ""
            disease_name = treatment.get("disease_name") or ""
            disease_line = f"  disease: {disease_name}"
            if disease_id:
                disease_line += f" ({disease_id})"
            if treatment.get("disease_is_curse"):
                disease_line += " [CURSE]"
            print(disease_line)
            if treatment.get("cure_disease_cost_text"):
                print(
                    f"  cure cost: {treatment.get('cure_disease_cost_text')}"
                    + ("" if treatment.get("cure_disease_available") else " [UNAVAILABLE]")
                )

        print()
        print("Quirks:")
        quirks = message.get("quirks") or []
        if not quirks:
            print("  none")
        else:
            for quirk in quirks:
                if not isinstance(quirk, dict):
                    print(f"  {quirk}")
                    continue
                flags = []
                if quirk.get("is_positive"):
                    flags.append("positive")
                if quirk.get("is_negative"):
                    flags.append("negative")
                if quirk.get("is_disease"):
                    flags.append("disease")
                if quirk.get("is_curse"):
                    flags.append("curse")
                if quirk.get("locked"):
                    flags.append("locked")
                if quirk.get("selected"):
                    flags.append("selected")
                suffix = f" [{', '.join(flags)}]" if flags else ""
                name = quirk.get("name") or ""
                print(f"  {quirk.get('quirk_id')}: {name}{suffix}")

        qt = message.get("quirk_treatment") or {}
        if isinstance(qt, dict) and qt.get("selected_quirk_id"):
            print(
                f"  selected treatment: {qt.get('selected_quirk_id')} | "
                f"lock={qt.get('lock_cost_text') or '-'} | "
                f"remove={qt.get('remove_cost_text') or '-'}"
            )

        store_items = message.get("store_items") or []
        if active_tab == "store":
            print()
            print("Pharmacy:")
            if not store_items:
                print("  out of stock")
            else:
                for item in store_items:
                    if not isinstance(item, dict):
                        print(f"  {item}")
                        continue
                    line = f"  slot {item.get('slot')}: {item.get('item_id') or '<unknown>'}"
                    if item.get("qty") is not None:
                        line += f" x{item.get('qty')}"
                    if item.get("price_text"):
                        line += f" | {item.get('price_text')}"
                    if item.get("can_afford") is False:
                        line += " [CAN'T AFFORD]"
                    print(line)

        print()
        print("Commands:")
        for action in message.get("available_actions", []) or []:
            if not isinstance(action, dict):
                continue
            name = action.get("action")
            if name == "select_hospital_actor":
                print(f"  actor {action.get('actor_guid')}")
            elif name == "hospital_tab":
                print(f"  tab {action.get('tab')}")
            elif name == "select_hospital_quirk":
                print(f"  quirk {action.get('quirk_id')}")
            elif name == "hospital_minor_heal":
                print("  minor_heal")
            elif name == "hospital_full_heal":
                print("  full_heal")
            elif name == "hospital_cure_disease":
                print("  cure_disease")
            elif name == "hospital_lock_quirk":
                print("  lock_quirk")
            elif name == "hospital_remove_quirk":
                print("  remove_quirk")
            elif name == "hospital_buy":
                print(f"  buy {action.get('slot')}")
            elif name == "hospital_state":
                print("  hospital")
            elif name == "leave_hospital":
                print("  leave")

        print("  state")
        print("  raw_actions")
        print("  quit")
        print()
        return

    if frontend_state == "store":
        items = message.get("items", []) or []

        print()
        print("Store:")

        if not items:
            print("  out of stock")
        else:
            for item in items:
                if not isinstance(item, dict):
                    print(f"  {item}")
                    continue

                slot = item.get("slot")
                item_id = item.get("item_id") or "<unknown>"
                qty = item.get("qty")
                price_text = item.get("price_text") or ""
                can_afford = item.get("can_afford")

                line = f"  slot {slot}: {item_id}"
                if qty is not None:
                    line += f" x{qty}"
                if price_text:
                    line += f" | {price_text}"
                if can_afford is False:
                    line += " [CAN'T AFFORD]"
                print(line)

        print()
        print("Commands:")

        for action in message.get("available_actions", []) or []:
            if not isinstance(action, dict):
                continue

            name = action.get("action")
            if name == "buy":
                slot = action.get("slot")
                item_id = action.get("item_id")
                suffix = f"  # {item_id}" if item_id else ""
                print(f"  buy {slot}{suffix}")
            elif name == "store_state":
                print("  store")
            elif name == "leave":
                print("  leave")

        print("  state")
        print("  raw_actions")
        print("  quit")
        print()
        return

    if frontend_state == "post_combat_loot":
        loot = message.get("loot", []) or []

        print()
        print("Loot:")

        if not loot:
            print("  none")
        else:
            for item in loot:
                if not isinstance(item, dict):
                    print(f"  {item}")
                    continue

                slot = item.get("slot")
                item_id = item.get("item_id") or "<unknown>"
                qty = item.get("qty")
                can_take = item.get("can_take")

                line = f"  slot {slot}: {item_id}"
                if qty is not None:
                    line += f" x{qty}"
                if can_take is False:
                    line += " [NO SPACE]"
                print(line)

        print()
        print("Commands:")

        actions = message.get("available_actions", []) or []
        for action in actions:
            if not isinstance(action, dict):
                continue

            name = action.get("action")
            if name == "take_item":
                slot = action.get("slot")
                item_id = action.get("item_id")
                qty = action.get("qty")
                suffix = ""
                if item_id:
                    suffix += f"  # {item_id}"
                    if qty is not None:
                        suffix += f" x{qty}"
                print(f"  take_item {slot}{suffix}")
            elif name == "take_all":
                print("  take_all")
            elif name == "continue":
                print("  continue")

        print("  state")
        print("  raw_actions")
        print("  quit")
        print()
        return

    if frontend_state == "story_choice":
        title = message.get("title") or ""
        story_type = message.get("story_type") or ""
        choices = message.get("choices", []) or []

        if title:
            print(f"Title: {title}")
        if story_type:
            print(f"Story type: {story_type}")

        print()
        print("Choices:")

        if not choices:
            print("  none")
        else:
            for choice in choices:
                if not isinstance(choice, dict):
                    print(f"  {choice}")
                    continue

                actor_guid = choice.get("actor_guid")
                actor_name = choice.get("actor_name") or ""
                actor_data_id = choice.get("actor_data_id") or ""
                choice_id = choice.get("choice_id") or ""

                label = f"[{actor_guid}]"
                if actor_name:
                    label += f" {actor_name}"
                if actor_data_id:
                    label += f" ({actor_data_id})"

                print(f"  {label}")
                if choice_id:
                    print(f"    choice: {choice_id}")

                bark = choice.get("bark") or ""
                if bark:
                    print(f"    bark: {bark}")

                quirk_id = choice.get("quirk_id") or ""
                quirk_name = choice.get("quirk_name") or ""
                if quirk_id or quirk_name:
                    quirk_text = quirk_name or quirk_id
                    if quirk_id and quirk_name and quirk_id != quirk_name:
                        quirk_text += f" ({quirk_id})"
                    print(f"    quirk: {quirk_text}")

                for heading, key in (
                    ("player", "player_previews"),
                    ("enemy", "enemy_previews"),
                ):
                    previews = choice.get(key, []) or []
                    if not previews:
                        continue

                    print(f"    {heading} previews:")

                    for preview in previews:
                        if not isinstance(preview, dict):
                            print(f"      - {preview}")
                            continue

                        preview_id = preview.get("preview_id") or "unknown"
                        value = preview.get("value", 0)
                        show_number = bool(preview.get("show_number"))
                        description = preview.get("description") or ""

                        value_text = ""
                        if value:
                            if show_number:
                                value_text = f" {value:+d}"
                            else:
                                value_text = " +" if value > 0 else " -"

                        line = f"      - {preview_id}{value_text}"
                        if description:
                            line += f": {description}"
                        print(line)

        print()
        print("Commands:")
        for choice in choices:
            if not isinstance(choice, dict):
                continue
            actor_guid = choice.get("actor_guid")
            actor_name = choice.get("actor_name") or ""
            suffix = f"  # {actor_name}" if actor_name else ""
            print(f"  choose {actor_guid}{suffix}")

        print("  state")
        print("  raw_actions")
        print("  quit")
        print()
        return

    if frontend_state == "route_choice":
        options = message.get("options", []) or []

        print()
        print("Routes:")

        if not options:
            print("  none")
        else:
            for option in options:
                if not isinstance(option, dict):
                    print(f"  {option}")
                    continue

                option_index = option.get("option_index")
                direction = option.get("direction") or "unknown"

                if option.get("node_revealed"):
                    node_type = option.get("node_type") or "unknown"
                    node_subtype = option.get("node_subtype") or ""
                    node_text = node_type
                    if node_subtype:
                        node_text += f"/{node_subtype}"
                else:
                    node_text = "unknown"

                if option.get("route_revealed"):
                    route_type = option.get("route_type") or "unknown"
                else:
                    route_type = "unknown"

                print(
                    f"  {option_index}: {direction} -> "
                    f"{node_text} via {route_type}"
                )

        print()
        print("Commands:")
        for option in options:
            if not isinstance(option, dict):
                continue
            option_index = option.get("option_index")
            direction = option.get("direction") or "unknown"
            print(f"  route {option_index}  # {direction}")

        print("  map")
        print("  state")
        print("  raw_actions")
        print("  quit")
        print()
        return

    if frontend_state == "stagecoach_ready":
        party = message.get("party", []) or []

        print()
        print("Party positions:")

        if not party:
            print("  unavailable")
        else:
            for item in party:
                if not isinstance(item, dict):
                    continue
                slot = item.get("slot")
                actor_guid = item.get("actor_guid")
                locked = item.get("locked")
                suffix = " [LOCKED]" if locked else ""
                print(f"  slot {slot}: [{actor_guid}]{suffix}")

        _print_equipment_snapshot(message.get("equipment"))

    if frontend_state == "hero_select":
        heroes = message.get("available_heroes", []) or []

        print()
        print("Heroes:")

        if not heroes:
            print("  none")
        else:
            for hero in heroes:
                if isinstance(hero, dict):
                    suffix = " [SELECTED]" if hero.get("selected") else ""
                    print(f"  {_hero_select_label(hero)}{suffix}")
                else:
                    print(f"  {hero}")

        _print_hero_select_party(message)
        _print_hero_select_commands(message)
        print()
        return

    actions = message.get("available_actions", [])
    print()
    print("Available actions:")

    if not actions:
        print("  none")
    else:
        for action in actions:
            if isinstance(action, dict):
                action_name = action.get("action", "<unknown>")
                if (
                    action_name == "select_boss"
                    and action.get("boss_id")
                ):
                    action_name += f" {action['boss_id']}"
                elif (
                    action_name == "choose_route"
                    and action.get("option_index") is not None
                ):
                    action_name += f" {action['option_index']}"
                    direction = action.get("direction")
                    if direction:
                        action_name += f" ({direction})"
                elif (
                    action_name == "move_party_member"
                    and action.get("actor_guid") is not None
                ):
                    action_name += f" {action['actor_guid']}"
                    current_slot = action.get("current_slot")
                    valid_slots = action.get("valid_slots")
                    if current_slot is not None:
                        action_name += f" (current slot {current_slot})"
                    if isinstance(valid_slots, list):
                        action_name += " -> [" + ", ".join(str(x) for x in valid_slots) + "]"
                elif (
                    action_name == "take_item"
                    and action.get("slot") is not None
                ):
                    action_name += f" {action['slot']}"
                    if action.get("item_id"):
                        action_name += f" ({action['item_id']}"
                        if action.get("qty") is not None:
                            action_name += f" x{action['qty']}"
                        action_name += ")"
            else:
                action_name = str(action)

            print(f"  {action_name}")

    print()


def print_combat_snapshot(message, heading):
    print()
    print(heading)
    print()

    current_actor = message.get("current_actor")
    current_actor_guid = message.get("current_actor_guid")
    actor_lookup = build_actor_lookup(message)

    print("Current actor:")

    if isinstance(current_actor, dict):
        print_actor_block(current_actor)
    elif current_actor_guid is not None:
        print(f"  [{current_actor_guid}]")
    else:
        print("  <unknown>")

    round_number = message.get("round")
    turn_index = message.get("turn_index")
    turn_order = message.get("turn_order", [])

    if round_number is not None:
        print()
        if turn_index is None:
            print(f"Round: {round_number}")
        else:
            print(
                f"Round: {round_number}   "
                f"native turn index: {turn_index}"
            )

    print()
    print("Turn order:")

    if not turn_order:
        print("  none")
    else:
        for index, guid in enumerate(turn_order):
            marker = " <- current" if index == 0 else ""
            print(
                f"  {index + 1}. "
                f"{format_target(guid, actor_lookup)}"
                f"{marker}"
            )

    friendly = message.get("friendly", [])
    enemy = message.get("enemy", [])

    if friendly:
        print()
        print("Party:")

        for actor in friendly:
            print_actor_block(actor)

    if enemy:
        print()
        print("Enemies:")

        for actor in enemy:
            print_actor_block(actor)

    actions = message.get("available_actions", [])

    print()
    print("Available actions:")

    if not actions:
        print("  none")
    else:
        for action in actions:
            skill_id = action.get("skill_id", "<unknown>")
            forced = action.get("forced", False)
            stealthed_targets = action.get("stealthed_targets", [])

            suffix = " [FORCED]" if forced else ""

            print(f"  {skill_id}{suffix}")
            _print_skill_info(action.get("skill_info"))
            _print_action_targets(action, actor_lookup)

            if stealthed_targets:
                formatted_stealthed = ", ".join(
                    format_target(guid, actor_lookup)
                    for guid in stealthed_targets
                )
                print(f"    stealthed targets: {formatted_stealthed}")

    print()



def _pipe_reader_worker(pipe, inbox):
    import ctypes
    from ctypes import wintypes
    import msvcrt
    import os
    import time

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    kernel32.PeekNamedPipe.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        wintypes.DWORD,
        ctypes.c_void_p,
        ctypes.POINTER(wintypes.DWORD),
        ctypes.c_void_p,
    ]
    kernel32.PeekNamedPipe.restype = wintypes.BOOL

    fd = pipe.fileno()
    handle = wintypes.HANDLE(msvcrt.get_osfhandle(fd))

    buffer = bytearray()

    try:
        while True:
            available = wintypes.DWORD(0)

            ok = kernel32.PeekNamedPipe(
                handle,
                None,
                0,
                None,
                ctypes.byref(available),
                None,
            )

            if not ok:
                error = ctypes.get_last_error()

                # ERROR_BROKEN_PIPE / ERROR_NO_DATA
                if error in (109, 232):
                    raise ConnectionError(
                        f"DD2Bridge disconnected (WinError {error})"
                    )

                raise ctypes.WinError(error)

            if available.value == 0:
                time.sleep(0.01)
                continue

            chunk = os.read(fd, available.value)

            if not chunk:
                raise ConnectionError("DD2Bridge disconnected")

            buffer.extend(chunk)

            while b"\n" in buffer:
                raw_line, _, remainder = buffer.partition(b"\n")
                buffer = bytearray(remainder)

                text = raw_line.decode("utf-8").strip()

                if not text:
                    continue

                message = json.loads(text)
                inbox.put(("message", message))

    except (ConnectionError, BrokenPipeError, OSError, ValueError) as ex:
        inbox.put(("disconnect", ex))
    except Exception as ex:
        inbox.put(("reader_error", ex))


def _command_reader_worker(session_state, inbox):
    """Run the existing blocking CLI/submenu reader off the session thread.

    This thread is deliberately daemonized by run_session. If DD2 disappears
    while input() is waiting, the main session thread can observe pipe EOF and
    terminate the host immediately instead of being trapped at the prompt.
    """
    try:
        command = read_command(session_state)
        inbox.put(("command", command))
    except EOFError:
        inbox.put(("command", None))
    except KeyboardInterrupt:
        inbox.put(("command", None))
    except Exception as ex:
        inbox.put(("input_error", ex))


def run_session(pipe):
    waiting_for_command = False
    session_state = {
        "frontend_snapshot": None,
        "hero_inspections": {},
        "pending_skills_actor_guid": None,
        "skills_context": None,
        "party_context": False,
        "activity": None,
    }

    inbox = queue.Queue()
    activity = ActivityIndicator()

    pipe_reader = threading.Thread(
        target=_pipe_reader_worker,
        args=(pipe, inbox),
        name="dd2-host-pipe-reader",
        daemon=True,
    )
    pipe_reader.start()

    command_reader = None

    while True:
        if waiting_for_command and (
            command_reader is None or not command_reader.is_alive()
        ):
            command_reader = threading.Thread(
                target=_command_reader_worker,
                args=(session_state, inbox),
                name="dd2-host-command-reader",
                daemon=True,
            )
            command_reader.start()

        try:
            item_type, payload = inbox.get(timeout=0.1)
        except queue.Empty:
            continue

        if item_type == "disconnect":
            activity.stop()
            print()
            print("DD2Bridge disconnected. The game/bridge has closed.")
            print("Closing host.")
            return

        if item_type == "reader_error":
            activity.stop()
            print()
            print(f"DD2Bridge reader failed: {payload}")
            print("Closing host.")
            return

        if item_type == "input_error":
            activity.stop()
            print()
            print(f"Host input failed: {payload}")
            print("Closing host.")
            return

        if item_type == "command":
            command_reader = None

            if payload is None:
                activity.stop()
                print("Closing host.")
                return

            try:
                send_message(pipe, payload)
            except (BrokenPipeError, OSError, ValueError):
                activity.stop()
                print()
                print("DD2Bridge disconnected while sending a command.")
                print("Closing host.")
                return

            waiting_for_command = False
            continue

        if item_type != "message":
            continue

        message = payload
        message_type = message.get("type")

        if message_type == "bridge_info":
            version = str(message.get("version") or "<unknown>")
            print(f"DD2Bridge is version {version}")
            continue

        if message_type == "status":
            activity_name = message.get("activity")
            session_state["activity"] = activity_name

            if not activity_name:
                activity.stop()
                print(
                    "UNKNOWN STATUS:",
                    json.dumps(message, ensure_ascii=False),
                )
                waiting_for_command = False
            elif activity_name == "driving":
                # ActivityIndicator.start() records the normal STATUS line.
                # Pause its animated redraw before input() starts; otherwise the
                # spinner and Windows console line editor would overwrite each
                # other while the user types the interrupt command.
                activity.start(activity_name)
                activity.stop()
                print("Driving is interruptible. Commands: map, stop_driving.")
                waiting_for_command = True
            else:
                activity.start(activity_name)
                waiting_for_command = False

            continue

        # Keep asynchronous events readable while an activity is spinning.
        activity.clear_line()

        if message_type == "event":
            event_name = message.get("event", "<unknown>")
            performer_guid = message.get("performer_guid")
            target_guid = message.get("target_guid")
            skill_id = message.get("skill_id")
            amount = message.get("amount")

            if event_name == "skill_used":
                target_guids = message.get("target_guids", [])
                targets_text = ", ".join(
                    f"[{guid}]" for guid in target_guids
                )

                print(
                    f"EVENT skill_used: "
                    f"[{performer_guid}] {skill_id}"
                    + (
                        f" -> {targets_text}"
                        if targets_text
                        else ""
                    )
                )

            elif (
                event_name in {"stress_damage", "stress_heal"}
                and message.get("stress_trigger")
            ):
                source_type = message.get("source_type") or ""
                source_id = message.get("source_id") or ""
                trigger_stress = message.get("trigger_stress")
                bark_valid = message.get("bark_valid")

                extras = []
                if source_type or source_id:
                    extras.append(
                        "source="
                        + "/".join(
                            part
                            for part in (source_type, source_id)
                            if part
                        )
                    )
                if trigger_stress is not None:
                    extras.append(f"trigger_stress={trigger_stress}")
                if bark_valid is not None:
                    extras.append(f"bark_valid={bark_valid}")

                extras_text = (
                    " (" + ", ".join(extras) + ")"
                    if extras
                    else ""
                )

                print(
                    f"EVENT {event_name}: "
                    f"[{target_guid}] amount={amount}"
                    f"{extras_text}"
                )

            elif event_name == "dot_tick":
                source_actor_guids = (
                    message.get("source_actor_guids") or []
                )
                dot_type = message.get("dot_type") or ""
                killed = message.get("killed")

                prefix = ""
                if source_actor_guids:
                    prefix = (
                        "[" + ",".join(
                            str(guid)
                            for guid in source_actor_guids
                        ) + "] -> "
                    )

                extras = []
                if killed:
                    extras.append("killed=True")

                extras_text = (
                    " (" + ", ".join(extras) + ")"
                    if extras
                    else ""
                )

                print(
                    f"EVENT dot_tick: {prefix}[{target_guid}] "
                    f"{dot_type} amount={amount}{extras_text}"
                )

            elif event_name in {
                "health_damage",
                "health_heal",
            }:
                source_type = message.get("source_type") or ""
                source_ids = message.get("source_ids") or []
                source_actor_guids = (
                    message.get("source_actor_guids") or []
                )
                requested_amount = message.get("requested_amount")
                is_crit = message.get("is_crit")
                is_riposte = message.get("is_riposte")
                entering_deaths_door = (
                    message.get("entering_deaths_door")
                )
                killed = message.get("killed")
                death_type = message.get("death_type") or ""
                provenance = message.get("provenance") or ""

                actor_prefix = ""
                if source_actor_guids:
                    actor_prefix = (
                        "[" + ",".join(
                            str(guid)
                            for guid in source_actor_guids
                        ) + "] -> "
                    )
                elif performer_guid not in (None, 0):
                    actor_prefix = f"[{performer_guid}] -> "

                extras = []

                if requested_amount is not None:
                    extras.append(
                        f"requested={requested_amount}"
                    )

                if source_type:
                    extras.append(
                        f"source_type={source_type}"
                    )

                if source_ids:
                    extras.append(
                        "source="
                        + ",".join(str(x) for x in source_ids)
                    )

                if is_crit is not None:
                    extras.append(f"crit={is_crit}")

                if is_riposte is not None:
                    extras.append(f"riposte={is_riposte}")

                if entering_deaths_door:
                    extras.append("entering_deaths_door=True")

                if killed:
                    extras.append("killed=True")

                if death_type:
                    extras.append(f"death_type={death_type}")

                if provenance:
                    extras.append(f"provenance={provenance}")

                if message.get("provenance_known") is False:
                    extras.append("provenance=<unknown>")

                extras_text = (
                    " (" + ", ".join(extras) + ")"
                    if extras
                    else ""
                )

                print(
                    f"EVENT {event_name}: "
                    f"{actor_prefix}[{target_guid}] "
                    f"amount={amount}{extras_text}"
                )

            elif event_name in {
                "stress_damage",
                "stress_heal",
            }:
                amount_text = (
                    f" amount={amount}"
                    if amount is not None
                    else ""
                )

                print(
                    f"EVENT {event_name}: "
                    f"[{performer_guid}] -> [{target_guid}]"
                    f"{amount_text}"
                )

            elif event_name == "position_changed":
                actor_guid = (
                    message.get("actor_guid")
                    if message.get("actor_guid") is not None
                    else target_guid
                )
                previous = message.get("previous_position")
                current = message.get("new_position")
                delta = message.get("delta")
                team_index = message.get("team_index")
                source_type = message.get("source_type") or ""
                source_actor_guid = message.get("source_actor_guid")
                move_effects_valid = message.get("is_move_effects_valid")

                if isinstance(delta, int):
                    delta_text = f"{delta:+d}"
                elif isinstance(delta, float):
                    delta_text = f"{delta:+g}"
                else:
                    delta_text = str(delta)

                extras = []

                if team_index is not None:
                    extras.append(f"team={team_index}")

                if source_type:
                    extras.append(f"source_type={source_type}")

                if source_actor_guid not in (None, 0):
                    extras.append(f"source_actor=[{source_actor_guid}]")

                if move_effects_valid is not None:
                    extras.append(
                        f"move_effects_valid={move_effects_valid}"
                    )

                extras_text = (
                    " (" + ", ".join(extras) + ")"
                    if extras
                    else ""
                )

                print(
                    f"EVENT position_changed: "
                    f"[{actor_guid}] "
                    f"{previous} -> {current} ({delta_text})"
                    f"{extras_text}"
                )

            elif event_name == "affinity_change":
                actor_guids = message.get("actor_guids") or []
                previous = message.get("previous")
                current = message.get("current")
                source_type = message.get("source_type")
                source_actor_guid = message.get("source_actor_guid")

                actors_text = " <-> ".join(
                    f"[{guid}]" for guid in actor_guids
                ) or "<unknown connection>"

                extras = []

                if previous is not None and current is not None:
                    extras.append(f"{previous} -> {current}")

                if source_type:
                    extras.append(f"source={source_type}")

                if source_actor_guid is not None:
                    extras.append(f"source_actor=[{source_actor_guid}]")

                extras_text = (
                    " (" + ", ".join(extras) + ")"
                    if extras
                    else ""
                )

                amount_text = (
                    f"{amount:+d}"
                    if isinstance(amount, int)
                    else (
                        f"{amount:+g}"
                        if isinstance(amount, float)
                        else str(amount)
                    )
                )

                print(
                    f"EVENT affinity_change: "
                    f"{actors_text} {amount_text}{extras_text}"
                )

            elif event_name in {
                "token_added",
                "token_removed",
                "token_changed",
            }:
                token_id = message.get("token_id") or "<unknown-token>"
                source_type = message.get("source_type") or ""
                source_id = message.get("source_id") or ""
                source_actor_guid = message.get("source_actor_guid")
                duration = message.get("duration")

                extras = []

                if duration is not None:
                    extras.append(f"duration={duration}")

                if source_type:
                    extras.append(f"source_type={source_type}")

                if source_id:
                    extras.append(f"source={source_id}")

                if source_actor_guid not in (None, 0):
                    extras.append(f"source_actor=[{source_actor_guid}]")

                if (
                    event_name == "token_removed"
                    and message.get("removal_source_known") is False
                ):
                    extras.append("removal_source=<unknown>")

                if event_name == "token_changed":
                    previous_duration = message.get("previous_duration")
                    if previous_duration is not None:
                        extras.append(
                            f"previous_duration={previous_duration}"
                        )

                extras_text = (
                    " " + " ".join(extras)
                    if extras
                    else ""
                )

                print(
                    f"EVENT {event_name}: "
                    f"[{target_guid}] {token_id}{extras_text}"
                )

            elif event_name in {
                "quirk_added",
                "quirk_removed",
                "quirk_changed",
            }:
                quirk_id = message.get("quirk_id") or "<unknown-quirk>"
                name = message.get("name") or ""
                description = message.get("description") or ""
                source_type = message.get("source_type") or ""
                source_id = message.get("source_id") or ""
                source_actor_guid = message.get("source_actor_guid")
                add_group = message.get("add_group")
                is_locked = message.get("is_locked")
                kind = message.get("kind") or ""

                name = re.sub(r"<[^>]+>", "", str(name)).strip()
                description = re.sub(r"<[^>]+>", "", str(description))
                description = " | ".join(
                    part.strip()
                    for part in description.splitlines()
                    if part.strip()
                )

                label = (
                    f"{name} ({quirk_id})"
                    if name and name != quirk_id
                    else quirk_id
                )

                extras = []

                if kind:
                    extras.append(f"kind={kind}")

                if description:
                    extras.append(f'description="{description}"')

                if source_type:
                    extras.append(f"source_type={source_type}")

                if source_id:
                    extras.append(f"source={source_id}")

                if source_actor_guid not in (None, 0):
                    extras.append(f"source_actor=[{source_actor_guid}]")

                if add_group is not None:
                    extras.append(f"add_group={add_group}")

                if is_locked is not None:
                    extras.append(f"locked={is_locked}")

                if (
                    event_name == "quirk_removed"
                    and message.get("removal_source_known") is False
                ):
                    extras.append("removal_source=<unknown>")

                extras_text = (
                    " " + " ".join(extras)
                    if extras
                    else ""
                )

                print(
                    f"EVENT {event_name}: "
                    f"[{target_guid}] {label}{extras_text}"
                )

            elif event_name in {
                "buff_added",
                "buff_removed",
                "buff_changed",
            }:
                buff_id = message.get("buff_id") or "<unknown-buff>"
                description = message.get("description") or ""
                source_type = message.get("source_type") or ""
                source_id = message.get("source_id") or ""
                source_actor_guid = message.get("source_actor_guid")
                duration = message.get("duration")

                # Native DD2 descriptions may contain Unity rich-text tags and
                # line breaks. Keep the native wording but make it CLI-friendly.
                description = re.sub(r"<[^>]+>", "", str(description))
                description = " | ".join(
                    part.strip()
                    for part in description.splitlines()
                    if part.strip()
                )

                kind = (
                    "debuff"
                    if message.get("is_debuff")
                    else (
                        "buff"
                        if message.get("is_buff")
                        else "condition"
                    )
                )

                extras = [f"kind={kind}"]

                if description:
                    extras.append(f'description="{description}"')

                if duration is not None:
                    extras.append(f"duration={duration}")

                if source_type:
                    extras.append(f"source_type={source_type}")

                if source_id:
                    extras.append(f"source={source_id}")

                if source_actor_guid not in (None, 0):
                    extras.append(f"source_actor=[{source_actor_guid}]")

                if (
                    event_name == "buff_removed"
                    and message.get("removal_source_known") is False
                ):
                    extras.append("removal_source=<unknown>")

                if event_name == "buff_changed":
                    previous_duration = message.get("previous_duration")
                    if previous_duration is not None:
                        extras.append(
                            f"previous_duration={previous_duration}"
                        )

                extras_text = (
                    " " + " ".join(extras)
                    if extras
                    else ""
                )

                print(
                    f"EVENT {event_name}: "
                    f"[{target_guid}] {buff_id}{extras_text}"
                )

            elif event_name in {
                "dot_added",
                "dot_removed",
                "dot_changed",
            }:
                dot = message.get("dot") or {}
                dot_id = (
                    message.get("dot_id")
                    or dot.get("m_Definition")
                    or "<unknown-dot>"
                )
                duration = (
                    message.get("duration")
                    if message.get("duration") is not None
                    else dot.get("m_DurationAmountRemaining")
                )
                source_id = message.get("source_id")
                source_type = message.get("source_type")

                extras = []

                if duration is not None:
                    extras.append(f"duration={duration}")

                if source_type:
                    extras.append(f"source_type={source_type}")

                if source_id:
                    extras.append(f"source={source_id}")

                extras_text = (
                    " " + " ".join(extras)
                    if extras
                    else ""
                )

                print(
                    f"EVENT {event_name}: "
                    f"[{performer_guid}] -> [{target_guid}] "
                    f"{dot_id}{extras_text}"
                )

            elif event_name in {
                "miss",
                "dodge",
                "crit",
                "blocked",
                "entered_deaths_door",
                "deathblow_survived",
                "actor_killed",
            }:
                suffix = (
                    f" skill={skill_id}"
                    if skill_id
                    else ""
                )

                print(
                    f"EVENT {event_name}: "
                    f"[{performer_guid}] -> [{target_guid}]"
                    f"{suffix}"
                )

            else:
                print(
                    "EVENT",
                    json.dumps(message, ensure_ascii=False),
                )

        elif message_type == "ready":
            activity.stop()
            session_state["activity"] = None

            if message.get("frontend_state"):
                session_state["frontend_snapshot"] = message

            if message.get("frontend_state") in {
                "hero_select_confirmation",
                "new_expedition_confirmation",
                "post_combat_loot_confirmation",
                "end_expedition_confirmation",
            }:
                session_state["party_context"] = False
                session_state["skills_context"] = None
                session_state["pending_skills_actor_guid"] = None

            print_frontend_snapshot(message, "READY")
            waiting_for_command = True

        elif message_type == "map_state":
            _print_map_state(message)
            waiting_for_command = True

        elif message_type == "state":
            if message.get("frontend_state"):
                session_state["frontend_snapshot"] = message

            if (
                message.get("frontend_state") == "hero_select"
                and session_state.get("party_context")
            ):
                _print_party_submenu(message)
            else:
                print_frontend_snapshot(message, "STATE")

            waiting_for_command = True

        elif message_type == "hero_inspection":
            actor_guid = message.get("actor_guid")
            if actor_guid is not None:
                session_state["hero_inspections"][actor_guid] = message

            pending_skills_actor_guid = session_state.get(
                "pending_skills_actor_guid"
            )

            if (
                actor_guid is not None
                and (
                    pending_skills_actor_guid == actor_guid
                    or session_state.get("skills_context") == actor_guid
                )
            ):
                session_state["pending_skills_actor_guid"] = None
                session_state["skills_context"] = actor_guid
                _print_skills_submenu(
                    session_state.get("frontend_snapshot") or {},
                    message,
                    actor_guid,
                )
                waiting_for_command = True
            else:
                print()
                print("HERO INSPECTION")
                print()

                guid = message.get("actor_guid")
                name = message.get("actor_name") or ""
                actor_id = message.get("actor_data_id") or ""

                label = f"[{guid}]"
                if name:
                    label += f" {name}"
                if actor_id:
                    label += f" ({actor_id})"
                print(label)

                path = message.get("path") or {}
                if isinstance(path, dict):
                    print()
                    print("Path:")
                    current_path = path.get("current_path_id")
                    available_paths = path.get("available_path_ids") or []
                    print(f"  current: {current_path or '<none>'}")
                    if available_paths:
                        print("  available: " + ", ".join(str(x) for x in available_paths))
                    else:
                        print("  available: none")

                stats = message.get("stats") or {}
                if isinstance(stats, dict):
                    print()
                    print("Stats:")
                    if stats.get("hp_max") is not None:
                        print(f"  hp max: {_format_number(stats.get('hp_max'))}")
                    if stats.get("speed") is not None:
                        print(f"  speed: {_format_number(stats.get('speed'))}")

                    resists = stats.get("resistances") or {}
                    if isinstance(resists, dict):
                        print("  resistances:")
                        if not resists:
                            print("    none")
                        else:
                            for resist_id, value in resists.items():
                                try:
                                    pct = float(value) * 100.0
                                    if pct.is_integer():
                                        value_text = f"{int(pct)}%"
                                    else:
                                        value_text = f"{pct:.2f}".rstrip("0").rstrip(".") + "%"
                                except (TypeError, ValueError):
                                    value_text = str(value)
                                print(f"    {resist_id}: {value_text}")

                skills = message.get("skills") or {}
                if isinstance(skills, dict):
                    print()
                    print("Skills:")
                    if skills.get("equipped_limit") is not None:
                        print(f"  equipped limit: {skills.get('equipped_limit')}")

                    equipped = skills.get("equipped") or []
                    print("  equipped:")
                    if equipped:
                        for skill_id in equipped:
                            print(f"    {skill_id}")
                    else:
                        print("    none")

                    available = skills.get("available") or []
                    print("  available:")
                    if available:
                        for skill_id in available:
                            print(f"    {skill_id}")
                    else:
                        print("    none")

                quirks = message.get("quirks") or {}
                if isinstance(quirks, dict):
                    print()
                    print("Quirks:")
                    for category in (
                        "positive",
                        "negative",
                        "disease_or_curse",
                        "unclassified",
                    ):
                        items = quirks.get(category) or []
                        print(f"  {category}:")
                        if not items:
                            print("    none")
                            continue

                        for item in items:
                            if isinstance(item, dict):
                                quirk_id = item.get("quirk_id") or "<unknown>"
                                flags = []
                                if item.get("is_disease"):
                                    flags.append("disease")
                                if item.get("is_curse"):
                                    flags.append("curse")
                                suffix = f" ({', '.join(flags)})" if flags else ""
                                print(f"    {quirk_id}{suffix}")
                            else:
                                print(f"    {item}")

                memories = message.get("memories") or []
                print()
                print("Memories:")
                if not memories:
                    print("  none")
                else:
                    for memory in memories:
                        if isinstance(memory, dict):
                            memory_id = (
                                memory.get("item_id")
                                or memory.get("id")
                                or memory.get("definition_id")
                                or "<unknown>"
                            )
                            name = (
                                memory.get("name")
                                or memory.get("display_name")
                                or ""
                            )
                            if name:
                                print(f"  {memory_id} - {name}")
                            else:
                                print(f"  {memory_id}")
                        else:
                            print(f"  {memory}")

                print()
                waiting_for_command = True

        elif message_type == "inn_end_expedition_state":
            end_expedition_snapshot = {
                "frontend_state": "inn_end_expedition",
                "current_mode": "inn",
                "forced": message.get("forced"),
                "total_hope": message.get("total_hope"),
                "scores": message.get("scores") or [],
                "available_actions": (message.get("available_actions") or []),
            }
            session_state["frontend_snapshot"] = end_expedition_snapshot
            print_frontend_snapshot(
                end_expedition_snapshot,
                "END EXPEDITION",
            )
            waiting_for_command = True

        elif message_type == "inn_items_state":
            inn_items_snapshot = {
                "frontend_state": "inn_items",
                "current_mode": message.get("current_mode") or "inn",
                "party": message.get("party") or [],
                "items": message.get("items") or [],
                "available_actions": message.get("available_actions") or [],
            }
            session_state["frontend_snapshot"] = inn_items_snapshot
            print_frontend_snapshot(inn_items_snapshot, "INN ITEMS")
            waiting_for_command = True

        elif message_type == "inn_item_result":
            print()
            print("CONSEQUENCES — INN ITEM")
            print(f"Item: {message.get('item_id') or '<unknown>'} (slot {message.get('slot')})")
            consequences = message.get("consequences") or []
            if not consequences:
                print("  no observable state changes")
            for c in consequences:
                if not isinstance(c, dict):
                    print(f"  {c}")
                    continue
                ctype = c.get("type") or "change"
                actor = c.get("actor_guid")
                line = f"  {ctype}" + (f" actor=[{actor}]" if actor is not None else "")
                if "delta" in c:
                    line += f" delta={c.get('delta')} ({c.get('before')} -> {c.get('after')})"
                elif ctype.startswith("quirk_"):
                    line += f" {c.get('name') or c.get('quirk_id') or ''}"
                else:
                    line += " " + json.dumps(c, ensure_ascii=False, separators=(",", ":"))
                print(line)
            print()

        elif message_type == "inn_route_choice_state":
            route_snapshot = {
                "frontend_state": "inn_route_choice",
                "current_mode": message.get("current_mode"),
                "selected_index": message.get("selected_index"),
                "choices": message.get("choices") or [],
                "available_actions": message.get("available_actions") or [],
            }
            session_state["frontend_snapshot"] = route_snapshot
            print_frontend_snapshot(route_snapshot, "CHOOSE ROUTE")
            waiting_for_command = True

        elif message_type == "inn_wainwright_state":
            wainwright_snapshot = {
                "frontend_state": "inn_wainwright",
                "current_mode": message.get("current_mode"),
                "stagecoach_name": message.get("stagecoach_name") or "",
                "general_slots": message.get("general_slots") or [],
                "trophy_slots": message.get("trophy_slots") or [],
                "pet_slots": message.get("pet_slots") or [],
                "flame_slots": message.get("flame_slots") or [],
                "inventory_items": message.get("inventory_items") or [],
                "available_actions": message.get("available_actions") or [],
            }
            session_state["frontend_snapshot"] = wainwright_snapshot
            print_frontend_snapshot(wainwright_snapshot, "WAINWRIGHT")
            waiting_for_command = True

        elif message_type == "inn_mastery_state":
            mastery_snapshot = {
                "frontend_state": "inn_mastery",
                "current_mode": message.get("current_mode"),
                "mastery_points": message.get("mastery_points"),
                "heroes": message.get("heroes") or [],
                "available_actions": message.get("available_actions") or [],
            }
            session_state["frontend_snapshot"] = mastery_snapshot
            print_frontend_snapshot(mastery_snapshot, "MASTERY TRAINER")
            waiting_for_command = True

        elif message_type == "inn_store_state":
            store_snapshot = {
                "frontend_state": "inn_store",
                "current_mode": message.get("current_mode"),
                "store_items": message.get("store_items") or [],
                "inventory_limit": message.get("inventory_limit"),
                "filled_slots": message.get("filled_slots"),
                "overfilled_count": message.get("overfilled_count"),
                "is_overfilled": message.get("is_overfilled"),
                "inventory_items": message.get("inventory_items") or [],
                "available_actions": message.get("available_actions") or [],
            }
            session_state["frontend_snapshot"] = store_snapshot
            print_frontend_snapshot(store_snapshot, "INN STORE")
            waiting_for_command = True

        elif message_type == "inn_travelogue_state":
            print_frontend_snapshot(
                {
                    "frontend_state": "inn_travelogue",
                    "current_mode": "inn",
                    "inn_name": message.get("inn_name") or "",
                    "journal": message.get("journal") or [],
                    "available_actions": [],
                },
                "TRAVELOGUE",
            )
            waiting_for_command = True

        elif message_type == "hospital_state":
            snapshot = session_state.get("frontend_snapshot")

            hospital_fields = (
                "active_tab",
                "selected_actor_guid",
                "selected_actor_name",
                "party",
                "treatment",
                "quirks",
                "quirk_treatment",
                "store_items",
                "available_actions",
            )

            if (
                isinstance(snapshot, dict)
                and snapshot.get("frontend_state") == "hospital"
            ):
                for field in hospital_fields:
                    snapshot[field] = message.get(field)
                print_frontend_snapshot(snapshot, "HOSPITAL STATE (updated)")
            else:
                hospital_snapshot = {"frontend_state": "hospital"}
                for field in hospital_fields:
                    hospital_snapshot[field] = message.get(field)
                print_frontend_snapshot(hospital_snapshot, "HOSPITAL STATE")

            waiting_for_command = True

        elif message_type == "store_state":
            snapshot = session_state.get("frontend_snapshot")

            if (
                isinstance(snapshot, dict)
                and snapshot.get("frontend_state") == "store"
            ):
                snapshot["items"] = message.get("items") or []
                snapshot["available_actions"] = (
                    message.get("available_actions") or []
                )
                print_frontend_snapshot(
                    snapshot,
                    "STORE STATE (updated)",
                )
            else:
                print_frontend_snapshot(
                    {
                        "frontend_state": "store",
                        "items": message.get("items") or [],
                        "available_actions": (
                            message.get("available_actions") or []
                        ),
                    },
                    "STORE STATE",
                )

            waiting_for_command = True

        elif message_type == "inventory_state":
            _print_inventory_state(message)
            waiting_for_command = True

        elif message_type == "equipment_state":
            equipment = message.get("equipment") or {}

            snapshot = session_state.get("frontend_snapshot")
            if isinstance(snapshot, dict):
                snapshot["equipment"] = equipment

            _print_equipment_snapshot(equipment)
            waiting_for_command = True

        elif message_type == "error":
            activity.stop()
            print(
                "ERROR:",
                message.get("message", "unknown error"),
            )

            # Command error does not consume the decision boundary.
            waiting_for_command = True

        else:
            print(
                "UNKNOWN MESSAGE:",
                json.dumps(message, ensure_ascii=False),
            )

def duplicate_pipe_for_read(pipe):
    import ctypes
    from ctypes import wintypes
    import msvcrt
    import os

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    kernel32.GetCurrentProcess.argtypes = []
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE

    kernel32.DuplicateHandle.argtypes = [
        wintypes.HANDLE,   # hSourceProcessHandle
        wintypes.HANDLE,   # hSourceHandle
        wintypes.HANDLE,   # hTargetProcessHandle
        ctypes.POINTER(wintypes.HANDLE),  # lpTargetHandle
        wintypes.DWORD,    # dwDesiredAccess
        wintypes.BOOL,     # bInheritHandle
        wintypes.DWORD,    # dwOptions
    ]
    kernel32.DuplicateHandle.restype = wintypes.BOOL

    current_process = kernel32.GetCurrentProcess()

    source_handle_value = msvcrt.get_osfhandle(pipe.fileno())
    source_handle = wintypes.HANDLE(source_handle_value)

    duplicate_handle = wintypes.HANDLE()

    DUPLICATE_SAME_ACCESS = 0x00000002

    ok = kernel32.DuplicateHandle(
        current_process,
        source_handle,
        current_process,
        ctypes.byref(duplicate_handle),
        0,
        False,
        DUPLICATE_SAME_ACCESS,
    )

    if not ok:
        raise ctypes.WinError(ctypes.get_last_error())

    fd = msvcrt.open_osfhandle(
        duplicate_handle.value,
        os.O_RDONLY | os.O_BINARY,
    )

    return os.fdopen(fd, "rb", buffering=0)

    if not ok:
        raise ctypes.WinError(ctypes.get_last_error())

    fd = msvcrt.open_osfhandle(
        duplicate_handle.value,
        os.O_BINARY,
    )

    return os.fdopen(fd, "rb", buffering=0)

def main():
    (
        transcript_path,
        transcript,
        original_stdout,
        original_stderr,
    ) = start_transcript()

    pipe = None

    try:
        print(f"Host transcript: {transcript_path}")
        pipe = connect()
        run_session(pipe)
    except ConnectionError as ex:
        print()
        print(ex)
    finally:
        if pipe is not None:
            pipe.close()

        try:
            print(f"Host transcript saved: {transcript_path}")
            sys.stdout.flush()
            sys.stderr.flush()
        finally:
            sys.stdout = original_stdout
            sys.stderr = original_stderr
            transcript.close()


if __name__ == "__main__":
    main()



