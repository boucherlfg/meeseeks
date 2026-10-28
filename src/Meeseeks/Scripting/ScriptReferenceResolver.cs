using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.CodeAnalysis;
using Meeseeks.Config;
using Meeseeks.Core;

namespace Meeseeks.Scripting;

public sealed record PreparedScript(
	string Code,
	ImmutableArray<PortableExecutableReference> References,
	IReadOnlyList<string> Packages);

/// <summary>
/// Handles #r directives before Roslyn sees the script:
///   #r "nuget: Newtonsoft.Json, 13.0.3"
///   #r "lib/MyLibrary.dll"
/// NuGet packages are restored once per package set and cached under ~/.meeseeks/nuget.
/// </summary>
public static class ScriptReferenceResolver
{
	private static readonly Regex Directive = new(
		@"^\s*#r\s+""(?<ref>[^""]+)""\s*(?://.*)?$",
		RegexOptions.Compiled);

	private static readonly Lazy<HashSet<string>> FrameworkAssemblies = new(BuildFrameworkSet, isThreadSafe: true);

	public static PreparedScript Prepare(string code, string cwd, CancellationToken cancellationToken)
	{
		var normalized = code.Replace("\r\n", "\n");
		var lines = normalized.Split('\n');
		var packageSpecs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		var localReferences = new List<string>();

		for (var index = 0; index < lines.Length; index++)
		{
			var match = Directive.Match(lines[index]);
			if (!match.Success)
			{
				continue;
			}

			var reference = match.Groups["ref"].Value.Trim();
			if (reference.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase))
			{
				var spec = reference["nuget:".Length..].Trim();
				if (spec.Length == 0)
				{
					throw new MeeseeksException("#r \"nuget:\" needs a package name.");
				}
				packageSpecs.Add(spec);
				lines[index] = "// " + lines[index].TrimStart();
				continue;
			}

			if (reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			{
				var fullPath = Path.IsPathRooted(reference)
					? reference
					: Path.GetFullPath(Path.Combine(cwd, reference));
				if (!File.Exists(fullPath))
				{
					throw new MeeseeksException($"#r reference not found: {fullPath}");
				}
				localReferences.Add(fullPath);
				lines[index] = "// " + lines[index].TrimStart();
			}
		}

		var references = ImmutableArray.CreateBuilder<PortableExecutableReference>();
		var packages = new List<string>();

		if (packageSpecs.Count > 0)
		{
			var resolved = NuGetReferenceResolver.Resolve(packageSpecs.ToList(), cancellationToken);
			packages.AddRange(resolved.Packages);
			foreach (var path in resolved.Paths)
			{
				references.Add(MetadataReference.CreateFromFile(path));
			}
		}

		foreach (var path in localReferences.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			references.Add(MetadataReference.CreateFromFile(path));
		}

		return new PreparedScript(string.Join("\n", lines), references.ToImmutable(), packages);
	}

	internal static bool IsFrameworkAssembly(string simpleName) =>
		FrameworkAssemblies.Value.Contains(simpleName);

	private static HashSet<string> BuildFrameworkSet()
	{
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)
			?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

		foreach (var path in trusted)
		{
			names.Add(Path.GetFileNameWithoutExtension(path));
		}
		return names;
	}
}

internal sealed record ResolvedPackages(List<string> Paths, List<string> Packages);

internal static class NuGetReferenceResolver
{
	private static readonly object Gate = new();
	private static readonly Dictionary<string, ResolvedPackages> Cache = new(StringComparer.Ordinal);

	public static ResolvedPackages Resolve(IReadOnlyList<string> specifications, CancellationToken cancellationToken)
	{
		var targetFramework = $"net{Environment.Version.Major}.0";
		var key = CacheKey(specifications, targetFramework);

		lock (Gate)
		{
			if (Cache.TryGetValue(key, out var cached))
			{
				return cached;
			}

			var resolved = ResolveCore(specifications, targetFramework, key, cancellationToken);
			Cache[key] = resolved;
			return resolved;
		}
	}

	private static ResolvedPackages ResolveCore(
		IReadOnlyList<string> specifications,
		string targetFramework,
		string key,
		CancellationToken cancellationToken)
	{
		var directory = Path.Combine(MeeseeksPaths.Home, "nuget", key);
		Directory.CreateDirectory(directory);

		var referencesFile = Path.Combine(directory, "refs.txt");
		var paths = TryLoadReferences(referencesFile);

		if (paths is null)
		{
			WriteProject(directory, specifications, targetFramework);
			RunRestore(directory, cancellationToken);
			paths = ReadReferencesFromAssets(directory, targetFramework);
			File.WriteAllLines(referencesFile, paths);
		}

		var filtered = FilterAndDeduplicate(paths);
		var packages = specifications.Select(ParseSpec).Select(spec => spec.Version is null ? spec.Id : $"{spec.Id} {spec.Version}").ToList();

		return new ResolvedPackages(filtered, packages);
	}

	private static List<string>? TryLoadReferences(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			var paths = File.ReadAllLines(path).Where(line => line.Trim().Length > 0).ToList();
			return paths.Count > 0 && paths.All(File.Exists) ? paths : null;
		}
		catch
		{
			return null;
		}
	}

	private static void WriteProject(string directory, IReadOnlyList<string> specifications, string targetFramework)
	{
		var builder = new StringBuilder();
		builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
		builder.AppendLine("  <PropertyGroup>");
		builder.AppendLine($"    <TargetFramework>{targetFramework}</TargetFramework>");
		builder.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
		builder.AppendLine("    <GenerateDocumentationFile>false</GenerateDocumentationFile>");
		builder.AppendLine("    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>");
		builder.AppendLine("    <NoWarn>NU1701;NU1603;NU1605;NU1702</NoWarn>");
		builder.AppendLine("  </PropertyGroup>");
		builder.AppendLine("  <ItemGroup>");

		foreach (var specification in specifications)
		{
			var (id, version) = ParseSpec(specification);
			var versionAttribute = version is null ? " Version=\"*\"" : $" Version=\"{XmlEscape(version)}\"";
			builder.AppendLine(
				$"    <PackageReference Include=\"{XmlEscape(id)}\"{versionAttribute} />");
		}

		builder.AppendLine("  </ItemGroup>");
		builder.AppendLine("</Project>");

		File.WriteAllText(Path.Combine(directory, "resolve.csproj"), builder.ToString());
		File.WriteAllLines(Path.Combine(directory, "specs.txt"), specifications);
	}

	private static void RunRestore(string directory, CancellationToken cancellationToken)
	{
		var project = Path.Combine(directory, "resolve.csproj");
		var startInfo = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = directory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		startInfo.ArgumentList.Add("restore");
		startInfo.ArgumentList.Add(project);
		startInfo.ArgumentList.Add("--nologo");
		startInfo.ArgumentList.Add("-v");
		startInfo.ArgumentList.Add("quiet");
		startInfo.Environment["DOTNET_NOLOGO"] = "1";
		startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

		using var process = new Process { StartInfo = startInfo };
		try
		{
			if (!process.Start())
			{
				throw new MeeseeksException("Could not start \"dotnet restore\".");
			}
		}
		catch (Exception ex) when (ex is not MeeseeksException)
		{
			throw new MeeseeksException(
				$"Could not run \"dotnet restore\". Is the .NET SDK on PATH? {ex.Message}");
		}

		var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

		try
		{
			process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch
			{
				// Already gone.
			}
			throw;
		}

		if (process.ExitCode != 0)
		{
			var details = (stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()).Trim();
			if (details.Length > 4000)
			{
				details = "…" + details[^4000..];
			}
			throw new MeeseeksException($"NuGet restore failed for {project}:\n{details}");
		}
	}

	private static List<string> ReadReferencesFromAssets(string directory, string targetFramework)
	{
		var assetsPath = Path.Combine(directory, "obj", "project.assets.json");
		if (!File.Exists(assetsPath))
		{
			throw new MeeseeksException($"NuGet restore produced no {assetsPath}.");
		}

		JsonNode? assets;
		try
		{
			assets = JsonNode.Parse(File.ReadAllText(assetsPath));
		}
		catch (JsonException ex)
		{
			throw new MeeseeksException($"Could not parse {assetsPath}: {ex.Message}");
		}

		if (assets?["targets"] is not JsonObject targets || assets["libraries"] is not JsonObject libraries)
		{
			throw new MeeseeksException($"Unexpected NuGet assets format in {assetsPath}.");
		}

		var target = (targets[targetFramework] as JsonObject)
			?? (targets.FirstOrDefault(pair => !pair.Key.Contains('/')).Value as JsonObject);

		if (target is null)
		{
			throw new MeeseeksException($"No {targetFramework} target in {assetsPath}.");
		}

		var packageFolders = (assets["packageFolders"] as JsonObject)?.Select(pair => pair.Key).ToList() ?? [];
		if (packageFolders.Count == 0)
		{
			var nugetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
				?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
			packageFolders.Add(nugetPackages);
		}

		var byAssembly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var package in target)
		{
			if (package.Value is not JsonObject entry)
			{
				continue;
			}

			var libraryPath = libraries[package.Key]?["path"]?.GetValue<string>();
			if (string.IsNullOrWhiteSpace(libraryPath))
			{
				continue;
			}

			var packageRoot = packageFolders
				.Select(folder => Path.Combine(folder, libraryPath.Replace('/', Path.DirectorySeparatorChar)))
				.FirstOrDefault(Directory.Exists);

			if (packageRoot is null)
			{
				continue;
			}

			// Compile assets first, then runtime assets (a runtime implementation beats a ref assembly).
			AddAssets(entry["compile"] as JsonObject, packageRoot, byAssembly, overwrite: false);
			AddAssets(entry["runtime"] as JsonObject, packageRoot, byAssembly, overwrite: true);

			if (entry["runtimeTargets"] is JsonObject runtimeTargets)
			{
				foreach (var asset in runtimeTargets)
				{
					if (asset.Value?["assetType"]?.GetValue<string>() == "runtime")
					{
						AddAssembly(asset.Key, packageRoot, byAssembly, overwrite: true);
					}
				}
			}
		}

		return byAssembly.Values
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static void AddAssets(
		JsonObject? assets,
		string packageRoot,
		Dictionary<string, string> byAssembly,
		bool overwrite)
	{
		if (assets is null)
		{
			return;
		}

		foreach (var asset in assets)
		{
			AddAssembly(asset.Key, packageRoot, byAssembly, overwrite);
		}
	}

	private static void AddAssembly(
		string relativePath,
		string packageRoot,
		Dictionary<string, string> byAssembly,
		bool overwrite)
	{
		if (!relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
			relativePath.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		var fileName = Path.GetFileName(relativePath);
		if (fileName.Equals("_._", StringComparison.Ordinal))
		{
			return;
		}

		var simpleName = Path.GetFileNameWithoutExtension(fileName);
		if (ScriptReferenceResolver.IsFrameworkAssembly(simpleName))
		{
			return;
		}

		var fullPath = Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(fullPath))
		{
			return;
		}

		if (!overwrite && byAssembly.ContainsKey(simpleName))
		{
			return;
		}

		byAssembly[simpleName] = fullPath;
	}

	private static List<string> FilterAndDeduplicate(IEnumerable<string> paths)
	{
		var byAssembly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in paths)
		{
			if (!File.Exists(path))
			{
				continue;
			}

			var simpleName = Path.GetFileNameWithoutExtension(path);
			if (ScriptReferenceResolver.IsFrameworkAssembly(simpleName))
			{
				continue;
			}

			byAssembly.TryAdd(simpleName, path);
		}

		return byAssembly.Values
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static (string Id, string? Version) ParseSpec(string specification)
	{
		var parts = specification.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
		{
			throw new MeeseeksException($"Invalid NuGet reference \"{specification}\".");
		}

		var id = parts[0];
		var version = parts.Length > 1 ? parts[1] : null;
		if (!Regex.IsMatch(id, @"^[A-Za-z0-9_.-]+$"))
		{
			throw new MeeseeksException($"Invalid NuGet package id \"{id}\".");
		}

		return (id, version);
	}

	private static string CacheKey(IReadOnlyList<string> specifications, string targetFramework)
	{
		var text = targetFramework + "\n" + string.Join("\n", specifications.OrderBy(spec => spec, StringComparer.OrdinalIgnoreCase));
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
		return Convert.ToHexString(hash)[..20].ToLowerInvariant();
	}

	private static string XmlEscape(string value) => SecurityElement.Escape(value) ?? value;
}
