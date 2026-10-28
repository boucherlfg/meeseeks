using System.Text;
using Meeseeks.Config;

namespace Meeseeks.Scripting;

public sealed record Skill(string Name, string Description, string Path, string BaseDir, string Body);

[Api("A discovered skill.")]
public sealed class SkillInfo
{
	[Api("Skill name, for Skills.GetAsync and Skills.RunAsync.", Order = 0)]
	public string Name { get; set; } = "";

	[Api("What the skill is for.", Order = 1)]
	public string Description { get; set; } = "";

	[Api("Absolute path to SKILL.md.", Order = 2)]
	public string Path { get; set; } = "";

	[Api("Skill directory; relative paths in the instructions resolve here.", Order = 3)]
	public string BaseDir { get; set; } = "";

	[Api("Full instructions with frontmatter removed. Only set by Skills.GetAsync.", Order = 4)]
	public string? Body { get; set; }

	public override string ToString() => $"{Name}: {Description}";
}

/// <summary>
/// Claude/pi-compatible SKILL.md discovery from ~/.meeseeks/skills and &lt;cwd&gt;/.meeseeks/skills.
/// A project skill with the same name replaces the global one.
/// </summary>
public sealed class SkillCatalog
{
	private List<Skill> _skills;

	private SkillCatalog(List<Skill> skills) => _skills = skills;

	public IReadOnlyList<Skill> Skills => _skills;

	public static SkillCatalog Load(string cwd)
	{
		var catalog = new SkillCatalog([]);
		catalog.Reload(cwd);
		return catalog;
	}

	/// <summary>Re-scan the skill directories in place, so existing references see new skills.</summary>
	public void Reload(string cwd)
	{
		var roots = new[]
		{
			MeeseeksPaths.SkillsDir,
			System.IO.Path.Combine(cwd, ".meeseeks", "skills"),
		};

		var byName = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
		foreach (var root in roots)
		{
			if (!Directory.Exists(root))
			{
				continue;
			}

			foreach (var directory in EnumerateSkillDirectories(root))
			{
				var skill = TryRead(directory);
				if (skill is not null)
				{
					byName[skill.Name] = skill;
				}
			}
		}

		_skills = byName.Values.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public Skill? Get(string name) =>
		_skills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase));

	public string DescribeAvailable() =>
		_skills.Count == 0 ? "(none discovered)" : string.Join(", ", _skills.Select(skill => skill.Name));

	public string ToSystemPromptSection()
	{
		if (_skills.Count == 0)
		{
			return "";
		}

		var builder = new StringBuilder();
		builder.AppendLine("## Skills");
		builder.AppendLine();
		builder.AppendLine("Skills are packaged instructions. Read one with `Skills.GetAsync(name)` and follow it, or delegate it with `Skills.RunAsync(name, input, options)`. Use a skill when its description matches the task. Relative paths in a skill resolve against its directory.");
		builder.AppendLine();
		foreach (var skill in _skills)
		{
			builder.AppendLine($"- `{skill.Name}` — {skill.Description}  ({skill.Path})");
		}
		return builder.ToString().TrimEnd();
	}

	private static IEnumerable<string> EnumerateSkillDirectories(string root)
	{
		var pending = new Stack<(string Path, int Depth)>();
		pending.Push((root, 0));

		while (pending.Count > 0)
		{
			var (directory, depth) = pending.Pop();
			if (File.Exists(System.IO.Path.Combine(directory, "SKILL.md")))
			{
				yield return directory;
				continue;
			}

			if (depth >= 6)
			{
				continue;
			}

			string[] children;
			try
			{
				children = Directory.GetDirectories(directory);
			}
			catch
			{
				continue;
			}

			foreach (var child in children)
			{
				var name = System.IO.Path.GetFileName(child);
				if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj")
				{
					continue;
				}
				pending.Push((child, depth + 1));
			}
		}
	}

	private static Skill? TryRead(string directory)
	{
		var path = System.IO.Path.Combine(directory, "SKILL.md");
		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch
		{
			return null;
		}

		var (frontmatter, body) = SplitFrontmatter(text);
		var name = frontmatter.GetValueOrDefault("name");
		if (string.IsNullOrWhiteSpace(name))
		{
			name = System.IO.Path.GetFileName(directory);
		}

		var description = frontmatter.GetValueOrDefault("description")?.Trim() ?? "";
		return new Skill(name.Trim(), description, path, directory, body.Trim());
	}

	private static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string text)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var normalized = text.Replace("\r\n", "\n");
		if (!normalized.StartsWith("---", StringComparison.Ordinal))
		{
			return (values, normalized);
		}

		var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
		if (end < 0)
		{
			return (values, normalized);
		}

		var header = normalized[3..end];
		var bodyStart = end + 4;
		if (bodyStart < normalized.Length && normalized[bodyStart] == '\n')
		{
			bodyStart++;
		}

		string? lastKey = null;
		foreach (var rawLine in header.Split('\n'))
		{
			var line = rawLine.TrimEnd();
			if (line.Length == 0 || line.TrimStart().StartsWith('#'))
			{
				continue;
			}

			if (char.IsWhiteSpace(line[0]) && lastKey is not null)
			{
				values[lastKey] = (values[lastKey] + " " + line.Trim()).Trim();
				continue;
			}

			var colon = line.IndexOf(':');
			if (colon <= 0)
			{
				continue;
			}

			lastKey = line[..colon].Trim();
			var value = line[(colon + 1)..].Trim();
			if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'')
			{
				value = value[1..^1];
			}
			values[lastKey] = value;
		}

		return (values, normalized[bodyStart..]);
	}
}
