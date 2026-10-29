using System.Text.Json.Nodes;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>
/// Discover, read, and run skills without writing a script. A fast path for the most common
/// skill operations: one tool call instead of a compile-and-run round trip.
/// </summary>
public sealed class SkillTool
{
	public const string ToolName = "skill";

	private readonly ScriptRuntime _runtime;

	public SkillTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Discover, read, and run skills without writing a script. One `action` per call.

			- `list` — list discovered skills, optionally filtered by a `query` substring of name or description.
			- `get` — load one skill by `name`; returns its full instructions (SKILL.md with frontmatter removed) plus path and directory. Read the instructions and follow them yourself.
			- `run` — run a skill as a child agent whose system prompt is the skill's instructions; pass `input` and optional `options`. Waits for the child's final answer.

			Relative paths inside a skill's instructions resolve against the skill's directory. Use a
			skill when its description matches the task.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["action"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "One of: list, get, run.",
				},
				["name"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Skill name (get/run).",
				},
				["query"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Filter for list: substring of name or description.",
				},
				["input"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Input for the skill's child agent (run).",
				},
				["options"] = new JsonObject
				{
					["type"] = "object",
					["description"] = "SpawnOptions for run: model, think, systemPrompt, timeoutMs, label.",
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
			?? throw new MeeseeksException("skill requires an \"action\": list, get, or run.");

		var catalog = _runtime.SkillCatalog;
		switch (action.ToLowerInvariant())
		{
			case "list":
			{
				var needle = ToolArgs.Text(call, "query")?.Trim();
				var skills = catalog.Skills
					.Where(skill => string.IsNullOrEmpty(needle)
						|| skill.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
						|| skill.Description.Contains(needle, StringComparison.OrdinalIgnoreCase))
					.Select(skill => $"{skill.Name}  ·  {skill.Description}  ({skill.Path})");
				var lines = skills.ToArray();
				return new ToolExecutionResult(
					lines.Length == 0 ? "No skills discovered." : string.Join("\n", lines),
					false);
			}

			case "get":
			{
				var name = ToolArgs.Text(call, "name")
					?? throw new MeeseeksException("skill get requires a \"name\" string.");
				var skill = RequireSkill(catalog, name);
				var header = $"{skill.Name}  ·  {skill.Description}\nPath: {skill.Path}\nDirectory: {skill.BaseDir}\n\n";
				return new ToolExecutionResult(header + skill.Body, false);
			}

			case "run":
			{
				var name = ToolArgs.Text(call, "name")
					?? throw new MeeseeksException("skill run requires a \"name\" string.");
				var input = ToolArgs.Text(call, "input")
					?? throw new MeeseeksException("skill run requires an \"input\" string for the skill's child agent.");
				var skill = RequireSkill(catalog, name);

				var options = AgentTool.ParseOptions(call);
				var systemPrompt = $"## Skill: {skill.Name}\n\nSkill directory: {skill.BaseDir}\n\n{skill.Body}";
				if (!string.IsNullOrWhiteSpace(options.SystemPrompt))
				{
					systemPrompt += "\n\n## Additional instructions\n\n" + options.SystemPrompt.Trim();
				}

				var merged = new SpawnOptions
				{
					Model = options.Model,
					Think = options.Think,
					TimeoutMs = options.TimeoutMs,
					Label = options.Label ?? skill.Name,
					SystemPrompt = systemPrompt,
				};

				var handle = await _runtime.Agents
					.StartAsync(input, merged, cancellationToken)
					.ConfigureAwait(false);
				_runtime.Track(handle);
				var result = await handle.WaitAsync(cancellationToken).ConfigureAwait(false);
				return new ToolExecutionResult(
					$"{result.Text}\n\n— skill {name} · {result.AgentId} · {result.Turns} turns · " +
					$"{result.ToolCalls} tool calls · {result.DurationMs / 1000.0:0.0}s",
					false);
			}

			default:
				throw new MeeseeksException($"Unknown skill action \"{action}\". Available: list, get, run.");
		}
	}

	private static Skill RequireSkill(SkillCatalog catalog, string name) =>
		catalog.Get(name)
		?? throw new MeeseeksException(
			$"Unknown skill \"{name}\". Available: {catalog.DescribeAvailable()}");
}