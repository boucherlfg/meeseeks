namespace Meeseeks.Cli;

/// <summary>
/// Polls the terminal for Esc while a turn is running. Cancelling aborts the in-flight
/// model request and tool call, not the conversation.
/// </summary>
internal sealed class EscapeWatcher : IDisposable
{
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _task;

	public EscapeWatcher(Action onEscape)
	{
		if (Console.IsInputRedirected)
		{
			_task = Task.CompletedTask;
			return;
		}

		_task = Task.Run(() => LoopAsync(onEscape));
	}

	private async Task LoopAsync(Action onEscape)
	{
		while (!_stop.IsCancellationRequested)
		{
			try
			{
				if (Console.KeyAvailable)
				{
					var key = Console.ReadKey(intercept: true);
					if (key.Key == ConsoleKey.Escape)
					{
						onEscape();
						return;
					}
				}
			}
			catch (InvalidOperationException)
			{
				return;
			}
			catch (IOException)
			{
				return;
			}

			try
			{
				await Task.Delay(25, _stop.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	public void Dispose()
	{
		_stop.Cancel();
		try
		{
			_task.Wait(TimeSpan.FromMilliseconds(200));
		}
		catch
		{
			// Best effort.
		}
		_stop.Dispose();
	}
}
