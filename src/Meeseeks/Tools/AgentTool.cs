using System.Text.Json.Nodes;
using Meeseeks.Agents;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>
/// Spawn, message, and coordinate subagents without writing a script. A fast path for the
/// most common subagent operations: one tool call instead of a compile-and-run round trip.
/// </summary>
public sealed class AgentTool
{
	public const string ToolName = "agent";

	private readonly ScriptRuntime _runtime;
	private readonly Dictionary<string, Task<AgentResult>> _started = new(StringComparer.OrdinalIgnoreCase);
	private readonly object _gate = new();

	public AgentTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Spawn and coordinate subagents without writing a script. One `action` per call.

			- `spawn` — run one child meeseeks process with a `prompt` and wait for its final text.
			- `start` — start a child and return immediately with its agent id; later `wait` for it or `send` it messages.
			- `wait` — wait for a child started with `start` (by `agentId`) and return its final result, or wait for multiple children (by `agentIds` array) and return all results.
			- `send` — send a `message` to a running agent id, or "*" to broadcast to every other agent.
			- `broadcast` — send a `message` to every other running agent.
			- `reply` — answer a message that arrived with a `#id` (`messageId`) and asked for a reply.
			- `inbox` — block until a message arrives (up to `timeoutMs`, default 120000) and return it.
			- `peers` — list running agents and their depth.
			- `messages` — list the messages received so far.

			`options` (for spawn/start): `model`, `think` (bool), `skills` (string[]), `systemPrompt`,
			`timeoutMs`, `label`, `images` (string[] of paths, URLs, or data URLs).

			Children not awaited keep running while this agent lives; a finished agent cannot receive
			messages. Give each child a complete brief: goal, paths, constraints, and what to report.
			To launch many children, call `start` for each one, then `wait` for each id: they run
			concurrently while you keep working. The `csharp` tool cannot spawn agents; scripts have
			no access to subagents, skills, or MCP.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["action"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "One of: spawn, start, wait, send, broadcast, reply, inbox, peers, messages.",
				},
				["prompt"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "The child's complete brief (spawn/start).",
				},
				["agentId"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Target agent id, e.g. root.1 (wait single/send), or * to broadcast (send).",
				},
				["agentIds"] = new JsonObject
				{
					["type"] = "array",
					["description"] = "Array of agent ids to wait for (wait multiple). Waits for all of them concurrently.",
				},
				["message"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Message text (send/broadcast) or reply text (reply).",
				},
				["messageId"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Id of the message being answered, e.g. m3 (reply).",
				},
				["timeoutMs"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "Wait limit in milliseconds (inbox, default 120000).",
				},
				["options"] = new JsonObject
				{
					["type"] = "object",
					["description"] = "SpawnOptions: model, think, skills, systemPrompt, timeoutMs, label, images.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			["required"] = new JsonArray("action"),
		},
	};

	public async Task<ToolExecutionResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
	{
		var action = ToolArgs.Text(call, "action")
			?? throw new MeeseeksException("agent requires an \"action\": spawn, start, wait, send, broadcast, reply, inbox, peers, or messages.");

		switch (action.ToLowerInvariant())
		{
			case "spawn":
			{
				var prompt = ToolArgs.Text(call, "prompt")
					?? throw new MeeseeksException("agent spawn requires a \"prompt\" string.");
				var handle = await StartAsync(prompt, ParseOptions(call), cancellationToken).ConfigureAwait(false);
				var result = await handle.WaitAsync(cancellationToken).ConfigureAwait(false);
				return new ToolExecutionResult(Format(result), false);
			}

			case "start":
			{
				var prompt = ToolArgs.Text(call, "prompt")
					?? throw new MeeseeksException("agent start requires a \"prompt\" string.");
				var handle = await StartAsync(prompt, ParseOptions(call), cancellationToken).ConfigureAwait(false);
				lock (_gate)
				{
					_started[handle.Id] = handle.Completion;
				}
				return new ToolExecutionResult(
					$"Started {handle.Id}. Use agent wait (agentId \"{handle.Id}\") to collect its result, or agent send to message it.",
					false);
			}

			case "wait":
			{
				// Check if we're waiting for multiple agents
				if (call.Arguments["agentIds"] is JsonArray agentIdsArray && agentIdsArray.Count > 0)
				{
					var agentIds = agentIdsArray
						.OfType<JsonValue>()
						.Select(v => v.TryGetValue<string>(out var s) ? s : null)
						.Where(s => !string.IsNullOrWhiteSpace(s))
						.Select(s => s!)
						.ToArray();

					if (agentIds.Length == 0)
					{
						throw new MeeseeksException("agent wait with agentIds requires at least one valid agent id in the array.");
					}

					var completions = TakeMultiple(agentIds);
					var results = await Task.WhenAll(completions.Values).ConfigureAwait(false);
					var formatted = string.Join("\n\n---\n\n", results.Select(Format));
					return new ToolExecutionResult(formatted, false);
				}

				// Single agent wait
				var agentId = ToolArgs.Text(call, "agentId")
					?? throw new MeeseeksException("agent wait requires either \"agentId\" (string) or \"agentIds\" (array).");
				var completion = Take(agentId);
				var result = await completion.ConfigureAwait(false);
				return new ToolExecutionResult(Format(result), false);
			}

			case "send":
			{
				var agentId = ToolArgs.Text(call, "agentId")
					?? throw new MeeseeksException("agent send requires an \"agentId\" (or \"*\" to broadcast).");
				var message = ToolArgs.Text(call, "message")
					?? throw new MeeseeksException("agent send requires a \"message\" string.");
				await Broker.SendAsync(agentId, message, wantReply: false, cancellationToken).ConfigureAwait(false);
				_runtime.AddTrace($"agent send({agentId})");
				return new ToolExecutionResult($"Sent to {agentId}.", false);
			}

			case "broadcast":
			{
				var message = ToolArgs.Text(call, "message")
					?? throw new MeeseeksException("agent broadcast requires a \"message\" string.");
				await Broker.SendAsync("*", message, wantReply: false, cancellationToken).ConfigureAwait(false);
				_runtime.AddTrace("agent broadcast()");
				return new ToolExecutionResult("Broadcast sent.", false);
			}

			case "reply":
			{
				var messageId = ToolArgs.Text(call, "messageId")
					?? throw new MeeseeksException("agent reply requires the \"messageId\" of the message being answered.");
				var message = ToolArgs.Text(call, "message")
					?? throw new MeeseeksException("agent reply requires a \"message\" string.");
				await Broker.ReplyAsync(messageId, message, cancellationToken).ConfigureAwait(false);
				return new ToolExecutionResult($"Replied to {messageId}.", false);
			}

			case "inbox":
			{
				var timeoutMs = ToolArgs.Number(call, "timeoutMs") ?? 120_000;
				var message = await _runtime.WaitForMessageAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
				return new ToolExecutionResult(
					message is null ? $"No message arrived within {timeoutMs} ms." : Format(message),
					false);
			}

			case "peers":
			{
				var peers = await Broker.PeersAsync(cancellationToken).ConfigureAwait(false);
				if (peers.Length == 0)
				{
					return new ToolExecutionResult("No other agents are running.", false);
				}
				var lines = peers.Select(peer => $"{peer.Id}  ·  depth {peer.Depth}  ·  parent {peer.Parent ?? "(none)"}");
				return new ToolExecutionResult(string.Join("\n", lines), false);
			}

			case "messages":
			{
				var messages = _runtime.Messages;
				if (messages.Count == 0)
				{
					return new ToolExecutionResult("No messages received so far.", false);
				}
				return new ToolExecutionResult(string.Join("\n\n", messages.Select(Format)), false);
			}

			default:
				throw new MeeseeksException(
					$"Unknown agent action \"{action}\". Available: spawn, start, wait, send, broadcast, reply, inbox, peers, messages.");
		}
	}

	private BrokerClient Broker =>
		_runtime.Broker ?? throw new MeeseeksException("This process has no message broker.");

	private async Task<AgentHandle> StartAsync(
		string prompt,
		SpawnOptions options,
		CancellationToken cancellationToken)
	{
		var handle = await _runtime.Agents
			.StartAsync(prompt, options, cancellationToken)
			.ConfigureAwait(false);
		_runtime.Track(handle);
		return handle;
	}

	private Task<AgentResult> Take(string agentId)
	{
		lock (_gate)
		{
			if (!_started.TryGetValue(agentId, out var completion))
			{
				throw new MeeseeksException(
					$"No started child \"{agentId}\" is known here. Only children started by this agent's `agent start` calls can be waited on; use agent peers to list running agents.");
			}
			_started.Remove(agentId);
			return completion;
		}
	}

	private Dictionary<string, Task<AgentResult>> TakeMultiple(string[] agentIds)
	{
		lock (_gate)
		{
			var completions = new Dictionary<string, Task<AgentResult>>();
			foreach (var agentId in agentIds)
			{
				if (!_started.TryGetValue(agentId, out var completion))
				{
					throw new MeeseeksException($"No started child \"{agentId}\" is known here. Only children started by this agent's `agent start` calls can be waited on; use agent peers to list running agents.");
				}
				completions[agentId] = completion;
				_started.Remove(agentId);
			}
			return completions;
		}
	}

	private static string RequireAgentId(ToolCall call, string action) =>
		ToolArgs.Text(call, "agentId")
		?? throw new MeeseeksException($"agent {action} requires an \"agentId\" string.");

	internal static SpawnOptions ParseOptions(ToolCall call)
	{
		if (call.Arguments["options"] is not JsonObject options)
		{
			return new SpawnOptions();
		}

		var parsed = new SpawnOptions();
		if (options["model"] is JsonValue model && model.TryGetValue<string>(out var modelText))
		{
			parsed.Model = modelText;
		}
		if (options["think"] is JsonValue think && think.TryGetValue<bool>(out var thinkFlag))
		{
			parsed.Think = thinkFlag;
		}
		if (options["skills"] is JsonArray skills)
		{
			parsed.Skills = [.. skills.OfType<JsonValue>()
				.Select(value => value.TryGetValue<string>(out var name) ? name : null)
				.Where(name => !string.IsNullOrWhiteSpace(name))
				.Select(name => name!)];
		}
		if (options["systemPrompt"] is JsonValue system && system.TryGetValue<string>(out var systemText))
		{
			parsed.SystemPrompt = systemText;
		}
		if (options["timeoutMs"] is JsonValue timeout && timeout.TryGetValue<int>(out var timeoutValue))
		{
			parsed.TimeoutMs = timeoutValue;
		}
		if (options["label"] is JsonValue label && label.TryGetValue<string>(out var labelText))
		{
			parsed.Label = labelText;
		}
		if (options["images"] is JsonArray images)
		{
			parsed.Images = [.. images.OfType<JsonValue>()
				.Select(value => value.TryGetValue<string>(out var reference) ? reference : null)
				.Where(reference => !string.IsNullOrWhiteSpace(reference))
				.Select(reference => reference!)];
		}
		return parsed;
	}

	private static string Format(AgentResult result) =>
		$"{result.Text}\n\n— {result.AgentId} · {result.Turns} turns · {result.ToolCalls} tool calls · " +
		$"{result.PromptTokens} in / {result.CompletionTokens} out · {result.DurationMs / 1000.0:0.0}s" +
		(string.IsNullOrWhiteSpace(result.Error) ? "" : $"\nError: {result.Error}");

	private static string Format(AgentMessage message) =>
		$"#{message.Id} from {message.From} at {message.At:HH:mm:ss}" +
		(message.WantReply ? " (wants a reply)" : "") + $"\n{message.Text}";
}