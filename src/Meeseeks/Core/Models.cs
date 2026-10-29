using System.Text.Json.Nodes;

namespace Meeseeks.Core;

public sealed class ToolCall
{
	public required string Id { get; init; }
	public required string Name { get; init; }
	public required JsonObject Arguments { get; init; }
}

/// <summary>
/// One image attached to a message. Ollama takes images as bare base64 strings on the message,
/// so the media type is kept only for display and for re-encoding when a source is re-read.
/// </summary>
public sealed class ImageAttachment
{
	/// <summary>Base64 of the raw image bytes, without a data-URL prefix.</summary>
	public required string Base64 { get; init; }

	/// <summary>Media type such as image/png, when known.</summary>
	public string? MediaType { get; init; }

	/// <summary>Where the image came from: a path, a URL, or a short label. Display only.</summary>
	public string? Source { get; init; }

	public int ByteCount => Base64.Length * 3 / 4;

	public static ImageAttachment FromBytes(byte[] bytes, string? mediaType, string? source) => new()
	{
		Base64 = Convert.ToBase64String(bytes),
		MediaType = mediaType,
		Source = source,
	};

	public byte[] ToBytes() => Convert.FromBase64String(Base64);
}

public sealed class ChatMessage
{
	public required string Role { get; init; }
	public string Content { get; set; } = "";

	/// <summary>Reasoning text. Displayed, never sent back to the model.</summary>
	public string? Thinking { get; set; }

	public List<ToolCall>? ToolCalls { get; set; }

	/// <summary>Set on role "tool" messages.</summary>
	public string? ToolName { get; set; }

	/// <summary>Set on role "tool" messages. Providers that require tool-call ids (Anthropic) echo it back; others ignore it.</summary>
	public string? ToolCallId { get; set; }

	/// <summary>Images attached to a user message. Sent to the model as Ollama's `images` array.</summary>
	public List<ImageAttachment>? Images { get; set; }
}

public sealed class ToolDefinition
{
	public required string Name { get; init; }
	public required string Description { get; init; }
	public required JsonObject Parameters { get; init; }
}

public readonly record struct Usage(int PromptTokens, int CompletionTokens)
{
	public int Total => PromptTokens + CompletionTokens;

	public static Usage operator +(Usage left, Usage right) =>
		new(left.PromptTokens + right.PromptTokens, left.CompletionTokens + right.CompletionTokens);

	public static Usage operator -(Usage left, Usage right) =>
		new(left.PromptTokens - right.PromptTokens, left.CompletionTokens - right.CompletionTokens);
}

public sealed record TurnResult(string Text, Usage Usage, int Steps, int ToolCalls);

/// <summary>The outcome of one tool call: transcript text plus optional image attachments.</summary>
public sealed record ToolExecutionResult(string Text, bool IsError, IReadOnlyList<ImageAttachment>? Images = null);

public interface IProgressSink
{
	void Text(string delta);
	void Thinking(string delta);
	void ToolStart(ToolCall call);
	void ToolEnd(ToolCall call, bool ok, TimeSpan elapsed);
	void TurnEnd(string text, Usage usage, TimeSpan elapsed);
	void Notice(string text);
}

public sealed class NullProgressSink : IProgressSink
{
	public static readonly NullProgressSink Instance = new();
	public void Text(string delta) { }
	public void Thinking(string delta) { }
	public void ToolStart(ToolCall call) { }
	public void ToolEnd(ToolCall call, bool ok, TimeSpan elapsed) { }
	public void TurnEnd(string text, Usage usage, TimeSpan elapsed) { }
	public void Notice(string text) { }
}

public sealed class MeeseeksException : Exception
{
	public MeeseeksException(string message) : base(message) { }
	public MeeseeksException(string message, Exception inner) : base(message, inner) { }
}
