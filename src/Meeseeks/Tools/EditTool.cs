using System.Text;
using System.Text.Json.Nodes;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>Apply a unified diff to one file. The diff-based edit tool.</summary>
public sealed class EditTool
{
	public const string ToolName = "edit";

	private readonly ScriptRuntime _runtime;

	public EditTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Apply a standard unified diff to one file and report the resulting diff. This is the tool for changing an existing file: it touches only the hunk regions, so the rest of the file is preserved byte for byte.

			`diff` is a standard unified diff for exactly one file: `---`/`+++` headers, one or more `@@ -a,b +c,d @@` hunks, and ` `/`-`/`+` body lines. Git-style output (`diff --git`, `a/` and `b/` prefixes, timestamps, `\ No newline at end of file`) is accepted. Generate it with `git diff` mentally, or write it by hand: context lines are copied from the file, `-` lines are removed, `+` lines are added.

			Hunks are located by content, not just line numbers: the stated line is tried first, then the nearest exact match, then a match ignoring whitespace. So slightly-off line numbers still apply, but the context lines must match the file's current contents — copy them from `read` output, which shows line numbers as `   12→text`; the `   12→` prefix is not part of the file.

			To create a file, use `write`. To change several files in one call, use `patch`.
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
				["diff"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Standard unified diff for this file: ---/+++ headers, @@ hunks, and space/-/+ body lines.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			["required"] = new JsonArray("path", "diff"),
		},
	};

	public ToolExecutionResult Execute(ToolCall call)
	{
		var path = ToolArgs.Text(call, "path") ?? throw new MeeseeksException("edit requires a \"path\" string.");
		var diff = ToolArgs.Text(call, "diff") ?? throw new MeeseeksException("edit requires a non-empty \"diff\" string.");

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

		var files = UnifiedDiff.Parse(diff);
		if (files.Count > 1)
		{
			throw new MeeseeksException(
				$"edit: the diff contains {files.Count} file sections; edit applies to one file. Use patch for multi-file diffs.");
		}

		var file = files[0];
		if (file.IsDelete)
		{
			throw new MeeseeksException(
				$"edit: the diff deletes {display} entirely; use shell (del/rm) for that, or write an empty file with write.");
		}

		string updated;
		try
		{
			updated = UnifiedDiff.Apply(original, file);
		}
		catch (MeeseeksException)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw new MeeseeksException($"edit: could not apply the diff to {display}: {ex.Message}");
		}

		if (updated == original)
		{
			return new ToolExecutionResult($"edit: {display} unchanged (the diff is a no-op).", false);
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
			.Append(file.Hunks.Count)
			.Append(file.Hunks.Count == 1 ? " hunk" : " hunks")
			.Append("  ·  +")
			.Append(added)
			.Append(" -")
			.Append(removed)
			.AppendLine();
		builder.Append(LineDiff.Render(before, after));

		return new ToolExecutionResult(builder.ToString().TrimEnd(), false);
	}
}