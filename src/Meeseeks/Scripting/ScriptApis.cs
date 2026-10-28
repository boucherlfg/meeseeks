using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Meeseeks.Agents;
using Meeseeks.Core;
using Meeseeks.Tools;

namespace Meeseeks.Scripting;

[Api("Subagents and messaging.")]
public sealed class AgentApi
{
	private readonly ScriptRuntime _runtime;

	internal AgentApi(ScriptRuntime runtime) => _runtime = runtime;

	[Api("This agent's id: `root`, `root.1`, `root.1.2`.", Order = 0)]
	public string Id => _runtime.AgentId;

	[Api("This agent's depth in the spawn tree. Root is 0.", Order = 1)]
	public int Depth => _runtime.Depth;

	[Api("Run one child meeseeks process and wait for its final text.", Order = 10)]
	public Task<AgentResult> SpawnAsync(string prompt, SpawnOptions? options = null) =>
		SpawnAsync(prompt, options, _runtime.CancellationToken);

	public async Task<AgentResult> SpawnAsync(
		string prompt,
		SpawnOptions? options,
		CancellationToken cancellationToken)
	{
		var handle = await StartAsync(prompt, options, cancellationToken).ConfigureAwait(false);
		return await handle.WaitAsync(Resolve(cancellationToken)).ConfigureAwait(false);
	}

	[Api("""
		Start a child process and return a live handle. Send messages with the handle, await
		WaitAsync(), or Stop() it. Children not awaited by the end of the script are stopped.
		""", Order = 11)]
	public async Task<AgentHandle> StartAsync(
		string prompt,
		SpawnOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		var handle = await _runtime.Agents
			.StartAsync(prompt, options, Resolve(cancellationToken))
			.ConfigureAwait(false);
		_runtime.Track(handle);
		return handle;
	}

	[Api("Run one child per prompt, in parallel, and wait for all of them.", Order = 12)]
	public async Task<AgentResult[]> SpawnManyAsync(
		IEnumerable<string> prompts,
		SpawnOptions? options = null)
	{
		var tasks = prompts.Select(prompt => SpawnAsync(prompt, options)).ToArray();
		return await Task.WhenAll(tasks).ConfigureAwait(false);
	}

	[Api("Fan out over arbitrary items, one child per item, and wait for all of them.", Order = 13)]
	public async Task<AgentResult[]> MapAsync<T>(
		IEnumerable<T> items,
		Func<T, string> prompt,
		SpawnOptions? options = null)
	{
		var tasks = items.Select(item => SpawnAsync(prompt(item), options)).ToArray();
		return await Task.WhenAll(tasks).ConfigureAwait(false);
	}

	[Api("Send a message to a running agent id, or \"*\" to broadcast.", Order = 20)]
	public async Task SendAsync(string agentId, string message)
	{
		await Broker.SendAsync(agentId, message, wantReply: false, _runtime.CancellationToken)
			.ConfigureAwait(false);
		_runtime.AddTrace($"Agent.SendAsync({agentId})");
	}

	[Api("Send a message to every other running agent.", Order = 21)]
	public Task BroadcastAsync(string message) => SendAsync("*", message);

	[Api("Answer a message that arrived with a #id and asked for a reply.", Order = 22)]
	public Task ReplyAsync(string messageId, string text) =>
		Broker.ReplyAsync(messageId, text, _runtime.CancellationToken);

	[Api("Block until a message arrives, or return null after the timeout.", Order = 23)]
	public Task<AgentMessage?> WaitForMessageAsync(
		int timeoutMs = 120000,
		CancellationToken cancellationToken = default) =>
		_runtime.WaitForMessageAsync(timeoutMs, Resolve(cancellationToken));

	[Api("List running agents and their depth.", Order = 24)]
	public async Task<AgentPeerInfo[]> PeersAsync()
	{
		var peers = await Broker.PeersAsync(_runtime.CancellationToken).ConfigureAwait(false);
		return peers.Select(peer => new AgentPeerInfo
		{
			Id = peer.Id,
			Parent = peer.Parent,
			Depth = peer.Depth,
		}).ToArray();
	}

	[Api("Messages this agent has received so far.", Order = 25)]
	public IReadOnlyList<AgentMessage> Messages() => _runtime.Messages;

	private BrokerClient Broker =>
		_runtime.Broker ?? throw new MeeseeksException("This process has no message broker.");

	private CancellationToken Resolve(CancellationToken cancellationToken) =>
		cancellationToken.CanBeCanceled ? cancellationToken : _runtime.CancellationToken;
}

[Api("Skill discovery and execution.")]
public sealed class SkillsApi
{
	private readonly ScriptRuntime _runtime;

	internal SkillsApi(ScriptRuntime runtime) => _runtime = runtime;

	[Api("List discovered skills, optionally filtered by a substring of name or description.", Order = 10)]
	public Task<SkillInfo[]> ListAsync(string? query = null)
	{
		var needle = query?.Trim();
		var skills = _runtime.SkillCatalog.Skills
			.Where(skill => string.IsNullOrEmpty(needle)
				|| skill.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
				|| skill.Description.Contains(needle, StringComparison.OrdinalIgnoreCase))
			.Select(skill => new SkillInfo
			{
				Name = skill.Name,
				Description = skill.Description,
				Path = skill.Path,
				BaseDir = skill.BaseDir,
			})
			.ToArray();

		return Task.FromResult(skills);
	}

	[Api("Load one skill. .Body is the full SKILL.md with frontmatter removed.", Order = 11)]
	public Task<SkillInfo> GetAsync(string name)
	{
		var skill = _runtime.SkillCatalog.Get(name)
			?? throw new MeeseeksException(
				$"Unknown skill \"{name}\". Available: {_runtime.SkillCatalog.DescribeAvailable()}");
		return Task.FromResult(new SkillInfo
		{
			Name = skill.Name,
			Description = skill.Description,
			Path = skill.Path,
			BaseDir = skill.BaseDir,
			Body = skill.Body,
		});
	}

	[Api("Run a skill as a child agent whose system prompt is the skill's instructions.", Order = 12)]
	public Task<AgentResult> RunAsync(string name, string input, SpawnOptions? options = null)
	{
		var skill = _runtime.SkillCatalog.Get(name)
			?? throw new MeeseeksException(
				$"Unknown skill \"{name}\". Available: {_runtime.SkillCatalog.DescribeAvailable()}");

		var systemPrompt = $"## Skill: {skill.Name}\n\nSkill directory: {skill.BaseDir}\n\n{skill.Body}";
		if (!string.IsNullOrWhiteSpace(options?.SystemPrompt))
		{
			systemPrompt += "\n\n## Additional instructions\n\n" + options.SystemPrompt.Trim();
		}

		var merged = new SpawnOptions
		{
			Model = options?.Model,
			Think = options?.Think,
			TimeoutMs = options?.TimeoutMs,
			Label = options?.Label ?? skill.Name,
			SystemPrompt = systemPrompt,
		};

		return new AgentApi(_runtime).SpawnAsync(input, merged);
	}
}

[Api("MCP servers and tools.")]
public sealed class McpApi
{
	private readonly ScriptRuntime _runtime;

	internal McpApi(ScriptRuntime runtime) => _runtime = runtime;

	[Api("Configured MCP servers. Does not connect to them.", Order = 10)]
	public Task<McpServerInfo[]> ServersAsync()
	{
		var servers = _runtime.Mcp.Servers.Select(pair => new McpServerInfo
		{
			Name = pair.Key,
			Transport = pair.Value.Transport,
			Enabled = pair.Value.Enabled,
			Description = pair.Value.Description,
		}).ToArray();
		return Task.FromResult(servers);
	}

	[Api("Connect (if needed) and list tools of one server or every enabled server.", Order = 11)]
	public async Task<McpToolInfo[]> ToolsAsync(string? server = null)
	{
		var tools = await _runtime.Mcp
			.ListToolsAsync(server, _runtime.CancellationToken)
			.ConfigureAwait(false);
		return tools.Select(tool => new McpToolInfo
		{
			Name = tool.Name,
			Server = tool.Server,
			Description = tool.Description,
		}).ToArray();
	}

	[Api("Call an MCP tool and return the raw JSON-RPC result.", Order = 12)]
	public async Task<JsonNode?> CallRawAsync(string server, string tool, object? arguments = null)
	{
		var result = await _runtime.Mcp.CallToolAsync(
			server,
			tool,
			JsonArguments.ToObject(arguments),
			_runtime.CancellationToken).ConfigureAwait(false);
		_runtime.AddTrace($"Mcp.CallRawAsync({server}/{tool})");
		return result;
	}

	[Api("Call an MCP tool and return its text content. Throws when the tool reports an error.", Order = 13)]
	public async Task<string> CallAsync(string server, string tool, object? arguments = null)
	{
		var result = await CallRawAsync(server, tool, arguments).ConfigureAwait(false);
		var text = ToolText.FromResult(result);
		if (ToolText.IsError(result))
		{
			throw new MeeseeksException($"MCP tool {server}/{tool} failed: {text}");
		}
		return text;
	}
}

[Api("Every callable tool: csharp and MCP tools.")]
public sealed class ToolsApi
{
	private readonly ScriptRuntime _runtime;

	internal ToolsApi(ScriptRuntime runtime) => _runtime = runtime;

	[Api("The csharp tool and every MCP tool. Servers that fail to connect are skipped.", Order = 10)]
	public async Task<ToolInfo[]> ListAsync()
	{
		var tools = new List<ToolInfo>
		{
			new() { Name = "csharp", Description = "Runs a C# script in this process (the model-facing tool)." },
		};

		foreach (var server in _runtime.Mcp.EnabledServers)
		{
			try
			{
				var serverTools = await _runtime.Mcp
					.ListToolsAsync(server, _runtime.CancellationToken)
					.ConfigureAwait(false);
				tools.AddRange(serverTools.Select(tool => new ToolInfo
				{
					Name = tool.FullName,
					Description = tool.Description,
				}));
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				// One unavailable server should not hide the others.
			}
		}

		return tools.ToArray();
	}

	[Api("Call any MCP tool by full name (mcp__server__tool). The csharp tool cannot call itself.", Order = 20)]
	public async Task<JsonNode?> CallRawAsync(string name, object? arguments = null)
	{
		if (name == "csharp")
		{
			throw new MeeseeksException(
				"The csharp tool cannot call itself. Use Agent.SpawnAsync for nested work, or call .NET directly.");
		}

		var result = await _runtime.Mcp
			.CallByNameAsync(name, JsonArguments.ToObject(arguments), _runtime.CancellationToken)
			.ConfigureAwait(false);
		_runtime.AddTrace($"Tools.CallRawAsync({name})");
		return result;
	}

	[Api("Call any MCP tool by full name and return its text content. Throws on error.", Order = 21)]
	public async Task<string> CallAsync(string name, object? arguments = null)
	{
		var result = await CallRawAsync(name, arguments).ConfigureAwait(false);
		var text = ToolText.FromResult(result);
		if (ToolText.IsError(result))
		{
			throw new MeeseeksException($"Tool {name} failed: {text}");
		}
		return text;
	}
}
[Api("Facts about this agent and its session.")]
public sealed class SessionApi
{
	private readonly ScriptRuntime _runtime;

	internal SessionApi(ScriptRuntime runtime) => _runtime = runtime;

	[Api("This agent's id.", Order = 0)]
	public string AgentId => _runtime.AgentId;

	[Api("Working directory for scripts.", Order = 1)]
	public string Cwd => _runtime.Cwd;

	[Api("This agent's depth; root is 0.", Order = 2)]
	public int Depth => _runtime.Depth;

	[Api("Deepest allowed subagent nesting.", Order = 3)]
	public int MaxDepth => _runtime.MaxDepth;

	[Api("Ollama model name.", Order = 4)]
	public string Model => _runtime.Model;

	[Api("Cancelled when the current turn is aborted or times out.", Order = 5)]
	public CancellationToken CancellationToken => _runtime.CancellationToken;
}

[Api("Globals available in every script.")]
public sealed class ScriptGlobals
{
	[Api("Subagents: spawn, send messages, ask questions, and coordinate.", Order = 1)]
	public AgentApi Agent { get; }

	[Api("Discover skills, read them, or run one as a child agent.", Order = 2)]
	public SkillsApi Skills { get; }

	[Api("MCP servers and tools.", Order = 3)]
	public McpApi Mcp { get; }

	[Api("Every callable tool by full name, including MCP tools.", Order = 4)]
	public ToolsApi Tools { get; }

	[Api("Facts about this agent and its session.", Order = 5)]
	public SessionApi Session { get; }

	internal ScriptGlobals(ScriptRuntime runtime)
	{
		Agent = new AgentApi(runtime);
		Skills = new SkillsApi(runtime);
		Mcp = new McpApi(runtime);
		Tools = new ToolsApi(runtime);
		Session = new SessionApi(runtime);
	}

	[Api("Write a value to the tool output. Objects print as indented JSON.", Order = 0)]
	public void Print(object? value) => Console.WriteLine(Format(value));

	[Api("Write \"label: value\" to the tool output.", Order = 0)]
	public void Print(string label, object? value) => Console.WriteLine($"{label}: {Format(value)}");

	public static string Format(object? value) => value switch
	{
		null => "null",
		string text => text,
		JsonNode node => node.ToJsonString(ToolText.Pretty),
		_ => System.Text.Json.JsonSerializer.Serialize(value, ToolText.Pretty),
	};
}
