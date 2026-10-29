using System.Text;
using System.Text.RegularExpressions;
using Meeseeks.Core;

namespace Meeseeks.Tools;

/// <summary>One `@@ -a,b +c,d @@` hunk of a unified diff.</summary>
public sealed class DiffHunk
{
	public required int OldStart { get; init; }
	public required int OldCount { get; init; }
	public required int NewStart { get; init; }
	public required int NewCount { get; init; }
	public required List<string> Lines { get; init; }
}

/// <summary>One file's section of a (possibly git-style) unified diff.</summary>
public sealed class DiffFile
{
	public required string Path { get; init; }
	public bool IsNew { get; init; }
	public bool IsDelete { get; init; }
	public bool OldNoNewline { get; init; }
	public bool NewNoNewline { get; init; }
	public required List<DiffHunk> Hunks { get; init; }
}

/// <summary>
/// Parse and apply standard unified diffs. The parser accepts plain per-file diffs and
/// git-style multi-file diffs (`diff --git`, `---`/`+++` headers, `@@` hunks, and
/// `\ No newline at end of file` markers). The applier locates each hunk by content —
/// at the stated line first, then by nearest match, then ignoring trailing whitespace —
/// so imperfect line numbers from a model still apply.
/// </summary>
public static partial class UnifiedDiff
{
	[GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled)]
	private static partial Regex HunkHeader();

	[GeneratedRegex(@"^diff --git a/(.+?) b/(.+?)(\t|$)", RegexOptions.Compiled)]
	private static partial Regex GitHeader();

	/// <summary>Parse a unified diff into per-file sections. Throws on malformed hunks.</summary>
	public static List<DiffFile> Parse(string diff)
	{
		var lines = diff.Replace("\r\n", "\n").Split('\n');
		if (lines.Length > 0 && lines[^1].Length == 0)
		{
			lines = lines[..^1];
		}

		var files = new List<DiffFile>();
		var index = 0;

		while (index < lines.Length)
		{
			var line = lines[index];

			if (line.StartsWith("diff --git ", StringComparison.Ordinal))
			{
				index++;
				continue;
			}

			if (!line.StartsWith("--- ", StringComparison.Ordinal))
			{
				index++;
				continue;
			}

			var oldPath = StripHeader(line[4..], out var oldNull);
			index++;

			if (index >= lines.Length || !lines[index].StartsWith("+++ ", StringComparison.Ordinal))
			{
				throw new MeeseeksException(
					$"diff: `--- {oldPath}` is not followed by a `+++` header. The diff must be standard unified format.");
			}

			var newPath = StripHeader(lines[index][4..], out var newNull);
			index++;

			var hunks = new List<DiffHunk>();
			var oldNoNewline = false;
			var newNoNewline = false;

			while (index < lines.Length)
			{
				var body = lines[index];

				if (body.StartsWith("@@ ", StringComparison.Ordinal))
				{
					var match = HunkHeader().Match(body);
					if (!match.Success)
					{
						throw new MeeseeksException($"diff: malformed hunk header: `{body}`");
					}

					index++;
					var hunkLines = new List<string>();
					while (index < lines.Length)
					{
						var hunkLine = lines[index];
						if (hunkLine.Length == 0)
						{
							// A trailing-whitespace-stripped empty context line.
							hunkLines.Add(" ");
							index++;
							continue;
						}

						var lead = hunkLine[0];
						if (lead is ' ' or '+' or '-')
						{
							hunkLines.Add(hunkLine);
							index++;
							continue;
						}

						if (lead == '\\' && hunkLines.Count > 0)
						{
							var previous = hunkLines[^1];
							if (previous.StartsWith('+'))
							{
								newNoNewline = true;
							}
							else
							{
								oldNoNewline = true;
							}

							index++;
							continue;
						}

						break;
					}

					hunks.Add(new DiffHunk
					{
						OldStart = int.Parse(match.Groups[1].Value),
						OldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1,
						NewStart = int.Parse(match.Groups[3].Value),
						NewCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1,
						Lines = hunkLines,
					});
					continue;
				}

				break;
			}

			if (hunks.Count == 0)
			{
				throw new MeeseeksException(
					$"diff: no `@@` hunks found for `{newPath}`. Every file section needs at least one hunk.");
			}

			files.Add(new DiffFile
			{
				Path = newNull ? oldPath : newPath,
				IsNew = oldNull,
				IsDelete = newNull,
				OldNoNewline = oldNoNewline,
				NewNoNewline = newNoNewline,
				Hunks = hunks,
			});
		}

		if (files.Count == 0)
		{
			throw new MeeseeksException(
				"diff: no file section found. The diff must start with `--- ` / `+++ ` headers (git-style output is accepted).");
		}

		return files;
	}

	/// <summary>Strip `a/`/`b/` prefixes and tab-separated timestamps from a header path.</summary>
	private static string StripHeader(string raw, out bool isNull)
	{
		var text = raw.Split('\t')[0].Trim();
		isNull = text == "/dev/null";
		if (isNull)
		{
			return "";
		}

		if (text.Length > 2 && text[1] == '/' && text[0] is 'a' or 'b')
		{
			text = text[2..];
		}

		return text.Replace('\\', '/');
	}

	/// <summary>
	/// Apply one file's hunks to <paramref name="original"/> and return the updated text.
	/// Hunks are located in the original content and applied bottom-up, so they compose
	/// exactly as the diff author intended.
	/// </summary>
	public static string Apply(string original, DiffFile file)
	{
		var (beforeLines, trailing) = FilePaths.SplitLines(original);
		var lines = new List<string>(beforeLines);
		var used = new List<(int Start, int End)>();
		var plans = new List<(int Position, string[] Old, string[] New)>();

		foreach (var hunk in file.Hunks)
		{
			var (oldBlock, newBlock) = Split(hunk);
			var position = Locate(beforeLines, oldBlock, hunk.OldStart - 1, used, file.Path);
			used.Add((position, position + oldBlock.Length));
			plans.Add((position, oldBlock, newBlock));
		}

		for (var index = plans.Count - 1; index >= 0; index--)
		{
			var (position, oldBlock, newBlock) = plans[index];
			lines.RemoveRange(position, oldBlock.Length);
			lines.InsertRange(position, newBlock);
		}

		var resultTrailing = file.OldNoNewline || file.NewNoNewline
			? !file.NewNoNewline
			: trailing;
		return FilePaths.JoinLines(lines, resultTrailing);
	}

	private static (string[] Old, string[] New) Split(DiffHunk hunk)
	{
		var oldLines = new List<string>();
		var newLines = new List<string>();

		foreach (var line in hunk.Lines)
		{
			var content = line.Length == 0 ? "" : line[1..];
			switch (line.Length == 0 ? ' ' : line[0])
			{
				case ' ':
					oldLines.Add(content);
					newLines.Add(content);
					break;
				case '-':
					oldLines.Add(content);
					break;
				case '+':
					newLines.Add(content);
					break;
			}
		}

		return (oldLines.ToArray(), newLines.ToArray());
	}

	/// <summary>
	/// Find where a hunk's old block sits in the file: exact match at the stated line first,
	/// then the nearest exact match anywhere, then the nearest match ignoring trailing
	/// whitespace. Throws with a closest-line hint when nothing matches.
	/// </summary>
	private static int Locate(
		string[] lines,
		string[] oldBlock,
		int preferred,
		List<(int Start, int End)> used,
		string display)
	{
		if (oldBlock.Length == 0)
		{
			var insertion = Math.Clamp(preferred, 0, lines.Length);
			if (Overlaps(used, insertion, insertion))
			{
				throw new MeeseeksException(
					$"diff: insertion hunk for {display} lands inside another hunk's region.");
			}

			return insertion;
		}

		foreach (var tolerance in new[] { Tolerance.Exact, Tolerance.TrimEnd, Tolerance.Trim })
		{
			var best = -1;
			var bestDistance = int.MaxValue;

			for (var offset = 0; offset <= lines.Length - oldBlock.Length; offset++)
			{
				var distance = Math.Abs(offset - preferred);
				if (distance >= bestDistance || Overlaps(used, offset, offset + oldBlock.Length))
				{
					continue;
				}

				if (Matches(lines, oldBlock, offset, tolerance))
				{
					best = offset;
					bestDistance = distance;
				}
			}

			if (best >= 0)
			{
				return best;
			}
		}

		throw new MeeseeksException(
			$"diff: hunk for {display} does not match the file.{Closest(lines, oldBlock[0])}");
	}

	private enum Tolerance
	{
		Exact,
		TrimEnd,
		Trim,
	}

	private static bool Matches(string[] lines, string[] block, int offset, Tolerance tolerance)
	{
		for (var index = 0; index < block.Length; index++)
		{
			var left = lines[offset + index];
			var right = block[index];
			var equal = tolerance switch
			{
				Tolerance.Exact => left == right,
				Tolerance.TrimEnd => left.TrimEnd() == right.TrimEnd(),
				_ => left.Trim() == right.Trim(),
			};

			if (!equal)
			{
				return false;
			}
		}

		return true;
	}

	private static bool Overlaps(List<(int Start, int End)> used, int start, int end) =>
		used.Any(range => start < range.End && range.Start < end);

	/// <summary>Point at the line that most resembles the first line of the failed hunk.</summary>
	public static string Closest(string[] lines, string needle)
	{
		var target = needle.Trim();
		if (target.Length < 3)
		{
			return " Re-read the file and regenerate the diff from its current contents.";
		}

		var best = -1;
		var bestScore = 0.0;

		for (var index = 0; index < lines.Length; index++)
		{
			var candidate = lines[index].Trim();
			if (candidate.Length == 0)
			{
				continue;
			}

			var score = Similarity(target, candidate);
			if (score > bestScore)
			{
				bestScore = score;
				best = index;
			}
		}

		if (best < 0 || bestScore < 0.5)
		{
			return " Re-read the file and regenerate the diff from its current contents.";
		}

		var snippet = lines[best].Length > 160 ? lines[best][..160] + "…" : lines[best];
		return $" Closest line is {best + 1}: `{snippet}`. Re-read the file and regenerate the diff from its current contents.";
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