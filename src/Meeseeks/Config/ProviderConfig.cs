using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meeseeks.Config;

/// <summary>
/// All provider and harness settings. Stored at ~/.meeseeks/provider.json.
/// </summary>
public sealed class ProviderConfig
{
	/// <summary>Which model backend to use: "ollama" (default) or "anthropic".</summary>
	public string Provider { get; set; } = "ollama";

	public string BaseUrl { get; set; } = "http://127.0.0.1:11434";

	public string Model { get; set; } = "glm-5.3-flash:cloud";

	/// <summary>Optional bearer token for Ollama Cloud or a protected endpoint.</summary>
	public string? ApiKey { get; set; }

	/// <summary>Endpoint behind the `web_search` tool. Needs the Ollama Cloud web-search API.</summary>
	public string WebSearchUrl { get; set; } = "https://ollama.com/api/web_search";

	/// <summary>Thinking output: true, false, or a model-defined level from /api/show.</summary>
	public ThinkSetting? Think { get; set; } = ThinkSetting.On;

	public double Temperature { get; set; } = 0.2;

	public int NumCtx { get; set; } = 32768;

	public int? NumPredict { get; set; }

	public string KeepAlive { get; set; } = "10m";

	public int TimeoutSeconds { get; set; } = 600;

	/// <summary>Wall-clock limit for one subagent process, unless SpawnOptions.TimeoutMs overrides it.</summary>
	public int SubagentTimeoutSeconds { get; set; } = 1800;

	/// <summary>How deep subagents may nest. Root is 0.</summary>
	public int MaxDepth { get; set; } = 2;

	/// <summary>How many subagent processes this agent may run at once.</summary>
	public int MaxParallelSubagents { get; set; } = 4;

	[JsonIgnore]
	public string Endpoint => $"{BaseUrl.TrimEnd('/')}/api/chat";

	public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>Load provider.json, creating a default file when it is missing.</summary>
	public static ProviderConfig LoadOrCreate(out bool created)
	{
		created = false;
		var path = MeeseeksPaths.ProviderFile;

		if (!File.Exists(path))
		{
			var defaults = new ProviderConfig();
			File.WriteAllText(path, JsonSerializer.Serialize(defaults, Json));
			created = true;
			return defaults;
		}

		var text = File.ReadAllText(path);
		var config = JsonSerializer.Deserialize<ProviderConfig>(text, Json);
		if (config is null)
		{
			throw new InvalidOperationException($"Could not read {path}.");
		}

		if (string.IsNullOrWhiteSpace(config.BaseUrl))
		{
			config.BaseUrl = "http://127.0.0.1:11434";
		}
		if (string.IsNullOrWhiteSpace(config.Model))
		{
			throw new InvalidOperationException($"Set \"model\" in {path}.");
		}
		if (string.IsNullOrWhiteSpace(config.WebSearchUrl))
		{
			config.WebSearchUrl = "https://ollama.com/api/web_search";
		}

		return config;
	}

	public void Save()
	{
		MeeseeksPaths.Ensure();
		var path = MeeseeksPaths.ProviderFile;
		var temporary = path + ".tmp";
		File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
		File.Move(temporary, path, overwrite: true);
	}

	public ProviderConfig Clone() => (ProviderConfig)MemberwiseClone();
}
