using System.Text;
using System.Text.Json.Nodes;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>Create or overwrite a whole file.</summary>
public sealed class WriteTool
{
	public const string ToolName = "write";

	private readonly ScriptRuntime _runtime;

	public WriteTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Create a file, or replace its entire contents. Parent directories are created as needed.

			`content` is the complete new file, written as UTF-8 without a BOM; `\n` line endings are kept as given. Overwriting an existing file needs `overwrite: true`; without it the call fails and tells you the file exists, so a stale path cannot silently destroy work. The result reports whether the file was created or replaced, its line count, and a short diff against the previous contents.

			For a small change to an existing file prefer `edit`, which applies a unified diff and leaves the rest of the file untouched.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "File to write. Relative paths resolve against the working directory.",
				},
				["content"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Complete new contents of the file.",
				},
				["overwrite"] = new JsonObject
				{
					["type"] = "boolean",
					["description"] = "Allow replacing an existing file. Defaults to false.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			["required"] = new JsonArray("path", "content"),
		},
	};

	public ToolExecutionResult Execute(ToolCall call)
	{
		var path = ToolArgs.Text(call, "path") ?? throw new MeeseeksException("write requires a \"path\" string.");
		if (call.Arguments["content"] is not JsonValue contentValue ||
			!contentValue.TryGetValue<string>(out var content))
		{
			throw new MeeseeksException("write requires a \"content\" string.");
		}

		var full = FilePaths.Resolve(_runtime, path, "write");
		var display = FilePaths.Display(_runtime, full);
		var exists = File.Exists(full);

		if (exists && !ToolArgs.Flag(call, "overwrite"))
		{
			throw new MeeseeksException(
				$"write: {display} already exists ({new FileInfo(full).Length} bytes). Pass overwrite: true to replace it, or use edit for a targeted change.");
		}

		string? previous = null;
		if (exists)
		{
			try
			{
				previous = File.ReadAllText(full);
			}
			catch (Exception ex)
			{
				throw new MeeseeksException($"write: could not read the existing {display}: {ex.Message}");
			}
		}

		try
		{
			FilePaths.WriteAtomic(full, content);
		}
		catch (Exception ex)
		{
			throw new MeeseeksException($"write: could not write {display}: {ex.Message}");
		}

		var (lines, _) = FilePaths.SplitLines(content);
		var builder = new StringBuilder();
		builder.Append(exists ? "replaced " : "created ")
			.Append(display)
			.Append("  ·  ")
			.Append(lines.Length)
			.Append(lines.Length == 1 ? " line" : " lines")
			.Append("  ·  ")
			.Append(content.Length)
			.Append(" chars");

		if (previous is not null)
		{
			var (oldLines, _) = FilePaths.SplitLines(previous);
			var (added, removed) = LineDiff.Count(oldLines, lines);
			builder.Append("  ·  +").Append(added).Append(" -").Append(removed);
		}

		return new ToolExecutionResult(builder.ToString(), false);
	}
}