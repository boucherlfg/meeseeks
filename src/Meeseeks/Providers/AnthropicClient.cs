using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meeseeks.Config;
using Meeseeks.Core;

namespace Meeseeks.Providers;

public sealed class AnthropicException : Exception
{
	public AnthropicException(string message) : base(message) { }
}

/// <summary>
/// Streaming Anthropic Messages API client (POST /v1/messages, SSE) with tool calling.
/// Maps meeseeks's neutral message model to Anthropic content blocks and back.
/// </summary>
public sealed class AnthropicClient : IModelClient
{
	private const string ApiVersion = "2023-06-01";
	private const string DefaultBaseUrl = "https://api.anthropic.com";

	private readonly ProviderConfig _config;
	private readonly HttpClient _http;

	public AnthropicClient(ProviderConfig config)
	{
		_config = config;
		_http = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(Math.Max(30, config.TimeoutSeconds)),
		};

		var key = config.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
		if (string.IsNullOrWhiteSpace(key))
		{
			throw new AnthropicException(
				"Anthropic provider needs an API key: set \"apiKey\" in ~/.meeseeks/provider.json or the ANTHROPIC_API_KEY environment variable.");
		}
		_http.DefaultRequestHeaders.Add("x-api-key", key);
		_http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
	}

	public async Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(10));

		using var response = await _http
			.GetAsync($"{BaseUrl}/v1/models?limit=100", timeout.Token)
			.ConfigureAwait(false);

		var payload = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(payload);
		}
		catch (JsonException)
		{
			throw new AnthropicException("Anthropic returned invalid JSON from /v1/models.");
		}

		if (node?["data"] is not JsonArray models)
		{
			return [];
		}

		var result = new List<OllamaModelInfo>();
		foreach (var model in models.OfType<JsonObject>())
		{
			var name = model["id"]?.GetValue<string>();
			if (string.IsNullOrWhiteSpace(name))
			{
				continue;
			}

			// Anthropic does not report capabilities per model; vision-capable names are a hint only.
			var capabilities = name.Contains("vision", StringComparison.OrdinalIgnoreCase)
				? (IReadOnlyList<string>)["vision"]
				: [];
			result.Add(new OllamaModelInfo(name, capabilities, null, null));
		}

		return result;
	}

	public Task<ThinkingSupport?> GetThinkingSupportAsync(string model, CancellationToken cancellationToken = default)
	{
		// Anthropic models either support extended thinking or ignore it; there is no discovery endpoint.
		var supports = model.Contains("sonnet", StringComparison.OrdinalIgnoreCase) ||
			model.Contains("opus", StringComparison.OrdinalIgnoreCase) ||
			model.Contains("haiku", StringComparison.OrdinalIgnoreCase);
		return Task.FromResult<ThinkingSupport?>(supports
			? new ThinkingSupport(["true", "false"], "false")
			: null);
	}

	public async Task<ChatResult> ChatAsync(
		IReadOnlyList<ChatMessage> messages,
		IReadOnlyList<ToolDefinition> tools,
		IProgressSink sink,
		CancellationToken cancellationToken)
	{
		var payload = BuildPayload(messages, tools);
		Exception? last = null;

		for (var attempt = 0; attempt < 3; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var emitted = false;

			try
			{
				return await StreamOnceAsync(payload, sink, () => emitted = true, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (AnthropicException)
			{
				throw;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex) when (!emitted && attempt < 2 && ex is HttpRequestException or IOException)
			{
				last = ex;
				await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), cancellationToken)
					.ConfigureAwait(false);
			}
		}

		throw new AnthropicException($"Could not reach Anthropic at {BaseUrl}: {last?.Message ?? "unknown error"}");
	}

	private string BaseUrl => (_config.BaseUrl is null || _config.BaseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
		? _config.BaseUrl ?? DefaultBaseUrl
		: DefaultBaseUrl).TrimEnd('/');

	private JsonObject BuildPayload(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools)
	{
		// Anthropic takes the system prompt as a top-level field, not a message.
		string system = "";
		var turns = new JsonArray();
		// Anthropic requires alternating user/assistant turns; consecutive same-role messages
		// (tool results, split assistant output) are merged into one turn's content blocks.
		var currentRole = "";
		var blocks = new List<JsonObject>();

		void Flush()
		{
			if (blocks.Count == 0)
			{
				return;
			}
			turns.Add(new JsonObject
			{
				["role"] = currentRole,
				["content"] = new JsonArray([.. blocks.Cast<JsonNode>()]),
			});
			blocks.Clear();
		}

		void Add(string role, JsonObject block)
		{
			if (currentRole != role)
			{
				Flush();
				currentRole = role;
			}
			blocks.Add(block);
		}

		foreach (var message in messages)
		{
			switch (message.Role)
			{
				case "system":
					system = message.Content;
					break;

				case "user":
					Add("user", UserTextBlock(message.Content));
					foreach (var image in message.Images ?? [])
					{
						Add("user", ImageBlock(image));
					}
					break;

				case "assistant":
					if (!string.IsNullOrEmpty(message.Thinking) && _config.Think is not { IsOff: true })
					{
						// Replay thinking as a block so tool_use stays valid inside the turn.
						Add("assistant", new JsonObject
						{
							["type"] = "thinking",
							["thinking"] = message.Thinking,
						});
					}
					if (message.Content.Length > 0)
					{
						Add("assistant", new JsonObject { ["type"] = "text", ["text"] = message.Content });
					}
					foreach (var call in message.ToolCalls ?? [])
					{
						Add("assistant", new JsonObject
						{
							["type"] = "tool_use",
							["id"] = call.Id,
							["name"] = call.Name,
							["input"] = call.Arguments.DeepClone(),
						});
					}
					break;

				case "tool":
					// Tool results ride in a user turn as tool_result blocks.
					Add("user", new JsonObject
					{
						["type"] = "tool_result",
						["tool_use_id"] = message.ToolCallId ?? message.ToolName ?? "unknown",
						["content"] = message.Content,
					});
					foreach (var image in message.Images ?? [])
					{
						Add("user", ImageBlock(image));
					}
					break;
			}
		}
		Flush();

		var payload = new JsonObject
		{
			["model"] = _config.Model,
			["max_tokens"] = _config.NumPredict is > 0 ? _config.NumPredict.Value : 8192,
			["messages"] = turns,
			["stream"] = true,
		};

		if (system.Length > 0)
		{
			payload["system"] = system;
		}

		if (_config.Think is { IsOff: false } think)
		{
			// Extended thinking requires a budget; map the setting to tokens.
			var budget = think.Level?.ToLowerInvariant() switch
			{
				"low" => 2048,
				"high" => 16384,
				_ => 8192,
			};
			payload["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
		}

		if (tools.Count > 0)
		{
			var array = new JsonArray();
			foreach (var tool in tools)
			{
				array.Add(new JsonObject
				{
					["name"] = tool.Name,
					["description"] = tool.Description,
					["input_schema"] = tool.Parameters.DeepClone(),
				});
			}
			payload["tools"] = array;
		}

		return payload;
	}

	private static JsonObject UserTextBlock(string text) => new()
	{
		["type"] = "text",
		["text"] = text.Length == 0 ? "(empty)" : text,
	};

	private static JsonObject ImageBlock(ImageAttachment image) => new()
	{
		["type"] = "image",
		["source"] = new JsonObject
		{
			["type"] = "base64",
			["media_type"] = string.IsNullOrWhiteSpace(image.MediaType) ? "image/png" : image.MediaType,
			["data"] = image.Base64,
		},
	};

	private async Task<ChatResult> StreamOnceAsync(
		JsonObject payload,
		IProgressSink sink,
		Action markEmitted,
		CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages")
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);

		using var response = await _http
			.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
			.ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			throw new AnthropicException(
				$"Anthropic returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body.Trim(), 2000)}");
		}

		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using var reader = new StreamReader(stream, Encoding.UTF8);

		var content = new StringBuilder();
		var thinking = new StringBuilder();
		var toolCalls = new List<ToolCall>();
		var promptTokens = 0;
		var completionTokens = 0;
		string? doneReason = null;
		// Partial tool_use JSON arrives across input_json_delta events.
		var toolIndex = -1;
		var toolId = "";
		var toolName = "";
		var toolJson = new StringBuilder();

		while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
		{
			if (line.Length == 0)
			{
				continue;
			}

			if (!line.StartsWith("data:", StringComparison.Ordinal))
			{
				continue; // event: lines carry no payload we need
			}

			var data = line[5..].Trim();
			if (data.Length == 0 || data == "[DONE]")
			{
				continue;
			}

			JsonNode? node;
			try
			{
				node = JsonNode.Parse(data);
			}
			catch (JsonException)
			{
				continue;
			}

			if (node is not JsonObject obj)
			{
				continue;
			}

			var type = obj["type"]?.GetValue<string>();
			switch (type)
			{
				case "error":
					throw new AnthropicException(
						$"Anthropic error: {obj["error"]?["message"]?.GetValue<string>() ?? obj.ToJsonString()}");

				case "content_block_start":
					var startBlock = obj["content_block"] as JsonObject;
					if (startBlock?["type"]?.GetValue<string>() == "tool_use")
					{
						FlushToolBlock();
						toolIndex = obj["index"]?.GetValue<int>() ?? -1;
						toolId = startBlock["id"]?.GetValue<string>() ?? "";
						toolName = startBlock["name"]?.GetValue<string>() ?? "";
						toolJson.Clear();
					}
					break;

				case "content_block_delta":
					var delta = obj["delta"] as JsonObject;
					switch (delta?["type"]?.GetValue<string>())
					{
						case "thinking_delta":
							var thinkingChunk = delta["thinking"]?.GetValue<string>() ?? "";
							if (thinkingChunk.Length > 0)
							{
								thinking.Append(thinkingChunk);
								sink.Thinking(thinkingChunk);
								markEmitted();
							}
							break;

						case "text_delta":
							var textChunk = delta["text"]?.GetValue<string>() ?? "";
							if (textChunk.Length > 0)
							{
								content.Append(textChunk);
								sink.Text(textChunk);
								markEmitted();
							}
							break;

						case "input_json_delta":
							toolJson.Append(delta["partial_json"]?.GetValue<string>() ?? "");
							break;
					}
					break;

				case "content_block_stop":
					if (obj["index"]?.GetValue<int>() == toolIndex)
					{
						FlushToolBlock();
					}
					break;

				case "message_start":
					promptTokens = obj["message"]?["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
					break;

				case "message_delta":
					completionTokens = obj["usage"]?["output_tokens"]?.GetValue<int>() ?? completionTokens;
					doneReason = obj["delta"]?["stop_reason"]?.GetValue<string>() ?? doneReason;
					break;
			}
		}
		FlushToolBlock();

		var assistant = new ChatMessage
		{
			Role = "assistant",
			Content = content.ToString(),
			Thinking = thinking.Length > 0 ? thinking.ToString() : null,
			ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
		};

		return new ChatResult(assistant, new Usage(promptTokens, completionTokens), doneReason);

		void FlushToolBlock()
		{
			if (toolIndex < 0 || string.IsNullOrWhiteSpace(toolName))
			{
				toolIndex = -1;
				return;
			}

			JsonObject arguments;
			try
			{
				arguments = JsonNode.Parse(toolJson.Length == 0 ? "{}" : toolJson.ToString()) as JsonObject
					?? new JsonObject();
			}
			catch (JsonException)
			{
				arguments = new JsonObject();
			}

			toolCalls.Add(new ToolCall
			{
				Id = string.IsNullOrWhiteSpace(toolId) ? $"call_{toolCalls.Count + 1}" : toolId,
				Name = toolName,
				Arguments = arguments,
			});

			toolIndex = -1;
			toolId = "";
			toolName = "";
			toolJson.Clear();
		}
	}

	private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new AnthropicException(
				$"Anthropic returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body.Trim(), 1000)}");
		}
		return body;
	}

	private static string Truncate(string text, int max) =>
		text.Length <= max ? text : text[..max] + "…";

	public void Dispose() => _http.Dispose();
}