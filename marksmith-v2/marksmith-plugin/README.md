# MarkSmith MCP plugin

Bundles the `MarkSmith.Mcp` server (this repo's stdio MCP server) as a
Claude Code plugin, so installing it gives Claude Code direct access to
MarkSmith's Markdown/DOCX tools without hand-editing `.mcp.json`.

Tools exposed: render, patch, diff, validate Markdown; inspect, patch,
convert, diff DOCX; manage the AI 3-block authoring cycle. Plus 3 prompts
and 3 resources (syntax contract, templates catalog, patch spec) — see
[`MarkSmith.Mcp/Server/McpServer.cs`](../MarkSmith.Mcp/Server/McpServer.cs).

## Requirements

- .NET 8 **SDK** on PATH (not just the runtime) — the plugin launches the
  server with `dotnet run`, which builds on first start.

## Try it without installing

```bash
claude --plugin-dir marksmith-v2/marksmith-plugin
```

## Install via a local marketplace

From an interactive Claude Code session, run inside this repo:

```
/plugin marketplace add ./marksmith-v2
/plugin install marksmith-mcp@marksmith-local
```

(`marketplace.json` lives at `marksmith-v2/marketplace.json` and points at
`./marksmith-plugin`.)

After editing `.mcp.json` or `plugin.json`, run `/reload-plugins` to pick
up changes without restarting the session.

## Notes

- `.mcp.json`'s `command` resolves the `MarkSmith.Mcp` project via
  `${CLAUDE_PLUGIN_ROOT}/../MarkSmith.Mcp/MarkSmith.Mcp.csproj`, i.e. the
  sibling project in this same repo — this plugin is not meant to be
  copied out and used standalone.
- To skip the `dotnet run` build-on-start cost, publish once
  (`dotnet publish -c Release -o bin`) and point `command` at the
  resulting `bin/marksmith-mcp[.exe]` instead.
