using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #37 / spec, Testing Decisions: `--vault=...`, `@file` and
// `--json=false` sent as the value of every string argument of every listed
// tool leave the bound vault and the envelope unchanged - values reach the
// CLI as `--opt=value` or after `--`, never as options of their own, and
// response files are off.
//
// Driven off tools/list, so each tool a later ticket adds is covered without
// touching this file.
public class HostileInputTests
{
    private const string OtherMarker = "zebracornmarker";

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task HostileStringArguments_NeverEscapeTheBoundVaultOrTheEnvelope(string transport)
    {
        using var bound = new TempVault();
        bound.Run("init", bound.Path, "--name", "bound", "--json");
        bound.RunStdin("Bound body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s", "--json");

        using var other = new TempVault();
        other.Run("init", other.Path, "--name", "other", "--json");
        other.RunStdin($"{OtherMarker} lives here.", "page", "upsert", "--type", "entity", "--title", OtherMarker, "--summary", OtherMarker, "--json");

        var responseFile = Path.Combine(bound.Path, "hostile.rsp");
        File.WriteAllText(responseFile, $"--vault\n{other.Path}\n");
        string[] hostile = [$"--vault={other.Path}", "@" + responseFile, "--json=false", $"--vault {other.Path}"];

        await using var mcp = await McpHarness.StartAsync(bound, transport);
        var tools = await mcp.Client.ListToolsAsync();
        var calls = 0;

        foreach (var tool in tools)
        {
            var schema = tool.ProtocolTool.InputSchema;
            var properties = schema.GetProperty("properties").EnumerateObject()
                .Where(p => IsString(p.Value))
                .Select(p => p.Name)
                .ToArray();
            var required = schema.TryGetProperty("required", out var r)
                ? r.EnumerateArray().Select(e => e.GetString()!).ToArray()
                : [];

            foreach (var property in properties)
            foreach (var value in hostile)
            {
                var args = required.ToDictionary(n => n, _ => (object?)"contoso");
                args[property] = value;

                var result = await mcp.CallAsync(tool.Name, args);
                calls++;

                // Still the JSON envelope (--json=false never reached the CLI as an option).
                var s = result.Structured();
                Assert.Equal(1, s.GetProperty("v").GetInt32());
                Assert.True(s.TryGetProperty("exitCode", out _), $"{tool.Name}.{property}={value}");
                // The value reached the command as a plain value: a parse
                // error (`unknown-command`) would mean it was read as an option.
                var codes = s.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString());
                Assert.DoesNotContain("unknown-command", codes);
                // And never the other vault's content.
                Assert.DoesNotContain(OtherMarker, JsonSerializer.Serialize(result.StructuredContent), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(OtherMarker, result.Text(), StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.True(calls > 0);
    }

    private static bool IsString(JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type))
            return false;
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(t => t.GetString() == "string")
            : type.GetString() == "string";
    }
}
