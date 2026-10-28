# meeseeks

A coding-agent harness written in C#, from scratch. It has **one** general-purpose model-facing tool — a C# code runner — plus eight fast paths for the things a model does constantly: `read`, `write`, `edit`, `patch`, `glob`, `grep`, `web_search`, and `web_fetch`.

The model writes C# scripts. Scripts are ordinary .NET — file system, `Process`, `HttpClient`, JSON, regex,
cryptography — and they get an API for the three things a harness normally needs extra tools for:

- **subagents** — separate `meeseeks` processes, with an API for agents to message each other
- **MCP** — stdio and streamable-HTTP servers from `~/.meeseeks/mcp.json`
- **skills** — Claude/pi-compatible `SKILL.md` packages

Nine tools, one permission surface, nine descriptions in the prompt. Everything else is code.

```
 user ──▶ root meeseeks ──▶ csharp tool ──▶ Roslyn C# script
                │                                  │
                │◀───────── ScriptGlobals ─────────┤
                │      Agent / Skills / Mcp / Tools
                │
        loopback broker ◀── ▶ child meeseeks ◀── ▶ child meeseeks
```

## Requirements

- .NET SDK 10
- Ollama running locally (or Ollama Cloud) with a tool-capable model
- Windows, Linux, or macOS

## Build

```sh
cd meeseeks
dotnet build -c Release
```

The executable is at `src/Meeseeks/bin/Release/net10.0/Meeseeks.exe` (`.dll` plus `dotnet` elsewhere).
`meeseeks.cmd` in the repository root runs the Release build, falling back to `dotnet run` when needed.

```sh
./meeseeks.cmd "hello"
# or
dotnet run --project src/Meeseeks -c Release -- "hello"
```

Create the config directories on first use:

```sh
./meeseeks.cmd --init
```

## Configuration

### `~/.meeseeks/provider.json`

All Ollama and harness settings live here. The file is created with defaults on first run.

```json
{
  "baseUrl": "http://127.0.0.1:11434",
  "model": "glm-5.3-flash:cloud",
  "think": true,
  "temperature": 0.2,
  "numCtx": 32768,
  "keepAlive": "10m",
  "timeoutSeconds": 600,
  "subagentTimeoutSeconds": 1800,
  "maxDepth": 2,
  "maxParallelSubagents": 4
}
```

| Field | Meaning |
| --- | --- |
| `baseUrl` | Ollama base URL, no `/api/chat` suffix |
| `model` | Ollama model name; must support tools for useful turns |
| `apiKey` | optional bearer token for Ollama Cloud or a protected endpoint |
| `think` | `true`/`false`, or a model level such as `"low"`, `"high"`, `"max"`; shown dim in the console |
| `temperature`, `numCtx`, `numPredict` | Ollama request options (`num_ctx`, `num_predict`) |
| `keepAlive` | Ollama `keep_alive` |
| `timeoutSeconds` | one model request |
| `subagentTimeoutSeconds` | one child process, unless `SpawnOptions.TimeoutMs` is set |
| `maxDepth` | deepest subagent nesting; root is 0 |
| `maxParallelSubagents` | child processes this agent may run at once |
| `apiKey` | bearer token for Ollama Cloud; also used by `web_search` |
| `webSearchUrl` | endpoint behind `web_search`; defaults to `https://ollama.com/api/web_search` |

### `~/.meeseeks/mcp.json`

Claude/pi-compatible `mcpServers` shape. Servers are connected lazily, on first use from a script.

```json
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "."],
      "env": { "LOG_LEVEL": "warn" }
    },
    "docs": {
      "url": "https://example.com/mcp",
      "headers": { "Authorization": "Bearer ${DOCS_TOKEN}" }
    }
  }
}
```

`${VAR}` is expanded from the environment in `env` and `headers` values. HTTP servers are spoken to with
JSON-RPC over POST, including server-sent-event responses and `Mcp-Session-Id`. OAuth is not implemented; use
a static bearer token or a local stdio server.

MCP tools are **not** declared to the model. Scripts discover them with `Mcp.ServersAsync()` /
`Mcp.ToolsAsync()` and call them with `Mcp.CallAsync(server, tool, args)` or
`Tools.CallAsync("mcp__server__tool", args)`.

### Skills

A skill is a directory with `SKILL.md` and YAML frontmatter:

```markdown
---
name: poet
description: Writes short poems about programming topics.
---

Reply with exactly one haiku (5-7-5) and nothing else.
```

Discovery paths:

```
~/.meeseeks/skills/<name>/SKILL.md
<cwd>/.meeseeks/skills/<name>/SKILL.md
```

The catalogue is appended to the system prompt. A project skill replaces a global skill with the same name.
`Skills.GetAsync(name)` returns the body (frontmatter stripped) and `BaseDir`; `Skills.RunAsync(name, input)`
starts a child agent whose system prompt includes the skill instructions.

## Usage

```sh
meeseeks [options] [prompt]
```

With a prompt it runs one turn and exits. Without one it starts a streaming REPL.

| Flag | Meaning |
| --- | --- |
| `-m, --model <name>` | override the model from provider.json (inherited by subagents) |
| `-C, --cwd <dir>` | working directory for scripts, skills, and children |
| `--max-depth <n>` | subagent nesting limit |
| `--json` | print the final answer as JSON (one-shot) |
| `--no-mcp` | do not load MCP servers |
| `--init` | create `~/.meeseeks` config files and exit |
| `--api` | print the generated script API reference |
| `--version`, `-h`, `--help` | usual |

REPL commands: `/help`, `/new`, `/model`, `/think`, `/skills`, `/mcp`, `/tools`, `/session`, `/exit`. `/skills` lists the discovered skills, `/mcp` lists the configured MCP servers and their tools, and `/tools` lists the built-in tools. `!<command>` runs a console command in the working directory and streams its output, without involving the model. `/model` lists the Ollama models and marks the current one; `/model <name>` switches model and saves it to `~/.meeseeks/provider.json`. `/think` shows the model's supported thinking values and the current setting; `/think on|off|<level>` changes it and saves it. `Esc` or `Ctrl+C` aborts the
running turn: the in-flight model request and tool call are cancelled, and you return to the prompt with the
conversation intact. `Ctrl+C` while idle exits.

Consecutive tool calls with the same effective arguments are refused with a tool error, to break loops. For `csharp`, the cosmetic `purpose` label is ignored; the code and other arguments still count. The guard resets on each user turn.

Tool calls use one line: yellow while running, then green with `✓` on success or red with `✗` on failure, including the purpose and duration. On a non-terminal output the in-progress line is skipped, so logs still get one line per tool.

Colors and Markdown are on when stdout is a terminal. Force them with `MEESEEKS_COLOR=1` or disable with
`NO_COLOR` / `MEESEEKS_COLOR=0`. Supported Markdown: headings, lists, blockquotes, horizontal rules, fenced
code, inline code, bold, italic, links, and autolinks. Thinking is dim italic; tool calls are yellow, success
green, failure red; the prompt is cyan.

## File tools

`csharp` can do anything, but a compile-and-run round trip is a poor fit for "show me this file" or "change
this line". Six tools cover those cases in one call each:

| Tool | What it does |
| --- | --- |
| `read` | Returns a file with line numbers (`   12→text`). `offset`/`limit` page through large files; binary files and files over 16 MB are refused. |
| `write` | Creates a file or replaces it whole. Parent directories are created. Replacing an existing file needs `overwrite: true`, so a stale path cannot silently destroy work. |
| `edit` | Replaces an exact string and reports the diff. `oldText` must match exactly and be unique unless `replaceAll` is true; a failed match names the closest line. |
| `patch` | Applies several exact-string replacements across one or more files in one call, and reports one diff per file. |
| `glob` | Finds files by pattern. |
| `grep` | Searches file contents with a regular expression. |

`edit` is the tool for changing an existing file: it touches only the matched text, so the rest of the file —
including CRLF line endings and a missing trailing newline — is preserved byte for byte. Writes go through a
temp file and a rename, so a failed write never leaves a half-written file.

`patch` is the tool for a change that spans files — a rename, a signature change, its call sites, the tests.
Each entry in `edits` is `{ path, oldText, newText, replaceAll? }` and follows `edit`'s matching rules. Hunks
apply in the order given, so a later hunk in the same file can match text an earlier one introduced. The call
is atomic by default: every hunk is validated first, and if any hunk fails nothing is written and the error
names the failing hunk with the same closest-line diagnostic. Pass `atomic: false` to apply the hunks that do
match and report the ones that did not.

## Web tools

`web_search` and `web_fetch` cover the two web operations a coding agent needs, without scraping a search
engine.

| Tool | What it does |
| --- | --- |
| `web_search` | Searches the web through the Ollama Cloud web-search API; returns title, URL, and snippet. |
| `web_fetch` | Fetches a URL and returns it as readable text. |

`web_search` posts to the Ollama Cloud web-search API:

```
POST https://ollama.com/api/web_search
Authorization: Bearer $OLLAMA_API_KEY
{ "query": "...", "max_results": 5 }
```

It returns up to 10 results, each with `title`, `url`, and a `content` snippet. The key comes from `apiKey` in
`provider.json`; without one the tool fails with a message pointing at that file rather than silently
returning nothing. Point `webSearchUrl` elsewhere to use a different backend that speaks the same shape.

`web_fetch` needs no key. It follows up to 10 redirects, sends a browser user agent, and returns the body as
text: HTML is reduced to readable text (scripts, styles, and elements whose `class`/`id` marks them as
navigation chrome are dropped), while JSON, XML, and plain text pass through unchanged. `maxChars` caps the
returned text at 20000 characters by default and 200000 at most; bodies over 4 MB are truncated.

## The C# API

The reference below is generated at startup from `[Api]` attributes on the script classes
(`src/Meeseeks/Scripting/ScriptApis.cs`, `Types.cs`, `SkillCatalog.cs`, and the agent types). Add or
change a public script member, mark it with `[Api("...")]`, and the `csharp` tool description updates
automatically. Preview the exact text the model sees with:

```sh
meeseeks --api
```

Scripts are top-level statements. `await` works. `return value;` becomes the structured result. These
namespaces are imported automatically:

```
System  System.IO  System.Linq  System.Collections.Generic  System.Collections.Concurrent
System.Threading  System.Threading.Tasks  System.Diagnostics  System.Globalization
System.Net  System.Net.Http  System.Net.Http.Json  System.Security.Cryptography
System.Text  System.Text.Json  System.Text.Json.Nodes  System.Text.RegularExpressions
Meeseeks  Meeseeks.Core  Meeseeks.Agents  Meeseeks.Scripting
```

Only console output and the returned value are visible to the model. The working directory is `Session.Cwd`.

```csharp
// Files, processes, HTTP: ordinary .NET
var files = Directory.GetFiles(Session.Cwd, "*.cs", SearchOption.AllDirectories);
foreach (var file in files) Console.WriteLine($"{file}: {new FileInfo(file).Length} bytes");

using (var process = Process.Start(new ProcessStartInfo("dotnet", "test")
{
    WorkingDirectory = Session.Cwd,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
}))
{
    var stdout = await process!.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new MeeseeksException(stdout);
}

// Subagents
var results = await Agent.SpawnManyAsync(new[]
{
    "Find every place this repo reads files from disk. Reply with paths and line numbers.",
    "Find every place this repo writes files. Reply with paths and line numbers.",
});

// Skills and MCP
var review = await Skills.RunAsync("code-review", "Review src/Meeseeks/Core/AgentSession.cs");
var issues = await Mcp.CallAsync("github", "list_issues", new { repo = "acme/app", state = "open" });

return new { files = files.Length, review = review.Text, issues };
```

### Globals

| Member | Description |
| --- | --- |
| `Agent.SpawnAsync(prompt, options?)` | start a child and wait for its final text |
| `Agent.StartAsync(prompt, options?)` | start a child and return a live `AgentHandle` |
| `Agent.SpawnManyAsync(prompts, options?)` | run one child per prompt, in parallel |
| `Agent.MapAsync(items, item => prompt, options?)` | fan out over arbitrary items |
| `Agent.SendAsync(agentId, text)` / `BroadcastAsync` | message a running agent, or all of them |
| `Agent.ReplyAsync(messageId, text)` | answer a message that asked for a reply |
| `Agent.WaitForMessageAsync(timeoutMs)` | block inside a script until a message arrives |
| `Agent.PeersAsync()` / `Messages()` | running agents / messages received so far |
| `Skills.ListAsync(query?)` | discovered skills, filtered by name/description |
| `Skills.GetAsync(name)` | one skill; `.Body` is the full `SKILL.md` |
| `Skills.RunAsync(name, input, options?)` | run a skill as a child agent |
| `Mcp.ServersAsync()` / `ToolsAsync(server?)` | configured servers / their tools |
| `Mcp.CallAsync(server, tool, args?)` | tool text content; throws on `isError` |
| `Mcp.CallRawAsync(...)` | the JSON-RPC result object as `JsonNode` |
| `Tools.ListAsync()` | `csharp` and every MCP tool |
| `Tools.CallAsync(name, args?)` / `CallRawAsync` | call any callable tool by full name |
| `Print(value)` / `Print(label, value)` | write to output; objects print as indented JSON |
| `Session.AgentId` / `Cwd` / `Depth` / `MaxDepth` / `Model` / `CancellationToken` | session facts |

`SpawnOptions`: `Model`, `Think`, `Skills` (names), `SystemPrompt`, `TimeoutMs`, `Label`.

`AgentResult`: `Text`, `AgentId`, `Turns`, `ToolCalls`, `PromptTokens`, `CompletionTokens`, `DurationMs`, `Error`.

## Subagents and messaging

A subagent is a separate `meeseeks` process with the same nine tools, a fresh context, its own MCP
connections, and no access to the parent conversation. Depth and parallel-process limits are enforced by the
parent; exceeding either throws a catchable `MeeseeksException`. Model, thinking, and depth limit are
inherited; `SpawnOptions` can override model and thinking, and `Skills`/`SystemPrompt` extend the child's
system prompt.

Agents are identified by a tree path: `root`, `root.1`, `root.1.2`.

The root process runs a loopback broker. Two kinds of communication exist:

1. **Direct calls.** `handle.SendAsync` sends a message; `handle.AskAsync` sends a question and waits for a
   reply. `Agent.SendAsync(id, text)` and `Agent.BroadcastAsync(text)` work for any running agent id.
2. **Injected messages.** Every running agent drains its inbox before each model step. Messages arrive as user
   messages tagged `[message #m3 from root.1]`. If the sender wants a reply, the tag says so and the agent
   answers with `Agent.ReplyAsync("m3", "...")`.

`Agent.WaitForMessageAsync` lets a script block for a message explicitly, which is how a child can park and
serve requests:

```csharp
// child script
var message = await Agent.WaitForMessageAsync(120000);
if (message is not null)
{
    await Agent.ReplyAsync(message.Id, "pong: " + message.Text);
    return "done";
}
```

```csharp
// parent script
var child = await Agent.StartAsync(
    "Use one C# script: wait with Agent.WaitForMessageAsync(120000), reply to the message, then return done.");
var reply = await child.AskAsync("ping", 120000);
var result = await child.WaitAsync();
return new { reply, child = result.Text };
```

A finished child is gone; it cannot receive more messages. A child started with `StartAsync` but never awaited
is stopped when the script that started it ends. `AskAsync` replies come back from the call, not as a second
injected message.

## Referencing NuGet packages and DLLs

Scripts may start with `#r` directives:

```csharp
#r "nuget: Newtonsoft.Json, 13.0.3"
#r "nuget: Humanizer"
#r "lib/MyLibrary.dll"

using Newtonsoft.Json;
return JsonConvert.SerializeObject(new { ok = true });
```

- A version is optional; without one, NuGet resolves the newest stable version (`Version="*"`).
- Packages are restored once per package set with `dotnet restore` and cached under `~/.meeseeks/nuget/<hash>`.
  Delete that directory to force a refresh.
- Local paths resolve against `Session.Cwd`.
- Framework assemblies are already referenced and are filtered out of NuGet refs to avoid conflicts.
- Managed NuGet dependencies work. Native runtime assets are not deployed, so packages that need a native
  `.dll`/`.so`/`.dylib` at runtime are not supported yet.
- Directives must appear before other code. Line numbers in compiler errors are preserved.

## How it works

- `Providers/OllamaClient.cs` streams `POST /api/chat` as NDJSON, accumulates `content`, `thinking`, and
  `tool_calls`, and reports token usage.
- `Core/AgentSession.cs` runs the tool loop: send messages, execute returned tool calls, append tool results,
  repeat until the model answers without a tool call. It trims whole old turns when the request would exceed
  `numCtx`.
- `Tools/CsharpTool.cs` describes the `csharp` tool and hands scripts to `Scripting/ScriptRunner.cs`.
- `Tools/ToolRegistry.cs` holds the toolbelt: `csharp`, plus `read`, `write`, `edit`, `patch`, `glob`, and `grep` as fast paths for file work and searching.
- `Tools/ReadTool.cs`, `Tools/WriteTool.cs`, and `Tools/EditTool.cs` read a file with line numbers, create or replace a whole file, and replace an exact string while reporting the diff. `Tools/PatchTool.cs` applies many such replacements across files, validating every hunk before it writes. `Tools/LineDiff.cs` computes the diffs; `Tools/FilePaths.cs` handles path resolution and atomic writes.
- `Tools/GlobTool.cs` and `Tools/GrepTool.cs` find files by pattern and search file contents, so a search costs one tool call instead of a script round trip.
- `Tools/WebSearchTool.cs` posts to the Ollama Cloud web-search API with the `apiKey` from `provider.json`; `Tools/WebFetchTool.cs` fetches a URL and reduces HTML to text. `Tools/WebClient.cs` holds the shared HTTP client, bounded body reader, and HTML-to-text extraction.
- `ScriptRunner` compiles with Roslyn, injects `ScriptGlobals`, captures `Console`, and keeps the process warm
  so later scripts compile quickly.
- `Scripting/ScriptReferenceResolver.cs` handles `#r` directives (NuGet restore and local DLLs) before Roslyn
  sees the script.
- `Agents/` contains the child-process supervisor, the loopback message broker, and its client.
- `Mcp/` contains the JSON-RPC client and lazy manager.
- `Cli/` is the console renderer, argument parser, root runner, and child runner.

The project is a single console app; folders keep the responsibilities separate.

## Security

Scripts and child agents run as the current user with the full .NET runtime. There is no sandbox, no approval
prompt, and no tool allowlist beyond the nine tools above. Treat this exactly like handing the model a
shell.

Messages between agents are authenticated only by a random token on a loopback TCP port and are not encrypted.

## Known limitations

- Roslyn scripts reject `using var x = ...;` declarations. Use `using (...) { }` or `Dispose()`.
- Subagents cannot outlive the script that started them unless the script awaited them.
- Messages are cooperative: a finished child cannot receive; a busy model call delays delivery until the next
  step.
- The context trimmer drops whole old turns rather than summarizing.
- No session persistence; conversations live in memory for the process lifetime.
- Child agents do not stream their text into the parent's console. The parent shows the tool call and result.
- Cancellation covers `Task`/`ValueTask` awaits (they are wrapped so `Esc` and timeouts stop them promptly). Synchronous loops, `Thread.Sleep`, and blocking console/file calls may not stop until they return.
- MCP: no OAuth, no server-initiated sampling/roots requests, no resource subscriptions.
- NuGet: native runtime assets and `#load` are not supported.

## Development

```sh
dotnet build
dotnet run --project src/Meeseeks -- --init
dotnet run --project src/Meeseeks -- "Use the csharp tool to return 2+2"
MEESEEKS_DEBUG=1 dotnet run --project src/Meeseeks -- "..."   # child process diagnostics on stderr
```

## License

MIT
