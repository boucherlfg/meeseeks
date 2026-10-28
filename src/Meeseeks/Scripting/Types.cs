using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meeseeks.Scripting;

[Api("Options for Agent.SpawnAsync, StartAsync, SpawnManyAsync, MapAsync, and Skills.RunAsync.")]
public sealed class SpawnOptions
{
	[Api("Model override as an Ollama model name. Defaults to the parent's model.", Order = 0)]
	public string? Model { get; set; }

	[Api("Override thinking output for the child.", Order = 1)]
	public bool? Think { get; set; }

	[Api("Skill names whose instructions are added to the child's system prompt.", Order = 2)]
	public string[]? Skills { get; set; }

	[Api("Extra system-prompt instructions for the child.", Order = 3)]
	public string? SystemPrompt { get; set; }

	[Api("Wall-clock limit for the child process, in milliseconds.", Order = 4)]
	public int? TimeoutMs { get; set; }

	[Api("Short label for logs and diagnostics.", Order = 5)]
	public string? Label { get; set; }

	[Api("""
		Images attached to the child's prompt: file paths (relative to Session.Cwd), http(s) URLs,
		or data URLs. The child's model must support vision.
		""", Order = 6)]
	public string[]? Images { get; set; }
}

[Api("The final result of a child agent.")]
public sealed class AgentResult
{
	[Api("The child's final answer.", Order = 0)]
	public string Text { get; set; } = "";

	[Api("The child's agent id, for example root.1.", Order = 1)]
	public string AgentId { get; set; } = "";

	[Api("Model steps the child used.", Order = 2)]
	public int Turns { get; set; }

	[Api("Tool calls the child made.", Order = 3)]
	public int ToolCalls { get; set; }

	[Api("Prompt tokens the child used.", Order = 4)]
	public int PromptTokens { get; set; }

	[Api("Completion tokens the child used.", Order = 5)]
	public int CompletionTokens { get; set; }

	[Api("Wall-clock duration in milliseconds.", Order = 6)]
	public long DurationMs { get; set; }

	[Api("Error text, when the child reported one.", Order = 7)]
	public string? Error { get; set; }

	public override string ToString() => Text;
}

[Api("A running agent returned by Agent.PeersAsync.")]
public sealed class AgentPeerInfo
{
	[Api("Agent id.", Order = 0)]
	public string Id { get; set; } = "";

	[Api("Parent agent id, or null for root.", Order = 1)]
	public string? Parent { get; set; }

	[Api("Depth in the spawn tree; root is 0.", Order = 2)]
	public int Depth { get; set; }
}

[Api("A configured MCP server.")]
public sealed class McpServerInfo
{
	[Api("Server name as used by the other Mcp methods.", Order = 0)]
	public string Name { get; set; } = "";

	[Api("\"stdio\" or \"http\".", Order = 1)]
	public string Transport { get; set; } = "";

	[Api("Whether the server is enabled.", Order = 2)]
	public bool Enabled { get; set; }

	[Api("Optional description from mcp.json.", Order = 3)]
	public string? Description { get; set; }

	public override string ToString() => $"{Name} ({Transport})";
}

[Api("One MCP tool.")]
public sealed class McpToolInfo
{
	[Api("Tool name as passed to Mcp.CallAsync.", Order = 0)]
	public string Name { get; set; } = "";

	[Api("Server that owns the tool.", Order = 1)]
	public string Server { get; set; } = "";

	[Api("Tool description.", Order = 2)]
	public string Description { get; set; } = "";

	public override string ToString() => $"{Server}/{Name}";
}

[Api("A callable tool.")]
public sealed class ToolInfo
{
	[Api("Tool name. MCP tools use mcp__server__tool.", Order = 0)]
	public string Name { get; set; } = "";

	[Api("Tool description.", Order = 1)]
	public string Description { get; set; } = "";

	public override string ToString() => Name;
}

internal static class JsonArguments
{
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

	public static JsonObject ToObject(object? value)
	{
		if (value is null)
		{
			return new JsonObject();
		}
		if (value is JsonObject obj)
		{
			return (JsonObject)obj.DeepClone();
		}
		if (value is JsonNode node)
		{
			return node as JsonObject ?? new JsonObject { ["value"] = node.DeepClone() };
		}
		return JsonSerializer.SerializeToNode(value, Options) as JsonObject ?? new JsonObject();
	}
}

internal static class ToolText
{
	public static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web) { WriteIndented = true };

	public static string FromResult(JsonNode? result)
	{
		if (result is null)
		{
			return "";
		}

		if (result is JsonObject obj)
		{
			if (obj["content"] is JsonArray content)
			{
				var parts = content
					.OfType<JsonObject>()
					.Where(block => block["type"]?.GetValue<string>() == "text")
					.Select(block => block["text"]?.GetValue<string>() ?? "")
					.Where(text => text.Length > 0)
					.ToList();

				if (parts.Count > 0)
				{
					return string.Join("\n", parts);
				}
			}

			if (obj["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var text))
			{
				return text;
			}
		}

		if (result is JsonValue value && value.TryGetValue<string>(out var scalar))
		{
			return scalar;
		}

		return result.ToJsonString(Pretty);
	}

	public static bool IsError(JsonNode? result) =>
		result is JsonObject obj && obj["isError"]?.GetValue<bool>() == true;
}
