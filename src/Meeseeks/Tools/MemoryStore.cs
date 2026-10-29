using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meeseeks.Core;

namespace Meeseeks.Tools;

/// <summary>
/// A per-folder key-value store backed by `.meeseeks/memory.json` in the working directory.
/// Values are arbitrary JSON; writes are serialized with a lock file and applied atomically
/// so concurrent agents in the same folder cannot corrupt the store.
/// </summary>
public sealed class MemoryStore
{
	public const int MaxValueBytes = 64 * 1024;
	public const int MaxEntries = 256;

	private static readonly string DirName = ".meeseeks";
	private static readonly string FileName = "memory.json";
	private static readonly string LockName = "memory.lock";

	private readonly string _directory;

	public MemoryStore(string cwd) => _directory = Path.Combine(cwd, DirName);

	private string FilePath => Path.Combine(_directory, FileName);
	private string LockPath => Path.Combine(_directory, LockName);

	/// <summary>Read a value; throws when the key is missing.</summary>
	public JsonNode Get(string key)
	{
		var store = Load();
		if (!store.TryGetValue(key, out var value))
		{
			throw new MeeseeksException($"memory: key \"{key}\" not found. Use list to see stored keys.");
		}
		return value;
	}

	/// <summary>Insert or update a key. Enforces the per-value and per-store caps.</summary>
	public void Set(string key, JsonNode value)
	{
		var serialized = value.ToJsonString(JsonSerializerOptions);
		var bytes = Encoding.UTF8.GetByteCount(serialized);
		if (bytes > MaxValueBytes)
		{
			throw new MeeseeksException(
				$"memory: value for \"{key}\" is {bytes:N0} bytes; the limit is {MaxValueBytes:N0} bytes per value.");
		}

		WithLock(() =>
		{
			var store = Load();
			var isNew = !store.ContainsKey(key);
			if (isNew && store.Count >= MaxEntries)
			{
				throw new MeeseeksException(
					$"memory: the store already holds {MaxEntries} entries; delete a key before adding \"{key}\".");
			}

			store[key] = JsonNode.Parse(serialized)!;
			Save(store);
			return isNew ? "created" : "updated";
		});
	}

	/// <summary>Remove a key; deleting a missing key is a no-op success.</summary>
	public bool Delete(string key)
	{
		return WithLock(() =>
		{
			var store = Load();
			if (!store.Remove(key, out var removed))
			{
				return false;
			}

			Save(store);
			return true;
		});
	}

	/// <summary>Key metadata for `list`: key, byte size, and a short type label.</summary>
	public IReadOnlyList<(string Key, int Bytes, string Type)> List()
	{
		return Load()
			.OrderBy(entry => entry.Key, StringComparer.Ordinal)
			.Select(entry => (entry.Key, Encoding.UTF8.GetByteCount(entry.Value.ToJsonString(JsonSerializerOptions)), Describe(entry.Value)))
			.ToList();
	}

	private static string Describe(JsonNode value) => value switch
	{
		JsonObject => "object",
		JsonArray => "array",
		JsonValue v when v.TryGetValue<string>(out _) => "string",
		JsonValue v when v.TryGetValue<bool>(out _) => "bool",
		JsonValue v when v.TryGetValue<double>(out _) => "number",
		_ => "value",
	};

	/// <summary>Load the store; a missing or unreadable file is an empty store.</summary>
	private Dictionary<string, JsonNode> Load()
	{
		try
		{
			if (!File.Exists(FilePath))
			{
				return [];
			}

			var text = File.ReadAllText(FilePath);
			if (JsonNode.Parse(text) is not JsonObject root)
			{
				return [];
			}

			var store = new Dictionary<string, JsonNode>();
			foreach (var (key, value) in root)
			{
				if (value is not null)
				{
					store[key] = value;
				}
			}
			return store;
		}
		catch (Exception)
		{
			// A corrupt file should not brick the agent; treat it as empty so `set` can rebuild.
			return [];
		}
	}

	private void Save(Dictionary<string, JsonNode> store)
	{
		Directory.CreateDirectory(_directory);
		var root = new JsonObject();
		foreach (var (key, value) in store.OrderBy(entry => entry.Key, StringComparer.Ordinal))
		{
			root[key] = JsonNode.Parse(value.ToJsonString(JsonSerializerOptions))!;
		}

		FilePaths.WriteAtomic(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
	}

	/// <summary>Serialize writers with a lock file so concurrent agents cannot interleave read-modify-write cycles.</summary>
	private T WithLock<T>(Func<T> action)
	{
		Directory.CreateDirectory(_directory);
		var strategy = new LockFileStrategy(LockPath);
		try
		{
			strategy.Acquire();
			return action();
		}
		finally
		{
			strategy.Release();
		}
	}

	private static readonly JsonSerializerOptions JsonSerializerOptions = new();

	/// <summary>Spin-waiting lock over an exclusive-open lock file, with a bounded wait.</summary>
	private sealed class LockFileStrategy
	{
		private readonly string _path;
		private FileStream? _stream;

		public LockFileStrategy(string path) => _path = path;

		public void Acquire()
		{
			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (true)
			{
				try
				{
					_stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
					return;
				}
				catch (IOException) when (DateTime.UtcNow < deadline)
				{
					Thread.Sleep(25);
				}
			}
		}

		public void Release()
		{
			_stream?.Dispose();
			_stream = null;
			try
			{
				File.Delete(_path);
			}
			catch (Exception)
			{
				// A leftover lock file is harmless; the next acquire opens it exclusively.
			}
		}
	}
}