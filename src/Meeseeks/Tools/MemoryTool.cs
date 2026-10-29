using System.Text.Json.Nodes;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>Per-folder persistent key-value memory, backed by `.meeseeks/memory.json`.</summary>
public sealed class MemoryTool
{
	public const string ToolName = "memory";

	private readonly ScriptRuntime _runtime;

	public MemoryTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Persistent key-value memory scoped to the current working directory, stored in `.meeseeks/memory.json`. Use it to remember facts across sessions in this folder: user preferences, project conventions, decisions, pointers to important files.

			Values are arbitrary JSON (strings, numbers, booleans, objects, arrays). Keys are case-sensitive strings. Limits: 64 KB per value, 256 entries per folder. Writes are atomic and safe from concurrent agents.

			Actions: `set` (upsert a key), `get` (read a key), `delete` (remove a key; deleting a missing key succeeds), `list` (keys with sizes and types, not full values).
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["action"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "One of: set, get, delete, list.",
					["enum"] = new JsonArray("set", "get", "delete", "list"),
				},
				["key"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Key to act on. Required for set, get, and delete.",
				},
				["value"] = new JsonObject
				{
					["description"] = "Value to store (any JSON). Required for set.",
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

	public ToolExecutionResult Execute(ToolCall call)
	{
		var action = ToolArgs.Text(call, "action")?.ToLowerInvariant()
			?? throw new MeeseeksException("memory requires an \"action\": set, get, delete, or list.");
		var store = new MemoryStore(_runtime.Cwd);

		switch (action)
		{
			case "set":
			{
				var key = RequireKey(call);
				if (call.Arguments["value"] is not JsonNode value || value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var s) && s is null)
				{
					throw new MeeseeksException("memory set requires a \"value\" (any JSON).");
				}

				store.Set(key, value);
				return new ToolExecutionResult($"memory: set \"{key}\" ({DescribeSize(value)}).", false);
			}

			case "get":
			{
				var key = RequireKey(call);
				var value = store.Get(key);
				return new ToolExecutionResult(value.ToJsonString(JsonSerializerOptions), false);
			}

			case "delete":
			{
				var key = RequireKey(call);
				return new ToolExecutionResult(
					store.Delete(key) ? $"memory: deleted \"{key}\"." : $"memory: \"{key}\" was not set; nothing to delete.",
					false);
			}

			case "list":
			{
				var entries = store.List();
				if (entries.Count == 0)
				{
					return new ToolExecutionResult("memory: the store is empty.", false);
				}

				var builder = new System.Text.StringBuilder();
				foreach (var (key, bytes, type) in entries)
				{
					builder.Append(key).Append("  ·  ").Append(type).Append("  ·  ").Append(bytes).AppendLine(" bytes");
				}
				return new ToolExecutionResult(builder.ToString().TrimEnd(), false);
			}

			default:
				throw new MeeseeksException($"memory: unknown action \"{action}\". Use set, get, delete, or list.");
		}
	}

	private static string RequireKey(ToolCall call) =>
		ToolArgs.Text(call, "key") ?? throw new MeeseeksException("memory requires a \"key\" string for this action.");

	private static string DescribeSize(JsonNode value)
	{
		var bytes = System.Text.Encoding.UTF8.GetByteCount(value.ToJsonString(JsonSerializerOptions));
		return $"{bytes:N0} bytes";
	}

	private static readonly System.Text.Json.JsonSerializerOptions JsonSerializerOptions = new()
	{
		WriteIndented = true,
	};
}