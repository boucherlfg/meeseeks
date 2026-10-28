using System.Text;
using System.Text.Json.Nodes;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>
/// Apply several exact-string replacements across one or more files in a single call.
/// Every hunk is validated before anything is written, so a multi-file change is
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
			Apply several exact-string replacements across one or more files in a single call, and report one diff per file.

			Each entry in `edits` is `{ path, oldText, newText, replaceAll? }` and follows the same matching rules as `edit`: `oldText` must match exactly, including indentation and line endings, and must be unique unless `replaceAll` is true. Hunks are applied in the order given, so a later hunk in the same file can match text introduced by an earlier one.

			By default the call is atomic: every hunk is validated first, and if any hunk fails nothing is written and the error names the failing hunk with the same "not found / found N times, closest line is…" diagnostics `edit` gives. Pass `atomic: false` to apply the hunks that do match and report the ones that did not.

			Use this instead of many `edit` calls when one change spans several files — a rename, a signature change, its call sites, and the tests.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["edits"] = new JsonObject
				{
					["type"] = "array",
					["description"] = "Replacements to apply, in order.",
					["items"] = new JsonObject
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
						},
						["required"] = new JsonArray("path", "oldText", "newText"),
					},
				},
				["atomic"] = new JsonObject
				{
					["type"] = "boolean",
					["description"] = "Validate every hunk before writing anything. Defaults to true.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			["required"] = new JsonArray("edits"),
		},
	};

	/// <summary>One requested replacement, resolved to a file.</summary>
	private sealed record Hunk(int Index, string Full, string Display, string OldText, string NewText, bool ReplaceAll);

	/// <summary>A file's contents before and after the hunks that applied to it.</summary>
	private sealed class FileChange
	{
		public required string Full { get; init; }
		public required string Display { get; init; }
		public required string Original { get; init; }
		public string Current { get; set; } = "";
		public int Replacements { get; set; }
	}

	public ToolExecutionResult Execute(ToolCall call)
	{
		if (call.Arguments["edits"] is not JsonArray edits || edits.Count == 0)
		{
			throw new MeeseeksException("patch requires a non-empty \"edits\" array.");
		}

		var atomic = !call.Arguments.ContainsKey("atomic") || ToolArgs.Flag(call, "atomic");
		var hunks = ReadHunks(edits);

		// Load every distinct file once, so hunks in the same file compose in order.
		var files = new Dictionary<string, FileChange>(StringComparer.OrdinalIgnoreCase);
		foreach (var hunk in hunks)
		{
			if (files.ContainsKey(hunk.Full))
			{
				continue;
			}

			if (!File.Exists(hunk.Full))
			{
				throw new MeeseeksException(
					$"patch: file not found: {hunk.Display} (hunk {hunk.Index + 1}). Use write to create it.");
			}

			string original;
			try
			{
				original = File.ReadAllText(hunk.Full);
			}
			catch (Exception ex)
			{
				throw new MeeseeksException($"patch: could not read {hunk.Display}: {ex.Message}");
			}

			files[hunk.Full] = new FileChange
			{
				Full = hunk.Full,
				Display = hunk.Display,
				Original = original,
				Current = original,
			};
		}

		var failures = new List<string>();
		var applied = new List<Hunk>();

		foreach (var hunk in hunks)
		{
			var file = files[hunk.Full];
			var occurrences = CountOccurrences(file.Current, hunk.OldText);

			if (occurrences == 0)
			{
				failures.Add(
					$"hunk {hunk.Index + 1} ({file.Display}): oldText was not found.{Closest(file.Current, hunk.OldText)}");
				continue;
			}

			if (occurrences > 1 && !hunk.ReplaceAll)
			{
				failures.Add(
					$"hunk {hunk.Index + 1} ({file.Display}): oldText matches {occurrences} times. " +
					"Include more surrounding lines to make it unique, or pass replaceAll: true.");
				continue;
			}

			file.Current = hunk.ReplaceAll
				? file.Current.Replace(hunk.OldText, hunk.NewText, StringComparison.Ordinal)
				: ReplaceFirst(file.Current, hunk.OldText, hunk.NewText);
			file.Replacements += hunk.ReplaceAll ? occurrences : 1;
			applied.Add(hunk);
		}

		if (failures.Count > 0 && atomic)
		{
			throw new MeeseeksException(
				$"patch: {failures.Count} of {hunks.Count} hunks did not match, so nothing was written.\n" +
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

		return new ToolExecutionResult(Render(hunks.Count, applied.Count, changed, failures), failures.Count > 0);
	}

	private List<Hunk> ReadHunks(JsonArray edits)
	{
		var hunks = new List<Hunk>();

		for (var index = 0; index < edits.Count; index++)
		{
			if (edits[index] is not JsonObject entry)
			{
				throw new MeeseeksException($"patch: edits[{index}] must be an object.");
			}

			var path = entry["path"] is JsonValue pathValue &&
				pathValue.TryGetValue<string>(out var pathText) &&
				!string.IsNullOrWhiteSpace(pathText)
					? pathText
					: throw new MeeseeksException($"patch: edits[{index}] requires a \"path\" string.");

			var oldText = entry["oldText"] is JsonValue oldValue &&
				oldValue.TryGetValue<string>(out var oldString) &&
				!string.IsNullOrEmpty(oldString)
					? oldString
					: throw new MeeseeksException($"patch: edits[{index}] requires a non-empty \"oldText\" string.");

			if (entry["newText"] is not JsonValue newValue || !newValue.TryGetValue<string>(out var newText))
			{
				throw new MeeseeksException($"patch: edits[{index}] requires a \"newText\" string (use \"\" to delete).");
			}

			var replaceAll = entry["replaceAll"] is JsonValue flagValue &&
				flagValue.TryGetValue<bool>(out var flag) && flag;

			var full = FilePaths.Resolve(_runtime, path, "patch");
			hunks.Add(new Hunk(index, full, FilePaths.Display(_runtime, full), oldText, newText, replaceAll));
		}

		return hunks;
	}

	private static string Render(
		int total,
		int applied,
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
			var replacements = changed.Sum(file => file.Replacements);
			builder.Append("patched ")
				.Append(changed.Count)
				.Append(changed.Count == 1 ? " file" : " files")
				.Append("  ·  ")
				.Append(applied)
				.Append(" of ")
				.Append(total)
				.Append(" hunks")
				.Append("  ·  ")
				.Append(replacements)
				.Append(replacements == 1 ? " replacement" : " replacements");

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
				.Append(failures.Count == 1 ? " hunk" : " hunks")
				.AppendLine(" did not match and were skipped:");
			foreach (var failure in failures)
			{
				builder.Append("- ").AppendLine(failure);
			}
		}

		return builder.ToString().TrimEnd();
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
