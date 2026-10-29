using System.Text;
using System.Text.Json.Nodes;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>
/// Apply a git-style unified diff across one or more files in a single call.
/// Every file section is validated before anything is written, so a multi-file change is
/// all-or-nothing by default instead of half-applied.
/// </summary>
public sealed class PatchTool
{
	public const string ToolName = "patch";

	private readonly ScriptRuntime _runtime;

	public PatchTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Apply a standard (git-style) unified diff across one or more files in a single call, and report one diff per file.

			`diff` is standard unified diff output, exactly as `git diff` produces it: one or more file sections, each with `--- a/path` / `+++ b/path` headers and `@@ -a,b +c,d @@` hunks of ` `/`-`/`+` lines. `diff --git` lines, timestamps, `a/`/`b/` prefixes, and `\ No newline at end of file` markers are all accepted. Copy context lines from `read` output, which shows line numbers as `   12→text`; the `   12→` prefix is not part of the file.

			Hunks are located by content, not just line numbers: the stated line is tried first, then the nearest exact match, then a match ignoring whitespace. So slightly-off line numbers still apply, but context lines must match the file's current contents.

			By default the call is atomic: every file section is validated first, and if any section fails nothing is written and the error names the failing file. Pass `atomic: false` to apply the sections that do match and report the ones that did not.

			Use this instead of many `edit` calls when one change spans several files — a rename, a signature change, its call sites, and the tests.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["diff"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Standard unified diff, possibly covering several files, as git diff would produce.",
				},
				["atomic"] = new JsonObject
				{
					["type"] = "boolean",
					["description"] = "Validate every file section before writing anything. Defaults to true.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			["required"] = new JsonArray("diff"),
		},
	};

	/// <summary>A file's contents before and after its hunks applied.</summary>
	private sealed class FileChange
	{
		public required string Full { get; init; }
		public required string Display { get; init; }
		public required string Original { get; init; }
		public required DiffFile File { get; init; }
		public string Current { get; set; } = "";
	}

	public ToolExecutionResult Execute(ToolCall call)
	{
		var diff = ToolArgs.Text(call, "diff")
			?? throw new MeeseeksException("patch requires a non-empty \"diff\" string.");
		var atomic = !call.Arguments.ContainsKey("atomic") || ToolArgs.Flag(call, "atomic");

		var sections = UnifiedDiff.Parse(diff);

		// Load every distinct file once, so hunks in the same file compose in order.
		var files = new Dictionary<string, FileChange>(StringComparer.OrdinalIgnoreCase);
		var failures = new List<string>();

		foreach (var section in sections)
		{
			var full = FilePaths.Resolve(_runtime, section.Path, "patch");
			var display = FilePaths.Display(_runtime, full);

			if (section.IsNew)
			{
				failures.Add(
					$"{display}: the diff creates this file; use write to create new files, not patch.");
				continue;
			}

			if (section.IsDelete)
			{
				failures.Add(
					$"{display}: the diff deletes this file; use shell (del/rm) to delete files, not patch.");
				continue;
			}

			if (!File.Exists(full))
			{
				failures.Add($"{display}: file not found. Use write to create it.");
				continue;
			}

			string original;
			try
			{
				original = File.ReadAllText(full);
			}
			catch (Exception ex)
			{
				failures.Add($"{display}: could not read: {ex.Message}");
				continue;
			}

			string updated;
			try
			{
				updated = UnifiedDiff.Apply(original, section);
			}
			catch (MeeseeksException ex)
			{
				failures.Add(ex.Message.StartsWith("diff:", StringComparison.Ordinal)
					? ex.Message
					: $"{display}: {ex.Message}");
				continue;
			}
			catch (Exception ex)
			{
				failures.Add($"{display}: could not apply the diff: {ex.Message}");
				continue;
			}

			if (files.ContainsKey(full))
			{
				failures.Add($"{display}: the diff contains two sections for this file; combine them into one.");
				continue;
			}

			files[full] = new FileChange
			{
				Full = full,
				Display = display,
				Original = original,
				File = section,
				Current = updated,
			};
		}

		if (failures.Count > 0 && atomic)
		{
			throw new MeeseeksException(
				$"patch: {failures.Count} of {sections.Count} file sections did not apply, so nothing was written.\n" +
				string.Join("\n", failures.Select(failure => "- " + failure)));
		}

		var changed = files.Values
			.Where(file => file.Current != file.Original)
			.OrderBy(file => file.Display, StringComparer.OrdinalIgnoreCase)
			.ToList();

		foreach (var file in changed)
		{
			try
			{
				FilePaths.WriteAtomic(file.Full, file.Current);
			}
			catch (Exception ex)
			{
				throw new MeeseeksException($"patch: could not write {file.Display}: {ex.Message}");
			}
		}

		return new ToolExecutionResult(Render(sections.Count, changed, failures), failures.Count > 0);
	}

	private static string Render(
		int total,
		IReadOnlyList<FileChange> changed,
		IReadOnlyList<string> failures)
	{
		var builder = new StringBuilder();

		if (changed.Count == 0)
		{
			builder.Append("patch: no files changed");
		}
		else
		{
			var hunks = changed.Sum(file => file.File.Hunks.Count);
			builder.Append("patched ")
				.Append(changed.Count)
				.Append(changed.Count == 1 ? " file" : " files")
				.Append("  ·  ")
				.Append(hunks)
				.Append(hunks == 1 ? " hunk" : " hunks");

			foreach (var file in changed)
			{
				var (before, _) = FilePaths.SplitLines(file.Original);
				var (after, _) = FilePaths.SplitLines(file.Current);
				var (added, removed) = LineDiff.Count(before, after);

				builder.AppendLine();
				builder.Append("--- ")
					.Append(file.Display)
					.Append("  ·  +")
					.Append(added)
					.Append(" -")
					.Append(removed)
					.AppendLine();
				builder.Append(LineDiff.Render(before, after));
			}
		}

		if (failures.Count > 0)
		{
			builder.AppendLine();
			builder.AppendLine();
			builder.Append("patch: ")
				.Append(failures.Count)
				.Append(failures.Count == 1 ? " section" : " sections")
				.AppendLine(" did not apply and were skipped:");
			foreach (var failure in failures)
			{
				builder.Append("- ").AppendLine(failure);
			}
		}

		return builder.ToString().TrimEnd();
	}
}