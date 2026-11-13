using System.IO;
using System.Text.Json;
using ModelContextProtocol;
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
// touching this file - under every value of its selector arguments (#55).
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
            var strings = schema.GetProperty("properties").EnumerateObject()
                .Where(p => IsString(p.Value))
                .ToArray();
            // Selectors (an `enum`, e.g. browse_wiki's view) pick the CLI
            // command, so every free-text argument is attacked under every
            // selector value. A hostile selector value never reaches the CLI:
            // it is a schema failure.
            var selectors = strings.Where(p => p.Value.TryGetProperty("enum", out _)).ToArray();
            var properties = strings.Except(selectors).Select(p => p.Name).ToArray();
            var required = schema.TryGetProperty("required", out var r)
                ? r.EnumerateArray().Select(e => e.GetString()!).ToArray()
                : [];

            foreach (var selector in selectors)
            foreach (var value in hostile)
            {
                var ex = await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallAsync(tool.Name,
                    new Dictionary<string, object?>(required.Select(n => KeyValuePair.Create(n, (object?)"contoso"))) { [selector.Name] = value }));
                Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
                calls++;
            }

            foreach (var mode in Modes(selectors))
            foreach (var property in properties)
            foreach (var value in hostile)
            {
                var args = required.ToDictionary(n => n, _ => (object?)"contoso");
                // Under a selector, give every free-text argument a plain
                // value so the chosen command has what it needs.
                if (selectors.Length > 0)
                    foreach (var p in properties)
                        args[p] = "contoso";
                foreach (var (name, selected) in mode)
                    args[name] = selected;
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

    // Every combination of selector values; one empty mode when there are none.
    private static IEnumerable<(string Name, string Value)[]> Modes(JsonProperty[] selectors)
    {
        IEnumerable<(string Name, string Value)[]> modes = [[]];
        foreach (var selector in selectors)
        {
            var values = selector.Value.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();
            modes = modes.SelectMany(m => values.Select(v => m.Append((selector.Name, v)).ToArray())).ToArray();
        }
        return modes;
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
