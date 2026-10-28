using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>Find files by glob pattern. A fast path for the most common script.</summary>
public sealed class GlobTool
{
	public const string ToolName = "glob";

	private readonly ScriptRuntime _runtime;

	public GlobTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Find files by glob pattern. Returns matching paths, newest first, one per line.

			Patterns use `*` (any run of characters except `/`), `**` (any number of directories), `?` (one character), and `{a,b}` (alternatives). A pattern with no `/` is matched against file names anywhere under the root; a pattern with a `/` is matched against the path relative to the root.

			Skips `.git`, `node_modules`, `bin`, `obj`, and other build/VCS directories unless `includeIgnored` is true. Relative `path` resolves against the working directory.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["pattern"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Glob pattern, for example `**/*.cs` or `src/**/Test*.cs`.",
				},
				["path"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Directory to search. Defaults to the working directory.",
				},
				["includeIgnored"] = new JsonObject
				{
					["type"] = "boolean",
					["description"] = "Also search build and VCS directories. Defaults to false.",
				},
				["maxResults"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "Maximum number of paths to return. Defaults to 200.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			["required"] = new JsonArray("pattern"),
		},
	};

	private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
	{
		".git", ".vs", ".idea", ".vscode", "node_modules", "bin", "obj", "dist", "build",
		"target", "packages", ".nuget", "__pycache__", ".venv", "venv", ".next", ".cache",
	};

	public ToolExecutionResult Execute(ToolCall call)
	{
		var pattern = ToolArgs.Text(call, "pattern") ?? throw new MeeseeksException("glob requires a \"pattern\" string.");
		var root = Path.GetFullPath(Path.Combine(_runtime.Cwd, ToolArgs.Text(call, "path") ?? "."));
		if (!Directory.Exists(root))
		{
			throw new MeeseeksException($"Directory not found: {root}");
		}

		var includeIgnored = ToolArgs.Flag(call, "includeIgnored");
		var max = ToolArgs.Number(call, "maxResults") ?? 200;
		var regex = CompileGlob(pattern);
		var matchFullPath = pattern.Contains('/') || pattern.Contains('\\');

		var matches = new List<(string Full, DateTime Modified)>();

		foreach (var file in Walk(root, includeIgnored))
		{
			var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
			var subject = matchFullPath ? relative : Path.GetFileName(file);
			if (!regex.IsMatch(subject))
			{
				continue;
			}

			matches.Add((file, SafeModified(file)));
		}

		var ordered = matches
			.OrderByDescending(entry => entry.Modified)
			.ThenBy(entry => entry.Full, StringComparer.OrdinalIgnoreCase)
			.ToList();

		var shown = ordered.Take(max).ToList();
		var builder = new StringBuilder();
		foreach (var entry in shown)
		{
			builder.AppendLine(Path.GetRelativePath(_runtime.Cwd, entry.Full).Replace('\\', '/'));
		}

		if (shown.Count == 0)
		{
			return new ToolExecutionResult($"No files matched \"{pattern}\" under {root}.", false);
		}

		if (ordered.Count > shown.Count)
		{
			builder.AppendLine($"[{shown.Count} of {ordered.Count} matches shown; raise maxResults for more]");
		}

		return new ToolExecutionResult(builder.ToString().TrimEnd(), false);
	}

	/// <summary>Depth-first walk that skips build and VCS directories and symlinked loops.</summary>
	internal static IEnumerable<string> Walk(string root, bool includeIgnored)
	{
		var pending = new Stack<string>();
		pending.Push(root);

		while (pending.Count > 0)
		{
			var directory = pending.Pop();
			string[] files;
			string[] children;
			try
			{
				files = Directory.GetFiles(directory);
				children = Directory.GetDirectories(directory);
			}
			catch (Exception)
			{
				continue;
			}

			foreach (var file in files)
			{
				yield return file;
			}

			foreach (var child in children)
			{
				var name = Path.GetFileName(child);
				if (!includeIgnored && Ignored.Contains(name))
				{
					continue;
				}

				FileAttributes attributes;
				try
				{
					attributes = File.GetAttributes(child);
				}
				catch (Exception)
				{
					continue;
				}

				if (attributes.HasFlag(FileAttributes.ReparsePoint))
				{
					continue;
				}

				pending.Push(child);
			}
		}
	}

	/// <summary>Translate a glob into a regex: `*`, `**`, `?`, and `{a,b}`.</summary>
	internal static Regex CompileGlob(string pattern)
	{
		var normalized = pattern.Replace('\\', '/');
		var builder = new StringBuilder("^");
		var i = 0;

		while (i < normalized.Length)
		{
			var c = normalized[i];
			switch (c)
			{
				case '*':
					if (i + 1 < normalized.Length && normalized[i + 1] == '*')
					{
						i += 2;
						if (i < normalized.Length && normalized[i] == '/')
						{
							i++;
							builder.Append("(?:.*/)?");
						}
						else
						{
							builder.Append(".*");
						}
					}
					else
					{
						i++;
						builder.Append("[^/]*");
					}
					break;

				case '?':
					i++;
					builder.Append("[^/]");
					break;

				case '{':
					var close = normalized.IndexOf('}', i);
					if (close < 0)
					{
						i++;
						builder.Append(Regex.Escape("{"));
						break;
					}

					var alternatives = normalized[(i + 1)..close]
						.Split(',')
						.Select(part => Regex.Escape(part));
					builder.Append("(?:").Append(string.Join("|", alternatives)).Append(')');
					i = close + 1;
					break;

				default:
					i++;
					builder.Append(Regex.Escape(c.ToString()));
					break;
			}
		}

		builder.Append('$');
		return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	}

	private static DateTime SafeModified(string path)
	{
		try
		{
			return File.GetLastWriteTimeUtc(path);
		}
		catch (Exception)
		{
			return DateTime.MinValue;
		}
	}
}
