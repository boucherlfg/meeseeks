namespace Meeseeks.Scripting;

/// <summary>
/// Carries the active script token through async continuations. Script awaits are rewritten
/// to go through <see cref="CancellableAwait"/> so they observe it.
/// </summary>
public static class ScriptCancellation
{
	// Scripts are serialized by the tool gate, so one process-wide token is enough.
	// (AsyncLocal did not flow through Roslyn's script execution context.)
	private static CancellationToken _token;

	public static CancellationToken Token
	{
		get => _token;
		set => _token = value;
	}
}

/// <summary>
/// Overloads used by the await rewriter. Task and ValueTask waits become cancellable; custom
/// awaitables fall through unchanged.
/// </summary>
public static class CancellableAwait
{
	public static Task Wait(Task task) => task.WaitAsync(ScriptCancellation.Token);

	public static Task<T> Wait<T>(Task<T> task) => task.WaitAsync(ScriptCancellation.Token);

	public static ValueTask Wait(ValueTask task) =>
		new(task.AsTask().WaitAsync(ScriptCancellation.Token));

	public static ValueTask<T> Wait<T>(ValueTask<T> task) =>
		new(task.AsTask().WaitAsync(ScriptCancellation.Token));

	/// <summary>Fallback for custom awaitables: compile, but cannot be cancelled.</summary>
	public static T Wait<T>(T awaitable) => awaitable;
}
