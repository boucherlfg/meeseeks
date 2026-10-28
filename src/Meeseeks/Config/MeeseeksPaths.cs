namespace Meeseeks.Config;

/// <summary>Everything lives under ~/.meeseeks so the harness has one obvious home.</summary>
public static class MeeseeksPaths
{
	public static string Home =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".meeseeks");

	public static string ProviderFile => Path.Combine(Home, "provider.json");
	public static string McpFile => Path.Combine(Home, "mcp.json");
	public static string SkillsDir => Path.Combine(Home, "skills");
	public static string TmpDir => Path.Combine(Home, "tmp");
	public static string LogsDir => Path.Combine(Home, "logs");
	public static string ConversationsDir => Path.Combine(Home, "conversations");

	public static void Ensure()
	{
		Directory.CreateDirectory(Home);
		Directory.CreateDirectory(TmpDir);
		Directory.CreateDirectory(LogsDir);
		Directory.CreateDirectory(ConversationsDir);
	}
}
