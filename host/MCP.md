# DDCLI MCP (experimental, post-RC1)

The interactive console remains the default:

```powershell
python -u P:\Vault\DD2AgentHost\host.py
```

The opt-in MCP transport is:

```powershell
python -u P:\Vault\DD2AgentHost\host.py --mcp
```

It uses the **same bridge-native JSON commands** and the same `\\.\pipe\dd2-agent` connection. No C# changes, save-file access, hidden engine observations, network listener, or extra controller process. **Note:** RC1 bridge currently rejects `state` in `inn` with `no stable decision boundary is available`. Use `ddcli_snapshot` for the last emitted READY and avoid treating it as a fresh query. The `implemented: false` field in that READY originates from `BuildInnReadyMessage` and does not imply the listed actions are unimplemented.

## Requirements

- Windows with the Darkest Dungeon II bridge running.
- Python **3.11+**.
- MCP Python SDK **2.x**: `python -m pip install "mcp>=2,<3"`.
- The same Windows user/SID permissions required by the existing DDCLI Named Pipe.
- Both `host.py` **and** `ddcli_mcp.py` in the deployed host directory.

MCP stdout contains protocol traffic only. Unlike the interactive CLI, MCP mode does **not** create a transcript in the user's Documents folder; use the agent's own run notes or MCP client logs. Do not start a second host process while another controls the bridge.

## Codex CLI setup

After deploying the updated host files:

```powershell
codex mcp add ddcli -- python -u "P:\Vault\DD2AgentHost\host.py" --mcp
codex mcp list
```

Restart Codex and check `/mcp`. Codex will launch the server process over stdio; the agent calls MCP tools rather than typing into a terminal.

Equivalent `config.toml` entry:

```toml
[mcp_servers.ddcli]
command = "python"
args = ["-u", "P:\\Vault\\DD2AgentHost\\host.py", "--mcp"]
startup_timeout_sec = 20
tool_timeout_sec = 60
```

If the current Codex environment restricts access to the Windows Named Pipe, MCP does **not** bypass that restriction: diagnose the MCP server process permissions separately. Do not loosen DD2Bridge ACLs without review.

## Tools

- `ddcli_status()` — process connection status, last observed frontend, bridge version and latest message cursor. Connects on first use and reports errors if the pipe is inaccessible.
- `ddcli_state()` — sends the bridge-native `state` command. When the bridge rejects a refresh (for example, the inn is not supported by `HandleCurrentState`), the result explicitly reports `refresh_succeeded: false`, the error, and `frontend_state_source: last_observed`.
- `ddcli_command(action, arguments={})` — sends `{"type":"command","action":"..."}` with the provided action arguments directly to the existing bridge. This is **not** the human CLI's text alias parser. For combat, the bridge accepts **`action="skill"`** and `arguments={"performer_guid":<current_actor_guid>,"skill_id":"<skill_id>","target_guid":<one of targets>}`. Combat `available_actions` currently lists the skill and eligible targets, **not** the command envelope; see the example below.
- `ddcli_snapshot()` — return the most recent bridge-emitted READY/state snapshot without sending a new command. **Cached, not a live refresh**; use when the bridge does not support `state` in the current frontend.
- `travelogue` while at `inn` is a **read-only journal query**, returning `inn_travelogue_state`. It does **not** change the frontend or require a back action. The MCP adapter keeps the last `inn` READY as the decision snapshot and labels the response `read_only_query: true`. This is distinct from the actual `inn_travelogue` UI decision boundary, whose READY advertises `continue_travelogue` when applicable.
- `ddcli_events(since=0, limit=100)` — retrieve observed bridge messages in sequence order. Cursor paging and a `history_gap` flag expose bounded history.

Each tool call is serialized against the same pipe connection. The bridge continues to validate game actions; no tool bypasses player-visible DDCLI rules. An action waits for a response boundary (READY, state, error or driving status), with a bounded timeout.

## Command outcome and recovery semantics (post-RC1 experimental)

The v0.0.56 bridge protocol **does not** echo a command ID, and therefore the host **cannot prove** that a later READY or gameplay event was caused by a particular command. In particular a skill may execute but the MCP wait for the next READY can time out. The adapter deliberately does **not** claim exactly-once execution.

`ddcli_command` now returns a structured object even when the **post-send** wait times out or the Named Pipe disconnects:

- `response_kind: "unknown"`, `error.code: "command_outcome_unknown"`, `retry_safe: false`: command may have executed; **never blindly retry**. The tool result includes any messages already received, `start_sequence` and `latest_sequence`.
- `response_kind: "bridge_rejected"`: an explicit bridge error arrived; inspect `error.code`, and correct the command rather than assuming a transport failure.
- `response_kind: "ready_observed"` or `"state_observed"`: the host saw a boundary/state after the write. This is observation, **not a correlated execution ACK**.
- `response_kind: "activity_observed"`: an interruptible activity such as driving has started; this is not a completed action.
- `command_completion_confirmed: false` is intentional until the protocol supports request IDs and correlated terminal results.
- `snapshot_stale: true` means an action was sent after the cached READY/state and no new bridge-emitted READY/current state has refreshed that cache. `ddcli_snapshot` remains historical even when `snapshot_stale: false`; a snapshot cannot promise its information is current at the instant of use.

### Combat example

Given a **fresh** combat READY with `current_actor_guid: 247`, skill `hel_iron_swan` and eligible target `266`:

```json
{"action":"skill","arguments":{"performer_guid":247,"skill_id":"hel_iron_swan","target_guid":266}}
```

The skill name is **not** the `action` value; `use_skill` is also not a supported action. Use the current player-visible actor and allowed target IDs, not the example values above.

### On uncertain execution

1. Do not resubmit the same side-effecting call.
2. Read `ddcli_events(since=<start_sequence>)`, page if `has_more`, and check `history_gap`.
3. Call `ddcli_state()` where supported; compare the fresh result with the cached boundary. `ddcli_snapshot()` alone cannot reconcile a timeout.
4. If observed state/events still cannot establish a safe next action, stop and report an unresolved outcome. A future bridge-side command-ID protocol is required for strict command-result correlation.

A failed pipe **open / initial handshake** may still throw, because no write has been attempted. Invalid local action arguments are rejected before sending. Post-send transport loss, however, is an unknown outcome.

## Test before autonomous play

1. Keep the existing RC1 client and bridge available; update only the Python host files in the deployed directory.
2. Run `python -m unittest test_ddcli_mcp.py` from the deployed host directory.
3. Start Darkest Dungeon II, then start Codex and confirm `ddcli_state` reports the correct menu or expedition state.
4. Call `ddcli_events` and `ddcli_status`, then test a **read-only** command such as `inspect_hero` using an actual available actor GUID.
5. Validate an ordinary game transition only after checking authorization and state handling. Confirm that Codex does not request per-action terminal `write_stdin` approvals.
6. Run several live regression scenarios before merging this PR.

**Unverified in this PR:** live Windows Named Pipe connectivity through a Codex-managed MCP process, the full range of DD2 frontend response boundaries, and extended unattended expeditions. Do not mark these as PASS without in-game testing.

The fixed RC1 snapshot is the `rc1` branch at commit `6dcfa87c9bdbb24df368a9910a0af1f1ca9fccf5`; this feature is post-RC1.
