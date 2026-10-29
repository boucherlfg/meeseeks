using System.Diagnostics;
using System.Text;
using Meeseeks.Agents;
using Meeseeks.Config;
using Meeseeks.Providers;
using Meeseeks.Tools;

namespace Meeseeks.Core;

/// <summary>One conversation: system prompt, user turns, tool calls, and usage.</summary>
public sealed class AgentSession
{
	private readonly OllamaClient _client;
	private readonly ProviderConfig _config;
	private readonly ToolRegistry _tools;
	private readonly IProgressSink _sink;
	private readonly BrokerClient? _broker;
	/// <summary>Canonical keys of recent tool calls, used to refuse duplicate calls.</summary>
	private readonly List<string> _recentCalls = new();
	private const int RecentCallWindow = 8;
	private readonly List<ChatMessage> _messages = new();
	private string _systemPrompt;

	public AgentSession(
		OllamaClient client,
		ProviderConfig config,
		ToolRegistry tools,
		IProgressSink sink,
		string systemPrompt,
		BrokerClient? broker)
	{
		_client = client;
		_config = config;
		_tools = tools;
		_sink = sink;
		_broker = broker;
		_systemPrompt = systemPrompt;
		_messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
	}

	public IReadOnlyList<ChatMessage> Messages => _messages;

	public Usage TotalUsage { get; private set; }

	public int ToolCallCount { get; private set; }

	public void UpdateSystemPrompt(string systemPrompt)
	{
		_systemPrompt = systemPrompt;
		if (_messages.Count > 0 && _messages[0].Role == "system")
		{
			_messages[0].Content = systemPrompt;
		}
		else
		{
			_messages.Insert(0, new ChatMessage { Role = "system", Content = systemPrompt });
		}
	}

	public void Reset(string systemPrompt)
	{
		_systemPrompt = systemPrompt;
		_messages.Clear();
		_messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
		TotalUsage = default;
		ToolCallCount = 0;
		_recentCalls.Clear();
	}

	/// <summary>Append a restored message (used by /resume); does not touch usage counters.</summary>
	public void AppendMessage(ChatMessage message) => _messages.Add(message);

	public async Task<TurnResult> RunTurnAsync(string userText, CancellationToken cancellationToken) =>
		await RunTurnAsync(userText, images: null, cancellationToken).ConfigureAwait(false);

	/// <summary>Run one user turn, optionally with images attached to the user message.</summary>
	public async Task<TurnResult> RunTurnAsync(
		string userText,
		IReadOnlyList<ImageAttachment>? images,
		CancellationToken cancellationToken)
	{
		_messages.Add(new ChatMessage
		{
			Role = "user",
			Content = userText,
			Images = images is { Count: > 0 } ? [.. images] : null,
		});
		return await ContinueAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Drive the model until it answers without asking for another tool call.</summary>
	public async Task<TurnResult> ContinueAsync(CancellationToken cancellationToken)
	{
		var started = Stopwatch.StartNew();
		var usageBefore = TotalUsage;
		var steps = 0;
		var toolCalls = 0;

		while (true)
		{
			steps++;
			InjectMessages();
			TrimContext();

			var result = await _client
				.ChatAsync(_messages, _tools.Definitions, _sink, cancellationToken)
				.ConfigureAwait(false);

			_messages.Add(result.Message);
			TotalUsage += result.Usage;

			if (result.Message.ToolCalls is not { Count: > 0 })
			{
				var text = result.Message.Content ?? "";
				_sink.TurnEnd(text, TotalUsage - usageBefore, started.Elapsed);
				return new TurnResult(text, TotalUsage - usageBefore, steps, toolCalls);
			}

			var assistantIndex = _messages.Count - 1;
			try
			{
				foreach (var call in result.Message.ToolCalls)
				{
					toolCalls++;
					ToolCallCount++;

					var attempt = ToolCallKey(call);
					if (_recentCalls.Contains(attempt))
					{
						_sink.ToolStart(call);
						_sink.ToolEnd(call, false, TimeSpan.Zero);
						_messages.Add(new ChatMessage
						{
							Role = "tool",
							ToolName = call.Name,
							Content = $"ERROR: Refusing to run \"{call.Name}\" with arguments already used recently. Change the arguments or the approach.",
						});
						continue;
					}

					_recentCalls.Add(attempt);
					if (_recentCalls.Count > RecentCallWindow)
					{
						_recentCalls.RemoveRange(0, _recentCalls.Count - RecentCallWindow);
					}
					_sink.ToolStart(call);
					var callWatch = Stopwatch.StartNew();
					var output = "";
					IReadOnlyList<ImageAttachment>? images = null;
					var ok = true;

					try
					{
						var execution = await _tools.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
						output = execution.Text;
						ok = !execution.IsError;
						images = execution.Images;
					}
					catch (OperationCanceledException)
					{
						ok = false;
						throw;
					}
					catch (Exception ex)
					{
						ok = false;
						output = "ERROR: " + ex.Message;
					}
					finally
					{
						callWatch.Stop();
						_sink.ToolEnd(call, ok, callWatch.Elapsed);
					}

					_messages.Add(new ChatMessage
					{
						Role = "tool",
						ToolName = call.Name,
						Content = Truncate(output, 64_000),
						Images = images is { Count: > 0 } ? [.. images] : null,
					});
				}
			}
			catch (OperationCanceledException)
			{
				// Keep the conversation valid: drop the interrupted assistant/tool step.
				_messages.RemoveRange(assistantIndex, _messages.Count - assistantIndex);
				throw;
			}
		}
	}

	/// <summary>Canonical identity of a call: tool name plus arguments with sorted object keys.</summary>
	private static string ToolCallKey(ToolCall call)
	{
		var builder = new StringBuilder();
		builder.Append(call.Name).Append('\n');

		// "purpose" is a cosmetic label. Ignore it so changing the label cannot bypass the guard.
		if (string.Equals(call.Name, "csharp", StringComparison.OrdinalIgnoreCase) &&
			call.Arguments.ContainsKey("purpose"))
		{
			var effective = (System.Text.Json.Nodes.JsonObject)call.Arguments.DeepClone();
			effective.Remove("purpose");
			AppendCanonical(builder, effective);
			return builder.ToString();
		}

		AppendCanonical(builder, call.Arguments);
		return builder.ToString();
	}

	private static void AppendCanonical(StringBuilder builder, System.Text.Json.Nodes.JsonNode? node)
	{
		switch (node)
		{
			case null:
				builder.Append("null");
				break;
			case System.Text.Json.Nodes.JsonObject obj:
				builder.Append('{');
				var first = true;
				foreach (var pair in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
				{
					if (!first)
					{
						builder.Append(',');
					}
					first = false;
					builder.Append(System.Text.Json.Nodes.JsonValue.Create(pair.Key)!.ToJsonString());
					builder.Append(':');
					AppendCanonical(builder, pair.Value);
				}
				builder.Append('}');
				break;
			case System.Text.Json.Nodes.JsonArray array:
				builder.Append('[');
				for (var index = 0; index < array.Count; index++)
				{
					if (index > 0)
					{
						builder.Append(',');
					}
					AppendCanonical(builder, array[index]);
				}
				builder.Append(']');
				break;
			default:
				builder.Append(node.ToJsonString());
				break;
		}
	}

	private void InjectMessages()
	{
		if (_broker is null)
		{
			return;
		}

		var injected = 0;
		while (injected < 8 && _broker.TryDequeue(out var message))
		{
			injected++;
			var hint = message.WantReply
				? $" Reply with the agent tool's reply action (messageId: \"{message.Id}\")."
				: "";
			_messages.Add(new ChatMessage
			{
				Role = "user",
				Content = $"[message #{message.Id} from {message.From}]{hint}\n{message.Text}",
			});
		}
	}

	/// <summary>Keep the request under the configured context window by dropping complete old turns.</summary>
	private void TrimContext()
	{
		var budgetChars = Math.Max(4096, _config.NumCtx - 2048) * 4;

		while (EstimateChars() > budgetChars && _messages.Count > 4)
		{
			var nextUser = -1;
			for (var index = 1; index < _messages.Count - 2; index++)
			{
				if (_messages[index].Role == "user")
				{
					nextUser = index;
					break;
				}
			}

			if (nextUser <= 1)
			{
				break;
			}

			_messages.RemoveRange(1, nextUser - 1);
		}
	}

	private int EstimateChars() =>
		_messages.Sum(message =>
			(message.Content?.Length ?? 0) +
			(message.Thinking?.Length ?? 0) +
			(message.ToolCalls?.Sum(call => call.Arguments.ToJsonString().Length) ?? 0) +
			// An image costs a fixed number of vision tokens, not its base64 length.
			(message.Images?.Count ?? 0) * ImageTokenCost * 4 +
			64);

	/// <summary>Rough vision-token cost of one image, used only for context trimming.</summary>
	private const int ImageTokenCost = 1024;

	private static string Truncate(string text, int max) =>
		text.Length <= max ? text : text[..max] + $"\n… truncated ({text.Length} characters) …";

	internal string SystemPrompt => _systemPrompt;
}
