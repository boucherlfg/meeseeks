namespace Meeseeks.Cli;

public sealed class CliArgs
{
	public List<string> Positionals { get; } = new();

	/// <summary>Images attached to the prompt: paths, http(s) URLs, or data URLs.</summary>
	public List<string> Images { get; } = new();

	public string? Model { get; set; }
	public string? Cwd { get; set; }
	public int? MaxDepth { get; set; }
	public bool NoMcp { get; set; }
	public bool Json { get; set; }
	public bool Help { get; set; }
	public bool Version { get; set; }
	public bool Init { get; set; }
	public bool Api { get; set; }

	public bool InternalChild { get; set; }
	public string? AgentId { get; set; }
	public string? Parent { get; set; }
	public string? BrokerUrl { get; set; }
	public string? BrokerToken { get; set; }
	public string? PromptFile { get; set; }
	public string? ResultFile { get; set; }
	public string? SystemFile { get; set; }
	public string? ImagesFile { get; set; }
	public int Depth { get; set; }
	public string? Think { get; set; }

	public string Prompt => string.Join(" ", Positionals).Trim();

	public static CliArgs Parse(string[] argv)
	{
		var result = new CliArgs();

		for (var index = 0; index < argv.Length; index++)
		{
			var argument = argv[index];
			string? inlineValue = null;

			if (argument.StartsWith("--", StringComparison.Ordinal) && argument.Contains('='))
			{
				var separator = argument.IndexOf('=');
				inlineValue = argument[(separator + 1)..];
				argument = argument[..separator];
			}

			string Value(string name)
			{
				if (inlineValue is not null)
				{
					return inlineValue;
				}
				if (index + 1 >= argv.Length)
				{
					throw new ArgumentException($"{name} needs a value.");
				}
				return argv[++index];
			}

			switch (argument)
			{
				case "-h":
				case "--help":
					result.Help = true;
					break;
				case "--version":
					result.Version = true;
					break;
				case "--init":
					result.Init = true;
					break;
				case "--api":
					result.Api = true;
					break;
				case "-m":
				case "--model":
					result.Model = Value(argument);
					break;
				case "-C":
				case "--cwd":
					result.Cwd = Value(argument);
					break;
				case "--max-depth":
					result.MaxDepth = int.Parse(Value(argument));
					break;
				case "--json":
					result.Json = true;
					break;
				case "-i":
				case "--image":
					result.Images.Add(Value(argument));
					break;
				case "--no-mcp":
					result.NoMcp = true;
					break;

				case "--internal-child":
					result.InternalChild = true;
					break;
				case "--agent-id":
					result.AgentId = Value(argument);
					break;
				case "--parent":
					result.Parent = Value(argument);
					break;
				case "--broker-url":
					result.BrokerUrl = Value(argument);
					break;
				case "--broker-token":
					result.BrokerToken = Value(argument);
					break;
				case "--prompt-file":
					result.PromptFile = Value(argument);
					break;
				case "--result-file":
					result.ResultFile = Value(argument);
					break;
				case "--system-file":
					result.SystemFile = Value(argument);
					break;
				case "--images-file":
					result.ImagesFile = Value(argument);
					break;
				case "--depth":
					result.Depth = int.Parse(Value(argument));
					break;
				case "--think":
					result.Think = Value(argument);
					break;

				default:
					if (argument.StartsWith('-'))
					{
						throw new ArgumentException($"Unknown option \"{argument}\".");
					}
					result.Positionals.Add(argument);
					break;
			}
		}

		return result;
	}
}
