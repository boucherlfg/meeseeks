using System.Text;

namespace Meeseeks.Tools;

/// <summary>Line diff used to report what an edit changed.</summary>
internal static class LineDiff
{
	/// <summary>Count added and removed lines with a longest-common-subsequence diff.</summary>
	public static (int Added, int Removed) Count(IReadOnlyList<string> before, IReadOnlyList<string> after)
	{
		var prefix = 0;
		var max = Math.Min(before.Count, after.Count);
		while (prefix < max && before[prefix] == after[prefix])
		{
			prefix++;
		}

		var suffix = 0;
		while (suffix < max - prefix &&
			before[before.Count - 1 - suffix] == after[after.Count - 1 - suffix])
		{
			suffix++;
		}

		var oldMiddle = before.Skip(prefix).Take(before.Count - prefix - suffix).ToArray();
		var newMiddle = after.Skip(prefix).Take(after.Count - prefix - suffix).ToArray();

		if (oldMiddle.Length == 0 || newMiddle.Length == 0)
		{
			return (newMiddle.Length, oldMiddle.Length);
		}

		var common = LongestCommonSubsequence(oldMiddle, newMiddle);
		return (newMiddle.Length - common, oldMiddle.Length - common);
	}

	/// <summary>Render a unified diff of the changed region, capped at <paramref name="maxLines"/>.</summary>
	public static string Render(
		IReadOnlyList<string> before,
		IReadOnlyList<string> after,
		int context = 3,
		int maxLines = 60)
	{
		var prefix = 0;
		var max = Math.Min(before.Count, after.Count);
		while (prefix < max && before[prefix] == after[prefix])
		{
			prefix++;
		}

		var suffix = 0;
		while (suffix < max - prefix &&
			before[before.Count - 1 - suffix] == after[after.Count - 1 - suffix])
		{
			suffix++;
		}

		var oldMiddle = before.Skip(prefix).Take(before.Count - prefix - suffix).ToArray();
		var newMiddle = after.Skip(prefix).Take(after.Count - prefix - suffix).ToArray();

		var builder = new StringBuilder();
		var emitted = 0;

		void Emit(string line)
		{
			if (emitted < maxLines)
			{
				builder.AppendLine(line);
				emitted++;
			}
		}

		var lead = Math.Min(context, prefix);
		for (var index = prefix - lead; index < prefix; index++)
		{
			Emit("  " + before[index]);
		}

		foreach (var line in DiffLines(oldMiddle, newMiddle))
		{
			Emit(line);
		}

		var trail = Math.Min(context, suffix);
		for (var index = 0; index < trail; index++)
		{
			Emit("  " + after[after.Count - suffix + index]);
		}

		if (emitted >= maxLines)
		{
			builder.AppendLine("… diff truncated");
		}

		return builder.ToString().TrimEnd();
	}

	private static IEnumerable<string> DiffLines(string[] oldLines, string[] newLines)
	{
		// Trim the common head and tail of the changed region so the table stays small.
		var head = 0;
		var limit = Math.Min(oldLines.Length, newLines.Length);
		while (head < limit && oldLines[head] == newLines[head])
		{
			yield return "  " + oldLines[head];
			head++;
		}

		var tail = 0;
		while (tail < limit - head && oldLines[oldLines.Length - 1 - tail] == newLines[newLines.Length - 1 - tail])
		{
			tail++;
		}

		var oldCore = oldLines.Skip(head).Take(oldLines.Length - head - tail).ToArray();
		var newCore = newLines.Skip(head).Take(newLines.Length - head - tail).ToArray();

		foreach (var line in Table(oldCore, newCore))
		{
			yield return line;
		}

		for (var index = newLines.Length - tail; index < newLines.Length; index++)
		{
			yield return "  " + newLines[index];
		}
	}

	private static IEnumerable<string> Table(string[] oldLines, string[] newLines)
	{
		var rows = oldLines.Length;
		var columns = newLines.Length;
		var table = new int[rows + 1, columns + 1];

		for (var i = rows - 1; i >= 0; i--)
		{
			for (var j = columns - 1; j >= 0; j--)
			{
				table[i, j] = oldLines[i] == newLines[j]
					? table[i + 1, j + 1] + 1
					: Math.Max(table[i + 1, j], table[i, j + 1]);
			}
		}

		var x = 0;
		var y = 0;
		while (x < rows && y < columns)
		{
			if (oldLines[x] == newLines[y])
			{
				yield return "  " + oldLines[x];
				x++;
				y++;
			}
			else if (table[x + 1, y] >= table[x, y + 1])
			{
				yield return "- " + oldLines[x];
				x++;
			}
			else
			{
				yield return "+ " + newLines[y];
				y++;
			}
		}

		while (x < rows)
		{
			yield return "- " + oldLines[x++];
		}

		while (y < columns)
		{
			yield return "+ " + newLines[y++];
		}
	}

	private static int LongestCommonSubsequence(string[] oldLines, string[] newLines)
	{
		var previous = new int[newLines.Length + 1];
		var current = new int[newLines.Length + 1];

		foreach (var oldLine in oldLines)
		{
			for (var j = 0; j < newLines.Length; j++)
			{
				current[j + 1] = oldLine == newLines[j]
					? previous[j] + 1
					: Math.Max(previous[j + 1], current[j]);
			}

			(previous, current) = (current, previous);
			Array.Clear(current);
		}

		return previous[newLines.Length];
	}
}