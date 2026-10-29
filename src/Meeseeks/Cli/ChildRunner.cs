using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meeseeks.Agents;
using Meeseeks.Config;
using Meeseeks.Core;
using Meeseeks.Mcp;
using Meeseeks.Providers;
using Meeseeks.Scripting;
using Meeseeks.Tools;

namespace Meeseeks.Cli;

/// <summary>Runs one prompt as a child meeseeks process and writes a JSON result for the parent.</summary>
public static class ChildRunner
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
	};

	public static async Task<int> RunAsync(CliArgs args)
	{
		var debug = Environment.GetEnvironmentVariable("MEESEEKS_DEBUG") is not null;

		MeeseeksPaths.Ensure();
		var config = ProviderConfig.LoadOrCreate(out _);
		if (!string.IsNullOrWhiteSpace(args.Model))
		{
			config.Model = args.Model;
		}
		if (!string.IsNullOrWhiteSpace(args.Think))
		{
			config.Think = ThinkSetting.Parse(args.Think);
		}

		var cwd = Path.GetFullPath(args.Cwd ?? Directory.GetCurrentDirectory());
		Directory.SetCurrentDirectory(cwd);

		var agentId = string.IsNullOrWhiteSpace(args.AgentId) ? "root.1" : args.AgentId;
		var depth = Math.Max(1, args.Depth);
		var prompt = await ReadFileAsync(args.PromptFile);
		var extra = await ReadFileAsync(args.SystemFile);
		var images = await ReadImagesAsync(args.ImagesFile);

		var skills = SkillCatalog.Load(cwd);
		var mcp = args.NoMcp ? McpManager.Empty() : McpManager.Load(MeeseeksPaths.McpFile);

		BrokerClient? broker = null;
		AgentRuntime? agents = null;
		IModelClient? client = null;
		TurnResult? turn = null;
		string? error = null;

		try
		{
			if (!string.IsNullOrWhiteSpace(args.BrokerUrl) && !string.IsNullOrWhiteSpace(args.BrokerToken))
			{
				try
				{
					broker = await BrokerClient.ConnectAsync(
						args.BrokerUrl,
						args.BrokerToken,
						agentId,
						args.Parent,
						depth,
						CancellationToken.None);
					broker.StartReceiving();
				}
				catch (Exception ex)
				{
					if (debug)
					{
						Console.Error.WriteLine($"[child] broker unavailable: {ex.Message}");
					}
				}
			}

			agents = new AgentRuntime(config, agentId, depth, cwd, broker, skills, !args.NoMcp);

			var runtime = new ScriptRuntime
			{
				AgentId = agentId,
				Depth = depth,
				MaxDepth = config.MaxDepth,
				Cwd = cwd,
				Model = config.Model,
				CancellationToken = CancellationToken.None,
				Agents = agents,
				SkillCatalog = skills,
				Mcp = mcp,
				Broker = broker,
			};

			client = ModelClientFactory.Create(config);
			var tools = new ToolRegistry(runtime, config);
			var systemPrompt = SystemPrompt.Build(config, agentId, args.Parent, depth, cwd, skills, extra);
			IProgressSink sink = debug ? new DebugSink() : NullProgressSink.Instance;
			var session = new AgentSession(client, config, tools, sink, systemPrompt, broker);

			if (debug)
			{
				Console.Error.WriteLine($"[child] {agentId} depth {depth} model {config.Model}");
			}

			turn = await session.RunTurnAsync(prompt, images, CancellationToken.None);
		}
		catch (OperationCanceledException)
		{
			error = "cancelled";
		}
		catch (Exception ex)
		{
			error = ex.Message;
			if (debug)
			{
				Console.Error.WriteLine($"[child] {ex}");
			}
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
		}

		var payload = new
		{
			text = turn?.Text ?? "",
			turns = turn?.Steps ?? 0,
			toolCalls = turn?.ToolCalls ?? 0,
			promptTokens = turn?.Usage.PromptTokens ?? 0,
			completionTokens = turn?.Usage.CompletionTokens ?? 0,
			error,
		};

		var json = JsonSerializer.Serialize(payload, Json);
		if (!string.IsNullOrWhiteSpace(args.ResultFile))
		{
			try
			{
				await File.WriteAllTextAsync(args.ResultFile, json, Encoding.UTF8);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[child] could not write result file: {ex.Message}");
			}
		}
		else
		{
			Console.Out.WriteLine(json);
		}

		return error is null ? 0 : 1;
	}

	private static async Task<string> ReadFileAsync(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return "";
		}
		return await File.ReadAllTextAsync(path);
	}

	/// <summary>Read the images the parent wrote for this child's prompt.</summary>
	private static async Task<IReadOnlyList<ImageAttachment>> ReadImagesAsync(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return [];
		}

		var text = await File.ReadAllTextAsync(path);
		if (JsonNode.Parse(text) is not JsonArray array)
		{
			return [];
		}

		var images = new List<ImageAttachment>(array.Count);
		foreach (var node in array.OfType<JsonObject>())
		{
			var base64 = node["base64"]?.GetValue<string>();
			if (string.IsNullOrWhiteSpace(base64))
			{
				continue;
			}

			images.Add(new ImageAttachment
			{
				Base64 = base64,
				MediaType = node["mediaType"]?.GetValue<string>(),
				Source = node["source"]?.GetValue<string>(),
			});
		}

		return images;
	}
}
