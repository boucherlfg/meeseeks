using System.Globalization;
using System.Reflection;
using System.Text;
using Meeseeks.Agents;
using Meeseeks.Core;

namespace Meeseeks.Scripting;

/// <summary>
/// Marks a script-facing type, method, or property. The csharp tool description is generated
/// from these at startup, so the model always sees the live signature.
/// </summary>
[AttributeUsage(
	AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Property,
	AllowMultiple = false)]
public sealed class ApiAttribute : Attribute
{
	public ApiAttribute(string description) => Description = description;

	public string Description { get; }

	/// <summary>Sort key inside its group. Lower comes first; ties keep declaration order.</summary>
	public int Order { get; set; } = 100;

	/// <summary>Optional signature override for cases reflection cannot express nicely.</summary>
	public string? Signature { get; set; }
}

/// <summary>Builds the API section of the tool description from the attributes above.</summary>
public static class ApiDocumentation
{
	private static readonly Lazy<string> Cached = new(GenerateCore, isThreadSafe: true);
	private static readonly NullabilityInfoContext Nullability = new();

	private static readonly Dictionary<Type, string> Aliases = new()
	{
		[typeof(void)] = "void",
		[typeof(string)] = "string",
		[typeof(object)] = "object",
		[typeof(bool)] = "bool",
		[typeof(byte)] = "byte",
		[typeof(sbyte)] = "sbyte",
		[typeof(short)] = "short",
		[typeof(ushort)] = "ushort",
		[typeof(int)] = "int",
		[typeof(uint)] = "uint",
		[typeof(long)] = "long",
		[typeof(ulong)] = "ulong",
		[typeof(float)] = "float",
		[typeof(double)] = "double",
		[typeof(decimal)] = "decimal",
		[typeof(char)] = "char",
		[typeof(Task)] = "Task",
		[typeof(ValueTask)] = "ValueTask",
		[typeof(CancellationToken)] = "CancellationToken",
	};

	private static readonly Type[] DataTypes =
	[
		typeof(SpawnOptions),
		typeof(AgentResult),
		typeof(AgentHandle),
		typeof(AgentMessage),
		typeof(AgentPeerInfo),
		typeof(SkillInfo),
		typeof(McpServerInfo),
		typeof(McpToolInfo),
		typeof(ToolInfo),
	];

	public static string Text => Cached.Value;

	private static string GenerateCore()
	{
		var globals = typeof(ScriptGlobals);
		var rootProperties = globals
			.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
			.Select(property => (Property: property, Api: property.GetCustomAttribute<ApiAttribute>()))
			.Where(pair => pair.Api is not null)
			.OrderBy(pair => pair.Api!.Order)
			.ThenBy(pair => pair.Property.MetadataToken)
			.ToList();

		var builder = new StringBuilder();
		builder.AppendLine("## Script globals");
		builder.AppendLine();
		builder.AppendLine("Signatures below are generated from the live API.");
		builder.AppendLine();
		builder.AppendLine("```csharp");

		foreach (var method in AttributeMethods(globals, @static: false))
		{
			EmitDescription(builder, method.Api.Description);
			builder.AppendLine(method.Signature);
		}

		if (globals.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
			.Any(method => method.GetCustomAttribute<ApiAttribute>() is not null))
		{
			builder.AppendLine();
		}

		foreach (var (property, api) in rootProperties)
		{
			EmitDescription(builder, api!.Description);
			builder.AppendLine($"{FormatType(property.PropertyType)} {property.Name}");
		}

		builder.AppendLine();

		foreach (var (property, api) in rootProperties)
		{
			var type = property.PropertyType;
			var methods = AttributeMethods(type, @static: false, prefix: property.Name + ".").ToList();
			var properties = AttributeProperties(type).ToList();
			if (methods.Count == 0 && properties.Count == 0)
			{
				continue;
			}

			builder.AppendLine($"// --- {property.Name} ({type.Name}) ---");
			foreach (var (typeProperty, typeApi) in properties)
			{
				EmitDescription(builder, typeApi!.Description);
				builder.AppendLine($"{FormatType(typeProperty.PropertyType, SafeNullability(typeProperty))} {property.Name}.{typeProperty.Name}");
			}
			foreach (var method in methods)
			{
				EmitDescription(builder, method.Api.Description);
				builder.AppendLine(method.Signature);
			}
			builder.AppendLine();
		}

		builder.AppendLine("```");
		builder.AppendLine();
		builder.AppendLine("### Types");
		builder.AppendLine();
		builder.AppendLine("```csharp");

		foreach (var type in DataTypes)
		{
			var typeApi = type.GetCustomAttribute<ApiAttribute>();
			if (typeApi is null)
			{
				continue;
			}

			EmitDescription(builder, typeApi.Description);
			builder.AppendLine($"class {type.Name}");
			builder.AppendLine("{");

			foreach (var (property, api) in AttributeProperties(type))
			{
				EmitDescription(builder, api!.Description, indent: "    ");
				builder.AppendLine($"    {FormatProperty(property)};");
			}

			foreach (var method in AttributeMethods(type, @static: false))
			{
				EmitDescription(builder, method.Api.Description, indent: "    ");
				builder.AppendLine($"    {method.Signature};");
			}

			builder.AppendLine("}");
		}

		builder.AppendLine("```");
		return builder.ToString().TrimEnd();
	}

	private static IEnumerable<(MethodInfo Method, ApiAttribute Api, string Signature)> AttributeMethods(
		Type type,
		bool @static,
		string? prefix = null)
	{
		var flags = BindingFlags.Public | BindingFlags.DeclaredOnly |
			(@static ? BindingFlags.Static : BindingFlags.Instance);

		return type.GetMethods(flags)
			.Select(method => (Method: method, Api: method.GetCustomAttribute<ApiAttribute>()))
			.Where(pair => pair.Api is not null)
			.OrderBy(pair => pair.Api!.Order)
			.ThenBy(pair => pair.Method.MetadataToken)
			.Select(pair => (pair.Method, pair.Api!, pair.Api!.Signature ?? BuildSignature(pair.Method, prefix)));
	}

	private static IEnumerable<(PropertyInfo Property, ApiAttribute Api)> AttributeProperties(Type type) =>
		type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
			.Select(property => (Property: property, Api: property.GetCustomAttribute<ApiAttribute>()))
			.Where(pair => pair.Api is not null)
			.OrderBy(pair => pair.Api!.Order)
			.ThenBy(pair => pair.Property.MetadataToken)
			.Select(pair => (pair.Property, pair.Api!));

	private static string BuildSignature(MethodInfo method, string? prefix)
	{
		var name = method.Name;
		if (method.IsGenericMethodDefinition)
		{
			name += "<" + string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name)) + ">";
		}

		var returnType = FormatReturnType(method);
		var parameters = string.Join(", ", method.GetParameters().Select(FormatParameter));
		return $"{returnType} {prefix}{name}({parameters})";
	}

	private static string FormatProperty(PropertyInfo property)
	{
		var nullability = SafeNullability(property);
		return $"{FormatType(property.PropertyType, nullability)} {property.Name}";
	}

	private static string FormatParameter(ParameterInfo parameter)
	{
		var nullability = SafeNullability(parameter);
		var type = FormatType(parameter.ParameterType, nullability);
		var name = parameter.Name ?? "arg";
		return $"{type} {name}{FormatDefault(parameter)}";
	}

	private static string FormatReturnType(MethodInfo method)
	{
		var nullability = SafeNullability(method.ReturnParameter);
		return FormatType(method.ReturnType, nullability);
	}

	private static NullabilityInfo? SafeNullability(ParameterInfo parameter)
	{
		try
		{
			return Nullability.Create(parameter);
		}
		catch
		{
			return null;
		}
	}

	private static NullabilityInfo? SafeNullability(PropertyInfo property)
	{
		try
		{
			return Nullability.Create(property);
		}
		catch
		{
			return null;
		}
	}

	private static string FormatType(Type type, NullabilityInfo? info = null)
	{
		if (Nullable.GetUnderlyingType(type) is { } underlying)
		{
			return FormatType(underlying) + "?";
		}

		if (Aliases.TryGetValue(type, out var alias))
		{
			return AppendNullable(alias, info);
		}

		if (type.IsArray)
		{
			return AppendNullable(FormatType(type.GetElementType()!, info?.ElementType) + "[]", info);
		}

		if (type.IsGenericParameter)
		{
			return type.Name;
		}

		if (type.IsGenericType)
		{
			var definition = type.GetGenericTypeDefinition();
			var name = StripArity(definition.Name.Replace('+', '.'));
			var arguments = type.GetGenericArguments();
			var argumentInfo = info?.GenericTypeArguments ?? [];
			var formatted = arguments.Select((argument, index) =>
				FormatType(argument, index < argumentInfo.Length ? argumentInfo[index] : null));
			return AppendNullable($"{name}<{string.Join(", ", formatted)}>", info);
		}

		return AppendNullable(type.Name.Replace('+', '.'), info);
	}

	private static string AppendNullable(string text, NullabilityInfo? info) =>
		info?.ReadState == NullabilityState.Nullable && !text.EndsWith('?') ? text + "?" : text;

	private static string StripArity(string name)
	{
		var backtick = name.IndexOf('`');
		return backtick > 0 ? name[..backtick] : name;
	}

	private static string FormatDefault(ParameterInfo parameter)
	{
		if (!parameter.IsOptional)
		{
			return "";
		}

		if (parameter.ParameterType == typeof(CancellationToken))
		{
			return " = default";
		}

		var value = parameter.DefaultValue;
		if (value is null || ReferenceEquals(value, Missing.Value) || value == DBNull.Value)
		{
			return parameter.ParameterType.IsValueType ? " = default" : " = null";
		}

		if (parameter.ParameterType.IsEnum)
		{
			return $" = {parameter.ParameterType.Name}.{value}";
		}

		return value switch
		{
			bool flag => flag ? " = true" : " = false",
			string text => $" = \"{text}\"",
			char character => $" = '{character}'",
			IFormattable formattable => " = " + formattable.ToString(null, CultureInfo.InvariantCulture),
			_ => " = " + value,
		};
	}

	private static void EmitDescription(StringBuilder builder, string description, string indent = "")
	{
		foreach (var line in description.Split('\n'))
		{
			builder.AppendLine($"{indent}// {line.TrimEnd()}");
		}
	}
}
