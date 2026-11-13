using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp;

// Spec, Error contract: arguments that fail the tool's input schema are a
// JSON-RPC protocol error (InvalidParams), distinguishable from a vault
// failure. The SDK itself reports a binding failure (missing required value,
// wrong type) as an isError tool result, so this call-tool filter checks the
// arguments against the schema the tool advertises in tools/list before the
// tool runs. Undeclared arguments are rejected too: a tool argument can never
// smuggle in a value (a `vault`, say) the tool does not declare.
//
// It checks the subset of JSON Schema the SDK generates for primitive
// parameters - `properties`, `required` and `type` (a name or an array of
// names, e.g. ["string","null"]) - which is why tool parameters stay
// primitive (string, bool, int?).
public static class ToolArgumentGuard
{
    public static IMcpServerBuilder WithToolArgumentGuard(this IMcpServerBuilder builder)
        => builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            if (context.MatchedPrimitive is McpServerTool tool)
                Check(tool.ProtocolTool, context.Params?.Arguments);
            return await next(context, cancellationToken);
        }));

    private static void Check(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        var schema = tool.InputSchema;
        var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : default;
        arguments ??= new Dictionary<string, JsonElement>();

        foreach (var (name, value) in arguments)
        {
            if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(name, out var property))
                throw Invalid(tool, $"unknown argument '{name}'");
            if (property.TryGetProperty("type", out var type) && !Matches(type, value))
                throw Invalid(tool, $"argument '{name}' must be {Describe(type)}, got {value.ValueKind.ToString().ToLowerInvariant()}");
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
            {
                if (!arguments.ContainsKey(name))
                    throw Invalid(tool, $"missing required argument '{name}'");
            }
        }
    }

    private static bool Matches(JsonElement type, JsonElement value) => type.ValueKind switch
    {
        JsonValueKind.String => MatchesOne(type.GetString()!, value),
        JsonValueKind.Array => type.EnumerateArray().Any(t => MatchesOne(t.GetString()!, value)),
        _ => true,
    };

    private static bool MatchesOne(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "null" => value.ValueKind == JsonValueKind.Null,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => false,
    };

    private static string Describe(JsonElement type) => type.ValueKind == JsonValueKind.Array
        ? string.Join(" or ", type.EnumerateArray().Select(t => t.GetString()))
        : type.GetString() ?? "?";

    private static McpProtocolException Invalid(Tool tool, string message)
        => new($"{tool.Name}: {message}", McpErrorCode.InvalidParams);
}
