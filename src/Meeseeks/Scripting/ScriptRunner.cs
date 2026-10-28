using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;
using Meeseeks.Core;

namespace Meeseeks.Scripting;

public sealed record ScriptOutcome(
	string Output,
	object? Result,
	string? Error,
	IReadOnlyList<string> Trace,
	TimeSpan Duration);

/// <summary>Roslyn C# scripting with a warm compilation cache.</summary>
public sealed class ScriptRunner
{
	private readonly ScriptOptions _options;

	public ScriptRunner()
	{
		var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
		var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)
			?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

		foreach (var path in trusted.Concat(AppDomain.CurrentDomain.GetAssemblies()
			.Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
			.Select(assembly => assembly.Location)))
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				continue;
			}

			try
			{
				references.TryAdd(path, MetadataReference.CreateFromFile(path));
			}
			catch (IOException)
			{
				// Duplicate or unreadable; not worth failing over.
			}
		}

		_options = ScriptOptions.Default
			.WithReferences(references.Values)
			.WithImports(
				"System",
				"System.IO",
				"System.Linq",
				"System.Collections.Generic",
				"System.Collections.Concurrent",
				"System.Threading",
				"System.Threading.Tasks",
				"System.Diagnostics",
				"System.Globalization",
				"System.Net",
				"System.Net.Http",
				"System.Net.Http.Json",
				"System.Security.Cryptography",
				"System.Text",
				"System.Text.Json",
				"System.Text.Json.Nodes",
				"System.Text.Json.Serialization",
				"System.Text.RegularExpressions",
				"Meeseeks",
				"Meeseeks.Core",
				"Meeseeks.Agents",
				"Meeseeks.Scripting")
			.WithLanguageVersion(LanguageVersion.Latest)
			.WithOptimizationLevel(OptimizationLevel.Release)
			.WithAllowUnsafe(true);
	}

	public async Task<ScriptOutcome> RunAsync(
		string code,
		ScriptRuntime runtime,
		int timeoutMs,
		CancellationToken cancellationToken)
	{
		runtime.ClearTrace();
		var stopwatch = Stopwatch.StartNew();
		var output = new StringWriter();
		var originalOut = Console.Out;
		var originalError = Console.Error;

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		if (timeoutMs > 0)
		{
			timeout.CancelAfter(timeoutMs);
		}

		object? result = null;
		string? error = null;

		try
		{
			Console.SetOut(output);
			Console.SetError(output);

			PreparedScript prepared;
			try
			{
				prepared = ScriptReferenceResolver.Prepare(code, runtime.Cwd, timeout.Token);
			}
			catch (OperationCanceledException)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				error = $"Script timed out while resolving references after {timeoutMs} ms.";
				return new ScriptOutcome(output.ToString(), null, error, runtime.TraceSnapshot(), stopwatch.Elapsed);
			}
			catch (Exception ex)
			{
				error = Describe(ex);
				return new ScriptOutcome(output.ToString(), null, error, runtime.TraceSnapshot(), stopwatch.Elapsed);
			}

			foreach (var package in prepared.Packages)
			{
				runtime.AddTrace($"#r nuget: {package}");
			}

			var scriptOptions = prepared.References.IsEmpty
				? _options
				: _options.AddReferences(prepared.References);

			var rewrittenCode = RewriteAwaits(prepared.Code);
			if (Environment.GetEnvironmentVariable("MEESEEKS_DEBUG_SCRIPT") is not null)
			{
				originalError.WriteLine("[meeseeks] rewritten script:");
				originalError.WriteLine(rewrittenCode);
			}

			Script<object?> script;
			try
			{
				script = CSharpScript.Create<object?>(rewrittenCode, scriptOptions, typeof(ScriptGlobals));
			}
			catch (Exception ex)
			{
				error = Describe(ex);
				return new ScriptOutcome(output.ToString(), null, error, runtime.TraceSnapshot(), stopwatch.Elapsed);
			}

			var errors = script.Compile()
				.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
				.ToList();

			if (errors.Count > 0)
			{
				error = "Compilation failed:\n" + string.Join("\n", errors.Select(FormatDiagnostic));
			}
			else
			{
				ScriptCancellation.Token = timeout.Token;
				try
				{
					var state = await script
						.RunAsync(
							new ScriptGlobals(runtime),
							catchException: ex => ex is not OperationCanceledException,
							cancellationToken: timeout.Token)
						.ConfigureAwait(false);

					result = state.ReturnValue;
					if (state.Exception is not null)
					{
						error = Describe(state.Exception);
					}
				}
				catch (OperationCanceledException)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					error = $"Script timed out after {timeoutMs} ms.";
				}
				catch (CompilationErrorException ex)
				{
					error = "Compilation failed:\n" + string.Join(
						"\n",
						ex.Diagnostics
							.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
							.Select(FormatDiagnostic));
				}
				catch (Exception ex)
				{
					error = Describe(ex);
				}
				finally
				{
					ScriptCancellation.Token = default;
				}
			}
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
			runtime.StopDangling();
		}

		stopwatch.Stop();
		return new ScriptOutcome(output.ToString(), result, error, runtime.TraceSnapshot(), stopwatch.Elapsed);
	}

	/// <summary>
	/// Rewrites every await to CancellableAwait.Wait(expr). Task/ValueTask waits then observe
	/// the script token, so Esc and timeouts stop even when the awaited operation ignores it.
	/// </summary>
	private static string RewriteAwaits(string code)
	{
		var tree = CSharpSyntaxTree.ParseText(
			code,
			new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Script));
		var rewritten = new AwaitRewriter().Visit(tree.GetRoot());
		return rewritten?.ToFullString() ?? code;
	}

	private sealed class AwaitRewriter : CSharpSyntaxRewriter
	{
		public override SyntaxNode? VisitAwaitExpression(AwaitExpressionSyntax node)
		{
			var visited = (AwaitExpressionSyntax)base.VisitAwaitExpression(node)!;
			var expression = visited.Expression;
			var call = SyntaxFactory.InvocationExpression(
				SyntaxFactory.MemberAccessExpression(
					SyntaxKind.SimpleMemberAccessExpression,
					SyntaxFactory.ParseExpression("Meeseeks.Scripting.CancellableAwait"),
					SyntaxFactory.IdentifierName("Wait")),
				SyntaxFactory.ArgumentList(
					SyntaxFactory.SingletonSeparatedList(
						SyntaxFactory.Argument(expression.WithoutTrivia()))));
			return visited.WithExpression(call.WithTriviaFrom(expression));
		}
	}

	private static string FormatDiagnostic(Diagnostic diagnostic)
	{
		var span = diagnostic.Location.GetLineSpan();
		var position = span.IsValid
			? $" (line {span.StartLinePosition.Line + 1}, column {span.StartLinePosition.Character + 1})"
			: "";
		return $"{diagnostic.Id}{position}: {diagnostic.GetMessage()}";
	}

	private static string Describe(Exception exception)
	{
		if (exception is MeeseeksException)
		{
			return exception.Message;
		}

		var text = new StringBuilder($"{exception.GetType().Name}: {exception.Message}");
		if (exception.InnerException is not null)
		{
			text.Append($"\nCaused by {exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
		}

		if (exception.StackTrace is { Length: > 0 } stack)
		{
			var frames = stack
				.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(frame => !frame.Contains("Microsoft.CodeAnalysis", StringComparison.Ordinal))
				.Take(8);
			text.Append('\n').AppendJoin('\n', frames);
		}

		return text.ToString();
	}
}
