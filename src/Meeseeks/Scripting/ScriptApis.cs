using System.Text.Json.Nodes;

namespace Meeseeks.Scripting;

[Api("Facts about this agent and its session.")]
public sealed class SessionApi
{
	private readonly ScriptRuntime _runtime;

	internal SessionApi(ScriptRuntime runtime) => _runtime = runtime;

	[Api("This agent's id.", Order = 0)]
	public string AgentId => _runtime.AgentId;

	[Api("Working directory for scripts.", Order = 1)]
	public string Cwd => _runtime.Cwd;

	[Api("This agent's depth; root is 0.", Order = 2)]
	public int Depth => _runtime.Depth;

	[Api("Deepest allowed subagent nesting.", Order = 3)]
	public int MaxDepth => _runtime.MaxDepth;

	[Api("Ollama model name.", Order = 4)]
	public string Model => _runtime.Model;

	[Api("Cancelled when the current turn is aborted or times out.", Order = 5)]
	public CancellationToken CancellationToken => _runtime.CancellationToken;
}

[Api("Globals available in every script.")]
public sealed class ScriptGlobals
{
	[Api("Facts about this agent and its session.", Order = 1)]
	public SessionApi Session { get; }

	internal ScriptGlobals(ScriptRuntime runtime)
	{
		Session = new SessionApi(runtime);
	}

	[Api("Write a value to the tool output. Objects print as indented JSON.", Order = 0)]
	public void Print(object? value) => Console.WriteLine(Format(value));

	[Api("Write \"label: value\" to the tool output.", Order = 0)]
	public void Print(string label, object? value) => Console.WriteLine($"{label}: {Format(value)}");

	public static string Format(object? value) => value switch
	{
		null => "null",
		string text => text,
		JsonNode node => node.ToJsonString(ToolText.Pretty),
		_ => System.Text.Json.JsonSerializer.Serialize(value, ToolText.Pretty),
	};
}