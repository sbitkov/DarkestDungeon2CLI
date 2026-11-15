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
- `ddcli_command(action, arguments={})` — sends `{"type":"command","action":"..."}` with the provided action arguments directly to the existing bridge. Obtain action names and parameters from the current `available_actions`; this is **not** the human CLI's text alias parser.
- `ddcli_snapshot()` — return the most recent bridge-emitted READY/state snapshot without sending a new command. **Cached, not a live refresh**; use when the bridge does not support `state` in the current frontend.
- `ddcli_events(since=0, limit=100)` — retrieve observed bridge messages in sequence order. Cursor paging and a `history_gap` flag expose bounded history.

Each tool call is serialized against the same pipe connection. The bridge continues to validate game actions; no tool bypasses player-visible DDCLI rules. An action waits for a response boundary (READY, state, error or driving status), with a bounded timeout. The client must refresh state following uncertain outcomes.

## Test before autonomous play

1. Keep the existing RC1 client and bridge available; update only the Python host files in the deployed directory.
2. Run `python -m unittest test_ddcli_mcp.py` from the deployed host directory.
3. Start Darkest Dungeon II, then start Codex and confirm `ddcli_state` reports the correct menu or expedition state.
4. Call `ddcli_events` and `ddcli_status`, then test a **read-only** command such as `inspect_hero` using an actual available actor GUID.
5. Validate an ordinary game transition only after checking authorization and state handling. Confirm that Codex does not request per-action terminal `write_stdin` approvals.
6. Run several live regression scenarios before merging this PR.

**Unverified in this PR:** live Windows Named Pipe connectivity through a Codex-managed MCP process, the full range of DD2 frontend response boundaries, and extended unattended expeditions. Do not mark these as PASS without in-game testing.

The fixed RC1 snapshot is the `rc1` branch at commit `6dcfa87c9bdbb24df368a9910a0af1f1ca9fccf5`; this feature is post-RC1.
