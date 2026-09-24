using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VibeRe.Application.Tools;

namespace VibeRe.Tools;

/// <summary>
/// Base for tools implemented in VibeRe itself. The input schema is generated from <typeparamref name="TArguments"/>
/// (snake_case properties, <see cref="DescriptionAttribute"/>s become descriptions, constructor parameters without
/// defaults become required), and arguments are validated against the same contract before the tool runs.
/// </summary>
public abstract class BuiltinTool<TArguments> : ITool
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    protected BuiltinTool()
    {
        var schema = JsonOptions.GetJsonSchemaAsNode(typeof(TArguments), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = AddDescription,
        });
        InputSchema = JsonSerializer.SerializeToElement(schema);
    }

    public abstract string Name { get; }

    public abstract string Description { get; }

    string? ITool.Description => Description;

    public JsonElement InputSchema { get; }

    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        TArguments parsed;
        try
        {
            parsed = arguments.Deserialize<TArguments>(JsonOptions)
                     ?? throw new JsonException("Arguments must be a JSON object.");
        }
        catch (JsonException exception)
        {
            return ToolResult.Error($"Invalid arguments for '{Name}': {exception.Message}");
        }

        try
        {
            return await InvokeAsync(parsed, context, cancellationToken);
        }
        catch (ToolException exception)
        {
            return ToolResult.Error(exception.Message);
        }
    }

    protected abstract Task<ToolResult> InvokeAsync(
        TArguments arguments,
        ToolContext context,
        CancellationToken cancellationToken);

    private static JsonNode AddDescription(JsonSchemaExporterContext context, JsonNode schema)
    {
        var provider = context.PropertyInfo?.AttributeProvider;
        if (provider is ParameterInfo or PropertyInfo
            && provider.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
                is [DescriptionAttribute { Description: var description }, ..]
            && schema is JsonObject properties)
        {
            properties.Insert(0, "description", description);
        }

        return schema;
    }
}

/// <summary>An expected failure (bad path, no match, ...) whose message is shown to the model as-is.</summary>
public sealed class ToolException(string message) : Exception(message);
