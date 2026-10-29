using Meeseeks.Config;
using Meeseeks.Core;

namespace Meeseeks.Providers;

/// <summary>
/// A chat-completion backend. Implementations translate meeseeks's neutral message and tool
/// model into a provider's wire format and stream the reply back through an IProgressSink.
/// OllamaClient is the built-in implementation; add one class per provider here.
/// </summary>
public interface IModelClient : IDisposable
{
	/// <summary>One streaming chat turn with tool definitions available.</summary>
	Task<ChatResult> ChatAsync(
		IReadOnlyList<ChatMessage> messages,
		IReadOnlyList<ToolDefinition> tools,
		IProgressSink sink,
		CancellationToken cancellationToken);

	/// <summary>List models the provider offers, for the /model command. May throw when unsupported.</summary>
	Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);

	/// <summary>Thinking levels a model supports, or null when it advertises none.</summary>
	Task<ThinkingSupport?> GetThinkingSupportAsync(string model, CancellationToken cancellationToken = default);
}

/// <summary>Creates the model client configured in provider.json.</summary>
public static class ModelClientFactory
{
	public static IModelClient Create(ProviderConfig config) => config.Provider?.Trim().ToLowerInvariant() switch
	{
		"anthropic" => new AnthropicClient(config),
		_ => new OllamaClient(config),
	};
}