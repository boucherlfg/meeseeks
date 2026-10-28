using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Tools;

/// <summary>Read a file with line numbers, or attach an image file to the result. A fast path for the most common script.</summary>
public sealed class ReadTool
{
	public const string ToolName = "read";

	private const long MaxFileBytes = 16 * 1024 * 1024;
	private const int MaxLineChars = 2000;

	private readonly ScriptRuntime _runtime;

	public ReadTool(ScriptRuntime runtime) => _runtime = runtime;

	public ToolDefinition Definition { get; } = new()
	{
		Name = ToolName,
		Description = """
			Read a text file and return it with line numbers (`   12→text`), so you can quote exact lines back to `edit`.

			`path` is a file; relative paths resolve against the working directory. Use `offset` (1-based first line) and `limit` (line count) to page through a large file; the header says how many lines the file has and what was shown. Lines longer than 2000 characters are truncated. Files over 16 MB are refused.

			Image files (png, jpeg, gif, webp, bmp) are returned as an image attached to the tool result, so a vision-capable model can see the picture itself; the text result describes the file. Other binary files are refused.

			Prefer this over a `csharp` script when you only need to look at a file: it is one call instead of a compile-and-run round trip.
			""",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "File to read. Relative paths resolve against the working directory.",
				},
				["offset"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "1-based line to start at. Defaults to 1.",
				},
				["limit"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "Maximum number of lines to return. Defaults to 2000.",
				},
				["summary"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Short one-line description of what this call is for; shown in the console while it runs.",
				},
			},
			["required"] = new JsonArray("path"),
		},
	};

	public ToolExecutionResult Execute(ToolCall call)
	{
		var path = ToolArgs.Text(call, "path") ?? throw new MeeseeksException("read requires a \"path\" string.");
		var full = FilePaths.Resolve(_runtime, path, "read");
		if (!File.Exists(full))
		{
			throw new MeeseeksException($"read: file not found: {full}");
		}

		var info = new FileInfo(full);
		if (info.Length > MaxFileBytes)
		{
			throw new MeeseeksException(
				$"read: {FilePaths.Display(_runtime, full)} is {info.Length / (1024 * 1024)} MB; the limit is 16 MB. Use grep or a csharp script to inspect it.");
		}

		// Image fast path: attach the file to the result so a vision model can see it directly.
		var mediaType = SniffHeader(full);
		if (mediaType is not null)
		{
			var bytes = File.ReadAllBytes(full);
			var image = ImageAttachment.FromBytes(bytes, mediaType, FilePaths.Display(_runtime, full));
			var dimensions = ImageSource.TryGetDimensions(bytes, out var imageWidth, out var imageHeight)
				? $"  ·  {imageWidth}×{imageHeight}px"
				: "";
			var imageText = $"{FilePaths.Display(_runtime, full)}  ·  {mediaType}  ·  {FormatSize(image.ByteCount)}{dimensions}\n" +
				"The image is attached to this tool result; analyze the attachment, not this text.";
			return new ToolExecutionResult(imageText, false, [image]);
		}

		string text;
		try
		{
			text = File.ReadAllText(full);
		}
		catch (Exception ex)
		{
			throw new MeeseeksException($"read: could not read {full}: {ex.Message}");
		}

		if (text.IndexOf('\0') >= 0)
		{
			throw new MeeseeksException($"read: {FilePaths.Display(_runtime, full)} looks binary; use a csharp script if you really need its bytes.");
		}

		var (lines, _) = FilePaths.SplitLines(text);
		var offset = Math.Max(1, ToolArgs.Number(call, "offset") ?? 1);
		var limit = Math.Max(1, ToolArgs.Number(call, "limit") ?? 2000);

		if (lines.Length == 0)
		{
			return new ToolExecutionResult($"{FilePaths.Display(_runtime, full)}: empty file (0 lines).", false);
		}

		if (offset > lines.Length)
		{
			throw new MeeseeksException(
				$"read: offset {offset} is past the end of {FilePaths.Display(_runtime, full)} ({lines.Length} lines).");
		}

		var start = offset - 1;
		var count = Math.Min(limit, lines.Length - start);
		var width = (start + count).ToString(CultureInfo.InvariantCulture).Length;

		var builder = new StringBuilder();
		builder.Append(FilePaths.Display(_runtime, full))
			.Append("  ·  ")
			.Append(lines.Length)
			.Append(" lines")
			.Append("  ·  showing ")
			.Append(offset)
			.Append('-')
			.Append(start + count)
			.AppendLine();

		for (var index = start; index < start + count; index++)
		{
			var line = lines[index];
			if (line.Length > MaxLineChars)
			{
				line = line[..MaxLineChars] + $"… (+{line.Length - MaxLineChars} chars)";
			}

			builder.Append((index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width))
				.Append("→")
				.AppendLine(line);
		}

		if (start + count < lines.Length)
		{
			builder.AppendLine($"[{lines.Length - (start + count)} more lines; call read again with offset {start + count + 1}]");
		}

		return new ToolExecutionResult(builder.ToString().TrimEnd(), false);
	}

	private const int SniffLength = 12;

	/// <summary>Identify an image format from the first bytes; null when the file is not an image.</summary>
	private static string? SniffHeader(string full)
	{
		using var stream = File.OpenRead(full);
		var header = new byte[SniffLength];
		var read = stream.ReadAtLeast(header, SniffLength, throwOnEndOfStream: false);
		return ImageSource.Sniff(header.AsSpan(0, read));
	}

	private static string FormatSize(int bytes) =>
		bytes >= 1024 * 1024
			? $"{bytes / (1024.0 * 1024.0):0.0} MB"
			: $"{Math.Max(1, bytes / 1024)} KB";
}