using System.Text;

namespace Meeseeks.Cli;

/// <summary>ANSI styling that degrades to plain text when color is off.</summary>
public sealed class AnsiPalette
{
	public bool Enabled { get; }

	public AnsiPalette(bool enabled) => Enabled = enabled;

	private string Wrap(string code, string text) =>
		Enabled && text.Length > 0 ? $"\u001b[{code}m{text}\u001b[0m" : text;

	public string Bold(string text) => Wrap("1", text);
	public string Italic(string text) => Wrap("3", text);
	public string Underline(string text) => Wrap("4", text);
	public string Dim(string text) => Wrap("2", text);
	public string Red(string text) => Wrap("31", text);
	public string Green(string text) => Wrap("32", text);
	public string Yellow(string text) => Wrap("33", text);
	public string Blue(string text) => Wrap("34", text);
	public string Cyan(string text) => Wrap("36", text);
	public string BrightCyan(string text) => Wrap("96", text);
	public string Gray(string text) => Wrap("38;5;245", text);
	public string Tan(string text) => Wrap("38;5;180", text);
	public string Code(string text) => Wrap("38;5;245", text);
}

/// <summary>Buffers a stream into complete lines and hands each line to a renderer.</summary>
public sealed class LineStream
{
	private readonly StringBuilder _pending = new();
	private readonly Action<string> _render;

	public LineStream(Action<string> render) => _render = render;

	public void Write(string delta)
	{
		_pending.Append(delta);
		while (true)
		{
			var newline = _pending.ToString().IndexOf('\n');
			if (newline < 0)
			{
				break;
			}

			var line = _pending.ToString(0, newline);
			_pending.Remove(0, newline + 1);
			_render(line);
		}
	}

	public void Flush()
	{
		if (_pending.Length == 0)
		{
			return;
		}

		var line = _pending.ToString();
		_pending.Clear();
		_render(line);
	}

	public void Reset()
	{
		_pending.Clear();
	}
}

/// <summary>Small Markdown renderer for streamed assistant text: headings, lists, code fences, bold, italic, code, links.</summary>
public sealed class MarkdownRenderer
{
	private readonly AnsiPalette _ansi;
	private bool _inFence;
	private string _fenceLanguage = "";

	public MarkdownRenderer(AnsiPalette ansi) => _ansi = ansi;

	public void Reset()
	{
		_inFence = false;
		_fenceLanguage = "";
	}

	public string RenderLine(string line)
	{
		var trimmed = line.TrimStart();

		if (trimmed.StartsWith("```", StringComparison.Ordinal))
		{
			if (_inFence)
			{
				_inFence = false;
				_fenceLanguage = "";
			}
			else
			{
				_inFence = true;
				_fenceLanguage = trimmed[3..].Trim();
			}

			return _ansi.Dim(line);
		}

		if (_inFence)
		{
			return _ansi.Dim("│ ") + _ansi.Code(line);
		}

		if (string.IsNullOrWhiteSpace(line))
		{
			return "";
		}

		if (IsHorizontalRule(trimmed))
		{
			return _ansi.Dim("────────────────────────────────────────");
		}

		var heading = Heading(trimmed);
		if (heading is not null)
		{
			return _ansi.Bold(_ansi.Underline(_ansi.Cyan(heading)));
		}

		if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
		{
			var content = trimmed.Length > 1 ? trimmed[2..] : "";
			return _ansi.Dim("│ ") + _ansi.Dim(Inline(content));
		}

		var bullet = Bullet(trimmed);
		if (bullet is not null)
		{
			return _ansi.Yellow(bullet.Value.Marker) + " " + Inline(bullet.Value.Content);
		}

		return Inline(line);
	}

	private static string? Heading(string trimmed)
	{
		var hashes = 0;
		while (hashes < trimmed.Length && trimmed[hashes] == '#')
		{
			hashes++;
		}

		if (hashes is 0 or > 6 || hashes >= trimmed.Length || trimmed[hashes] != ' ')
		{
			return null;
		}

		return trimmed[(hashes + 1)..].TrimEnd('#').Trim();
	}

	private static bool IsHorizontalRule(string trimmed) =>
		trimmed.Length >= 3 &&
		(trimmed.All(ch => ch == '-') || trimmed.All(ch => ch == '*') || trimmed.All(ch => ch == '_'));

	private static (string Marker, string Content)? Bullet(string trimmed)
	{
		if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
		{
			return ("•", trimmed[2..]);
		}

		var digits = 0;
		while (digits < trimmed.Length && char.IsDigit(trimmed[digits]))
		{
			digits++;
		}

		if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ')
		{
			return (trimmed[..(digits + 1)], trimmed[(digits + 2)..]);
		}

		return null;
	}

	private string Inline(string text, int depth = 0)
	{
		if (depth > 4 || text.Length == 0)
		{
			return text;
		}

		var builder = new StringBuilder(text.Length + 16);

		for (var index = 0; index < text.Length;)
		{
			var current = text[index];

			if (current == '\\' && index + 1 < text.Length && "*_`[]<>".Contains(text[index + 1]))
			{
				builder.Append(text[index + 1]);
				index += 2;
				continue;
			}

			if (current == '`')
			{
				var end = text.IndexOf('`', index + 1);
				if (end > index + 1)
				{
					builder.Append(_ansi.Tan(text[(index + 1)..end]));
					index = end + 1;
					continue;
				}
			}

			if (current == '*' && index + 1 < text.Length && text[index + 1] == '*')
			{
				var end = text.IndexOf("**", index + 2, StringComparison.Ordinal);
				if (end > index + 2)
				{
					builder.Append(_ansi.Bold(Inline(text[(index + 2)..end], depth + 1)));
					index = end + 2;
					continue;
				}
			}

			if (current is '*' or '_')
			{
				var previousIsWord = index > 0 && char.IsLetterOrDigit(text[index - 1]);
				if (!(current == '_' && previousIsWord))
				{
					var end = text.IndexOf(current, index + 1);
					var nextIsWord = end + 1 < text.Length && char.IsLetterOrDigit(text[end + 1]);
					if (end > index + 1 && !(current == '_' && nextIsWord))
					{
						builder.Append(_ansi.Italic(Inline(text[(index + 1)..end], depth + 1)));
						index = end + 1;
						continue;
					}
				}
			}

			if (current == '<')
			{
				var end = text.IndexOf('>', index + 1);
				if (end > index + 1 && text[(index + 1)..end].Contains("://", StringComparison.Ordinal))
				{
					var url = text[(index + 1)..end];
					builder.Append(_ansi.Underline(_ansi.Blue(url)));
					index = end + 1;
					continue;
				}
			}

			if (current == '[')
			{
				var close = text.IndexOf(']', index + 1);
				if (close > index && close + 1 < text.Length && text[close + 1] == '(')
				{
					var open = text.IndexOf(')', close + 2);
					if (open > close + 1)
					{
						var label = text[(index + 1)..close];
						var url = text[(close + 2)..open];
						builder.Append(_ansi.Underline(Inline(label, depth + 1)));
						if (!string.Equals(label, url, StringComparison.Ordinal))
						{
							builder.Append(_ansi.Dim($" ({url})"));
						}
						index = open + 1;
						continue;
					}
				}
			}

			builder.Append(current);
			index++;
		}

		return builder.ToString();
	}
}
