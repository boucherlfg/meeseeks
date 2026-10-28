using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>Search file contents with a regular expression.</summary>
public sealed class GrepTool
{
	public const string ToolName = "grep";

	private const long MaxFileBytes = 4 * 1024 * 1024;

	private readonly ScriptRuntime _runtime;

	public GrepTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Search file contents with a .NET regular expression. Returns `path:line: text` for each match.

			`glob` narrows which files are searched (default `**/*`). `path` is a file or directory; relative paths resolve against the working directory. Skips binary files and build/VCS directories unless `includeIgnored` is true. Set `filesOnly` to list just the matching file names.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["pattern"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Regular expression to search for.",
				},
				["path"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "File or directory to search. Defaults to the working directory.",
				},
				["glob"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Only search files matching this glob, for example `**/*.cs`. Defaults to `**/*`.",
				},
				["ignoreCase"] = new JsonObject
				{
					["type"] = "boolean",
					["description"] = "Case-insensitive match. Defaults to false.",
				},
				["filesOnly"] = new JsonObject
				{
					["type"] = "boolean",
					["description"] = "Return only the names of files with at least one match. Defaults to false.",
				},
				["includeIgnored"] = new JsonObject
				{
					["type"] = "boolean",
					["description"] = "Also search build and VCS directories. Defaults to false.",
				},
				["maxResults"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "Maximum number of matching lines to return. Defaults to 200.",
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

	public ToolExecutionResult Execute(ToolCall call)
	{
		var pattern = ToolArgs.Text(call, "pattern") ?? throw new MeeseeksException("grep requires a \"pattern\" string.");
		var target = Path.GetFullPath(Path.Combine(_runtime.Cwd, ToolArgs.Text(call, "path") ?? "."));
		var glob = ToolArgs.Text(call, "glob") ?? "**/*";
		var ignoreCase = ToolArgs.Flag(call, "ignoreCase");
		var filesOnly = ToolArgs.Flag(call, "filesOnly");
		var includeIgnored = ToolArgs.Flag(call, "includeIgnored");
		var max = ToolArgs.Number(call, "maxResults") ?? 200;

		Regex regex;
		try
		{
			regex = new Regex(
				pattern,
				(ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant);
		}
		catch (ArgumentException ex)
		{
			throw new MeeseeksException($"Invalid regular expression: {ex.Message}");
		}

		var files = new List<string>();
		if (File.Exists(target))
		{
			files.Add(target);
		}
		else if (Directory.Exists(target))
		{
			var fileRegex = GlobTool.CompileGlob(glob);
			var matchFullPath = glob.Contains('/') || glob.Contains('\\');
			foreach (var file in GlobTool.Walk(target, includeIgnored))
			{
				var relative = Path.GetRelativePath(target, file).Replace('\\', '/');
				var subject = matchFullPath ? relative : Path.GetFileName(file);
				if (fileRegex.IsMatch(subject))
				{
					files.Add(file);
				}
			}
		}
		else
		{
			throw new MeeseeksException($"Path not found: {target}");
		}

		var builder = new StringBuilder();
		var hits = 0;
		var matchedFiles = 0;
		var truncated = false;

		foreach (var file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			string[] lines;
			try
			{
				if (new FileInfo(file).Length > MaxFileBytes)
				{
					continue;
				}

				lines = File.ReadAllLines(file);
			}
			catch (Exception)
			{
				continue;
			}

			var display = Path.GetRelativePath(_runtime.Cwd, file).Replace('\\', '/');
			var fileHits = 0;

			for (var index = 0; index < lines.Length; index++)
			{
				var line = lines[index];
				if (line.Length > 4000 || line.IndexOf('\0') >= 0)
				{
					continue;
				}

				if (!regex.IsMatch(line))
				{
					continue;
				}

				fileHits++;
				hits++;

				if (filesOnly)
				{
					break;
				}

				if (hits > max)
				{
					truncated = true;
					break;
				}

				var text = line.Length > 240 ? line[..240] + "…" : line;
				builder.Append(display).Append(':').Append(index + 1).Append(": ").AppendLine(text.TrimEnd());
			}

			if (fileHits > 0)
			{
				matchedFiles++;
				if (filesOnly)
				{
					builder.AppendLine(display);
				}
			}

			if (truncated)
			{
				break;
			}
		}

		if (matchedFiles == 0)
		{
			return new ToolExecutionResult($"No matches for \"{pattern}\" in {target}.", false);
		}

		if (truncated)
		{
			builder.AppendLine($"[stopped at {max} matches; raise maxResults for more]");
		}

		return new ToolExecutionResult(builder.ToString().TrimEnd(), false);
	}
}
