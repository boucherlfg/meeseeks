using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>
/// Run a shell command and capture its output. A fast path for the most common process script:
/// one tool call instead of a compile-and-run round trip.
///
/// Background mode: `action: "start"` launches a command and returns a process id immediately;
/// `log`, `wait`, `stop`, and `list` manage it from later calls. Processes are tracked per
/// session and killed with their whole process tree.
/// </summary>
public sealed class ShellTool
{
	public const string ToolName = "shell";

	private const int BufferLimit = 100_000;

	private readonly ScriptRuntime _runtime;
	private readonly ConcurrentDictionary<string, BackgroundProcess> _background = new();

	public ShellTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Run a shell command and return its stdout, stderr, and exit code. On Windows the command runs via `cmd.exe /c`; on Linux and macOS via `sh -c`, so pipes, redirects, and `&&` work.

			Relative `cwd` resolves against the working directory. The command is killed after `timeoutMs` (default 120000). Output is truncated to the last `maxChars` characters (default 20000) so long builds stay readable. Prefer `read`, `write`, `edit`, `patch`, `glob`, and `grep` for file work; use `shell` for builds, tests, git, package managers, and anything else a command covers.

			Background commands: pass `action` to manage long-running processes (dev servers, watchers) without blocking.
			- `start` — launch `command` and return a process id (for example `p1`) immediately.
			- `log` — return the output emitted so far by `procId`, plus its status; does not block.
			- `wait` — block until `procId` exits or `timeoutMs` elapses, then return its full output and exit code.
			- `stop` — kill `procId` and its whole process tree.
			- `list` — show every background process of this session.
			Background output is kept in a capped buffer (last ~100k characters); `log` and `wait` return the tail.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["command"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "The command line to run, for example `dotnet build` or `git status --short`. Required for `run` and `start`.",
				},
				["action"] = new JsonObject
				{
					["type"] = "string",
					["enum"] = new JsonArray("run", "start", "log", "wait", "stop", "list"),
					["description"] = "Defaults to `run` (foreground, as always). Use `start`/`log`/`wait`/`stop`/`list` for background process management.",
				},
				["procId"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Background process id (for example `p1`), as returned by `start`. Required for `log`, `wait`, and `stop`.",
				},
				["cwd"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Directory to run the command in. Defaults to the working directory.",
				},
				["timeoutMs"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "Wall-clock limit in milliseconds. Foreground: the command is killed after this (default 120000). `wait`: how long to wait for exit (default 120000).",
				},
				["maxChars"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "Maximum characters of combined output to return, keeping the tail. Defaults to 20000.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			// `command` is required for `run` and `start`; each action validates its own arguments.
			["required"] = new JsonArray(),
		},
	};

	public async Task<ToolExecutionResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
	{
		var action = ToolArgs.Text(call, "action")?.ToLowerInvariant() ?? "run";
		return action switch
		{
			"run" => await RunAsync(call, cancellationToken).ConfigureAwait(false),
			"start" => await StartAsync(call).ConfigureAwait(false),
			"log" => Log(call),
			"wait" => await WaitAsync(call, cancellationToken).ConfigureAwait(false),
			"stop" => Stop(call),
			"list" => List(),
			_ => throw new MeeseeksException($"Unknown shell action \"{action}\". Available: run, start, log, wait, stop, list."),
		};
	}

	// --- Foreground (original behavior) ---

	private async Task<ToolExecutionResult> RunAsync(ToolCall call, CancellationToken cancellationToken)
	{
		var command = ToolArgs.Text(call, "command") ?? throw new MeeseeksException("shell requires a \"command\" string.");
		var cwd = ResolveCwd(call);
		var timeoutMs = ToolArgs.Number(call, "timeoutMs") ?? 120_000;
		var maxChars = ToolArgs.Number(call, "maxChars") ?? 20_000;

		using var process = CreateProcess(command, cwd);
		var output = new StringBuilder();
		process.OutputDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };
		process.ErrorDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };

		if (!process.Start())
		{
			throw new MeeseeksException("Failed to start the shell process.");
		}

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(timeoutMs);
		try
		{
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			TryKill(process);
			throw new MeeseeksException($"Command timed out after {timeoutMs} ms and was killed.");
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			throw;
		}

		var text = output.ToString().TrimEnd();
		if (text.Length > maxChars)
		{
			text = $"[...{text.Length - maxChars} characters cut; showing the tail...]\n" + text[^maxChars..];
		}

		var header = $"exit {process.ExitCode} · {cwd}";
		var body = text.Length == 0 ? "(no output)" : text;
		return new ToolExecutionResult($"{header}\n{body}", process.ExitCode != 0);
	}

	// --- Background ---

	private async Task<ToolExecutionResult> StartAsync(ToolCall call)
	{
		var command = ToolArgs.Text(call, "command") ?? throw new MeeseeksException("shell start requires a \"command\" string.");
		var cwd = ResolveCwd(call);

		var process = CreateProcess(command, cwd);
		if (!process.Start())
		{
			throw new MeeseeksException("Failed to start the shell process.");
		}

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		var id = $"p{_background.Count + 1}";
		var background = new BackgroundProcess(id, command, cwd, process);
		_background[id] = background;

		// Reap the entry when the process exits so `list` stays truthful; output stays readable.
		_ = process.WaitForExitAsync().ContinueWith(
			_ => background.Exited.TrySetResult(),
			TaskScheduler.Default);

		return new ToolExecutionResult(
			$"started {id} · pid {process.Id} · {cwd}\ncommand: {command}\nUse shell with action \"log\", \"wait\", or \"stop\" and procId \"{id}\".",
			false);
	}

	private ToolExecutionResult Log(ToolCall call)
	{
		var background = Require(call);
		var maxChars = ToolArgs.Number(call, "maxChars") ?? 20_000;
		var text = background.Tail(maxChars);
		var status = background.Process.HasExited ? $"exited ({background.Process.ExitCode})" : "running";
		var header = $"{background.Id} · {status} · {background.Cwd}";
		return new ToolExecutionResult($"{header}\n{(text.Length == 0 ? "(no output yet)" : text)}", false);
	}

	private async Task<ToolExecutionResult> WaitAsync(ToolCall call, CancellationToken cancellationToken)
	{
		var background = Require(call);
		var timeoutMs = ToolArgs.Number(call, "timeoutMs") ?? 120_000;
		var maxChars = ToolArgs.Number(call, "maxChars") ?? 20_000;

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(timeoutMs);
		try
		{
			await background.Process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			var partial = background.Tail(maxChars);
			return new ToolExecutionResult(
				$"{background.Id} · still running after {timeoutMs} ms · {background.Cwd}\n{(partial.Length == 0 ? "(no output yet)" : partial)}",
				false);
		}

		var text = background.Tail(maxChars);
		var header = $"{background.Id} · exit {background.Process.ExitCode} · {background.Cwd}";
		return new ToolExecutionResult($"{header}\n{(text.Length == 0 ? "(no output)" : text)}", background.Process.ExitCode != 0);
	}

	private ToolExecutionResult Stop(ToolCall call)
	{
		var background = Require(call);
		TryKill(background.Process);
		var text = background.Tail(20_000);
		return new ToolExecutionResult(
			$"stopped {background.Id} · {background.Cwd}\n{(text.Length == 0 ? "(no output)" : text)}",
			false);
	}

	private ToolExecutionResult List()
	{
		if (_background.IsEmpty)
		{
			return new ToolExecutionResult("(no background processes)", false);
		}

		var lines = _background.Values.OrderBy(entry => entry.Id).Select(entry =>
		{
			var status = entry.Process.HasExited ? $"exited ({entry.Process.ExitCode})" : "running";
			return $"{entry.Id} · {status} · {entry.Command}";
		});
		return new ToolExecutionResult(string.Join("\n", lines), false);
	}

	// --- Shared helpers ---

	private string ResolveCwd(ToolCall call)
	{
		var cwd = Path.GetFullPath(Path.Combine(_runtime.Cwd, ToolArgs.Text(call, "cwd") ?? "."));
		if (!Directory.Exists(cwd))
		{
			throw new MeeseeksException($"Directory not found: {cwd}");
		}

		return cwd;
	}

	private static Process CreateProcess(string command, string cwd)
	{
		var isWindows = OperatingSystem.IsWindows();
		var fileName = isWindows ? "cmd.exe" : "/bin/sh";
		var arguments = isWindows ? $"/c {command}" : $"-c {Quote(command)}";

		return new Process
		{
			StartInfo = new ProcessStartInfo(fileName, arguments)
			{
				WorkingDirectory = cwd,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = false,
				UseShellExecute = false,
				CreateNoWindow = true,
				StandardOutputEncoding = ConsoleOutputEncoding(),
				StandardErrorEncoding = ConsoleOutputEncoding(),
			},
			EnableRaisingEvents = true,
		};
	}

	private BackgroundProcess Require(ToolCall call)
	{
		var id = ToolArgs.Text(call, "procId")
			?? throw new MeeseeksException("shell requires a \"procId\" (as returned by action \"start\") for this action.");
		if (!_background.TryGetValue(id, out var background))
		{
			throw new MeeseeksException($"Unknown procId \"{id}\". Use action \"list\" to see background processes.");
		}

		return background;
	}

	private static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (Exception)
		{
			// The process may have already exited.
		}
	}

	private static string Quote(string command) =>
		"'" + command.Replace("'", "'\\''") + "'";

	private static Encoding ConsoleOutputEncoding()
	{
		if (!OperatingSystem.IsWindows())
		{
			return Encoding.UTF8;
		}

		try
		{
			var codepage = Console.OutputEncoding.CodePage;
			return codepage is 65001 ? Encoding.UTF8 : Encoding.GetEncoding(codepage);
		}
		catch (Exception)
		{
			return Encoding.UTF8;
		}
	}

	/// <summary>A background process with a capped output buffer, shared across tool calls.</summary>
	private sealed class BackgroundProcess
	{
		private readonly object _lock = new();
		private readonly StringBuilder _buffer = new();

		public BackgroundProcess(string id, string command, string cwd, Process process)
		{
			Id = id;
			Command = command;
			Cwd = cwd;
			Process = process;
			process.OutputDataReceived += (_, args) => Append(args.Data);
			process.ErrorDataReceived += (_, args) => Append(args.Data);
		}

		public string Id { get; }
		public string Command { get; }
		public string Cwd { get; }
		public Process Process { get; }
		public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		private void Append(string? data)
		{
			if (data is null)
			{
				return;
			}

			lock (_lock)
			{
				_buffer.AppendLine(data);
				if (_buffer.Length > BufferLimit)
				{
					_buffer.Remove(0, _buffer.Length - BufferLimit);
				}
			}
		}

		public string Tail(int maxChars)
		{
			lock (_lock)
			{
				var text = _buffer.ToString().TrimEnd();
				if (text.Length > maxChars)
				{
					text = $"[...{text.Length - maxChars} characters cut; showing the tail...]\n" + text[^maxChars..];
				}

				return text;
			}
		}
	}
}