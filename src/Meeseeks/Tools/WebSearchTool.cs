using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meeseeks.Config;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>Search the web through the Ollama Cloud web-search API.</summary>
public sealed class WebSearchTool
{
	public const string ToolName = "web_search";

	private const int DefaultResults = 5;
	private const int MaxResults = 10;

	private readonly ProviderConfig _config;

	public WebSearchTool(ProviderConfig config) => _config = config;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Search the web and return ranked results with a content snippet for each.

			Uses the Ollama Cloud web-search API (`POST https://ollama.com/api/web_search`) with the `apiKey` from `~/.meeseeks/provider.json`. Returns up to 10 results as `title`, `url`, and `content`. Follow up with `web_fetch` when a snippet is not enough.

			Fails with a clear message when no `apiKey` is configured, so check that first if you see an authorization error.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["query"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Search query.",
				},
				["maxResults"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "Maximum results to return. Defaults to 5; the API caps it at 10.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			["required"] = new JsonArray("query"),
		},
	};

	public async Task<ToolExecutionResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
	{
		var query = ToolArgs.Text(call, "query")
			?? throw new MeeseeksException("web_search requires a \"query\" string.");

		if (string.IsNullOrWhiteSpace(_config.ApiKey))
		{
			throw new MeeseeksException(
				$"web_search needs an Ollama API key. Set \"apiKey\" in {MeeseeksPaths.ProviderFile} " +
				"(create one at https://ollama.com/settings/keys), or use `web_fetch` on a URL you already know.");
		}

		var max = Math.Clamp(ToolArgs.Number(call, "maxResults") ?? DefaultResults, 1, MaxResults);

		var payload = new JsonObject
		{
			["query"] = query,
			["max_results"] = max,
		};

		using var request = new HttpRequestMessage(HttpMethod.Post, _config.WebSearchUrl)
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

		HttpResponseMessage response;
		try
		{
			response = await WebClient.Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			throw new MeeseeksException($"web_search could not reach {_config.WebSearchUrl}: {WebClient.Describe(ex)}");
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new MeeseeksException($"web_search timed out after 30s contacting {_config.WebSearchUrl}.");
		}

		using (response)
		{
			var body = await WebClient
				.ReadBoundedAsync(response, 2 * 1024 * 1024, cancellationToken)
				.ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				var detail = WebClient.OneLine(body, 300);
				var hint = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
					? " Check that \"apiKey\" in provider.json is a valid Ollama key with web-search access."
					: "";
				throw new MeeseeksException(
					$"web_search failed: HTTP {(int)response.StatusCode} {response.StatusCode}.{hint}" +
					(detail.Length > 0 ? $"\n{detail}" : ""));
			}

			return new ToolExecutionResult(Format(query, body), false);
		}
	}

	private static string Format(string query, string body)
	{
		JsonNode? parsed;
		try
		{
			parsed = JsonNode.Parse(body);
		}
		catch (JsonException ex)
		{
			throw new MeeseeksException($"web_search returned a response that is not JSON: {ex.Message}");
		}

		if (parsed?["results"] is not JsonArray results || results.Count == 0)
		{
			return $"No results for \"{query}\".";
		}

		var builder = new StringBuilder();
		builder.Append("Results for \"").Append(query).Append("\"  ·  ").Append(results.Count)
			.Append(results.Count == 1 ? " result" : " results").AppendLine();

		var index = 0;
		foreach (var node in results)
		{
			if (node is not JsonObject result)
			{
				continue;
			}

			index++;
			var title = result["title"]?.GetValue<string>() ?? "(untitled)";
			var url = result["url"]?.GetValue<string>() ?? "";
			var content = result["content"]?.GetValue<string>() ?? "";

			builder.AppendLine();
			builder.Append(index).Append(". ").AppendLine(title.Trim());
			if (url.Length > 0)
			{
				builder.Append("   ").AppendLine(url.Trim());
			}
			if (content.Length > 0)
			{
				builder.Append("   ").AppendLine(WebClient.OneLine(content, 400));
			}
		}

		return builder.ToString().TrimEnd();
	}
}
