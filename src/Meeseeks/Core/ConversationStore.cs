using System.Text.Json;
using System.Text.Json.Serialization;
using Meeseeks.Config;

namespace Meeseeks.Core;

/// <summary>One persisted conversation: metadata plus the full message list.</summary>
public sealed class PersistedConversation
{
	public int Version { get; set; } = 1;

	/// <summary>Filename stem, also used as the /resume argument.</summary>
	public string Id { get; set; } = "";

	/// <summary>First user message, trimmed; shown in /resume listings.</summary>
	public string Title { get; set; } = "";

	/// <summary>Model that produced the messages, so a resumed session can default to it.</summary>
	public string Model { get; set; } = "";

	/// <summary>Working directory the conversation ran in.</summary>
	public string Cwd { get; set; } = "";

	public DateTimeOffset Started { get; set; }

	public DateTimeOffset Updated { get; set; }

	public List<ChatMessage> Messages { get; set; } = [];
}

/// <summary>
/// Persists conversations as JSON under ~/.meeseeks/conversations. One file per conversation,
/// named by a GUID; the file stem is the resume id.
/// </summary>
public static class ConversationStore
{
	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter() },
	};

	public static string Directory => MeeseeksPaths.ConversationsDir;

	/// <summary>Filename stem for a new conversation: a GUID, unique even across simultaneous starts.</summary>
	public static string NewId() => Guid.NewGuid().ToString("D");

	/// <summary>Save (create or overwrite) a conversation file. Returns the id.</summary>
	public static string Save(PersistedConversation conversation)
	{
		MeeseeksPaths.Ensure();
		var id = string.IsNullOrEmpty(conversation.Id) ? NewId() : conversation.Id;
		conversation.Id = id;
		var path = PathFor(id);
		var json = JsonSerializer.Serialize(conversation, Options);
		File.WriteAllText(path, json);
		return id;
	}

	public static PersistedConversation? Load(string id)
	{
		var path = PathFor(id);
		if (!File.Exists(path))
		{
			return null;
		}

		var json = File.ReadAllText(path);
		return JsonSerializer.Deserialize<PersistedConversation>(json, Options);
	}

	/// <summary>Every saved conversation, newest first.</summary>
	public static IReadOnlyList<PersistedConversation> List()
	{
		MeeseeksPaths.Ensure();
		var result = new List<PersistedConversation>();
		foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
		{
			try
			{
				var conversation = JsonSerializer.Deserialize<PersistedConversation>(
					File.ReadAllText(path), Options);
				if (conversation is not null)
				{
					result.Add(conversation);
				}
			}
			catch (JsonException)
			{
				// Skip unreadable files rather than failing the whole listing.
			}
		}
		return [.. result.OrderByDescending(conversation => conversation.Updated)];
	}

	public static string PathFor(string id) =>
		Path.Combine(Directory, id + ".json");

	/// <summary>True when the id looks like a filename stem we would have written.</summary>
	public static bool IsValidId(string id) =>
		id.Length > 0 && id.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;
}