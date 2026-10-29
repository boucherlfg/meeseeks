using System.Runtime.InteropServices;
using System.Text;
using Meeseeks.Core;

namespace Meeseeks.Cli;

/// <summary>Plain console renderer with light Markdown and color.</summary>
public sealed class ConsoleUi : IProgressSink
{
	private readonly TextWriter _out;
	private readonly AnsiPalette _ansi;
	private readonly MarkdownRenderer _markdown;
	private readonly LineStream _text;
	private readonly LineStream _thinking;
	private char _last = '\0';
	private bool _thinkingVisible;
	private bool _toolOpen;

	public ConsoleUi()
	{
		_out = Console.Out;
		var colorOverride = Environment.GetEnvironmentVariable("MEESEEKS_COLOR");
		var color = colorOverride == "1" ||
			(colorOverride != "0" &&
			 !Console.IsOutputRedirected &&
			 Environment.GetEnvironmentVariable("NO_COLOR") is null);
		_ansi = new AnsiPalette(color);
		_markdown = new MarkdownRenderer(_ansi);
		_text = new LineStream(line => EmitLine(_markdown.RenderLine(line)));
		_thinking = new LineStream(line => EmitLine(_ansi.Dim(_ansi.Italic(line))));
	}

	public AnsiPalette Ansi => _ansi;

	public static void EnableVirtualTerminal()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		var handle = GetStdHandle(-11);
		if (GetConsoleMode(handle, out var mode))
		{
			SetConsoleMode(handle, mode | 0x0004);
		}
	}

	public void Banner(string model, string cwd)
	{
		EmitLine(_ansi.Bold("meeseeks") + _ansi.Dim($"  ·  {cwd}  ·  {model}"));
		EmitLine("");
	}

	public void Prompt()
	{
		EnsureNewline();
		ResetTurn();
		Emit(_ansi.BrightCyan("❯ "));
	}

	public void AfterInput()
	{
		_last = '\n';
	}

	public void Text(string delta)
	{
		if (_thinkingVisible)
		{
			_thinking.Flush();
			_thinkingVisible = false;
			EnsureNewline();
		}
		_text.Write(delta);
	}

	public void Thinking(string delta)
	{
		_thinkingVisible = true;
		_thinking.Write(delta);
	}

	public void ToolStart(ToolCall call)
	{
		FlushStreams();
		EnsureNewline();

		var detail = ToolDetail(call);
		if (!_ansi.Enabled)
		{
			// No cursor control: print only the completed line.
			_toolOpen = false;
			return;
		}

		Emit(_ansi.Yellow($"● {call.Name}{detail}"));
		_toolOpen = true;
	}

	public void ToolEnd(ToolCall call, bool ok, TimeSpan elapsed)
	{
		var detail = ToolDetail(call);
		var line = $"{(ok ? "✓" : "✗")} {call.Name}{detail}  ·  {elapsed.TotalSeconds:0.0}s";
		var styled = ok ? _ansi.Green(line) : _ansi.Red(line);

		if (_toolOpen)
		{
			Emit("\r\u001b[2K");
			EmitLine(styled);
			_toolOpen = false;
		}
		else
		{
			EmitLine(styled);
		}
	}

	private static string ToolDetail(ToolCall call)
	{
		// Every tool accepts a `summary`; it wins over the derived label.
		if (PurposeLabel(call) is { } summary)
		{
			return $"  ·  {summary}";
		}

		if (string.Equals(call.Name, "read", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(call.Name, "write", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(call.Name, "edit", StringComparison.OrdinalIgnoreCase))
		{
			return PathLabel(call) is { } path ? $"  ·  {path}" : "";
		}

		if (string.Equals(call.Name, "patch", StringComparison.OrdinalIgnoreCase))
		{
			return PatchLabel(call);
		}

		if (string.Equals(call.Name, "glob", StringComparison.OrdinalIgnoreCase))
		{
			return GlobLabel(call);
		}

		if (string.Equals(call.Name, "grep", StringComparison.OrdinalIgnoreCase))
		{
			return GrepLabel(call);
		}

		if (string.Equals(call.Name, "web_fetch", StringComparison.OrdinalIgnoreCase))
		{
			return Label(call, "url") is { } url ? $"  ·  {url}" : "";
		}

		if (string.Equals(call.Name, "web_search", StringComparison.OrdinalIgnoreCase))
		{
			return Label(call, "query") is { } query ? $"  ·  {query}" : "";
		}

		if (!string.Equals(call.Name, "csharp", StringComparison.OrdinalIgnoreCase))
		{
			return "";
		}

		if (PurposeLabel(call) is { } purpose)
		{
			return $"  ·  {purpose}";
		}

		if (call.Arguments["code"] is System.Text.Json.Nodes.JsonValue value &&
			value.TryGetValue<string>(out var code))
		{
			var lines = code.Count(ch => ch == '\n') + 1;
			if (lines > 0)
			{
				return $"  ·  {lines} lines";
			}
		}

		return "";
	}

	/// <summary>Summarize a glob call: the pattern, and where it searched.</summary>
	private static string GlobLabel(ToolCall call)
	{
		var pattern = Label(call, "pattern");
		if (pattern is null)
		{
			return "";
		}

		var path = Label(call, "path");
		return path is null ? $"  ·  {pattern}" : $"  ·  {pattern} in {path}";
	}

	/// <summary>Summarize a grep call: the pattern, the file glob, and where it searched.</summary>
	private static string GrepLabel(ToolCall call)
	{
		var pattern = Label(call, "pattern");
		if (pattern is null)
		{
			return "";
		}

		var parts = new List<string> { pattern };
		if (Label(call, "glob") is { } glob)
		{
			parts.Add($"in {glob}");
		}
		if (Label(call, "path") is { } path)
		{
			parts.Add($"under {path}");
		}

		return "  ·  " + string.Join(" ", parts);
	}

	/// <summary>Summarize a patch call: how many hunks, across how many files.</summary>
	private static string PatchLabel(ToolCall call)
	{
		if (call.Arguments["edits"] is not System.Text.Json.Nodes.JsonArray edits || edits.Count == 0)
		{
			return "";
		}

		var files = edits
			.OfType<System.Text.Json.Nodes.JsonObject>()
			.Select(edit => edit["path"] is System.Text.Json.Nodes.JsonValue value &&
				value.TryGetValue<string>(out var path) ? path : null)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Count();

		var hunks = $"{edits.Count} hunk{(edits.Count == 1 ? "" : "s")}";
		return files > 1 ? $"  ·  {hunks} in {files} files" : $"  ·  {hunks}";
	}

	private static string? PathLabel(ToolCall call) =>
		call.Arguments["path"] is System.Text.Json.Nodes.JsonValue value &&
			value.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path)
			? TruncateLabel(path)
			: null;

	/// <summary>The call's `summary`, or `csharp`'s older `purpose` alias.</summary>
	private static string? PurposeLabel(ToolCall call) =>
		Label(call, "summary") ?? Label(call, "purpose");

	private static string? Label(ToolCall call, string name) =>
		call.Arguments[name] is System.Text.Json.Nodes.JsonValue value &&
			value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
			? TruncateLabel(text)
			: null;

	private static string TruncateLabel(string purpose)
	{
		var line = purpose.Split('\n')[0].Trim();
		return line.Length <= 60 ? line : line[..59] + "…";
	}

	public void TurnEnd(string text, Usage usage, TimeSpan elapsed)
	{
		FlushStreams();
		EnsureNewline();

		var stats = usage.Total > 0
			? $"· {elapsed.TotalSeconds:0.0}s  ·  {Tokens(usage.PromptTokens)} in → {Tokens(usage.CompletionTokens)} out"
			: $"· {elapsed.TotalSeconds:0.0}s";
		EmitLine(_ansi.Dim(stats));
		ResetTurn();
	}

	public void Notice(string text)
	{
		EnsureNewline();
		EmitLine(_ansi.Dim($"✉ {text}"));
	}

	/// <summary>
	/// Render a restored conversation so the visible history matches the resumed one:
	/// user turns with the prompt marker, assistant text through the Markdown renderer,
	/// thinking dimmed, and tool calls/results as their usual lines.
	/// </summary>
	public void ReplayHistory(IReadOnlyList<ChatMessage> messages)
	{
		foreach (var message in messages)
		{
			switch (message.Role)
			{
				case "system":
					break;

				case "user":
					EnsureNewline();
					Emit(_ansi.BrightCyan("❯ "));
					_markdown.Reset();
					foreach (var line in SplitLines(message.Content))
					{
						EmitLine(_markdown.RenderLine(line));
					}
					break;

				case "assistant":
					EnsureNewline();
					if (message.Thinking is { Length: > 0 } thinking)
					{
						foreach (var line in SplitLines(thinking))
						{
							EmitLine(_ansi.Dim(_ansi.Italic(line)));
						}
					}
					_markdown.Reset();
					foreach (var line in SplitLines(message.Content))
					{
						EmitLine(_markdown.RenderLine(line));
					}
					if (message.ToolCalls is { Count: > 0 } calls)
					{
						foreach (var call in calls)
						{
							EmitLine(_ansi.Yellow($"● {call.Name}{ToolDetail(call)}"));
						}
					}
					break;

				case "tool":
					EmitLine(_ansi.Green($"✓ {message.ToolName ?? "tool"}"));
					break;
			}
		}

		EnsureNewline();
		ResetTurn();
	}

	private static IEnumerable<string> SplitLines(string text)
	{
		if (text.Length == 0)
		{
			yield break;
		}

		foreach (var line in text.Split('\n'))
		{
			yield return line.TrimEnd('\r');
		}
	}

	public void Info(string text)
	{
		EnsureNewline();
		EmitLine(_ansi.Dim(text));
	}

	public void Error(string text)
	{
		EnsureNewline();
		EmitLine(_ansi.Red("error: ") + text);
	}

	public void WriteLine(string text) => EmitLine(text);

	public void EnsureNewline()
	{
		if (_toolOpen)
		{
			EmitLine("");
			_toolOpen = false;
		}

		if (_last != '\n' && _last != '\0')
		{
			EmitLine("");
		}
	}

	private void FlushStreams()
	{
		_text.Flush();
		if (_thinkingVisible)
		{
			_thinking.Flush();
			_thinkingVisible = false;
		}
	}

	private void ResetTurn()
	{
		_text.Reset();
		_thinking.Reset();
		_markdown.Reset();
		_thinkingVisible = false;
		_toolOpen = false;
	}

	private void Emit(string text)
	{
		if (text.Length == 0)
		{
			return;
		}
		_out.Write(text);
		_out.Flush();
		_last = text[^1];
	}

	private void EmitLine(string text) => Emit(text + "\n");

	private static string Tokens(int tokens) =>
		tokens >= 10_000 ? $"{tokens / 1000.0:0.0}k" : tokens.ToString();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GetStdHandle(int handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}

/// <summary>Child-process sink: silent unless MEESEEKS_DEBUG is set.</summary>
public sealed class DebugSink : IProgressSink
{
	private readonly bool _enabled;
	private readonly object _gate = new();

	public DebugSink() =>
		_enabled = Environment.GetEnvironmentVariable("MEESEEKS_DEBUG") is not null;

	public void Text(string delta) { }

	public void Thinking(string delta) { }

	public void ToolStart(ToolCall call)
	{
		var purpose = call.Arguments["purpose"] is System.Text.Json.Nodes.JsonValue value &&
			value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
		Log(purpose is null ? $"csharp start ({call.Name})" : $"csharp start ({call.Name}) · {purpose}");
	}

	public void ToolEnd(ToolCall call, bool ok, TimeSpan elapsed) =>
		Log($"csharp {(ok ? "ok" : "fail")} {elapsed.TotalSeconds:0.0}s");

	public void TurnEnd(string text, Usage usage, TimeSpan elapsed) =>
		Log($"turn {elapsed.TotalSeconds:0.0}s {usage.PromptTokens}->{usage.CompletionTokens}");

	public void Notice(string text) => Log(text);

	private void Log(string message)
	{
		if (!_enabled)
		{
			return;
		}
		lock (_gate)
		{
			Console.Error.WriteLine($"[child] {message}");
			Console.Error.Flush();
		}
	}
}
