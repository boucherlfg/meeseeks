using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meeseeks.Config;
using Meeseeks.Core;

namespace Meeseeks.Providers;

public sealed class OllamaException : Exception
{
	public OllamaException(string message) : base(message) { }
}

public sealed record ChatResult(ChatMessage Message, Usage Usage, string? DoneReason);

public sealed record OllamaModelInfo(
	string Name,
	IReadOnlyList<string> Capabilities,
	string? ParameterSize,
	long? ContextLength);

public sealed record ThinkingSupport(IReadOnlyList<string> Values, string? Default)
{
	public bool SupportsOff => Values.Contains("false", StringComparer.OrdinalIgnoreCase);

	public bool SupportsThinking => Values.Any(value =>
		!string.Equals(value, "false", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Streaming Ollama /api/chat client with tool calling.</summary>
public sealed class OllamaClient : IModelClient
{
	private readonly ProviderConfig _config;
	private readonly HttpClient _http;

	public OllamaClient(ProviderConfig config)
	{
		_config = config;
		_http = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(Math.Max(30, config.TimeoutSeconds)),
		};

		if (!string.IsNullOrWhiteSpace(config.ApiKey))
		{
			_http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
		}
	}

	public async Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(10));

		using var response = await _http
			.GetAsync($"{_config.BaseUrl.TrimEnd('/')}/api/tags", timeout.Token)
			.ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
			throw new OllamaException(
				$"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body.Trim(), 1000)}");
		}

		var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(payload);
		}
		catch (JsonException)
		{
			throw new OllamaException("Ollama returned invalid JSON from /api/tags.");
		}

		if (node?["models"] is not JsonArray models)
		{
			return [];
		}

		var result = new List<OllamaModelInfo>();
		foreach (var model in models.OfType<JsonObject>())
		{
			var name = model["name"]?.GetValue<string>();
			if (string.IsNullOrWhiteSpace(name))
			{
				continue;
			}

			var capabilities = (model["capabilities"] as JsonArray)?
				.Select(capability => capability?.GetValue<string>() ?? "")
				.Where(capability => capability.Length > 0)
				.ToArray() ?? [];

			var details = model["details"] as JsonObject;
			var parameterSize = details?["parameter_size"]?.GetValue<string>();
			long? contextLength = details?["context_length"] is JsonValue contextValue &&
				contextValue.TryGetValue<long>(out var context)
					? context
					: null;

			result.Add(new OllamaModelInfo(name, capabilities, parameterSize, contextLength));
		}

		return result;
	}

	public async Task<ThinkingSupport?> GetThinkingSupportAsync(
		string model,
		CancellationToken cancellationToken = default)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(10));

		var body = new JsonObject { ["model"] = model }.ToJsonString();
		using var content = new StringContent(body, Encoding.UTF8, "application/json");
		using var response = await _http
			.PostAsync($"{_config.BaseUrl.TrimEnd('/')}/api/show", content, timeout.Token)
			.ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			var error = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
			throw new OllamaException(
				$"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(error.Trim(), 1000)}");
		}

		var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(payload);
		}
		catch (JsonException)
		{
			throw new OllamaException("Ollama returned invalid JSON from /api/show.");
		}

		if (node?["thinking"] is not JsonObject thinking)
		{
			return null;
		}

		var values = (thinking["values"] as JsonArray)?
			.Select(ThinkToken)
			.Where(value => value is not null)
			.Select(value => value!)
			.ToArray() ?? [];

		return new ThinkingSupport(values, ThinkToken(thinking["default"]));
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
			catch (OllamaException)
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

		throw new OllamaException($"Could not reach Ollama at {_config.BaseUrl}: {last?.Message ?? "unknown error"}");
	}

	private JsonObject BuildPayload(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools)
	{
		var messageArray = new JsonArray();
		foreach (var message in messages)
		{
			messageArray.Add(ToMessageNode(message));
		}

		var options = new JsonObject
		{
			["temperature"] = _config.Temperature,
			["num_ctx"] = _config.NumCtx,
		};

		if (_config.NumPredict is > 0)
		{
			options["num_predict"] = _config.NumPredict.Value;
		}

		var payload = new JsonObject
		{
			["model"] = _config.Model,
			["messages"] = messageArray,
			["stream"] = true,
			["options"] = options,
		};

		if (!string.IsNullOrWhiteSpace(_config.KeepAlive))
		{
			payload["keep_alive"] = _config.KeepAlive;
		}

		if (_config.Think?.ToJsonNode() is { } think)
		{
			payload["think"] = think;
		}

		if (tools.Count > 0)
		{
			var array = new JsonArray();
			foreach (var tool in tools)
			{
				array.Add(new JsonObject
				{
					["type"] = "function",
					["function"] = new JsonObject
					{
						["name"] = tool.Name,
						["description"] = tool.Description,
						["parameters"] = tool.Parameters.DeepClone(),
					},
				});
			}
			payload["tools"] = array;
		}

		return payload;
	}

	private static JsonObject ToMessageNode(ChatMessage message)
	{
		// Reasoning is display-only. Sending it back is not part of the request contract.
		var node = new JsonObject
		{
			["role"] = message.Role,
			["content"] = message.Content,
		};

		// Ollama's /api/chat takes images as bare base64 strings alongside the text content.
		if (message.Images is { Count: > 0 } images)
		{
			var array = new JsonArray();
			foreach (var image in images)
			{
				array.Add(image.Base64);
			}
			node["images"] = array;
		}

		if (message.Role == "tool")
		{
			if (!string.IsNullOrEmpty(message.ToolName))
			{
				node["tool_name"] = message.ToolName;
			}
			return node;
		}

		if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
		{
			var calls = new JsonArray();
			foreach (var call in message.ToolCalls)
			{
				calls.Add(new JsonObject
				{
					["function"] = new JsonObject
					{
						["name"] = call.Name,
						["arguments"] = call.Arguments.DeepClone(),
					},
				});
			}
			node["tool_calls"] = calls;
		}

		return node;
	}

	private async Task<ChatResult> StreamOnceAsync(
		JsonObject payload,
		IProgressSink sink,
		Action markEmitted,
		CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint)
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
		};

		using var response = await _http
			.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
			.ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			throw new OllamaException(
				$"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body.Trim(), 2000)}");
		}

		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using var reader = new StreamReader(stream, Encoding.UTF8);

		var content = new StringBuilder();
		var thinking = new StringBuilder();
		var toolCalls = new List<ToolCall>();
		var callIndex = 0;
		var promptTokens = 0;
		var completionTokens = 0;
		string? doneReason = null;

		while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			JsonNode? node;
			try
			{
				node = JsonNode.Parse(line);
			}
			catch (JsonException)
			{
				continue;
			}

			if (node is not JsonObject obj)
			{
				continue;
			}

			if (obj["error"] is JsonValue errorValue)
			{
				throw new OllamaException($"Ollama error: {errorValue}");
			}

			if (obj["message"] is JsonObject message)
			{
				if (message["thinking"] is JsonValue thinkingValue && TryGetString(thinkingValue, out var thinkingChunk) &&
					thinkingChunk.Length > 0)
				{
					thinking.Append(thinkingChunk);
					sink.Thinking(thinkingChunk);
					markEmitted();
				}

				if (message["content"] is JsonValue contentValue && TryGetString(contentValue, out var contentChunk) &&
					contentChunk.Length > 0)
				{
					content.Append(contentChunk);
					sink.Text(contentChunk);
					markEmitted();
				}

				if (message["tool_calls"] is JsonArray calls)
				{
					foreach (var nodeCall in calls)
					{
						var parsed = ParseToolCall(nodeCall, ref callIndex);
						if (parsed is not null)
						{
							toolCalls.Add(parsed);
							markEmitted();
						}
					}
				}
			}

			if (obj["done"]?.GetValue<bool>() == true)
			{
				doneReason = TryGetString(obj["done_reason"], out var reason) ? reason : null;
				promptTokens = GetInt(obj["prompt_eval_count"]);
				completionTokens = GetInt(obj["eval_count"]);
				break;
			}
		}

		var assistant = new ChatMessage
		{
			Role = "assistant",
			Content = content.ToString(),
			Thinking = thinking.Length > 0 ? thinking.ToString() : null,
			ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
		};

		return new ChatResult(assistant, new Usage(promptTokens, completionTokens), doneReason);
	}

	private static ToolCall? ParseToolCall(JsonNode? node, ref int index)
	{
		var function = node?["function"];
		if (function is null)
		{
			return null;
		}

		if (!TryGetString(function["name"], out var name) || string.IsNullOrWhiteSpace(name))
		{
			return null;
		}

		var arguments = function["arguments"] switch
		{
			JsonObject obj => (JsonObject)obj.DeepClone(),
			JsonValue value when TryGetString(value, out var text) => ParseArguments(text),
			_ => new JsonObject(),
		};

		var id = TryGetString(node?["id"], out var callId) && !string.IsNullOrWhiteSpace(callId)
			? callId
			: $"call_{++index}";

		return new ToolCall { Id = id, Name = name, Arguments = arguments };
	}

	private static JsonObject ParseArguments(string text)
	{
		try
		{
			return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
		}
		catch (JsonException)
		{
			return new JsonObject();
		}
	}

	private static string? ThinkToken(JsonNode? node)
	{
		if (node is JsonValue value)
		{
			if (value.TryGetValue<bool>(out var flag))
			{
				return flag ? "true" : "false";
			}
			if (value.TryGetValue<string>(out var text))
			{
				return text;
			}
		}
		return null;
	}

	private static bool TryGetString(JsonNode? node, out string value)
	{
		value = "";
		if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
		{
			value = text;
			return true;
		}
		return false;
	}

	private static int GetInt(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

	private static string Truncate(string text, int max) =>
		text.Length <= max ? text : text[..max] + "…";

	public void Dispose() => _http.Dispose();
}
