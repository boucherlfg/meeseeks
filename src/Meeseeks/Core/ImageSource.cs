using System.Buffers.Binary;
using Meeseeks.Tools;

namespace Meeseeks.Core;

/// <summary>
/// Turns a path, an http(s) URL, or a data URL into an <see cref="ImageAttachment"/>. Shared by
/// the REPL's <c>/image</c> command and by scripts that attach images to a subagent prompt.
/// </summary>
public static class ImageSource
{
	/// <summary>Ollama rejects very large payloads; keep one image well under that.</summary>
	public const int MaxBytes = 20 * 1024 * 1024;

	private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
	{
		[".png"] = "image/png",
		[".jpg"] = "image/jpeg",
		[".jpeg"] = "image/jpeg",
		[".gif"] = "image/gif",
		[".webp"] = "image/webp",
		[".bmp"] = "image/bmp",
	};

	/// <summary>Load an image from a file path (relative to <paramref name="cwd"/>), a URL, or a data URL.</summary>
	public static async Task<ImageAttachment> LoadAsync(
		string source,
		string cwd,
		CancellationToken cancellationToken = default)
	{
		var text = source.Trim();
		if (text.Length == 0)
		{
			throw new MeeseeksException("An image source needs a path, an http(s) URL, or a data URL.");
		}

		if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
		{
			return FromDataUrl(text);
		}

		if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
			(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
		{
			return await FromUrlAsync(uri, cancellationToken).ConfigureAwait(false);
		}

		return FromFile(text, cwd);
	}

	/// <summary>Load an image from a file path, relative to <paramref name="cwd"/>.</summary>
	public static ImageAttachment FromFile(string path, string cwd)
	{
		var full = Path.GetFullPath(Path.Combine(cwd, path));
		if (Directory.Exists(full))
		{
			throw new MeeseeksException($"{full} is a directory, not an image file.");
		}
		if (!File.Exists(full))
		{
			throw new MeeseeksException($"Image not found: {full}");
		}

		var info = new FileInfo(full);
		if (info.Length > MaxBytes)
		{
			throw new MeeseeksException(
				$"{full} is {info.Length / (1024 * 1024)} MB; the limit is {MaxBytes / (1024 * 1024)} MB per image.");
		}

		var bytes = File.ReadAllBytes(full);
		var mediaType = Sniff(bytes) ?? ByExtension.GetValueOrDefault(Path.GetExtension(full));
		return ImageAttachment.FromBytes(bytes, mediaType, Display(cwd, full));
	}

	/// <summary>Load an image over HTTP. The media type comes from the response, then the bytes.</summary>
	public static async Task<ImageAttachment> FromUrlAsync(Uri uri, CancellationToken cancellationToken = default)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, uri);
		request.Headers.Accept.ParseAdd("image/*,*/*;q=0.5");

		HttpResponseMessage response;
		try
		{
			response = await WebClient.Http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			throw new MeeseeksException($"Could not fetch {uri}: {WebClient.Describe(ex)}");
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new MeeseeksException($"Timed out fetching {uri}.");
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				throw new MeeseeksException($"{uri} returned HTTP {(int)response.StatusCode} {response.StatusCode}.");
			}

			var bytes = await WebClient
				.ReadBytesBoundedAsync(response, MaxBytes, cancellationToken)
				.ConfigureAwait(false);

			if (bytes.Length == 0)
			{
				throw new MeeseeksException($"{uri} returned an empty body.");
			}

			var declared = response.Content.Headers.ContentType?.MediaType;
			var mediaType = declared is not null && declared.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
				? declared
				: Sniff(bytes) ?? declared;
			return ImageAttachment.FromBytes(bytes, mediaType, uri.ToString());
		}
	}

	/// <summary>Decode <c>data:image/png;base64,...</c>.</summary>
	public static ImageAttachment FromDataUrl(string dataUrl)
	{
		var comma = dataUrl.IndexOf(',');
		if (comma < 0)
		{
			throw new MeeseeksException("Malformed data URL: no comma before the payload.");
		}

		var header = dataUrl[5..comma];
		var payload = dataUrl[(comma + 1)..];
		var parts = header.Split(';', StringSplitOptions.RemoveEmptyEntries);
		var mediaType = parts.Length > 0 && parts[0].Contains('/') ? parts[0] : null;
		var isBase64 = parts.Any(part => string.Equals(part, "base64", StringComparison.OrdinalIgnoreCase));

		byte[] bytes;
		try
		{
			bytes = isBase64
				? Convert.FromBase64String(payload)
				: System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
		}
		catch (FormatException)
		{
			throw new MeeseeksException("Malformed data URL: the payload is not valid base64.");
		}

		if (bytes.Length == 0)
		{
			throw new MeeseeksException("The data URL carries no image bytes.");
		}

		return ImageAttachment.FromBytes(bytes, Sniff(bytes) ?? mediaType, "data URL");
	}

	/// <summary>Identify the format from magic bytes; null when it is not a known image.</summary>
	public static string? Sniff(ReadOnlySpan<byte> bytes)
	{
		if (bytes.Length >= 8 &&
			bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
		{
			return "image/png";
		}
		if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
		{
			return "image/jpeg";
		}
		if (bytes.Length >= 6 &&
			bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8')
		{
			return "image/gif";
		}
		if (bytes.Length >= 12 &&
			bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
			bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
		{
			return "image/webp";
		}
		if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
		{
			return "image/bmp";
		}
		return null;
	}

	/// <summary>
	/// Read pixel dimensions from the file header without decoding the image. Supports PNG, GIF,
	/// BMP, and JPEG; returns false for other or malformed data.
	/// </summary>
	public static bool TryGetDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
	{
		width = height = 0;
		if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
		{
			// PNG: IHDR carries big-endian width and height at offsets 16 and 20.
			width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..]);
			height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..]);
			return width > 0 && height > 0;
		}

		if (bytes.Length >= 10 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
		{
			// GIF: little-endian logical screen size at offsets 6 and 8.
			width = bytes[6] | (bytes[7] << 8);
			height = bytes[8] | (bytes[9] << 8);
			return width > 0 && height > 0;
		}

		if (bytes.Length >= 26 && bytes[0] == 'B' && bytes[1] == 'M')
		{
			// BMP: little-endian DIB header; height may be negative for top-down rows.
			width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]);
			height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]));
			return width > 0 && height > 0;
		}

		if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
		{
			return TryGetJpegDimensions(bytes, out width, out height);
		}

		return false;
	}

	/// <summary>Walk JPEG markers to the first start-of-frame segment and read its dimensions.</summary>
	private static bool TryGetJpegDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
	{
		width = height = 0;
		var index = 2;
		while (index + 4 <= bytes.Length)
		{
			if (bytes[index] != 0xFF)
			{
				return false;
			}

			var marker = bytes[index + 1];
			if (marker == 0xFF || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
			{
				index += 2; // Standalone markers carry no length field.
				continue;
			}

			var length = (bytes[index + 2] << 8) | bytes[index + 3];
			if (length < 2)
			{
				return false;
			}

			// Start of frame (baseline and progressive alike) holds the dimensions.
			if (marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC)
			{
				if (index + 9 > bytes.Length)
				{
					return false;
				}

				height = (bytes[index + 5] << 8) | bytes[index + 6];
				width = (bytes[index + 7] << 8) | bytes[index + 8];
				return width > 0 && height > 0;
			}

			index += 2 + length;
		}

		return false;
	}

	/// <summary>Short label for the console: the path relative to the working directory.</summary>
	public static string Display(string cwd, string full)
	{
		var relative = Path.GetRelativePath(cwd, full);
		return (relative.StartsWith("..", StringComparison.Ordinal) ? full : relative).Replace('\\', '/');
	}

	/// <summary>One-line description of an attachment for the console and for prompts.</summary>
	public static string Describe(ImageAttachment image)
	{
		var size = image.ByteCount >= 1024 * 1024
			? $"{image.ByteCount / (1024.0 * 1024.0):0.0} MB"
			: $"{Math.Max(1, image.ByteCount / 1024)} KB";
		var type = image.MediaType ?? "image";
		return image.Source is { Length: > 0 } source ? $"{source} ({type}, {size})" : $"{type}, {size}";
	}
}
