using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Meeseeks.Config;

/// <summary>
/// Ollama's `think` value: a boolean, a model-defined level such as "high", or null for the
/// model default. Serialized as a JSON boolean or string.
/// </summary>
[JsonConverter(typeof(ThinkSettingConverter))]
public sealed class ThinkSetting : IEquatable<ThinkSetting>
{
	public bool? Enabled { get; init; }
	public string? Level { get; init; }

	public static ThinkSetting On { get; } = new() { Enabled = true };
	public static ThinkSetting Off { get; } = new() { Enabled = false };

	public static ThinkSetting Named(string level) => new() { Level = level };

	public bool IsOff => Enabled == false;
	public bool IsOn => Enabled == true;

	public JsonNode? ToJsonNode() =>
		Level is not null
			? JsonValue.Create(Level)
			: Enabled is bool enabled
				? JsonValue.Create(enabled)
				: null;

	public static ThinkSetting? Parse(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		return value.Trim().ToLowerInvariant() switch
		{
			"on" or "true" => On,
			"off" or "false" => Off,
			"default" or "auto" or "null" => null,
			var level => Named(level),
		};
	}

	public override string ToString() =>
		Enabled == false ? "off" : Enabled == true ? "on" : Level ?? "default";

	public bool Equals(ThinkSetting? other) =>
		other is not null && Enabled == other.Enabled &&
		string.Equals(Level, other.Level, StringComparison.OrdinalIgnoreCase);

	public override bool Equals(object? obj) => Equals(obj as ThinkSetting);

	public override int GetHashCode() => HashCode.Combine(Enabled, Level);
}

public sealed class ThinkSettingConverter : JsonConverter<ThinkSetting>
{
	public override ThinkSetting? Read(
		ref Utf8JsonReader reader,
		Type typeToConvert,
		JsonSerializerOptions options) =>
		reader.TokenType switch
		{
			JsonTokenType.True => ThinkSetting.On,
			JsonTokenType.False => ThinkSetting.Off,
			JsonTokenType.String => ThinkSetting.Named(reader.GetString() ?? ""),
			JsonTokenType.Null => null,
			_ => throw new JsonException("think must be true, false, or a model-defined level name."),
		};

	public override void Write(Utf8JsonWriter writer, ThinkSetting value, JsonSerializerOptions options)
	{
		if (value.Level is not null)
		{
			writer.WriteStringValue(value.Level);
		}
		else if (value.Enabled is bool enabled)
		{
			writer.WriteBooleanValue(enabled);
		}
		else
		{
			writer.WriteNullValue();
		}
	}
}
