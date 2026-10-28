using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meeseeks.Scripting;

namespace Meeseeks.Agents;

[Api("A message from another agent.")]
public sealed class AgentMessage
{
	[Api("Message id, for example m3. Use it with Agent.ReplyAsync.", Order = 0)]
	public required string Id { get; init; }

	[Api("Sender agent id.", Order = 1)]
	public required string From { get; init; }

	[Api("Recipient agent id, or * for a broadcast.", Order = 2)]
	public required string To { get; init; }

	[Api("Message text.", Order = 3)]
	public string Text { get; init; } = "";

	[Api("True when the sender is waiting for Agent.ReplyAsync.", Order = 4)]
	public bool WantReply { get; init; }

	[Api("Set on a reply; the id of the message it answers.", Order = 5)]
	public string? InReplyTo { get; init; }

	[Api("When the broker received the message.", Order = 6)]
	public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AgentPeer(string Id, string? Parent, int Depth);

/// <summary>
/// Loopback message broker shared by a root meeseeks process and all of its subagent
/// processes. Messages survive only while the root runs.
/// </summary>
public sealed class BrokerServer : IDisposable
{
	private sealed class PeerState
	{
		public required string Id { get; init; }
		public string? Parent { get; init; }
		public int Depth { get; init; }
		public Queue<AgentMessage> Queue { get; } = new();
		public List<TaskCompletionSource<AgentMessage>> Waiters { get; } = new();
	}

	private readonly object _gate = new();
	private readonly Dictionary<string, PeerState> _peers = new();
	private readonly Dictionary<string, TaskCompletionSource<AgentMessage>> _replyWaiters = new();
	private readonly Dictionary<string, AgentMessage> _earlyReplies = new();
	private readonly Dictionary<string, string> _askSenders = new();
	private readonly TcpListener _listener;
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _acceptLoop;
	private long _messageCounter;

	public string Token { get; }
	public int Port { get; }

	private BrokerServer(TcpListener listener, string token)
	{
		_listener = listener;
		Token = token;
		Port = ((IPEndPoint)listener.LocalEndpoint).Port;
		_acceptLoop = Task.Run(AcceptLoopAsync);
	}

	public static BrokerServer Start()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
		return new BrokerServer(listener, token);
	}

	public string Url => $"127.0.0.1:{Port}";

	private async Task AcceptLoopAsync()
	{
		while (!_stop.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (SocketException)
			{
				break;
			}

			_ = Task.Run(() => HandleClientAsync(client));
		}
	}

	private async Task HandleClientAsync(TcpClient client)
	{
		using (client)
		{
			await using var stream = client.GetStream();
			using var reader = new StreamReader(stream, Encoding.UTF8);
			var writeLock = new SemaphoreSlim(1, 1);

			while (!_stop.IsCancellationRequested)
			{
				string? line;
				try
				{
					line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
				}
				catch
				{
					break;
				}

				if (line is null)
				{
					break;
				}

				if (line.Length is 0 or > 4_000_000)
				{
					continue;
				}

				_ = Task.Run(async () =>
				{
					JsonObject? response = null;
					try
					{
						response = await HandleRequestAsync(line).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						response = Error(null, ex.Message);
					}

					if (response is null)
					{
						return;
					}

					var payload = response.ToJsonString() + "\n";
					var bytes = Encoding.UTF8.GetBytes(payload);
					await writeLock.WaitAsync().ConfigureAwait(false);
					try
					{
						await stream.WriteAsync(bytes).ConfigureAwait(false);
						await stream.FlushAsync().ConfigureAwait(false);
					}
					catch
					{
						// The client went away while a long poll was outstanding.
					}
					finally
					{
						writeLock.Release();
					}
				});
			}
		}
	}

	private async Task<JsonObject?> HandleRequestAsync(string line)
	{
		JsonObject? request;
		try
		{
			request = JsonNode.Parse(line) as JsonObject;
		}
		catch (JsonException)
		{
			return Error(null, "Invalid JSON.");
		}

		if (request is null)
		{
			return Error(null, "Invalid request.");
		}

		var id = request["id"];
		if (!TokenEquals(request["token"]))
		{
			return Error(id, "Unauthorized.");
		}

		var method = request["method"]?.GetValue<string>() ?? "";
		var parameters = request["params"] as JsonObject ?? new JsonObject();

		return method switch
		{
			"register" => Register(id, parameters),
			"unregister" => Unregister(id, parameters),
			"send" => Send(id, parameters),
			"recv" => await ReceiveAsync(id, parameters).ConfigureAwait(false),
			"waitReply" => await WaitReplyAsync(id, parameters).ConfigureAwait(false),
			"reply" => Reply(id, parameters),
			"peers" => Peers(id),
			_ => Error(id, $"Unknown method \"{method}\"."),
		};
	}

	private bool TokenEquals(JsonNode? node)
	{
		var presented = node?.GetValue<string>() ?? "";
		return string.Equals(presented, Token, StringComparison.Ordinal);
	}

	private JsonObject Register(JsonNode? id, JsonObject parameters)
	{
		var agentId = GetString(parameters, "agentId");
		if (string.IsNullOrWhiteSpace(agentId) || !IsValidId(agentId))
		{
			return Error(id, "A valid agentId is required.");
		}

		var parent = parameters["parentId"]?.GetValue<string>();
		var depth = parameters["depth"]?.GetValue<int>() ?? 0;

		lock (_gate)
		{
			_peers[agentId] = new PeerState { Id = agentId, Parent = parent, Depth = depth };
			return Ok(id, new JsonObject { ["agentId"] = agentId, ["peers"] = PeersArray() });
		}
	}

	private JsonObject Unregister(JsonNode? id, JsonObject parameters)
	{
		var agentId = GetString(parameters, "agentId");
		lock (_gate)
		{
			if (_peers.Remove(agentId, out var peer))
			{
				foreach (var waiter in peer.Waiters)
				{
					waiter.TrySetCanceled();
				}
			}

			foreach (var key in _askSenders.Where(pair => pair.Value == agentId).Select(pair => pair.Key).ToList())
			{
				_askSenders.Remove(key);
				_earlyReplies.Remove(key);
				if (_replyWaiters.Remove(key, out var replyWaiter))
				{
					replyWaiter.TrySetCanceled();
				}
			}
		}
		return Ok(id, new JsonObject());
	}

	private JsonObject Send(JsonNode? id, JsonObject parameters)
	{
		var from = GetString(parameters, "from");
		var to = GetString(parameters, "to");
		var text = GetString(parameters, "text");
		var wantReply = parameters["wantReply"]?.GetValue<bool>() == true;

		if (!IsValidId(from) || string.IsNullOrWhiteSpace(text))
		{
			return Error(id, "from and text are required.");
		}

		var messageId = $"m{Interlocked.Increment(ref _messageCounter)}";
		var message = new AgentMessage
		{
			Id = messageId,
			From = from,
			To = to,
			Text = text,
			WantReply = wantReply,
		};

		lock (_gate)
		{
			if (!_peers.ContainsKey(from))
			{
				return Error(id, $"Agent \"{from}\" is not registered.");
			}

			if (to == "*")
			{
				foreach (var peer in _peers.Values.Where(peer => peer.Id != from))
				{
					EnqueueLocked(peer.Id, message);
				}
				return Ok(id, new JsonObject { ["messageId"] = messageId });
			}

			if (!_peers.ContainsKey(to))
			{
				return Error(id, $"Agent \"{to}\" is not running.");
			}

			if (wantReply)
			{
				_askSenders[messageId] = from;
			}

			EnqueueLocked(to, message);
			return Ok(id, new JsonObject { ["messageId"] = messageId });
		}
	}

	private void EnqueueLocked(string agentId, AgentMessage message)
	{
		if (!_peers.TryGetValue(agentId, out var peer))
		{
			return;
		}

		var waiter = peer.Waiters.FirstOrDefault(candidate => !candidate.Task.IsCompleted);
		if (waiter is not null)
		{
			peer.Waiters.Remove(waiter);
			waiter.TrySetResult(message);
			return;
		}

		peer.Queue.Enqueue(message);
	}

	private async Task<JsonObject> ReceiveAsync(JsonNode? id, JsonObject parameters)
	{
		var agentId = GetString(parameters, "agentId");
		var waitMs = Math.Clamp(parameters["waitMs"]?.GetValue<int>() ?? 15000, 0, 60000);

		TaskCompletionSource<AgentMessage>? waiter = null;
		lock (_gate)
		{
			if (!_peers.TryGetValue(agentId, out var peer))
			{
				return Error(id, $"Agent \"{agentId}\" is not registered.");
			}

			if (peer.Queue.Count > 0)
			{
				var messages = new JsonArray();
				while (peer.Queue.Count > 0)
				{
					messages.Add(MessageNode(peer.Queue.Dequeue()));
				}
				return Ok(id, new JsonObject { ["messages"] = messages });
			}

			waiter = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
			peer.Waiters.Add(waiter);
		}

		var winner = await Task.WhenAny(waiter.Task, Task.Delay(waitMs)).ConfigureAwait(false);
		if (winner == waiter.Task)
		{
			var message = await waiter.Task.ConfigureAwait(false);
			return Ok(id, new JsonObject { ["messages"] = new JsonArray(MessageNode(message)) });
		}

		lock (_gate)
		{
			if (_peers.TryGetValue(agentId, out var peer))
			{
				peer.Waiters.Remove(waiter);
			}
		}

		return Ok(id, new JsonObject { ["messages"] = new JsonArray() });
	}

	private async Task<JsonObject> WaitReplyAsync(JsonNode? id, JsonObject parameters)
	{
		var messageId = GetString(parameters, "messageId");
		var waitMs = Math.Clamp(parameters["waitMs"]?.GetValue<int>() ?? 120000, 0, 600000);

		TaskCompletionSource<AgentMessage> waiter;
		lock (_gate)
		{
			if (_earlyReplies.Remove(messageId, out var early))
			{
				return ReplyNode(id, early);
			}

			waiter = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
			_replyWaiters[messageId] = waiter;
		}

		var winner = await Task.WhenAny(waiter.Task, Task.Delay(waitMs)).ConfigureAwait(false);
		if (winner == waiter.Task)
		{
			return ReplyNode(id, await waiter.Task.ConfigureAwait(false));
		}

		lock (_gate)
		{
			_replyWaiters.Remove(messageId);
		}

		return Ok(id, new JsonObject { ["timeout"] = true });
	}

	private static JsonObject ReplyNode(JsonNode? id, AgentMessage reply) => Ok(id, new JsonObject
	{
		["text"] = reply.Text,
		["from"] = reply.From,
		["messageId"] = reply.InReplyTo,
	});

	private JsonObject Reply(JsonNode? id, JsonObject parameters)
	{
		var from = GetString(parameters, "from");
		var inReplyTo = GetString(parameters, "messageId");
		var text = GetString(parameters, "text");

		lock (_gate)
		{
			if (!_askSenders.TryGetValue(inReplyTo, out var originalSender))
			{
				return Error(id, $"Message \"{inReplyTo}\" is not waiting for a reply.");
			}

			var reply = new AgentMessage
			{
				Id = $"m{Interlocked.Increment(ref _messageCounter)}",
				From = from,
				To = originalSender,
				Text = text,
				InReplyTo = inReplyTo,
			};

			_askSenders.Remove(inReplyTo);

			if (_replyWaiters.Remove(inReplyTo, out var waiter))
			{
				waiter.TrySetResult(reply);
			}
			else
			{
				_earlyReplies[inReplyTo] = reply;
			}

			return Ok(id, new JsonObject { ["messageId"] = reply.Id });
		}
	}

	private JsonObject Peers(JsonNode? id)
	{
		lock (_gate)
		{
			return Ok(id, new JsonObject { ["peers"] = PeersArray() });
		}
	}

	private JsonArray PeersArray()
	{
		var array = new JsonArray();
		foreach (var peer in _peers.Values.OrderBy(peer => peer.Id))
		{
			array.Add(new JsonObject
			{
				["id"] = peer.Id,
				["parent"] = peer.Parent,
				["depth"] = peer.Depth,
			});
		}
		return array;
	}

	private static JsonObject MessageNode(AgentMessage message) => new()
	{
		["id"] = message.Id,
		["from"] = message.From,
		["to"] = message.To,
		["text"] = message.Text,
		["wantReply"] = message.WantReply,
		["inReplyTo"] = message.InReplyTo,
		["at"] = message.At.ToString("O"),
	};

	private static JsonObject Ok(JsonNode? id, JsonObject value) => new()
	{
		["id"] = id?.DeepClone(),
		["ok"] = true,
		["value"] = value,
	};

	private static JsonObject Error(JsonNode? id, string message) => new()
	{
		["id"] = id?.DeepClone(),
		["ok"] = false,
		["error"] = message,
	};

	private static string GetString(JsonObject parameters, string name) =>
		parameters[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

	private static bool IsValidId(string id) =>
		id.Length is > 0 and <= 128 && id.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');

	public void Dispose()
	{
		_stop.Cancel();
		_listener.Stop();
		try
		{
			_acceptLoop.Wait(TimeSpan.FromSeconds(2));
		}
		catch
		{
			// Best effort.
		}
		_stop.Dispose();
	}
}
