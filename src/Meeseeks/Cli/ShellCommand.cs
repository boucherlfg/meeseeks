using System.Diagnostics;

namespace Meeseeks.Cli;

/// <summary>Runs a console command typed at the REPL with the <c>!</c> prefix.</summary>
public static class ShellCommand
{
	/// <summary>
	/// Runs <paramref name="command"/> through the platform shell, streaming its output to the console.
	/// Returns the process exit code, or 1 when the command could not be started.
	/// </summary>
	public static async Task<int> RunAsync(string command, string cwd, ConsoleUi ui)
	{
		var start = new ProcessStartInfo
		{
			WorkingDirectory = cwd,
			UseShellExecute = false,
		};

		if (OperatingSystem.IsWindows())
		{
			// cmd.exe /c keeps built-ins (dir, echo, set) and PATH lookup working.
			start.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
			start.ArgumentList.Add("/c");
			start.ArgumentList.Add(command);
		}
		else
		{
			start.FileName = "/bin/sh";
			start.ArgumentList.Add("-c");
			start.ArgumentList.Add(command);
		}

		using var process = new Process { StartInfo = start };
		try
		{
			process.Start();
		}
		catch (Exception ex)
		{
			ui.Error($"could not run {command}: {ex.Message}");
			return 1;
		}

		// The child inherits the console, so its output streams live; nothing to pump.
		await process.WaitForExitAsync();
		return process.ExitCode;
	}
}
