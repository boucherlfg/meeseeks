using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meeseeks.Core;

namespace Meeseeks.Mcp;

public sealed class McpServerConfig
{
	public string? Command { get; set; }
	public string[]? Args { get; set; }
	public Dictionary<string, string>? Env { get; set; }
	public string? Cwd { get; set; }
	public string? Url { get; set; }
	public Dictionary<string, string>? Headers { get; set; }
	public bool Enabled { get; set; } = true;
	public int TimeoutSeconds { get; set; } = 30;
	public string? Description { get; set; }

	public string Transport => !string.IsNullOrWhiteSpace(Url) ? "http" : "stdio";
}

public sealed class McpConfigFile
{
	public Dictionary<string, McpServerConfig> McpServers { get; set; } =
		new(StringComparer.OrdinalIgnoreCase);
}

public sealed record McpToolRecord(string Server, string Name, string Description, JsonNode? InputSchema)
{
	public string FullName => $"mcp__{Server}__{Name}";
}

/// <summary>JSON-RPC MCP client for stdio and streamable HTTP servers.</summary>
internal sealed class McpClient : IAsyncDisposable
{
	private readonly string _name;
	private readonly McpServerConfig _config;
	private readonly HttpClient? _http;
	private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
	private readonly CancellationTokenSource _stop = new();
	private readonly StringBuilder _stderr = new();
	private readonly object _writeLock = new();
	private Process? _process;
	private StreamWriter? _stdin;
	private Task? _readLoop;
	private string? _sessionId;
	private int _nextId;

	private McpClient(string name, McpServerConfig config)
	{
		_name = name;
		_config = config;
		if (config.Transport == "http")
		{
			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds)) };
		}
	}

	public string Name => _name;
	public string? Instructions { get; private set; }
	public IReadOnlyList<McpToolRecord> Tools { get; private set; } = [];

	public static async Task<McpClient> ConnectAsync(
		string name,
		McpServerConfig config,
		CancellationToken cancellationToken)
	{
		var client = new McpClient(name, config);

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds)));

		try
		{
			if (config.Transport == "http")
			{
				if (string.IsNullOrWhiteSpace(config.Url))
				{
					throw new MeeseeksException($"MCP server \"{name}\" has no url.");
				}
			}
			else
			{
				client.StartStdio();
			}

			var result = await client.RequestAsync("initialize", new JsonObject
			{
				["protocolVersion"] = "2024-11-05",
				["capabilities"] = new JsonObject(),
				["clientInfo"] = new JsonObject { ["name"] = "meeseeks", ["version"] = "0.1.0" },
			}, timeout.Token).ConfigureAwait(false);

			client.Instructions = result?["instructions"]?.GetValue<string>();
			await client.NotifyAsync("notifications/initialized", null, timeout.Token).ConfigureAwait(false);
			client.Tools = await client.ListToolsAsync(timeout.Token).ConfigureAwait(false);
			return client;
		}
		catch
		{
			await client.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	private void StartStdio()
	{
		if (string.IsNullOrWhiteSpace(_config.Command))
		{
			throw new MeeseeksException($"MCP server \"{_name}\" has no command.");
		}

		var startInfo = new ProcessStartInfo
		{
			FileName = _config.Command,
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = string.IsNullOrWhiteSpace(_config.Cwd) ? Environment.CurrentDirectory : _config.Cwd,
		};

		foreach (var arg in _config.Args ?? [])
		{
			startInfo.ArgumentList.Add(arg);
		}

		foreach (var pair in _config.Env ?? new Dictionary<string, string>())
		{
			startInfo.Environment[pair.Key] = Expand(pair.Value);
		}

		_process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
		if (!_process.Start())
		{
			throw new MeeseeksException($"Could not start MCP server \"{_name}\".");
		}

		_stdin = _process.StandardInput;
		_stdin.AutoFlush = true;
		_readLoop = Task.Run(ReadStdioAsync);

		_ = Task.Run(async () =>
		{
			try
			{
				var buffer = new char[4096];
				while (true)
				{
					var read = await _process.StandardError.ReadAsync(buffer.AsMemory(), _stop.Token).ConfigureAwait(false);
					if (read == 0)
					{
						break;
					}
					lock (_stderr)
					{
						if (_stderr.Length < 32_000)
						{
							_stderr.Append(buffer, 0, read);
						}
					}
				}
			}
			catch
			{
				// The server closed or was killed.
			}
		});
	}

	private async Task ReadStdioAsync()
	{
		if (_process is null)
		{
			return;
		}

		try
		{
			while (!_stop.IsCancellationRequested)
			{
				var line = await _process.StandardOutput.ReadLineAsync(_stop.Token).ConfigureAwait(false);
				if (line is null)
				{
					break;
				}

				JsonObject? message;
				try
				{
					message = JsonNode.Parse(line) as JsonObject;
				}
				catch (JsonException)
				{
					continue;
				}

				Dispatch(message);
			}
		}
		catch (OperationCanceledException)
		{
			// Shutting down.
		}
		catch (Exception ex)
		{
			FailPending(new MeeseeksException($"MCP server \"{_name}\" stopped: {ex.Message}"));
		}
		finally
		{
			FailPending(new MeeseeksException($"MCP server \"{_name}\" closed the connection."));
		}
	}

	private void Dispatch(JsonObject? message)
	{
		if (message is null)
		{
			return;
		}

		if (message["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var id))
		{
			return; // Notification or server request; this harness does not need either.
		}

		if (!_pending.TryRemove(id, out var completion))
		{
			return;
		}

		if (message["error"] is JsonObject error)
		{
			completion.TrySetException(new MeeseeksException(
				$"MCP server \"{_name}\" error: {error["message"]?.GetValue<string>() ?? error.ToJsonString()}"));
			return;
		}

		completion.TrySetResult(message["result"] as JsonObject ?? new JsonObject());
	}

	private async Task<JsonObject?> RequestAsync(
		string method,
		JsonObject? parameters,
		CancellationToken cancellationToken)
	{
		var id = Interlocked.Increment(ref _nextId);
		var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = completion;

		using var registration = cancellationToken.Register(() =>
		{
			if (_pending.TryRemove(id, out var pending))
			{
				pending.TrySetCanceled(cancellationToken);
			}
		});

		try
		{
			if (_http is not null)
			{
				return await RequestHttpAsync(method, parameters, id, cancellationToken).ConfigureAwait(false);
			}

			SendStdio(new JsonObject
			{
				["jsonrpc"] = "2.0",
				["id"] = id,
				["method"] = method,
				["params"] = parameters ?? new JsonObject(),
			});

			return await completion.Task.ConfigureAwait(false);
		}
		finally
		{
			_pending.TryRemove(id, out _);
		}
	}

	private void SendStdio(JsonObject message)
	{
		if (_stdin is null)
		{
			throw new MeeseeksException($"MCP server \"{_name}\" is not running.");
		}

		lock (_writeLock)
		{
			_stdin.WriteLine(message.ToJsonString());
		}
	}

	private async Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
	{
		if (_http is not null)
		{
			await PostHttpAsync(new JsonObject
			{
				["jsonrpc"] = "2.0",
				["method"] = method,
				["params"] = parameters ?? new JsonObject(),
			}, cancellationToken).ConfigureAwait(false);
			return;
		}

		SendStdio(new JsonObject
		{
			["jsonrpc"] = "2.0",
			["method"] = method,
			["params"] = parameters ?? new JsonObject(),
		});
	}

	private async Task<JsonObject?> RequestHttpAsync(
		string method,
		JsonObject? parameters,
		int id,
		CancellationToken cancellationToken)
	{
		var response = await PostHttpAsync(new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = id,
			["method"] = method,
			["params"] = parameters ?? new JsonObject(),
		}, cancellationToken).ConfigureAwait(false);

		if (response is null)
		{
			throw new MeeseeksException($"MCP server \"{_name}\" returned no response to {method}.");
		}

		if (response["error"] is JsonObject error)
		{
			throw new MeeseeksException(
				$"MCP server \"{_name}\" error: {error["message"]?.GetValue<string>() ?? error.ToJsonString()}");
		}

		return response["result"] as JsonObject ?? new JsonObject();
	}

	private async Task<JsonObject?> PostHttpAsync(JsonObject payload, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, _config.Url)
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
		};

		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", "2024-11-05");

		foreach (var pair in _config.Headers ?? new Dictionary<string, string>())
		{
			request.Headers.TryAddWithoutValidation(pair.Key, Expand(pair.Value));
		}

		if (!string.IsNullOrEmpty(_sessionId))
		{
			request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
		}

		using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
			.ConfigureAwait(false);

		if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
		{
			_sessionId = sessionValues.FirstOrDefault() ?? _sessionId;
		}

		if (!response.IsSuccessStatusCode)
		{
			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			throw new MeeseeksException(
				$"MCP server \"{_name}\" returned {(int)response.StatusCode} {response.ReasonPhrase}: {Trim(body, 1500)}");
		}

		var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
		if (mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
		{
			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			using var reader = new StreamReader(stream, Encoding.UTF8);
			while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
			{
				if (!line.StartsWith("data:", StringComparison.Ordinal))
				{
					continue;
				}

				var data = line[5..].Trim();
				if (data.Length == 0 || data == "[DONE]")
				{
					continue;
				}

				JsonObject? parsed;
				try
				{
					parsed = JsonNode.Parse(data) as JsonObject;
				}
				catch (JsonException)
				{
					continue;
				}

				if (parsed is null)
				{
					continue;
				}

				if (parsed["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id) &&
					payload["id"] is JsonValue expected && expected.TryGetValue<int>(out var expectedId) && id == expectedId)
				{
					return parsed;
				}

				if (parsed["result"] is not null || parsed["error"] is not null)
				{
					return parsed;
				}
			}
			return null;
		}

		if (response.StatusCode == HttpStatusCode.Accepted)
		{
			return null;
		}

		var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}

		try
		{
			return JsonNode.Parse(text) as JsonObject;
		}
		catch (JsonException)
		{
			throw new MeeseeksException($"MCP server \"{_name}\" returned invalid JSON: {Trim(text, 1000)}");
		}
	}

	public async Task<IReadOnlyList<McpToolRecord>> ListToolsAsync(CancellationToken cancellationToken)
	{
		var tools = new List<McpToolRecord>();
		string? cursor = null;

		do
		{
			var parameters = new JsonObject();
			if (cursor is not null)
			{
				parameters["cursor"] = cursor;
			}

			var result = await RequestAsync("tools/list", parameters, cancellationToken).ConfigureAwait(false);
			if (result?["tools"] is JsonArray array)
			{
				foreach (var node in array.OfType<JsonObject>())
				{
					var name = node["name"]?.GetValue<string>();
					if (string.IsNullOrWhiteSpace(name))
					{
						continue;
					}

					tools.Add(new McpToolRecord(
						_name,
						name,
						node["description"]?.GetValue<string>() ?? "",
						node["inputSchema"]?.DeepClone()));
				}
			}

			cursor = result?["nextCursor"]?.GetValue<string>();
		} while (!string.IsNullOrWhiteSpace(cursor));

		return tools;
	}

	public async Task<JsonObject> CallToolAsync(
		string tool,
		JsonObject arguments,
		CancellationToken cancellationToken)
	{
		var result = await RequestAsync("tools/call", new JsonObject
		{
			["name"] = tool,
			["arguments"] = arguments.DeepClone(),
		}, cancellationToken).ConfigureAwait(false);

		return result ?? new JsonObject();
	}

	public string StderrTail()
	{
		lock (_stderr)
		{
			return _stderr.Length <= 2000 ? _stderr.ToString() : _stderr.ToString()[^2000..];
		}
	}

	private void FailPending(Exception error)
	{
		foreach (var pair in _pending)
		{
			if (_pending.TryRemove(pair.Key, out var completion))
			{
				completion.TrySetException(error);
			}
		}
	}

	private static string Expand(string value) =>
		System.Text.RegularExpressions.Regex.Replace(
			value,
			@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}",
			match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? "");

	private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";

	public async ValueTask DisposeAsync()
	{
		_stop.Cancel();

		if (_stdin is not null)
		{
			try
			{
				_stdin.Close();
			}
			catch
			{
				// Best effort.
			}
		}

		if (_process is not null)
		{
			try
			{
				if (!_process.HasExited)
				{
					_process.Kill(entireProcessTree: true);
				}
			}
			catch
			{
				// Already gone.
			}
			_process.Dispose();
		}

		if (_readLoop is not null)
		{
			try
			{
				await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
			}
			catch
			{
				// Best effort.
			}
		}

		_http?.Dispose();
		_stop.Dispose();
	}
}

/// <summary>Lazily connects configured MCP servers and caches their tool lists.</summary>
public sealed class McpManager : IAsyncDisposable
{
	private Dictionary<string, McpServerConfig> _servers;
	private readonly Dictionary<string, Task<McpClient>> _clients = new(StringComparer.OrdinalIgnoreCase);
	private readonly object _gate = new();

	public string? LoadError { get; private set; }

	private McpManager(Dictionary<string, McpServerConfig> servers, string? loadError)
	{
		_servers = servers;
		LoadError = loadError;
	}

	public static McpManager Empty() =>
		new(new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase), null);

	public static McpManager Load(string path)
	{
		if (!File.Exists(path))
		{
			return new McpManager(new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase), null);
		}

		try
		{
			var config = JsonSerializer.Deserialize<McpConfigFile>(
				File.ReadAllText(path),
				new JsonSerializerOptions(JsonSerializerDefaults.Web));
			var servers = config?.McpServers ?? new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
			return new McpManager(servers, null);
		}
		catch (Exception ex)
		{
			return new McpManager(
				new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase),
				$"Could not read {path}: {ex.Message}");
		}
	}

	public IReadOnlyDictionary<string, McpServerConfig> Servers => _servers;

	/// <summary>
	/// Re-read the config file and drop every connected client, so the next tool call
	/// reconnects with the new settings. Existing references to this manager stay valid.
	/// </summary>
	public async Task ReloadAsync(string path)
	{
		await DisposeAsync().ConfigureAwait(false);
		var reloaded = Load(path);
		_servers = new Dictionary<string, McpServerConfig>(reloaded._servers, StringComparer.OrdinalIgnoreCase);
		LoadError = reloaded.LoadError;
	}

	public IEnumerable<string> EnabledServers =>
		_servers.Where(pair => pair.Value.Enabled).Select(pair => pair.Key);

	private async Task<McpClient> ClientAsync(string server, CancellationToken cancellationToken)
	{
		Task<McpClient> task;
		lock (_gate)
		{
			if (!_servers.TryGetValue(server, out var config))
			{
				throw new MeeseeksException($"Unknown MCP server \"{server}\". Configured: {string.Join(", ", _servers.Keys)}");
			}

			if (!config.Enabled)
			{
				throw new MeeseeksException($"MCP server \"{server}\" is disabled.");
			}

			if (!_clients.TryGetValue(server, out task!))
			{
				task = McpClient.ConnectAsync(server, config, CancellationToken.None);
				_clients[server] = task;
			}
		}

		try
		{
			return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			lock (_gate)
			{
				_clients.Remove(server);
			}
			throw;
		}
	}

	public async Task<IReadOnlyList<McpToolRecord>> ListToolsAsync(
		string? server,
		CancellationToken cancellationToken)
	{
		var names = server is null ? EnabledServers.ToList() : [server];
		var records = new List<McpToolRecord>();

		foreach (var name in names)
		{
			var client = await ClientAsync(name, cancellationToken).ConfigureAwait(false);
			records.AddRange(client.Tools);
		}

		return records;
	}

	public async Task<JsonObject> CallToolAsync(
		string server,
		string tool,
		JsonObject arguments,
		CancellationToken cancellationToken)
	{
		var client = await ClientAsync(server, cancellationToken).ConfigureAwait(false);
		return await client.CallToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
	}

	public async Task<JsonObject> CallByNameAsync(
		string fullName,
		JsonObject arguments,
		CancellationToken cancellationToken)
	{
		var parsed = ParseToolName(fullName)
			?? throw new MeeseeksException($"\"{fullName}\" is not an MCP tool name. Expected mcp__server__tool.");

		return await CallToolAsync(parsed.Server, parsed.Tool, arguments, cancellationToken)
			.ConfigureAwait(false);
	}

	public static (string Server, string Tool)? ParseToolName(string name)
	{
		if (!name.StartsWith("mcp__", StringComparison.Ordinal))
		{
			return null;
		}

		var rest = name[5..];
		var separator = rest.IndexOf("__", StringComparison.Ordinal);
		if (separator <= 0)
		{
			return null;
		}

		return (rest[..separator], rest[(separator + 2)..]);
	}

	public async ValueTask DisposeAsync()
	{
		Task<McpClient>[] tasks;
		lock (_gate)
		{
			tasks = _clients.Values.ToArray();
			_clients.Clear();
		}

		foreach (var task in tasks)
		{
			try
			{
				var client = await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
				await client.DisposeAsync().ConfigureAwait(false);
			}
			catch
			{
				// Startup failures have nothing to dispose.
			}
		}
	}
}
