using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meeseeks.Config;
using Meeseeks.Core;
using Meeseeks.Scripting;

namespace Meeseeks.Agents;

/// <summary>Starts and supervises child meeseeks processes.</summary>
public sealed class AgentRuntime : IDisposable
{
	private readonly ProviderConfig _config;
	private readonly BrokerClient? _broker;
	private SkillCatalog _skills;
	private readonly SemaphoreSlim _slots;
	private readonly CancellationTokenSource _stop = new();
	private readonly object _handlesGate = new();
	private readonly List<AgentHandle> _handles = new();
	private int _counter;

	public string AgentId { get; }
	public int Depth { get; }
	public string Cwd { get; }
	public bool McpEnabled { get; }

	public AgentRuntime(
		ProviderConfig config,
		string agentId,
		int depth,
		string cwd,
		BrokerClient? broker,
		SkillCatalog skills,
		bool mcpEnabled)
	{
		_config = config;
		AgentId = agentId;
		Depth = depth;
		Cwd = cwd;
		_broker = broker;
		_skills = skills;
		McpEnabled = mcpEnabled;
		_slots = new SemaphoreSlim(Math.Max(1, config.MaxParallelSubagents));
	}

	public async Task<AgentHandle> StartAsync(
		string prompt,
		SpawnOptions? options,
		CancellationToken cancellationToken)
	{
		options ??= new SpawnOptions();
		if (string.IsNullOrWhiteSpace(prompt))
		{
			throw new MeeseeksException("A subagent needs a non-empty prompt.");
		}

		if (_broker is null)
		{
			throw new MeeseeksException(
				"Subagents are unavailable because this agent has no broker connection.");
		}

		var childDepth = Depth + 1;
		if (childDepth > _config.MaxDepth)
		{
			throw new MeeseeksException(
				$"Subagent depth limit reached (max {_config.MaxDepth}). Do this work directly, or raise maxDepth in provider.json.");
		}

		await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

		var agentId = $"{AgentId}.{Interlocked.Increment(ref _counter)}";
		var tempDir = Path.Combine(MeeseeksPaths.TmpDir, $"{agentId}-{Guid.NewGuid():n}");
		Directory.CreateDirectory(tempDir);

		var promptFile = Path.Combine(tempDir, "prompt.txt");
		var resultFile = Path.Combine(tempDir, "result.json");
		var systemFile = Path.Combine(tempDir, "system.txt");
		var imagesFile = Path.Combine(tempDir, "images.json");
		var extraSystem = BuildExtraSystem(options);

		try
		{
			await File.WriteAllTextAsync(promptFile, prompt, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(extraSystem))
			{
				await File.WriteAllTextAsync(systemFile, extraSystem, Encoding.UTF8, cancellationToken)
					.ConfigureAwait(false);
			}

			var images = await LoadImagesAsync(options.Images, cancellationToken).ConfigureAwait(false);
			if (images.Count > 0)
			{
				await File.WriteAllTextAsync(imagesFile, SerializeImages(images), Encoding.UTF8, cancellationToken)
					.ConfigureAwait(false);
			}

			var (fileName, prefixArgs) = LaunchCommand();
			var startInfo = new ProcessStartInfo
			{
				FileName = fileName,
				WorkingDirectory = Cwd,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};

			foreach (var arg in prefixArgs)
			{
				startInfo.ArgumentList.Add(arg);
			}

			void Add(string name, string value)
			{
				startInfo.ArgumentList.Add(name);
				startInfo.ArgumentList.Add(value);
			}

			startInfo.ArgumentList.Add("--internal-child");
			Add("--agent-id", agentId);
			Add("--parent", AgentId);
			Add("--depth", childDepth.ToString());
			Add("--max-depth", _config.MaxDepth.ToString());
			Add("--cwd", Cwd);
			Add("--broker-url", _broker.Url);
			Add("--broker-token", _broker.Token);
			Add("--prompt-file", promptFile);
			Add("--result-file", resultFile);
			if (!string.IsNullOrWhiteSpace(extraSystem))
			{
				Add("--system-file", systemFile);
			}
			if (images.Count > 0)
			{
				Add("--images-file", imagesFile);
			}
			Add("--model", string.IsNullOrWhiteSpace(options.Model) ? _config.Model : options.Model);
			var think = options.Think.HasValue
				? (options.Think.Value ? ThinkSetting.On : ThinkSetting.Off)
				: _config.Think;
			Add("--think", think?.ToString() ?? "default");
			if (!McpEnabled)
			{
				startInfo.ArgumentList.Add("--no-mcp");
			}
			startInfo.Environment["MEESEEKS_CHILD"] = "1";

			var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
			var started = Stopwatch.StartNew();
			if (!process.Start())
			{
				throw new MeeseeksException($"Could not start subagent {agentId}.");
			}

			var handle = new AgentHandle(
				agentId,
				process,
				resultFile,
				tempDir,
				_broker,
				_slots,
				options.TimeoutMs ?? Math.Max(60, _config.SubagentTimeoutSeconds) * 1000,
				started,
				cancellationToken);

			lock (_handlesGate)
			{
				_handles.Add(handle);
			}

			_ = handle.Completion.ContinueWith(
				completed =>
				{
					_ = completed.Exception;
					lock (_handlesGate)
					{
						_handles.Remove(handle);
					}
				},
				TaskScheduler.Default);

			return handle;
		}
		catch
		{
			_slots.Release();
			TryDelete(tempDir);
			throw;
		}
	}

	/// <summary>Load every image reference, failing the spawn when one cannot be read.</summary>
	private async Task<IReadOnlyList<ImageAttachment>> LoadImagesAsync(
		string[]? references,
		CancellationToken cancellationToken)
	{
		if (references is not { Length: > 0 })
		{
			return [];
		}

		var images = new List<ImageAttachment>(references.Length);
		foreach (var reference in references)
		{
			if (string.IsNullOrWhiteSpace(reference))
			{
				continue;
			}

			try
			{
				images.Add(await ImageSource.LoadAsync(reference, Cwd, cancellationToken).ConfigureAwait(false));
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new MeeseeksException($"Could not attach image \"{reference}\": {ex.Message}");
			}
		}

		return images;
	}

	/// <summary>The images file the child reads: base64 plus the display metadata.</summary>
	private static string SerializeImages(IReadOnlyList<ImageAttachment> images)
	{
		var array = new JsonArray();
		foreach (var image in images)
		{
			array.Add(new JsonObject
			{
				["base64"] = image.Base64,
				["mediaType"] = image.MediaType,
				["source"] = image.Source,
			});
		}
		return array.ToJsonString();
	}

	private string BuildExtraSystem(SpawnOptions options)
	{
		var parts = new List<string>();

		foreach (var name in options.Skills ?? [])
		{
			var skill = _skills.Get(name)
				?? throw new MeeseeksException($"Unknown skill \"{name}\". Available: {_skills.DescribeAvailable()}");
			parts.Add($"## Skill: {skill.Name}\n\nSkill directory: {skill.BaseDir}\n\n{skill.Body}");
		}

		if (!string.IsNullOrWhiteSpace(options.SystemPrompt))
		{
			parts.Add("## Additional instructions\n\n" + options.SystemPrompt.Trim());
		}

		return string.Join("\n\n", parts);
	}

	/// <summary>Point subagent skill resolution at a reloaded catalog.</summary>
	public void UpdateSkills(SkillCatalog skills) => _skills = skills;

	public void StopAll()
	{
		AgentHandle[] handles;
		lock (_handlesGate)
		{
			handles = _handles.ToArray();
		}

		foreach (var handle in handles)
		{
			handle.Stop();
		}
	}

	public void Dispose()
	{
		_stop.Cancel();
		StopAll();
		_stop.Dispose();
	}

	internal static (string FileName, string[] PrefixArgs) LaunchCommand()
	{
		var processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath))
		{
			var assembly = Assembly.GetEntryAssembly()?.Location
				?? throw new MeeseeksException("Could not locate the meeseeks executable.");
			return ("dotnet", [assembly]);
		}

		var fileName = Path.GetFileNameWithoutExtension(processPath);
		if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
		{
			var assembly = Assembly.GetEntryAssembly()?.Location
				?? throw new MeeseeksException("Could not locate the meeseeks assembly.");
			return (processPath, [assembly]);
		}

		return (processPath, []);
	}

	internal static void TryDelete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
		catch
		{
			// Best effort: Windows may still hold a lock on the result file.
		}
	}
}

/// <summary>A running child meeseeks process, exposed to scripts.</summary>
[Api("A running child process returned by Agent.StartAsync. Wait for it, message it, or stop it.")]
public sealed class AgentHandle
{
	private readonly Process _process;
	private readonly BrokerClient _broker;
	private readonly SemaphoreSlim _slots;
	private readonly string _resultFile;
	private readonly string _tempDir;
	private readonly Stopwatch _stopwatch;
	private readonly CancellationTokenRegistration _cancellation;
	private readonly Timer _timer;
	private readonly Task<string> _stdout;
	private readonly Task<string> _stderr;
	private readonly Task<AgentResult> _completion;
	private int _stopRequested;
	private int _timedOut;

	[Api("The child's agent id, for example root.1.", Order = 0)]
	public string Id { get; }

	[Api("Short label supplied in SpawnOptions.", Order = 1)]
	public string Label { get; }

	internal AgentHandle(
		string id,
		Process process,
		string resultFile,
		string tempDir,
		BrokerClient broker,
		SemaphoreSlim slots,
		int timeoutMs,
		Stopwatch stopwatch,
		CancellationToken cancellationToken)
	{
		Id = id;
		Label = id;
		_process = process;
		_resultFile = resultFile;
		_tempDir = tempDir;
		_broker = broker;
		_slots = slots;
		_stopwatch = stopwatch;
		_stdout = ReadBoundedAsync(process.StandardOutput, 256 * 1024);
		_stderr = ReadBoundedAsync(process.StandardError, 256 * 1024);
		_cancellation = cancellationToken.Register(Stop);

		_timer = new Timer(_ =>
		{
			Interlocked.Exchange(ref _timedOut, 1);
			Stop();
		}, null, Math.Max(1000, timeoutMs), Timeout.Infinite);

		_completion = Task.Run(CompleteAsync);
	}

	[Api("Completes with the child's final result.", Order = 10)]
	public Task<AgentResult> Completion => _completion;

	[Api("True when the child has finished.", Order = 11)]
	public bool IsCompleted => _completion.IsCompleted;

	[Api("Wait for the child and return its final result.", Order = 12)]
	public Task<AgentResult> WaitAsync(CancellationToken cancellationToken = default) =>
		_completion.WaitAsync(cancellationToken);

	[Api("Send a message to this child.", Order = 13)]
	public Task SendAsync(string text) => _broker.SendAsync(Id, text);

	[Api("Ask this child a question and wait for its reply.", Order = 14)]
	public Task<string> AskAsync(string text, int timeoutMs = 120000) =>
		_broker.AskAsync(Id, text, timeoutMs);

	[Api("Stop the child process tree.", Order = 15)]
	public void Stop()
	{
		if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
		{
			return;
		}

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
	}

	private async Task<AgentResult> CompleteAsync()
	{
		try
		{
			await _process.WaitForExitAsync().ConfigureAwait(false);
			var stdout = await _stdout.ConfigureAwait(false);
			var stderr = await _stderr.ConfigureAwait(false);
			_stopwatch.Stop();

			AgentResult? result = null;
			if (File.Exists(_resultFile))
			{
				try
				{
					result = JsonSerializer.Deserialize<AgentResult>(await File.ReadAllTextAsync(_resultFile).ConfigureAwait(false),
						new JsonSerializerOptions(JsonSerializerDefaults.Web));
				}
				catch (JsonException)
				{
					// Fall through to the error below.
				}
			}

			if (Interlocked.CompareExchange(ref _timedOut, 0, 1) == 1)
			{
				throw new MeeseeksException($"Subagent {Id} timed out after {_stopwatch.Elapsed.TotalSeconds:0}s.");
			}

			if (Interlocked.CompareExchange(ref _stopRequested, 0, 1) == 1)
			{
				throw new MeeseeksException($"Subagent {Id} was stopped.");
			}

			if (result is null)
			{
				var detail = Tail(stderr, 4000);
				var outDetail = Tail(stdout, 1000);
				throw new MeeseeksException(
					$"Subagent {Id} exited with code {_process.ExitCode} without a result." +
					(detail.Length > 0 ? $"\n{detail}" : outDetail.Length > 0 ? $"\n{outDetail}" : ""));
			}

			if (!string.IsNullOrWhiteSpace(result.Error))
			{
				var detail = Tail(stderr, 4000);
				throw new MeeseeksException(
					$"Subagent {Id} failed: {result.Error}" + (detail.Length > 0 ? $"\n{detail}" : ""));
			}

			result.AgentId = Id;
			result.DurationMs = (long)_stopwatch.ElapsedMilliseconds;
			return result;
		}
		finally
		{
			_timer.Dispose();
			_cancellation.Dispose();
			_slots.Release();
			AgentRuntime.TryDelete(_tempDir);
		}
	}

	private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxChars)
	{
		var builder = new StringBuilder();
		var buffer = new char[8192];
		while (true)
		{
			var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
			if (read == 0)
			{
				break;
			}
			if (builder.Length < maxChars)
			{
				builder.Append(buffer, 0, Math.Min(read, maxChars - builder.Length));
			}
		}
		return builder.ToString();
	}

	private static string Tail(string text, int max) =>
		text.Length <= max ? text.Trim() : "…" + text[^max..].Trim();
}
