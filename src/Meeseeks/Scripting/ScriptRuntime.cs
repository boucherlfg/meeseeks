using Meeseeks.Agents;
using Meeseeks.Mcp;

namespace Meeseeks.Scripting;

/// <summary>Everything a script can reach. One instance per agent process.</summary>
public sealed class ScriptRuntime
{
	public required string AgentId { get; init; }
	public required int Depth { get; init; }
	public required int MaxDepth { get; init; }
	public required string Cwd { get; init; }
	public required string Model { get; set; }
	public required CancellationToken CancellationToken { get; set; }
	public required AgentRuntime Agents { get; init; }
	public required SkillCatalog SkillCatalog { get; set; }
	public required McpManager Mcp { get; set; }
	public BrokerClient? Broker { get; init; }

	/// <summary>Short label of the running script, shown by the console UI while it runs.</summary>
	public string? Purpose { get; set; }

	public List<AgentHandle> Handles { get; } = new();
	public List<string> Trace { get; } = new();

	public void Track(AgentHandle handle) => Handles.Add(handle);

	public void ClearTrace()
	{
		lock (Trace)
		{
			Trace.Clear();
		}
	}

	public void AddTrace(string line)
	{
		lock (Trace)
		{
			Trace.Add(line);
		}
	}

	public IReadOnlyList<string> TraceSnapshot()
	{
		lock (Trace)
		{
			return Trace.ToArray();
		}
	}

	public IReadOnlyList<AgentMessage> Messages => Broker?.History() ?? [];

	public async Task<AgentMessage?> WaitForMessageAsync(int timeoutMs, CancellationToken cancellationToken)
	{
		if (Broker is null)
		{
			throw new InvalidOperationException("This process has no message broker.");
		}
		return await Broker.WaitForMessageAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Agents started but never awaited do not outlive the script that started them.</summary>
	public void StopDangling()
	{
		foreach (var handle in Handles.Where(handle => !handle.IsCompleted))
		{
			handle.Stop();
		}
		Handles.Clear();
	}
}
