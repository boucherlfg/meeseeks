using System.Text;
using System.Text.Json;
using Meeseeks.Agents;
using Meeseeks.Config;
using Meeseeks.Core;
using Meeseeks.Mcp;
using Meeseeks.Providers;
using Meeseeks.Scripting;
using Meeseeks.Tools;

namespace Meeseeks.Cli;

public static class RootRunner
{
	public static async Task<int> RunAsync(CliArgs args)
	{
		MeeseeksPaths.Ensure();

		var config = ProviderConfig.LoadOrCreate(out var created);
		var ui = new ConsoleUi();
		if (created)
		{
			ui.Info($"created {MeeseeksPaths.ProviderFile}");
		}

		if (!string.IsNullOrWhiteSpace(args.Model))
		{
			config.Model = args.Model;
		}
		if (args.MaxDepth is >= 0)
		{
			config.MaxDepth = args.MaxDepth.Value;
		}

		var cwd = Path.GetFullPath(args.Cwd ?? Directory.GetCurrentDirectory());
		if (!Directory.Exists(cwd))
		{
			ui.Error($"working directory not found: {cwd}");
			return 1;
		}
		Directory.SetCurrentDirectory(cwd);

		var skills = SkillCatalog.Load(cwd);
		var mcp = args.NoMcp ? McpManager.Empty() : McpManager.Load(MeeseeksPaths.McpFile);
		if (mcp.LoadError is not null)
		{
			ui.Error(mcp.LoadError);
		}

		BrokerServer? server = null;
		BrokerClient? broker = null;
		AgentRuntime? agents = null;
		OllamaClient? client = null;

		try
		{
			server = BrokerServer.Start();
			broker = await BrokerClient.ConnectAsync(
				server.Url,
				server.Token,
				"root",
				parentId: null,
				depth: 0,
				CancellationToken.None);
			broker.StartReceiving();
			broker.MessageReceived += message =>
				ui.Notice($"message from {message.From}: {Snippet(message.Text)}");

			agents = new AgentRuntime(config, "root", 0, cwd, broker, skills, !args.NoMcp);
			AppDomain.CurrentDomain.ProcessExit += (_, _) => agents.StopAll();

			var runtime = new ScriptRuntime
			{
				AgentId = "root",
				Depth = 0,
				MaxDepth = config.MaxDepth,
				Cwd = cwd,
				Model = config.Model,
				CancellationToken = CancellationToken.None,
				Agents = agents,
				SkillCatalog = skills,
				Mcp = mcp,
				Broker = broker,
			};

			client = new OllamaClient(config);
			var tools = new ToolRegistry(runtime, config);
			var systemPrompt = SystemPrompt.Build(config, "root", null, 0, cwd, skills, extra: null);
			IProgressSink sink = args.Json ? NullProgressSink.Instance : ui;
			var session = new AgentSession(client, config, tools, sink, systemPrompt, broker);

			var conversationId = ConversationStore.NewId();

			if (args.Prompt.Length > 0)
			{
				var exit = await RunOnce(ui, config, session, runtime, args);
				SaveConversation(session, config, cwd, conversationId);
				return exit;
			}

			return await RunRepl(ui, config, session, runtime, agents, mcp, skills, cwd, client, tools, conversationId);
		}
		catch (Exception ex)
		{
			ui.Error(ex.Message);
			return 1;
		}
		finally
		{
			agents?.StopAll();
			agents?.Dispose();
			if (broker is not null)
			{
				await broker.DisposeAsync();
			}
			await mcp.DisposeAsync();
			client?.Dispose();
			server?.Dispose();
		}
	}

	private static async Task<int> RunOnce(
		ConsoleUi ui,
		ProviderConfig config,
		AgentSession session,
		ScriptRuntime runtime,
		CliArgs args)
	{
		using var cancellation = new CancellationTokenSource();
		ConsoleCancelEventHandler handler = (_, eventArgs) =>
		{
			eventArgs.Cancel = true;
			cancellation.Cancel();
		};
		Console.CancelKeyPress += handler;
		using var escape = new EscapeWatcher(cancellation.Cancel);

		try
		{
			runtime.CancellationToken = cancellation.Token;
			var images = await LoadImagesAsync(ui, args.Images, runtime.Cwd, cancellation.Token);
			var result = await session.RunTurnAsync(args.Prompt, images, cancellation.Token);

			if (args.Json)
			{
				var payload = new
				{
					text = result.Text,
					agentId = "root",
					steps = result.Steps,
					toolCalls = result.ToolCalls,
					promptTokens = result.Usage.PromptTokens,
					completionTokens = result.Usage.CompletionTokens,
				};
				Console.Out.WriteLine(JsonSerializer.Serialize(
					payload,
					new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
			}
			else
			{
				ui.EnsureNewline();
			}

			return 0;
		}
		catch (OperationCanceledException)
		{
			ui.Error("aborted");
			return 130;
		}
		catch (Exception ex)
		{
			ui.Error(ex.Message);
			return 1;
		}
		finally
		{
			Console.CancelKeyPress -= handler;
		}
	}

	private static async Task<int> RunRepl(
		ConsoleUi ui,
		ProviderConfig config,
		AgentSession session,
		ScriptRuntime runtime,
		AgentRuntime agents,
		McpManager mcp,
		SkillCatalog skills,
		string cwd,
		OllamaClient client,
		ToolRegistry tools,
		string conversationId)
	{
		ui.Banner(config.Model, cwd);
		ui.Info($"conversation {conversationId}");

		CancellationTokenSource? current = null;
		var running = false;
		var pending = new List<ImageAttachment>();

		ConsoleCancelEventHandler handler = (_, eventArgs) =>
		{
			if (running)
			{
				eventArgs.Cancel = true;
				current?.Cancel();
				ui.Info("aborting…");
			}
			else
			{
				eventArgs.Cancel = false;
			}
		};
		Console.CancelKeyPress += handler;

		try
		{
			while (true)
			{
				ui.Prompt();
				var line = Console.ReadLine();
				ui.AfterInput();

				if (line is null)
				{
					break;
				}

				var text = line.Trim();
				if (text.Length == 0)
				{
					continue;
				}

				if (text is "/exit" or "/quit")
				{
					break;
				}

				if (text == "/help")
				{
					PrintHelp(ui);
					continue;
				}

				if (text == "/new")
				{
					agents.StopAll();
					session.Reset(session.SystemPrompt);
					pending.Clear();
					conversationId = ConversationStore.NewId();
					ui.Info($"new conversation {conversationId}");
					continue;
				}

				if (text == "/resume" || text.StartsWith("/resume ", StringComparison.Ordinal))
				{
					var argument = text.Length > "/resume".Length ? text["/resume".Length..].Trim() : "";
					conversationId = await HandleResumeAsync(ui, config, session, runtime, agents, skills, cwd, argument, conversationId);
					continue;
				}

				if (text == "/image" || text.StartsWith("/image ", StringComparison.Ordinal))
				{
					var argument = text.Length > "/image".Length ? text["/image".Length..].Trim() : "";
					await HandleImageAsync(ui, pending, cwd, argument);
					continue;
				}

				if (text == "/skills")
				{
					if (skills.Skills.Count == 0)
					{
						ui.Info("no skills found in ~/.meeseeks/skills or .meeseeks/skills");
					}
					else
					{
						foreach (var skill in skills.Skills)
						{
							ui.WriteLine($"{ui.Ansi.Bold(skill.Name)} {ui.Ansi.Dim("—")} {skill.Description}");
							ui.WriteLine(ui.Ansi.Dim($"  {skill.Path}"));
						}
					}
					continue;
				}

				if (text == "/model" || text.StartsWith("/model ", StringComparison.Ordinal))
				{
					var argument = text.Length > "/model".Length ? text["/model".Length..].Trim() : "";
					await HandleModelAsync(ui, client, config, session, runtime, skills, cwd, argument);
					continue;
				}

				if (text == "/think" || text.StartsWith("/think ", StringComparison.Ordinal))
				{
					var argument = text.Length > "/think".Length ? text["/think".Length..].Trim() : "";
					await HandleThinkAsync(ui, client, config, argument);
					continue;
				}

				if (text == "/session")
				{
					ui.WriteLine($"{ui.Ansi.Dim("agent")}   {runtime.AgentId}");
					ui.WriteLine($"{ui.Ansi.Dim("model")}   {config.Model}");
					ui.WriteLine($"{ui.Ansi.Dim("think")}   {config.Think?.ToString() ?? "default"}");
					ui.WriteLine($"{ui.Ansi.Dim("messages")} {session.Messages.Count}");
					ui.WriteLine($"{ui.Ansi.Dim("tokens")}   {session.TotalUsage.PromptTokens} in / {session.TotalUsage.CompletionTokens} out");
					ui.WriteLine($"{ui.Ansi.Dim("depth")}   0 of {config.MaxDepth}");
					continue;
				}

				if (text == "/reload")
				{
					await HandleReloadAsync(ui, config, session, runtime, agents, mcp, skills, cwd, tools);
					continue;
				}

				if (text == "/mcp")
				{
					await ShowMcpAsync(ui, mcp);
					continue;
				}

				if (text == "/tools")
				{
					ShowTools(ui, tools);
					continue;
				}

				if (text.StartsWith('!'))
				{
					var command = text[1..].Trim();
					if (command.Length == 0)
					{
						ui.Info("usage: !<command>");
						continue;
					}

					ui.Info($"$ {command}");
					await ShellCommand.RunAsync(command, cwd, ui);
					continue;
				}

				if (text.StartsWith('/'))
				{
					ui.Info($"unknown command {text}; try /help");
					continue;
				}

				using var cancellation = new CancellationTokenSource();
				using var escape = new EscapeWatcher(cancellation.Cancel);
				current = cancellation;
				running = true;
				runtime.CancellationToken = cancellation.Token;

				var images = new List<ImageAttachment>(pending);

				try
				{
					await session.RunTurnAsync(text, images, cancellation.Token);
					pending.Clear();
					SaveConversation(session, config, cwd, conversationId);
				}
				catch (OperationCanceledException)
				{
					ui.Info("aborted");
				}
				catch (Exception ex)
				{
					ui.Error(ex.Message);
				}
				finally
				{
					running = false;
					current = null;
				}
			}

			return 0;
		}
		finally
		{
			Console.CancelKeyPress -= handler;
		}
	}

	/// <summary>Write the current session to ~/.meeseeks/conversations, ignoring failures.</summary>
	private static void SaveConversation(AgentSession session, ProviderConfig config, string cwd, string id)
	{
		try
		{
			var messages = session.Messages
				.Where(message => message.Role != "system")
				.Select(message => CloneMessage(message))
				.ToList();
			var title = messages.FirstOrDefault(message => message.Role == "user")?.Content ?? "";
			title = title.Split('\n', 2)[0].Trim();
			if (title.Length > 80)
			{
				title = title[..80] + "…";
			}

			var existing = ConversationStore.Load(id);
			var conversation = new PersistedConversation
			{
				Id = id,
				Title = title,
				Model = config.Model,
				Cwd = cwd,
				Started = existing?.Started ?? DateTimeOffset.Now,
				Updated = DateTimeOffset.Now,
				Messages = messages,
			};
			ConversationStore.Save(conversation);
		}
		catch (Exception)
		{
			// Persistence is best-effort; never break a turn over it.
		}
	}

	private static ChatMessage CloneMessage(ChatMessage message) => new()
	{
		Role = message.Role,
		Content = message.Content,
		Thinking = message.Thinking,
		ToolCalls = message.ToolCalls,
		ToolName = message.ToolName,
		Images = message.Images,
	};

	/// <summary>
	/// With no argument, list saved conversations. With an id (or a unique prefix), load its
	/// messages into the session and continue the conversation. Returns the active id.
	/// </summary>
	private static async Task<string> HandleResumeAsync(
		ConsoleUi ui,
		ProviderConfig config,
		AgentSession session,
		ScriptRuntime runtime,
		AgentRuntime agents,
		SkillCatalog skills,
		string cwd,
		string argument,
		string currentId)
	{
		if (argument.Length == 0)
		{
			var saved = ConversationStore.List();
			if (saved.Count == 0)
			{
				ui.Info($"no saved conversations in {MeeseeksPaths.ConversationsDir}");
				return currentId;
			}

			ui.WriteLine(ui.Ansi.Dim("saved conversations (* = current)"));
			foreach (var conversation in saved)
			{
				var marker = conversation.Id == currentId ? ui.Ansi.Green("*") : " ";
				ui.WriteLine($"{marker} {ui.Ansi.Bold(conversation.Id)}  {ui.Ansi.Dim(conversation.Updated.ToString("yyyy-MM-dd HH:mm"))}  {conversation.Title}");
			}
			ui.Info("usage: /resume <id or prefix>");
			return currentId;
		}

		var id = argument.Trim().Trim('"', '\u0027');
		if (!ConversationStore.IsValidId(id))
		{
			ui.Error($"invalid conversation id: {id}");
			return currentId;
		}

		// Accept a unique prefix of an existing id.
		if (!File.Exists(ConversationStore.PathFor(id)))
		{
			var matches = ConversationStore.List()
				.Where(conversation => conversation.Id.StartsWith(id, StringComparison.Ordinal))
				.ToList();
			if (matches.Count == 1)
			{
				id = matches[0].Id;
			}
			else if (matches.Count == 0)
			{
				ui.Error($"no saved conversation matches \"{id}\"");
				return currentId;
			}
			else
			{
				ui.Error($"\"{id}\" matches {matches.Count} conversations: {string.Join(", ", matches.Select(match => match.Id))}");
				return currentId;
			}
		}

		PersistedConversation? loaded;
		try
		{
			loaded = ConversationStore.Load(id);
		}
		catch (Exception ex)
		{
			ui.Error($"could not load {id}: {ex.Message}");
			return currentId;
		}

		if (loaded is null)
		{
			ui.Error($"no saved conversation \"{id}\"");
			return currentId;
		}

		agents.StopAll();
		session.Reset(session.SystemPrompt);
		foreach (var message in loaded.Messages)
		{
			session.AppendMessage(CloneMessage(message));
		}

		// Prefer the model the conversation was saved with, when it still exists.
		if (loaded.Model.Length > 0 && loaded.Model != config.Model)
		{
			config.Model = loaded.Model;
			runtime.Model = loaded.Model;
			session.UpdateSystemPrompt(SystemPrompt.Build(config, runtime.AgentId, null, 0, cwd, skills, null));
			ui.Info($"model → {loaded.Model} (from the saved conversation)");
		}

		ui.Info($"resumed {id} — {loaded.Messages.Count} message{(loaded.Messages.Count == 1 ? "" : "s")}, {loaded.Title}");
		return id;
	}

	private static async Task HandleModelAsync(
		ConsoleUi ui,
		OllamaClient client,
		ProviderConfig config,
		AgentSession session,
		ScriptRuntime runtime,
		SkillCatalog skills,
		string cwd,
		string argument)
	{
		if (argument.Length == 0)
		{
			IReadOnlyList<OllamaModelInfo> models;
			try
			{
				models = await client.ListModelsAsync(CancellationToken.None);
			}
			catch (Exception ex)
			{
				ui.Error($"could not list models: {ex.Message}");
				ui.Info($"current: {config.Model}");
				return;
			}

			if (models.Count == 0)
			{
				ui.Info("Ollama reported no models");
				ui.Info($"current: {config.Model}");
				return;
			}

			var width = models.Max(model => model.Name.Length);
			ui.WriteLine(ui.Ansi.Dim("models (* = current)"));
			foreach (var model in models)
			{
				var marker = model.Name == config.Model ? ui.Ansi.Green("*") : " ";
				var capabilities = model.Capabilities.Count > 0
					? ui.Ansi.Dim("  " + string.Join(", ", model.Capabilities))
					: "";
				ui.WriteLine($"{marker} {model.Name.PadRight(width)}{capabilities}");
			}

			var known = models.Any(model => model.Name == config.Model);
			ui.Info($"current: {config.Model}" + (known ? "" : " (not in Ollama's list)"));
			return;
		}

		var name = argument.Trim().Trim('"', '\u0027');
		if (name.Length == 0)
		{
			ui.Info("usage: /model [name]");
			return;
		}

		config.Model = name;
		runtime.Model = name;
		session.UpdateSystemPrompt(SystemPrompt.Build(config, runtime.AgentId, null, 0, cwd, skills, null));
		try
		{
			config.Save();
			ui.Info($"model → {name}  (saved to {MeeseeksPaths.ProviderFile})");
		}
		catch (Exception ex)
		{
			ui.Error($"model changed for this session, but could not save provider.json: {ex.Message}");
		}
	}

	private static async Task HandleThinkAsync(
		ConsoleUi ui,
		OllamaClient client,
		ProviderConfig config,
		string argument)
	{
		ThinkingSupport? support = null;
		try
		{
			support = await client.GetThinkingSupportAsync(config.Model, CancellationToken.None);
		}
		catch (Exception ex)
		{
			ui.Error($"could not read thinking controls: {ex.Message}");
		}

		if (argument.Length == 0)
		{
			ui.WriteLine($"{ui.Ansi.Dim("thinking")}  {config.Think?.ToString() ?? "default"}");
			if (support is null)
			{
				ui.Info($"{config.Model} does not advertise thinking controls");
			}
			else
			{
				var values = support.Values.Count > 0 ? string.Join(", ", support.Values) : "(none)";
				var defaultValue = support.Default is not null ? $"  (default: {support.Default})" : "";
				ui.Info($"supported: {values}{defaultValue}");
			}
			return;
		}

		var value = argument.Trim().ToLowerInvariant();
		var setting = ThinkSetting.Parse(value);
		if (setting is not null && support is not null)
		{
			var accepted = setting.Level is not null
				? support.Values.Any(candidate =>
					string.Equals(candidate, setting.Level, StringComparison.OrdinalIgnoreCase))
				: setting.IsOff
					? support.SupportsOff
					: support.SupportsThinking;

			if (!accepted)
			{
				ui.Info($"note: {config.Model} supports {string.Join(", ", support.Values)}; sending {setting}");
			}
		}

		config.Think = setting;
		try
		{
			config.Save();
			ui.Info($"thinking → {setting?.ToString() ?? "default"}  (saved to {MeeseeksPaths.ProviderFile})");
		}
		catch (Exception ex)
		{
			ui.Error($"thinking changed for this session, but could not save provider.json: {ex.Message}");
		}
	}

	private static async Task HandleImageAsync(
		ConsoleUi ui,
		List<ImageAttachment> pending,
		string cwd,
		string argument)
	{
		if (argument.Length == 0)
		{
			if (pending.Count == 0)
			{
				ui.Info("no images attached; usage: /image <path|url> [more...]");
				return;
			}

			foreach (var image in pending)
			{
				ui.WriteLine($"{ui.Ansi.Dim("image")}  {ImageSource.Describe(image)}");
			}
			ui.Info($"{pending.Count} image{(pending.Count == 1 ? "" : "s")} attached to the next message; /image clear to drop them");
			return;
		}

		if (string.Equals(argument, "clear", StringComparison.OrdinalIgnoreCase))
		{
			pending.Clear();
			ui.Info("images cleared");
			return;
		}

		var references = SplitImageArguments(argument);

		var loaded = await LoadImagesAsync(ui, references, cwd, CancellationToken.None);
		pending.AddRange(loaded);
		if (loaded.Count > 0)
		{
			ui.Info($"{loaded.Count} image{(loaded.Count == 1 ? "" : "s")} attached to the next message");
		}
	}

	/// <summary>
	/// Split <c>/image</c> arguments on whitespace, keeping quoted paths with spaces intact and
	/// dropping the quote characters. <c>/image "C:\shots\screen shot.png"</c> is one reference.
	/// </summary>
	private static string[] SplitImageArguments(string argument)
	{
		var references = new List<string>();
		var current = new StringBuilder();
		var quote = '\0';

		foreach (var character in argument)
		{
			if (quote != '\0')
			{
				if (character == quote)
				{
					quote = '\0';
				}
				else
				{
					current.Append(character);
				}
				continue;
			}

			if (character is '"' or '\'')
			{
				quote = character;
				continue;
			}

			if (char.IsWhiteSpace(character))
			{
				if (current.Length > 0)
				{
					references.Add(current.ToString());
					current.Clear();
				}
				continue;
			}

			current.Append(character);
		}

		if (current.Length > 0)
		{
			references.Add(current.ToString());
		}

		return [.. references];
	}

	/// <summary>Load every reference, reporting the ones that fail instead of dropping the turn.</summary>
	private static async Task<IReadOnlyList<ImageAttachment>> LoadImagesAsync(
		ConsoleUi ui,
		IReadOnlyList<string> references,
		string cwd,
		CancellationToken cancellationToken)
	{
		var images = new List<ImageAttachment>();
		foreach (var reference in references)
		{
			try
			{
				var image = await ImageSource.LoadAsync(reference, cwd, cancellationToken);
				images.Add(image);
				ui.Info($"image  {ImageSource.Describe(image)}");
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				ui.Error(ex.Message);
			}
		}
		return images;
	}

	private static void ShowTools(ConsoleUi ui, ToolRegistry tools)
	{
		foreach (var definition in tools.Definitions)
		{
			var summary = definition.Description.Split('\n')[0].Trim();
			ui.WriteLine($"{ui.Ansi.Bold(definition.Name)} {ui.Ansi.Dim("—")} {summary}");
		}
	}

	private static async Task ShowMcpAsync(ConsoleUi ui, McpManager mcp)
	{
		if (mcp.Servers.Count == 0)
		{
			ui.Info($"no MCP servers configured in {MeeseeksPaths.McpFile}");
			return;
		}

		foreach (var pair in mcp.Servers.OrderBy(pair => pair.Key))
		{
			var state = pair.Value.Enabled ? "" : ui.Ansi.Dim("  disabled");
			ui.WriteLine($"{ui.Ansi.Bold(pair.Key)}  {ui.Ansi.Dim(pair.Value.Transport)}{state}");
		}

		foreach (var name in mcp.EnabledServers)
		{
			try
			{
				var tools = await mcp.ListToolsAsync(name, CancellationToken.None);
				ui.Info($"  {name}: {tools.Count} tool{(tools.Count == 1 ? "" : "s")}");
				foreach (var tool in tools)
				{
					ui.WriteLine(ui.Ansi.Dim($"    {tool.Name} — {tool.Description}"));
				}
			}
			catch (Exception ex)
			{
				ui.Error($"{name}: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Re-scan skills, re-read mcp.json, and rebuild the system prompt so the model sees the
	/// new skill list. Connected MCP servers are dropped and reconnect lazily on next use.
	/// </summary>
	private static async Task HandleReloadAsync(
		ConsoleUi ui,
		ProviderConfig config,
		AgentSession session,
		ScriptRuntime runtime,
		AgentRuntime agents,
		McpManager mcp,
		SkillCatalog skills,
		string cwd,
		ToolRegistry tools)
	{
		skills.Reload(cwd);
		agents.UpdateSkills(skills);
		runtime.SkillCatalog = skills;
		ui.Info($"skills: {skills.Skills.Count} discovered");

		var before = mcp.Servers.Count;
		await mcp.ReloadAsync(MeeseeksPaths.McpFile).ConfigureAwait(false);
		if (mcp.LoadError is not null)
		{
			ui.Error(mcp.LoadError);
		}
		ui.Info($"mcp: {mcp.Servers.Count} server{(mcp.Servers.Count == 1 ? "" : "s")} configured" +
			(before != mcp.Servers.Count ? $" (was {before})" : ""));

		session.UpdateSystemPrompt(SystemPrompt.Build(config, runtime.AgentId, null, 0, cwd, skills, null));
		ui.Info($"tools: {tools.Definitions.Count} built-in");
	}

	private static string Snippet(string text)
	{
		var firstLine = text.Split('\n', 2)[0].Trim();
		return firstLine.Length <= 120 ? firstLine : firstLine[..120] + "…";
	}

	private static void PrintHelp(ConsoleUi ui)
	{
		ui.WriteLine(
			ui.Ansi.Bold("/help") + "      this help\n" +
			ui.Ansi.Bold("/new") + "       start a fresh conversation\n" +
			ui.Ansi.Bold("/resume") + "    list saved conversations, or /resume <id> to continue one\n" +
			ui.Ansi.Bold("/skills") + "    list discovered skills\n" +
			ui.Ansi.Bold("/reload") + "    re-scan skills and re-read mcp.json\n" +
			ui.Ansi.Bold("/image") + "     attach an image to the next message: /image <path|url>, /image clear\n" +
			ui.Ansi.Bold("/mcp") + "       list MCP servers and their tools\n" +
			ui.Ansi.Bold("/tools") + "     list the built-in tools\n" +
			ui.Ansi.Bold("/session") + "   show agent, model, and usage\n" +
			ui.Ansi.Bold("!<cmd>") + "    run a console command in the working directory\n" +
			ui.Ansi.Bold("/exit") + "      leave");
	}
}
