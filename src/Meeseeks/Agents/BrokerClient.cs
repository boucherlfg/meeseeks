using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Meeseeks.Core;

namespace Meeseeks.Agents;

/// <summary>TCP client for the loopback broker. One connection multiplexes requests and receive polls.</summary>
public sealed class BrokerClient : IAsyncDisposable
{
	private readonly TcpClient _tcp;
	private readonly NetworkStream _stream;
	private readonly StreamReader _reader;
	private readonly StreamWriter _writer;
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
	private readonly Channel<AgentMessage> _inbox = Channel.CreateUnbounded<AgentMessage>();
	private readonly object _historyGate = new();
	private readonly List<AgentMessage> _history = new();
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _readLoop;
	private Task? _receiveLoop;
	private int _requestId;
	private bool _closed;

	public string AgentId { get; }
	public string Token { get; }
	public string Url { get; }

	public event Action<AgentMessage>? MessageReceived;

	private BrokerClient(string url, TcpClient tcp, string token, string agentId)
	{
		Url = url;
		_tcp = tcp;
		_stream = tcp.GetStream();
		_reader = new StreamReader(_stream, Encoding.UTF8);
		_writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };
		Token = token;
		AgentId = agentId;
		_readLoop = Task.Run(ReadLoopAsync);
	}

	public static async Task<BrokerClient> ConnectAsync(
		string url,
		string token,
		string agentId,
		string? parentId,
		int depth,
		CancellationToken cancellationToken)
	{
		var (host, port) = ParseUrl(url);
		var tcp = new TcpClient();
		await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);

		var client = new BrokerClient(url, tcp, token, agentId);
		try
		{
			await client.RequestAsync("register", new JsonObject
			{
				["agentId"] = agentId,
				["parentId"] = parentId,
				["depth"] = depth,
			}, cancellationToken).ConfigureAwait(false);
			return client;
		}
		catch
		{
			await client.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	public void StartReceiving()
	{
		_receiveLoop ??= Task.Run(ReceiveLoopAsync);
	}

	private async Task ReceiveLoopAsync()
	{
		while (!_stop.IsCancellationRequested)
		{
			try
			{
				var response = await RequestAsync("recv", new JsonObject
				{
					["agentId"] = AgentId,
					["waitMs"] = 15000,
				}, _stop.Token).ConfigureAwait(false);

				if (response["value"]?["messages"] is not JsonArray messages)
				{
					continue;
				}

				foreach (var node in messages)
				{
					if (node is not JsonObject obj)
					{
						continue;
					}

					var message = ParseMessage(obj);
					lock (_historyGate)
					{
						_history.Add(message);
					}
					_inbox.Writer.TryWrite(message);
					MessageReceived?.Invoke(message);
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception)
			{
				if (_closed)
				{
					break;
				}
				try
				{
					await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}
	}

	private async Task ReadLoopAsync()
	{
		try
		{
			while (!_stop.IsCancellationRequested)
			{
				var line = await _reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
				if (line is null)
				{
					break;
				}

				JsonObject? response;
				try
				{
					response = JsonNode.Parse(line) as JsonObject;
				}
				catch (JsonException)
				{
					continue;
				}

				if (response is null || response["id"] is not JsonValue idValue ||
					!idValue.TryGetValue<int>(out var requestId))
				{
					continue;
				}

				if (!_pending.TryRemove(requestId, out var completion))
				{
					continue;
				}

				if (response["ok"]?.GetValue<bool>() == true)
				{
					completion.TrySetResult(response);
				}
				else
				{
					completion.TrySetException(new MeeseeksException(
						response["error"]?.GetValue<string>() ?? "Broker request failed."));
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Shutting down.
		}
		catch (Exception ex)
		{
			FailPending(ex);
		}
		finally
		{
			FailPending(new MeeseeksException("The agent broker connection closed."));
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

	private async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
	{
		if (_closed)
		{
			throw new MeeseeksException("The agent broker connection is closed.");
		}

		var requestId = Interlocked.Increment(ref _requestId);
		var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[requestId] = completion;

		using var registration = cancellationToken.Register(() =>
		{
			if (_pending.TryRemove(requestId, out var pending))
			{
				pending.TrySetCanceled(cancellationToken);
			}
		});

		var payload = new JsonObject
		{
			["id"] = requestId,
			["token"] = Token,
			["method"] = method,
			["params"] = parameters,
		};

		await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await _writer.WriteLineAsync(payload.ToJsonString()).ConfigureAwait(false);
		}
		finally
		{
			_writeLock.Release();
		}

		return await completion.Task.ConfigureAwait(false);
	}

	public Task<AgentPeer[]> PeersAsync(CancellationToken cancellationToken = default) =>
		GetPeersAsync(cancellationToken);

	private async Task<AgentPeer[]> GetPeersAsync(CancellationToken cancellationToken)
	{
		var response = await RequestAsync("peers", new JsonObject(), cancellationToken).ConfigureAwait(false);
		var peers = response["value"]?["peers"] as JsonArray;
		if (peers is null)
		{
			return [];
		}

		return peers.OfType<JsonObject>()
			.Select(node => new AgentPeer(
				node["id"]?.GetValue<string>() ?? "?",
				node["parent"]?.GetValue<string>(),
				node["depth"]?.GetValue<int>() ?? 0))
			.ToArray();
	}

	public async Task<string> SendAsync(
		string to,
		string text,
		bool wantReply = false,
		CancellationToken cancellationToken = default)
	{
		var response = await RequestAsync("send", new JsonObject
		{
			["from"] = AgentId,
			["to"] = to,
			["text"] = text,
			["wantReply"] = wantReply,
		}, cancellationToken).ConfigureAwait(false);

		return response["value"]?["messageId"]?.GetValue<string>()
			?? throw new MeeseeksException("The broker did not return a message id.");
	}

	public async Task<string> AskAsync(
		string to,
		string text,
		int timeoutMs,
		CancellationToken cancellationToken = default)
	{
		var messageId = await SendAsync(to, text, wantReply: true, cancellationToken).ConfigureAwait(false);

		var response = await RequestAsync("waitReply", new JsonObject
		{
			["agentId"] = AgentId,
			["messageId"] = messageId,
			["waitMs"] = timeoutMs,
		}, cancellationToken).ConfigureAwait(false);

		var value = response["value"] as JsonObject;
		if (value?["timeout"]?.GetValue<bool>() == true)
		{
			throw new TimeoutException($"No reply from \"{to}\" within {timeoutMs} ms.");
		}

		return value?["text"]?.GetValue<string>() ?? "";
	}

	public async Task ReplyAsync(string messageId, string text, CancellationToken cancellationToken = default)
	{
		await RequestAsync("reply", new JsonObject
		{
			["from"] = AgentId,
			["messageId"] = messageId,
			["text"] = text,
		}, cancellationToken).ConfigureAwait(false);
	}

	public async Task<AgentMessage?> WaitForMessageAsync(int timeoutMs, CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(timeoutMs);

		try
		{
			return await _inbox.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return null;
		}
	}

	public bool TryDequeue(out AgentMessage message) => _inbox.Reader.TryRead(out message!);

	public IReadOnlyList<AgentMessage> History()
	{
		lock (_historyGate)
		{
			return _history.ToArray();
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_closed)
		{
			return;
		}

		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
			await RequestAsync("unregister", new JsonObject { ["agentId"] = AgentId }, timeout.Token)
				.ConfigureAwait(false);
		}
		catch
		{
			// The broker may already be gone.
		}

		_closed = true;
		_stop.Cancel();
		try
		{
			_writer.Close();
			_tcp.Close();
		}
		catch
		{
			// Best effort.
		}

		try
		{
			await Task.WhenAll(_readLoop, _receiveLoop ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2))
				.ConfigureAwait(false);
		}
		catch
		{
			// Best effort.
		}

		_stop.Dispose();
		_writeLock.Dispose();
	}

	private static AgentMessage ParseMessage(JsonObject node) => new()
	{
		Id = node["id"]?.GetValue<string>() ?? "?",
		From = node["from"]?.GetValue<string>() ?? "?",
		To = node["to"]?.GetValue<string>() ?? "?",
		Text = node["text"]?.GetValue<string>() ?? "",
		WantReply = node["wantReply"]?.GetValue<bool>() == true,
		InReplyTo = node["inReplyTo"]?.GetValue<string>(),
	};

	private static (string Host, int Port) ParseUrl(string url)
	{
		var cleaned = url.Replace("http://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
		var separator = cleaned.LastIndexOf(':');
		if (separator <= 0 || !int.TryParse(cleaned[(separator + 1)..], out var port))
		{
			throw new MeeseeksException($"Invalid broker address \"{url}\". Expected host:port.");
		}
		return (cleaned[..separator], port);
	}
}
