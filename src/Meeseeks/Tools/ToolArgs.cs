using System.Text.Json.Nodes;
using Meeseeks.Core;

namespace Meeseeks.Tools;

/// <summary>Argument readers shared by the file-search tools.</summary>
internal static class ToolArgs
{
	public static string? Text(ToolCall call, string name) =>
		call.Arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
			? text
			: null;

	public static bool Flag(ToolCall call, string name) =>
		call.Arguments[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

	/// <summary>The one-line `summary` every tool accepts, shown in the console while the call runs.</summary>
	public static string? Summary(ToolCall call) => Text(call, "summary");

	public static int? Number(ToolCall call, string name)
	{
		if (call.Arguments[name] is not JsonValue value)
		{
			return null;
		}

		if (value.TryGetValue<int>(out var integer))
		{
			return integer;
		}

		return value.TryGetValue<double>(out var real) ? (int)real : null;
	}
}