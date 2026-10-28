using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Meeseeks.Core;

namespace Meeseeks.Tools;

/// <summary>
/// HTTP plumbing shared by the web tools: one pooled client with a browser user agent, a
/// bounded body read, and HTML-to-text extraction.
/// </summary>
internal static class WebClient
{
	public const string UserAgent =
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

	private static readonly Lazy<HttpClient> Shared = new(Create, isThreadSafe: true);

	public static HttpClient Http => Shared.Value;

	private static HttpClient Create()
	{
		var handler = new HttpClientHandler
		{
			AllowAutoRedirect = true,
			MaxAutomaticRedirections = 10,
			AutomaticDecompression = DecompressionMethods.All,
		};

		var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
		client.DefaultRequestHeaders.Accept.ParseAdd(
			"text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.5");
		client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
		return client;
	}

	/// <summary>Read at most <paramref name="maxBytes"/> of the body, decoding by charset.</summary>
	public static async Task<string> ReadBoundedAsync(
		HttpResponseMessage response,
		int maxBytes,
		CancellationToken cancellationToken)
	{
		var bytes = await ReadBytesBoundedAsync(response, maxBytes, cancellationToken).ConfigureAwait(false);

		var charset = response.Content.Headers.ContentType?.CharSet;
		Encoding encoding = Encoding.UTF8;
		if (!string.IsNullOrWhiteSpace(charset))
		{
			try
			{
				encoding = Encoding.GetEncoding(charset.Trim('"'));
			}
			catch (ArgumentException)
			{
				// Unknown charset: UTF-8 is the safe default.
			}
		}

		return encoding.GetString(bytes);
	}

	/// <summary>Read at most <paramref name="maxBytes"/> of the body as raw bytes.</summary>
	public static async Task<byte[]> ReadBytesBoundedAsync(
		HttpResponseMessage response,
		int maxBytes,
		CancellationToken cancellationToken)
	{
		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		var buffer = new byte[8192];
		using var collected = new MemoryStream();
		var total = 0;

		while (total < maxBytes)
		{
			var read = await stream
				.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - total)), cancellationToken)
				.ConfigureAwait(false);
			if (read == 0)
			{
				break;
			}
			collected.Write(buffer, 0, read);
			total += read;
		}

		return collected.ToArray();
	}

	public static bool LooksLikeHtml(string contentType, string body) =>
		contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
		body.TrimStart().StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
		body.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Class and id fragments that mark an element as page chrome rather than content. Matching
	/// these lets one rule drop sidebars, menus, and tables of contents on any site.
	/// </summary>
	private static readonly Regex ChromeMarker = new(
		"\\b(nav|navbar|navigation|menu|dropdown|sidebar|side-bar|toc|breadcrumb|interlanguage|" +
		"mw-portlet|vector-|footer|header|masthead|banner|cookie|consent|share|social|related|" +
		"advert|ads|promo|newsletter|subscribe|skip-link|screen-reader)\\b",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>Strip a page down to readable text: drop script/style/nav noise, keep block breaks.</summary>
	public static string HtmlToText(string html)
	{
		var text = Regex.Replace(html, "<!--.*?-->", " ", RegexOptions.Singleline);
		text = Regex.Replace(
			text,
			"<(script|style|noscript|svg|head|nav|footer|form|iframe)\\b[^>]*>.*?</\\1\\s*>",
			" ",
			RegexOptions.Singleline | RegexOptions.IgnoreCase);

		text = StripChrome(text);

		// Block-level tags become line breaks so paragraphs survive.
		text = Regex.Replace(
			text,
			"</?(p|div|br|li|tr|h[1-6]|section|article|header|blockquote|pre|table|ul|ol|dl|dt|dd)\\b[^>]*>",
			"\n",
			RegexOptions.IgnoreCase);

		text = Regex.Replace(text, "<[^>]+>", " ");
		text = WebUtility.HtmlDecode(text);
		text = text.Replace('\u00a0', ' ');

		var lines = text
			.Split('\n')
			.Select(line => Regex.Replace(line, "[ \t\u000b\f\r]+", " ").Trim())
			.Where(line => line.Length > 0);

		return Regex.Replace(string.Join("\n", lines), "\n{3,}", "\n\n").Trim();
	}

	/// <summary>
	/// Remove elements whose class or id marks them as chrome, together with everything nested
	/// inside them. Tracks nesting so a matched container takes its whole subtree with it.
	/// </summary>
	private static string StripChrome(string html)
	{
		var builder = new StringBuilder(html.Length);
		var position = 0;
		var skipDepth = 0;
		string? skipTag = null;

		foreach (Match match in Regex.Matches(html, "<(/?)([a-zA-Z][a-zA-Z0-9]*)\\b[^>]*>"))
		{
			var closing = match.Groups[1].Value == "/";
			var tag = match.Groups[2].Value.ToLowerInvariant();

			if (skipDepth > 0)
			{
				if (tag == skipTag)
				{
					skipDepth += closing ? -1 : 1;
				}
				position = match.Index + match.Length;
				continue;
			}

			if (!closing && IsChrome(match.Value, tag))
			{
				// Keep everything up to this tag, then drop the element and its subtree.
				builder.Append(html, position, match.Index - position);
				skipTag = tag;
				skipDepth = 1;
				position = match.Index + match.Length;
				continue;
			}

			// Ordinary tag: keep it and the text before it.
			builder.Append(html, position, match.Index + match.Length - position);
			position = match.Index + match.Length;
		}

		if (skipDepth > 0)
		{
			// Unbalanced markup: keep what we had rather than dropping the rest of the page.
			return builder.ToString();
		}

		builder.Append(html, position, html.Length - position);
		return builder.ToString();
	}

	private static bool IsChrome(string openTag, string tag)
	{
		// The document shell carries site-wide classes (Wikipedia's <html> has `vector-feature-*`),
		// so matching it would discard the whole page.
		if (tag is "html" or "body" or "head" or "main" or "article")
		{
			return false;
		}

		var classMatch = Regex.Match(openTag, "\\b(?:class|id)\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase);
		return classMatch.Success && ChromeMarker.IsMatch(classMatch.Groups[1].Value);
	}

	/// <summary>Collapse a snippet to one line for compact search output.</summary>
	public static string OneLine(string text, int max)
	{
		var line = Regex.Replace(text, "\\s+", " ").Trim();
		return line.Length <= max ? line : line[..max].TrimEnd() + "…";
	}

	public static string Describe(HttpRequestException exception) => exception switch
	{
		{ StatusCode: { } status } => $"HTTP {(int)status} {status}",
		{ InnerException: { } inner } => $"{exception.Message} ({inner.Message})",
		_ => exception.Message,
	};
}
