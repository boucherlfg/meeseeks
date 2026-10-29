using System.Text.Json.Nodes;
using Meeseeks.Core;
using Meeseeks.Mcp;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>
/// List MCP servers and call MCP tools without writing a script. A fast path for the most
/// common MCP operations: one tool call instead of a compile-and-run round trip.
/// </summary>
public sealed class McpTool
{
	public const string ToolName = "mcp";

	private readonly ScriptRuntime _runtime;

	public McpTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			List MCP servers and call MCP tools without writing a script. One `action` per call.

			- `servers` — list configured MCP servers with transport and enabled state. Does not connect.
			- `tools` — connect (if needed) and list tools of one `server` or every enabled server.
			- `call` — call an MCP tool and return its text content. Identify the tool by `server` + `tool`, or by `name` in full form `mcp__server__tool`. Throws when the tool reports an error.
			- `callRaw` — like `call`, but returns the raw JSON-RPC result instead of extracted text.

			`arguments` is the tool's input as a JSON object. Servers that fail to connect are skipped
			by `tools`.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["action"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "One of: servers, tools, call, callRaw.",
				},
				["server"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "MCP server name (tools/call/callRaw).",
				},
				["tool"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Tool name within the server (call/callRaw).",
				},
				["name"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Full tool name mcp__server__tool (call/callRaw), instead of server + tool.",
				},
				["arguments"] = new JsonObject
				{
					["type"] = "object",
					["description"] = "The MCP tool's input arguments as a JSON object.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			["required"] = new JsonArray("action"),
		},
	};

	public async Task<ToolExecutionResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
	{
		var action = ToolArgs.Text(call, "action")
			?? throw new MeeseeksException("mcp requires an \"action\": servers, tools, call, or callRaw.");

		var mcp = _runtime.Mcp;
		switch (action.ToLowerInvariant())
		{
			case "servers":
			{
				var servers = mcp.Servers
					.Select(pair => new { pair.Key, pair.Value.Transport, pair.Value.Enabled, pair.Value.Description })
					.ToArray();
				if (servers.Length == 0)
				{
					return new ToolExecutionResult("No MCP servers configured.", false);
				}
				var lines = servers.Select(server =>
					$"{server.Key}  ·  {server.Transport}  ·  {(server.Enabled ? "enabled" : "disabled")}" +
					(string.IsNullOrWhiteSpace(server.Description) ? "" : $"  ·  {server.Description}"));
				return new ToolExecutionResult(string.Join("\n", lines), false);
			}

			case "tools":
			{
				var tools = await mcp
					.ListToolsAsync(ToolArgs.Text(call, "server"), cancellationToken)
					.ConfigureAwait(false);
				if (tools.Count == 0)
				{
					return new ToolExecutionResult("No MCP tools found.", false);
				}
				var lines = tools.Select(tool => $"mcp__{tool.Server}__{tool.Name}  ·  {tool.Description}");
				return new ToolExecutionResult(string.Join("\n", lines), false);
			}

			case "call":
			{
				var (server, tool) = Resolve(call);
				var result = await mcp.CallToolAsync(
					server,
					tool,
					Arguments(call) ?? new JsonObject(),
					cancellationToken).ConfigureAwait(false);
				_runtime.AddTrace($"mcp call({server}/{tool})");
				var text = ToolText.FromResult(result);
				if (ToolText.IsError(result))
				{
					throw new MeeseeksException($"MCP tool {server}/{tool} failed: {text}");
				}
				return new ToolExecutionResult(text, false);
			}

			case "callraw":
			{
				var (server, tool) = Resolve(call);
				var result = await mcp.CallToolAsync(
					server,
					tool,
					Arguments(call) ?? new JsonObject(),
					cancellationToken).ConfigureAwait(false);
				_runtime.AddTrace($"mcp callRaw({server}/{tool})");
				return new ToolExecutionResult(result?.ToJsonString(ToolText.Pretty) ?? "null", false);
			}

			default:
				throw new MeeseeksException($"Unknown mcp action \"{action}\". Available: servers, tools, call, callRaw.");
		}
	}

	private static (string Server, string Tool) Resolve(ToolCall call)
	{
		var server = ToolArgs.Text(call, "server");
		var tool = ToolArgs.Text(call, "tool");
		if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(tool))
		{
			return (server, tool);
		}

		var name = ToolArgs.Text(call, "name");
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new MeeseeksException(
				"mcp call needs \"server\" + \"tool\", or the full tool name in \"name\" (mcp__server__tool).");
		}

		var parsed = McpManager.ParseToolName(name)
			?? throw new MeeseeksException($"\"{name}\" is not an MCP tool name. Expected mcp__server__tool.");
		return parsed;
	}

	private static JsonObject? Arguments(ToolCall call) =>
		call.Arguments["arguments"] as JsonObject;
}