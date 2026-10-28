using System.Text;
using Meeseeks.Cli;
using Meeseeks.Config;
using Meeseeks.Scripting;

namespace Meeseeks;

public static class Program
{
	public const string Version = "0.1.0";

	public static async Task<int> Main(string[] args)
	{
		try
		{
			Console.OutputEncoding = Encoding.UTF8;
		}
		catch
		{
			// Redirected consoles can reject this; rendering still works.
		}

		ConsoleUi.EnableVirtualTerminal();

		CliArgs parsed;
		try
		{
			parsed = CliArgs.Parse(args);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"error: {ex.Message}");
			PrintHelp();
			return 2;
		}

		if (parsed.Help)
		{
			PrintHelp();
			return 0;
		}

		if (parsed.Version)
		{
			Console.WriteLine($"meeseeks {Version}");
			return 0;
		}

		if (parsed.Api)
		{
			Console.WriteLine(ApiDocumentation.Text);
			return 0;
		}

		if (parsed.Init)
		{
			Init();
			return 0;
		}

		if (parsed.InternalChild)
		{
			return await ChildRunner.RunAsync(parsed);
		}

		return await RootRunner.RunAsync(parsed);
	}

	private static void Init()
	{
		MeeseeksPaths.Ensure();
		var created = false;
		ProviderConfig.LoadOrCreate(out created);

		if (!File.Exists(MeeseeksPaths.McpFile))
		{
			File.WriteAllText(
				MeeseeksPaths.McpFile,
				"{\n  \"mcpServers\": {}\n}\n",
				Encoding.UTF8);
		}

		Directory.CreateDirectory(MeeseeksPaths.SkillsDir);

		Console.WriteLine(created
			? $"created {MeeseeksPaths.ProviderFile}"
			: $"kept    {MeeseeksPaths.ProviderFile}");
		Console.WriteLine($"mcp     {MeeseeksPaths.McpFile}");
		Console.WriteLine($"skills  {MeeseeksPaths.SkillsDir}");
		Console.WriteLine();
		Console.WriteLine("Edit provider.json to set your Ollama model, then run: meeseeks \"hello\"");
	}

	private static void PrintHelp()
	{
		Console.WriteLine("""
meeseeks — a coding agent whose general-purpose tool is a C# runner

Usage
  meeseeks [options] [prompt]        with a prompt: run once; without: interactive

Options
  -m, --model <name>     override the Ollama model from provider.json
  -C, --cwd <dir>        working directory (default: current)
      --max-depth <n>    subagent nesting limit (default from provider.json: 2)
      --json             print the final answer as JSON (one-shot)
      --no-mcp           do not load MCP servers
      --init             create ~/.meeseeks config files and exit
      --api              print the generated script API reference and exit
      --version          print the version
  -h, --help             this help

Interactive commands
  /help  /new  /model  /think  /skills  /mcp  /tools  /session  /reload  /exit

The C# tool
  Every task runs through one tool: `csharp`. Scripts are ordinary .NET with
  file system, Process, HttpClient, JSON, regex, and the Meeseeks API:

    Agent.SpawnAsync / StartAsync / SpawnManyAsync / SendAsync / AskAsync / ReplyAsync
    Skills.ListAsync / GetAsync / RunAsync
    Mcp.ServersAsync / ToolsAsync / CallAsync
    Tools.ListAsync / CallAsync
    Session.Cwd / AgentId / Depth / MaxDepth / Model

  Scripts may load NuGet packages and local assemblies:

    #r "nuget: Newtonsoft.Json, 13.0.3"
    #r "lib/MyLibrary.dll"

  Packages are restored once and cached in ~/.meeseeks/nuget.

Configuration
  ~/.meeseeks/provider.json   Ollama baseUrl, model, think, temperature, num_ctx,
                              timeouts, maxDepth, maxParallelSubagents
  ~/.meeseeks/mcp.json        MCP servers (Claude/pi "mcpServers" shape)
  ~/.meeseeks/skills/         global SKILL.md packages
  <cwd>/.meeseeks/skills/     project SKILL.md packages

Examples
  meeseeks --init
  meeseeks "List every TODO in src/ and group them by file"
  meeseeks -m glm-5.3-flash:cloud "Add a test for the retry helper and run it"
  meeseeks --json "Count the C# files in this repository"
""");
	}
}
