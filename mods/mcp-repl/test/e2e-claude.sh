#!/usr/bin/env bash
# E2E acceptance test for the mcp-repl mod: launch a headless Claude instance with the inutil-repl MCP server
# enabled and ask it to EVALUATE C# in the REPL — proving an agent connects (no install, remote server by URL)
# and gets a real result back. Requires: dotnet, an authed `claude` CLI (2.x), and network to the model.
#
# The MCP server is backed by a STANDALONE host (ReplMcpServer + a driven MainThread pump — pure-C# eval, no game
# boot needed for the transport/agent round-trip). The in-game eval path is covered separately by the ReplCases
# battery; this test is specifically "a real agent evaluates through the MCP".
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"; cd "$ROOT"

command -v dotnet >/dev/null || { echo "SKIP: dotnet not found"; exit 0; }
command -v claude >/dev/null || { echo "SKIP: claude CLI not found"; exit 0; }

# 1. Build + start the standalone MCP host; capture the port it binds.
dotnet build managed/src/Inutil.Mods.Tests/Inutil.Mods.Tests.csproj -c Release -v q --nologo >/dev/null
DLL="managed/src/Inutil.Mods.Tests/bin/Release/inutil-mods-tests.dll"
HOSTLOG="$(mktemp)"
dotnet "$DLL" serve-mcp > "$HOSTLOG" 2>/dev/null &
HOST=$!; trap 'kill $HOST 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do PORT="$(grep -oE 'MCP_PORT=[0-9]+' "$HOSTLOG" 2>/dev/null | cut -d= -f2 || true)"; [ -n "$PORT" ] && break; sleep 0.2; done
[ -n "${PORT:-}" ] || { echo "FAIL: MCP host did not report a port"; cat "$HOSTLOG"; exit 1; }
echo ">> inutil-repl MCP server on http://127.0.0.1:$PORT/sse"

# 2. MCP config for Claude — an SSE remote server, nothing installed on the agent side.
CFG="$(mktemp --suffix=.json)"
printf '{ "mcpServers": { "inutil-repl": { "type": "sse", "url": "http://127.0.0.1:%s/sse" } } }\n' "$PORT" > "$CFG"

# 3. Launch a headless Claude, MCP enabled, and ASK IT TO EVALUATE. Assert the agent's answer is 42.
echo ">> asking the agent to evaluate 40 + 2 via repl_eval ..."
OUT="$(claude -p 'Use the repl_eval tool to evaluate the C# expression 40 + 2 in the game REPL. Then reply with ONLY the resulting number, nothing else.' \
  --mcp-config "$CFG" --strict-mcp-config \
  --allowedTools 'mcp__inutil-repl__repl_eval' \
  --dangerously-skip-permissions \
  --output-format text 2>&1)" || { echo "FAIL: claude invocation errored:"; echo "$OUT"; exit 1; }

echo ">> agent replied: $OUT"
if grep -qw 42 <<<"$OUT"; then
  echo "PASS: a real agent evaluated 40 + 2 -> 42 through the inutil-repl MCP server"; exit 0
else
  echo "FAIL: expected 42 in the agent's reply"; exit 1
fi
