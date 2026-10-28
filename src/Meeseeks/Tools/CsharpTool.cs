using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meeseeks.Config;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>The single model-facing tool.</summary>
public sealed class CsharpTool
{
	private static readonly string ToolDescription = BuildDescription();

	private readonly ScriptRuntime _runtime;
	private readonly ScriptRunner _runner = new();
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly int _defaultTimeoutMs;

	public ToolDefinition Definition { get; }

	public CsharpTool(ScriptRuntime runtime, ProviderConfig config)
	{
		_runtime = runtime;
		_defaultTimeoutMs = Math.Max(10, config.TimeoutSeconds) * 1000;
		Definition = new ToolDefinition
		{
			Name = "csharp",
			Description = ToolDescription,
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["code"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "C# script source using top-level statements. `await` and `return` are allowed.",
					},
					["purpose"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "Short one-line description of what this script is for; shown in the console while it runs.",
					},
					["summary"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "Alias for `purpose`; either one labels the call in the console.",
					},
					["timeoutMs"] = new JsonObject
					{
						["type"] = "number",
						["description"] = "Optional wall-clock limit for this script in milliseconds.",
					},
				},
				["required"] = new JsonArray("code"),
			},
		};
	}

	public async Task<ToolExecutionResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
	{
		if (call.Name != Definition.Name)
		{
			throw new MeeseeksException($"Unknown tool \"{call.Name}\".");
		}

		var code = call.Arguments["code"] is JsonValue codeValue && codeValue.TryGetValue<string>(out var text)
			? text
			: throw new MeeseeksException("The csharp tool requires a non-empty \"code\" string.");

		var timeoutMs = _defaultTimeoutMs;
		if (call.Arguments["timeoutMs"] is JsonValue timeoutValue)
		{
			if (timeoutValue.TryGetValue<int>(out var intValue))
			{
				timeoutMs = intValue;
			}
			else if (timeoutValue.TryGetValue<double>(out var doubleValue))
			{
				timeoutMs = (int)doubleValue;
			}
		}

		_runtime.Purpose = call.Arguments["purpose"] is JsonValue purposeValue &&
			purposeValue.TryGetValue<string>(out var purposeText) && !string.IsNullOrWhiteSpace(purposeText)
				? purposeText.Trim()
				: null;

		_runtime.CancellationToken = cancellationToken;
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var outcome = await _runner.RunAsync(StripFence(code), _runtime, timeoutMs, cancellationToken)
				.ConfigureAwait(false);
			return new ToolExecutionResult(Format(outcome), outcome.Error is not null);
		}
		finally
		{
			_gate.Release();
		}
	}

	private static string Format(ScriptOutcome outcome)
	{
		var parts = new List<string>();

		if (!string.IsNullOrWhiteSpace(outcome.Error))
		{
			parts.Add($"[error]\n{outcome.Error.Trim()}");
		}

		var output = outcome.Output.TrimEnd();
		if (output.Length > 0)
		{
			parts.Add($"[output]\n{output}");
		}

		if (outcome.Result is not null)
		{
			parts.Add($"[result]\n{FormatValue(outcome.Result)}");
		}

		if (outcome.Trace.Count > 0)
		{
			parts.Add($"[calls]\n{string.Join("\n", outcome.Trace.Select(line => $"- {line}"))}");
		}

		if (parts.Count == 0)
		{
			parts.Add("Script completed with no output.");
		}

		var text = string.Join("\n\n", parts);
		const int max = 100_000;
		if (text.Length <= max)
		{
			return text;
		}

		var half = max / 2;
		return $"{text[..half]}\n\n… output truncated ({text.Length} characters) …\n\n{text[^half..]}";
	}

	private static string FormatValue(object value) => value switch
	{
		string text => text,
		JsonNode node => node.ToJsonString(ToolText.Pretty),
		_ => JsonSerializer.Serialize(value, ToolText.Pretty),
	};

	private static string StripFence(string code)
	{
		var trimmed = code.Trim();
		if (!trimmed.StartsWith("```", StringComparison.Ordinal))
		{
			return code;
		}

		var firstNewline = trimmed.IndexOf('\n');
		if (firstNewline < 0)
		{
			return code;
		}

		var body = trimmed[(firstNewline + 1)..];
		var closing = body.LastIndexOf("```", StringComparison.Ordinal);
		return closing >= 0 ? body[..closing] : body;
	}

	private static string BuildDescription()
	{
		var builder = new StringBuilder();

		builder.AppendLine("""
			Run a C# script in this meeseeks process. This is the general-purpose tool: scripts are how you run commands, call HTTP APIs, spawn subagents, call MCP tools, and use skills. For file work, `read`, `write`, `edit`, and `patch` are one call each instead of a compile-and-run round trip. Pass a short `purpose` argument with every call; the console shows it while the script runs.

			The script is compiled with Roslyn as top-level statements, with `await` available. Imported namespaces: System, System.IO, System.Linq, System.Collections.Generic, System.Collections.Concurrent, System.Threading, System.Threading.Tasks, System.Diagnostics, System.Globalization, System.Net, System.Net.Http, System.Net.Http.Json, System.Security.Cryptography, System.Text, System.Text.Json, System.Text.Json.Nodes, System.Text.RegularExpressions, Meeseeks, Meeseeks.Core, Meeseeks.Agents, Meeseeks.Scripting.

			Only what the script writes (Console.Write / Console.WriteLine / Print) and the value it returns are visible to you. Output is returned even when the script throws or fails to compile. The working directory is Session.Cwd.
			""");

		builder.AppendLine();
		builder.AppendLine(ApiDocumentation.Text);
		builder.AppendLine();
		builder.AppendLine("""
			## Referencing libraries

			Start a script with `#r` directives when you need a package or local assembly:

			```csharp
			#r "nuget: Newtonsoft.Json, 13.0.3"
			#r "nuget: Humanizer"
			#r "lib/MyLibrary.dll"

			using Newtonsoft.Json;
			return JsonConvert.SerializeObject(new { ok = true });
			```

			A version is optional; without one NuGet resolves the newest stable version. Directives must appear before other code. Packages are restored once per package set and cached in `~/.meeseeks/nuget`, so the first use of a package is slower than later runs. Local paths resolve against `Session.Cwd`. Framework assemblies are already referenced and need no directive. Native runtime assets of packages are not deployed; managed NuGet dependencies work.

			## Rules

			- Use top-level statements. `return <value>;` sets the structured result; a script without a return just reports its output.
			- Roslyn scripts do not accept `using var x = ...;` declarations. Use a `using (...) { }` block, or a plain variable and call `Dispose()`.
			- Batch work into one script: read many files, run several commands, or fan out subagents, then print a compact summary. For text changes, prefer `read`, `write`, `edit`, and `patch`.
			- `Agent.SpawnAsync` / `SpawnManyAsync` wait for children. `StartAsync` children are stopped when the script ends unless you awaited `WaitAsync` or used `Stop`.
			- Errors thrown by the API are `MeeseeksException`s. Catch them when you want to continue.
			- Set the optional `timeoutMs` argument for scripts that may take more than ten minutes.
			- Always pass a short `purpose` argument (one line, e.g. "read mcp.json"); the console shows it while the script runs.
			- Final answers may use Markdown. The console renders bold, italic, inline code, code fences, headings, and lists.

			## Examples

			```csharp
			// Files, processes, HTTP: ordinary .NET
			var files = Directory.GetFiles(Session.Cwd, "*.cs", SearchOption.AllDirectories);
			foreach (var file in files) Console.WriteLine($"{file}: {new FileInfo(file).Length} bytes");

			var source = await File.ReadAllTextAsync("README.md");
			await File.WriteAllTextAsync("README.md", source.Replace("old", "new"));
			return new { files = files.Length, changed = source.Length };
			```

			```csharp
			// Run a process
			var start = new ProcessStartInfo("dotnet", "build")
			{
			    WorkingDirectory = Session.Cwd,
			    RedirectStandardOutput = true,
			    RedirectStandardError = true,
			};
			using (var process = Process.Start(start)!)
			{
			    var stdout = await process.StandardOutput.ReadToEndAsync();
			    var stderr = await process.StandardError.ReadToEndAsync();
			    await process.WaitForExitAsync();
			    Print("exit", process.ExitCode);
			    if (process.ExitCode != 0) throw new MeeseeksException(stderr);
			    return stdout;
			}
			```

			```csharp
			// Fan out and compare
			var results = await Agent.SpawnManyAsync(new[]
			{
			    "Find every place this repo reads files from disk. Reply with paths and line numbers.",
			    "Find every place this repo writes files. Reply with paths and line numbers.",
			});
			foreach (var result in results) Console.WriteLine($"[{result.AgentId}] {result.Text}");
			```

			```csharp
			// Talk to a running child
			var worker = await Agent.StartAsync("Wait with Agent.WaitForMessageAsync(120000), reply to the message, then return done.");
			await worker.SendAsync("context the child may need");
			var reply = await worker.AskAsync("Which file owns the retry logic?", 120000);
			var result = await worker.WaitAsync();
			return new { reply, result = result.Text };
			```

			```csharp
			// Skills and MCP
			var skill = await Skills.GetAsync("code-review");
			Print(skill.Body);

			foreach (var server in await Mcp.ServersAsync()) Print(server.Name, server.Transport);
			var issues = await Mcp.CallAsync("github", "list_issues", new { repo = "acme/app", state = "open" });
			return issues;
			```
			""");

		return builder.ToString().Trim();
	}
}
