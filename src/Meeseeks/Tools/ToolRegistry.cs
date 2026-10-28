using Meeseeks.Config;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>
/// The model-facing toolbelt. `csharp` is the general-purpose tool: everything else a harness
/// would expose as a tool is reachable from a script through <see cref="ScriptGlobals"/>.
/// `read`, `write`, and `edit` are fast paths for the three most common scripts, so touching a
/// file costs one tool call instead of a compile-and-run round trip; `glob` and `grep` do the
/// same for searching.
/// </summary>
public sealed class ToolRegistry
{
	public const string CsharpName = "csharp";

	private readonly CsharpTool _csharp;
	private readonly ReadTool _read;
	private readonly WriteTool _write;
	private readonly EditTool _edit;
	private readonly PatchTool _patch;
	private readonly GlobTool _glob;
	private readonly GrepTool _grep;
	private readonly WebSearchTool _webSearch;
	private readonly WebFetchTool _webFetch;

	public ToolRegistry(ScriptRuntime runtime, ProviderConfig config)
	{
		_csharp = new CsharpTool(runtime, config);
		_read = new ReadTool(runtime);
		_write = new WriteTool(runtime);
		_edit = new EditTool(runtime);
		_patch = new PatchTool(runtime);
		_glob = new GlobTool(runtime);
		_grep = new GrepTool(runtime);
		_webSearch = new WebSearchTool(config);
		_webFetch = new WebFetchTool();
	}

	/// <summary>Every tool the model can call.</summary>
	public IReadOnlyList<ToolDefinition> Definitions =>
	[
		_csharp.Definition,
		_read.Definition,
		_write.Definition,
		_edit.Definition,
		_patch.Definition,
		_glob.Definition,
		_grep.Definition,
		_webSearch.Definition,
		_webFetch.Definition,
	];

	public bool IsCsharp(string name) =>
		string.Equals(name, CsharpName, StringComparison.OrdinalIgnoreCase);

	public async Task<ToolExecutionResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
	{
		if (IsCsharp(call.Name))
		{
			return await _csharp.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
		}

		if (string.Equals(call.Name, ReadTool.ToolName, StringComparison.OrdinalIgnoreCase))
		{
			return _read.Execute(call);
		}

		if (string.Equals(call.Name, WriteTool.ToolName, StringComparison.OrdinalIgnoreCase))
		{
			return _write.Execute(call);
		}

		if (string.Equals(call.Name, EditTool.ToolName, StringComparison.OrdinalIgnoreCase))
		{
			return _edit.Execute(call);
		}

		if (string.Equals(call.Name, PatchTool.ToolName, StringComparison.OrdinalIgnoreCase))
		{
			return _patch.Execute(call);
		}

		if (string.Equals(call.Name, GlobTool.ToolName, StringComparison.OrdinalIgnoreCase))
		{
			return _glob.Execute(call);
		}

		if (string.Equals(call.Name, GrepTool.ToolName, StringComparison.OrdinalIgnoreCase))
		{
			return _grep.Execute(call);
		}

		if (string.Equals(call.Name, WebSearchTool.ToolName, StringComparison.OrdinalIgnoreCase))
		{
			return await _webSearch.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
		}

		if (string.Equals(call.Name, WebFetchTool.ToolName, StringComparison.OrdinalIgnoreCase))
		{
			return await _webFetch.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
		}

		throw new MeeseeksException(
			$"Unknown tool \"{call.Name}\". Available: {string.Join(", ", Definitions.Select(definition => definition.Name))}.");
	}
}