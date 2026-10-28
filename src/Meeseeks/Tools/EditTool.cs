using System.Text;
using System.Text.Json.Nodes;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>Replace an exact string in a file. The diff-based edit tool.</summary>
public sealed class EditTool
{
	public const string ToolName = "edit";

	private readonly ScriptRuntime _runtime;

	public EditTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Replace an exact string in a file and report the resulting diff. This is the tool for changing an existing file: it touches only the matched text, so the rest of the file is preserved byte for byte.

			`oldText` must match the file exactly, including indentation and line endings, and must be unique unless `replaceAll` is true. Copy it from `read` output, which shows line numbers as `   12→text`; the `   12→` prefix is not part of the file. If the match fails, the error says whether the text was not found or found more than once, and suggests the closest lines.

			`newText` is the replacement; use an empty string to delete. The result shows `+`/`-` lines around the change. Prefer this over rewriting a whole file with `write`.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "File to edit. Relative paths resolve against the working directory.",
				},
				["oldText"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Exact text to replace, copied from the file without the line-number prefix.",
				},
				["newText"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Replacement text. Empty string deletes the match.",
				},
				["replaceAll"] = new JsonObject
				{
					["type"] = "boolean",
					["description"] = "Replace every occurrence instead of requiring a unique match. Defaults to false.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			["required"] = new JsonArray("path", "oldText", "newText"),
		},
	};

	public ToolExecutionResult Execute(ToolCall call)
	{
		var path = ToolArgs.Text(call, "path") ?? throw new MeeseeksException("edit requires a \"path\" string.");
		var oldText = ToolArgs.Text(call, "oldText")
			?? throw new MeeseeksException("edit requires a non-empty \"oldText\" string.");
		if (call.Arguments["newText"] is not JsonValue newValue || !newValue.TryGetValue<string>(out var newText))
		{
			throw new MeeseeksException("edit requires a \"newText\" string (use \"\" to delete).");
		}

		var replaceAll = ToolArgs.Flag(call, "replaceAll");
		var full = FilePaths.Resolve(_runtime, path, "edit");
		var display = FilePaths.Display(_runtime, full);
		if (!File.Exists(full))
		{
			throw new MeeseeksException($"edit: file not found: {display}. Use write to create it.");
		}

		string original;
		try
		{
			original = File.ReadAllText(full);
		}
		catch (Exception ex)
		{
			throw new MeeseeksException($"edit: could not read {display}: {ex.Message}");
		}

		var occurrences = CountOccurrences(original, oldText);
		if (occurrences == 0)
		{
			throw new MeeseeksException(
				$"edit: oldText was not found in {display}.{Closest(original, oldText)}");
		}

		if (occurrences > 1 && !replaceAll)
		{
			throw new MeeseeksException(
				$"edit: oldText matches {occurrences} times in {display}. Include more surrounding lines to make it unique, or pass replaceAll: true.");
		}

		var updated = replaceAll
			? original.Replace(oldText, newText, StringComparison.Ordinal)
			: ReplaceFirst(original, oldText, newText);

		if (updated == original)
		{
			return new ToolExecutionResult($"edit: {display} unchanged (newText equals oldText).", false);
		}

		try
		{
			FilePaths.WriteAtomic(full, updated);
		}
		catch (Exception ex)
		{
			throw new MeeseeksException($"edit: could not write {display}: {ex.Message}");
		}

		var (before, _) = FilePaths.SplitLines(original);
		var (after, _) = FilePaths.SplitLines(updated);
		var (added, removed) = LineDiff.Count(before, after);

		var builder = new StringBuilder();
		builder.Append("edited ")
			.Append(display)
			.Append("  ·  ")
			.Append(replaceAll ? $"{occurrences} replacements" : "1 replacement")
			.Append("  ·  +")
			.Append(added)
			.Append(" -")
			.Append(removed)
			.AppendLine();
		builder.Append(LineDiff.Render(before, after));

		return new ToolExecutionResult(builder.ToString().TrimEnd(), false);
	}

	private static int CountOccurrences(string text, string needle)
	{
		var count = 0;
		var index = 0;
		while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += needle.Length;
		}
		return count;
	}

	private static string ReplaceFirst(string text, string oldText, string newText)
	{
		var index = text.IndexOf(oldText, StringComparison.Ordinal);
		return index < 0
			? text
			: string.Concat(text.AsSpan(0, index), newText, text.AsSpan(index + oldText.Length));
	}

	/// <summary>Point at the lines that most resemble the first line of the failed match.</summary>
	private static string Closest(string text, string oldText)
	{
		var needle = oldText.Split('\n')[0].Trim();
		if (needle.Length < 3)
		{
			return "";
		}

		var (lines, _) = FilePaths.SplitLines(text);
		var best = -1;
		var bestScore = 0.0;

		for (var index = 0; index < lines.Length; index++)
		{
			var candidate = lines[index].Trim();
			if (candidate.Length == 0)
			{
				continue;
			}

			var score = Similarity(needle, candidate);
			if (score > bestScore)
			{
				bestScore = score;
				best = index;
			}
		}

		if (best < 0 || bestScore < 0.5)
		{
			return " Read the file again and copy the text exactly.";
		}

		var snippet = lines[best].Length > 160 ? lines[best][..160] + "…" : lines[best];
		return $" Closest line is {best + 1}: `{snippet}`. Read the file again and copy the text exactly.";
	}

	private static double Similarity(string left, string right)
	{
		var shorter = left.Length <= right.Length ? left : right;
		var longer = left.Length <= right.Length ? right : left;
		var hits = 0;
		var cursor = 0;

		foreach (var character in shorter)
		{
			var found = longer.IndexOf(character, cursor);
			if (found >= 0)
			{
				hits++;
				cursor = found + 1;
			}
		}

		return (double)hits / longer.Length;
	}
}