using System.Text;
using System.Text.Json.Nodes;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>Fetch a URL and return it as readable text.</summary>
public sealed class WebFetchTool
{
	public const string ToolName = "web_fetch";

	private const int DefaultChars = 20_000;
	private const int MaxChars = 200_000;
	private const int MaxBytes = 4 * 1024 * 1024;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Fetch a URL over HTTP and return its content as text. HTML is reduced to readable text; JSON, XML, and plain text are returned as-is.

			Follows up to 10 redirects and sends a browser user agent. `maxChars` caps the returned text (default 20000, max 200000); the header says how much was cut. Bodies over 4 MB are truncated. Use `web_search` first when you do not have a URL.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["url"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Absolute http or https URL to fetch.",
				},
				["maxChars"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "Maximum characters of text to return. Defaults to 20000; the maximum is 200000.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			["required"] = new JsonArray("url"),
		},
	};

	public async Task<ToolExecutionResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
	{
		var raw = ToolArgs.Text(call, "url") ?? throw new MeeseeksException("web_fetch requires a \"url\" string.");

		if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) ||
			(uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
		{
			throw new MeeseeksException($"web_fetch: \"{raw}\" is not an absolute http or https URL.");
		}

		var maxChars = Math.Clamp(ToolArgs.Number(call, "maxChars") ?? DefaultChars, 500, MaxChars);

		using var request = new HttpRequestMessage(HttpMethod.Get, uri);
		request.Headers.Accept.ParseAdd(
			"text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,application/json;q=0.7,*/*;q=0.5");

		HttpResponseMessage response;
		try
		{
			response = await WebClient.Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			throw new MeeseeksException($"web_fetch could not reach {uri}: {WebClient.Describe(ex)}");
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new MeeseeksException($"web_fetch timed out after 30s fetching {uri}.");
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				throw new MeeseeksException(
					$"web_fetch: {uri} returned HTTP {(int)response.StatusCode} {response.StatusCode}.");
			}

			var body = await WebClient
				.ReadBoundedAsync(response, MaxBytes, cancellationToken)
				.ConfigureAwait(false);

			var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
			var text = WebClient.LooksLikeHtml(contentType, body) ? WebClient.HtmlToText(body) : body.Trim();

			if (text.Length == 0)
			{
				return new ToolExecutionResult($"{uri} returned no readable text ({contentType}).", false);
			}

			var builder = new StringBuilder();
			builder.Append(uri).Append("  ·  ");
			if (contentType.Length > 0)
			{
				builder.Append(contentType).Append("  ·  ");
			}
			builder.Append(text.Length).Append(" chars");
			if (response.RequestMessage?.RequestUri is { } final && final != uri)
			{
				builder.Append("  ·  redirected to ").Append(final);
			}
			builder.AppendLine().AppendLine();

			if (text.Length > maxChars)
			{
				builder.Append(text[..maxChars]);
				builder.AppendLine();
				builder.AppendLine($"[truncated: showing {maxChars} of {text.Length} chars; raise maxChars for more]");
			}
			else
			{
				builder.Append(text);
			}

			return new ToolExecutionResult(builder.ToString().TrimEnd(), false);
		}
	}
}
