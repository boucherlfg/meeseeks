using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>Path handling shared by the file tools.</summary>
internal static class FilePaths
{
	/// <summary>Resolve a tool path against the working directory and reject directories.</summary>
	public static string Resolve(ScriptRuntime runtime, string path, string tool)
	{
		var full = Path.GetFullPath(Path.Combine(runtime.Cwd, path));
		if (Directory.Exists(full))
		{
			throw new MeeseeksException($"{tool}: {full} is a directory, not a file.");
		}
		return full;
	}

	/// <summary>Path as the model should see it: relative to the working directory, forward slashes.</summary>
	public static string Display(ScriptRuntime runtime, string full)
	{
		var relative = Path.GetRelativePath(runtime.Cwd, full);
		return (relative.StartsWith("..", StringComparison.Ordinal) ? full : relative).Replace('\\', '/');
	}

	/// <summary>Split into lines, keeping a trailing-newline flag so a rewrite can round-trip.</summary>
	public static (string[] Lines, bool TrailingNewline) SplitLines(string text)
	{
		if (text.Length == 0)
		{
			return ([], false);
		}

		var trailing = text.EndsWith('\n');
		var body = trailing ? text[..^1] : text;
		var lines = body.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
		return (lines, trailing);
	}

	public static string JoinLines(IReadOnlyList<string> lines, bool trailingNewline)
	{
		if (lines.Count == 0)
		{
			return "";
		}
		return string.Join("\n", lines) + (trailingNewline ? "\n" : "");
	}

	/// <summary>Write through a temp file so a failed write never leaves a half-written file.</summary>
	public static void WriteAtomic(string full, string text)
	{
		var directory = Path.GetDirectoryName(full);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var temp = full + ".meeseeks-tmp";
		File.WriteAllText(temp, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		File.Move(temp, full, overwrite: true);
	}
}