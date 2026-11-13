using System.Collections;
using System.IO;
using System.Text.Json;
using ModelContextProtocol;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #62 (spec, Testing Decisions; #37 argument injection): hostile values
// - `--vault=...`, `@file`, `--json=false`, anything starting with `--` - sent
// as the value of every string argument of every tool, over every transport,
// leave the bound vault, the other vault, the environment and the response
// envelope unchanged. Values reach the CLI as `--opt=value` (or `--opt ""`)
// or after `--`, never as options of their own, and response files are off.
//
// Self-enforcing: the catalogue is enumerated from tools/list, and every
// argument a tool advertises must have a strategy here, or the test fails
// naming it. A selector (a schema `enum`) is swept through all its values;
// a free-text string argument needs an entry in Strategies (its plausible
// value while another argument is attacked); a boolean/integer argument is
// attacked through the schema. Any other argument type fails the test.
public class HostileInputTests
{
    private const string OtherMarker = "zebracornmarker";
    private const string SecretMarker = "quokkasecretmarker";

    // The plausible value of every free-text string argument, keyed
    // "tool.argument", used while another argument of the same call is
    // attacked so the call reaches the command it would really run (the
    // write tools really write, #56). A new tool or argument MUST be added
    // here: the test fails on any string argument without an entry, and on
    // an entry no tool advertises any more.
    private static Dictionary<string, string> Strategies(string pageId, string sourceId) => new()
    {
        ["search_wiki.query"] = "contoso",
        ["search_wiki.type"] = "entity",
        ["search_wiki.kind"] = "page",
        ["read_page.page"] = pageId,
        ["browse_wiki.type"] = "entity",
        ["browse_wiki.status"] = "needs-review",
        ["browse_wiki.category"] = "article",
        ["browse_wiki.source"] = sourceId,
        ["work_issues.kind"] = "page",
        ["work_issues.status"] = "needs-review",
        ["work_issues.issue"] = "contoso",
        ["work_issues.note"] = "contoso",
        ["audit_page.page"] = pageId,
        ["audit_page.verdict"] = "contoso",
        ["audit_page.note"] = "contoso",
        ["write_page.type"] = "entity",
        ["write_page.title"] = "Contoso",
        ["write_page.summary"] = "s",
        ["write_page.body"] = "Bound body.",
        ["write_page.id"] = pageId,
        ["write_page.sources"] = sourceId,
        ["write_page.tags"] = "t",
        ["rename_page.id"] = pageId,
        ["rename_page.newSlug"] = "contoso-renamed",
        ["add_category.id"] = "podcast",
        ["add_category.description"] = "d",
        ["retract_source.source"] = sourceId,
        ["retract_source.reason"] = "r",
        ["repair_vault.pages"] = pageId,
        ["repair_vault.status"] = "needs-review",
        ["ingest_source.category"] = "article",
        ["ingest_source.filename"] = "note.md",
        ["ingest_source.content"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Benign upload.")),
        ["ingest_source.categoryDescription"] = "d",
        ["ingest_source.categoryRationale"] = "contoso",
        ["continue_ingest.sourceId"] = sourceId,
        ["continue_ingest.to"] = "contoso",
        ["continue_ingest.touched"] = "contoso",
        ["propose_change.category"] = "contoso",
        ["propose_change.description"] = "d",
        ["propose_change.rationale"] = "contoso",
        ["propose_change.section"] = "contoso",
        ["propose_change.sources"] = sourceId,
        ["propose_change.text"] = "contoso",
        ["propose_change.changeset"] = "contoso",
        ["decide.id"] = pageId,
        ["decide.reason"] = "contoso",
        ["record_owner_statement.answer"] = "contoso",
        ["record_owner_statement.question"] = "contoso",
        // Blank = start a new transcript, so the answer is really recorded (#59).
        ["record_owner_statement.transcript"] = "",
    };

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task HostileArguments_AcrossTheWholeCatalogue_NeverEscapeTheBoundVaultOrTheEnvelope(string transport)
    {
        using var bound = new TempVault();
        bound.Run("init", bound.Path, "--name", "bound", "--json");
        var page = bound.RunStdin("Bound body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s", "--json");
        var pageId = Data(page).GetProperty("id").GetString()!;
        var sourceFile = Path.Combine(bound.Path, "input.md");
        File.WriteAllText(sourceFile, "content");
        var sourceId = Data(bound.Run("source", "add", sourceFile, "--category", "article", "--title", "T", "--json"))
            .GetProperty("id").GetString()!;
        var strategies = Strategies(pageId, sourceId);

        using var other = new TempVault();
        other.Run("init", other.Path, "--name", "other", "--json");
        other.RunStdin($"{OtherMarker} lives here.", "page", "upsert", "--type", "entity", "--title", OtherMarker, "--summary", OtherMarker, "--json");
        var otherBefore = Snapshot(other.Path);

        // Files an argument names, outside both vaults: a response file that
        // would redirect to the other vault, and a plain file whose content
        // must never be read into a response or the vault.
        var scratch = Path.Combine(Path.GetTempPath(), "wiki-hostile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var responseFile = Path.Combine(scratch, "hostile.rsp");
        File.WriteAllText(responseFile, $"--vault\n{other.Path}\n--title\n{SecretMarker}\n");
        var secretFile = Path.Combine(scratch, "secret.md");
        File.WriteAllText(secretFile, $"{SecretMarker} must never be read.");
        var scratchBefore = Snapshot(scratch);

        string[] hostile =
        [
            $"--vault={other.Path}",
            $"--vault {other.Path}",
            "@" + responseFile,
            "@" + secretFile,
            secretFile,
            "--json=false",
            "--",
            $"-- --vault={other.Path}",
            "--help",
            "--version",
            "--stdin",
            "--lock-timeout=0",
            // Not hostile, but the edge that used to make an option swallow
            // the next token (#57's `--opt=`): it must reach the command as
            // an empty value.
            "",
        ];

        var environmentBefore = Environment();
        var directoryBefore = Directory.GetCurrentDirectory();

        // ingest_source needs an inbox (#57); it lives outside both vaults.
        var inbox = Path.Combine(scratch, "inbox");
        try
        {
            await using var mcp = await McpHarness.StartAsync(bound, transport, null, "--inbox-root", inbox);
            var tools = await mcp.Client.ListToolsAsync();
            Assert.NotEmpty(tools);

            // Every advertised argument has a strategy, and every strategy
            // still names an advertised argument.
            var advertised = tools.SelectMany(t => t.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject()
                .Select(p => (Tool: t.Name, Argument: p.Name, Schema: p.Value))).ToArray();
            var freeText = advertised.Where(a => Type(a.Schema) == "string" && !a.Schema.TryGetProperty("enum", out _))
                .Select(a => a.Tool + "." + a.Argument).ToHashSet();
            Assert.True(freeText.SetEquals(strategies.Keys),
                "string arguments without a strategy: [" + string.Join(", ", freeText.Except(strategies.Keys).Order()) +
                "]; strategies for arguments no tool advertises: [" + string.Join(", ", strategies.Keys.Except(freeText).Order()) + "]");
            var untyped = advertised.Where(a => Type(a.Schema) is not ("string" or "boolean" or "integer"))
                .Select(a => $"{a.Tool}.{a.Argument}: {Type(a.Schema)}");
            Assert.Empty(untyped);

            var attacked = new HashSet<string>();
            foreach (var tool in tools)
            {
                var schema = tool.ProtocolTool.InputSchema;
                var properties = schema.GetProperty("properties").EnumerateObject().ToArray();
                var selectors = properties.Where(p => Type(p.Value) == "string" && p.Value.TryGetProperty("enum", out _)).ToArray();
                var texts = properties.Where(p => Type(p.Value) == "string").Except(selectors).Select(p => p.Name).ToArray();
                var typed = properties.Where(p => Type(p.Value) != "string").Select(p => p.Name).ToArray();

                // Selectors and boolean/integer arguments: a hostile value is
                // a schema failure and never reaches the CLI.
                foreach (var name in selectors.Select(p => p.Name).Concat(typed))
                foreach (var value in hostile)
                {
                    var args = texts.ToDictionary(n => n, n => (object?)strategies[tool.Name + "." + n]);
                    args[name] = value;
                    var ex = await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallAsync(tool.Name, args));
                    Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
                    attacked.Add(tool.Name + "." + name);
                }

                // Free-text arguments: attacked under every combination of
                // selector values, every other argument plausible.
                foreach (var mode in Modes(selectors))
                foreach (var property in texts)
                foreach (var value in hostile)
                {
                    var args = texts.ToDictionary(n => n, n => (object?)strategies[tool.Name + "." + n]);
                    foreach (var (name, selected) in mode)
                        args[name] = selected;
                    args[property] = value;
                    var call = $"{tool.Name}({string.Join(", ", mode.Select(m => m.Name + "=" + m.Value))}).{property}={value}";

                    attacked.Add(tool.Name + "." + property);
                    ModelContextProtocol.Protocol.CallToolResult result;
                    try
                    {
                        result = await mcp.CallAsync(tool.Name, args);
                    }
                    catch (McpProtocolException ex) when (value == "" && ex.ErrorCode == McpErrorCode.InvalidParams)
                    {
                        // A blank value may count as "not given" for a tool's
                        // argument combination (repair_vault: pages needs
                        // status), refused before any step runs. Every
                        // non-blank value must be answered with the envelope.
                        continue;
                    }

                    // Still the JSON envelope (--json=false never reached the
                    // CLI as an option, nor did --help or --version).
                    var s = result.Structured();
                    Assert.Equal(1, s.GetProperty("v").GetInt32());
                    Assert.True(s.TryGetProperty("ok", out _), call);
                    Assert.True(s.TryGetProperty("errors", out _), call);
                    Assert.True(s.TryGetProperty("exitCode", out _), call);
                    // The value reached the command as a plain value: a parse
                    // error (`unknown-command`) means it was read as an option.
                    var codes = s.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString());
                    Assert.True(!codes.Contains("unknown-command"), call + ": " + result.Text());
                    // Never the other vault's content, never a named file's.
                    var text = JsonSerializer.Serialize(result.StructuredContent) + "\n" + result.Text();
                    Assert.True(!text.Contains(OtherMarker, StringComparison.OrdinalIgnoreCase), call);
                    Assert.True(!text.Contains(SecretMarker, StringComparison.OrdinalIgnoreCase), call);
                }
            }

            // Every advertised argument was really attacked.
            Assert.Equal(advertised.Select(a => a.Tool + "." + a.Argument).Order(), attacked.Order());

            // No tool, write tools included, ever wrote to the other vault...
            Assert.Equal(otherBefore, Snapshot(other.Path));
            // ...or read a named file into the bound vault, or changed it.
            Assert.All(Snapshot(bound.Path), f => Assert.DoesNotContain(SecretMarker, Text(f.Value), StringComparison.OrdinalIgnoreCase));
            Assert.All(Snapshot(bound.Path), f => Assert.DoesNotContain(OtherMarker, Text(f.Value), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(scratchBefore, Snapshot(scratch).Where(f => !f.Key.StartsWith("inbox", StringComparison.Ordinal))
                .ToDictionary(f => f.Key, f => f.Value));
            // Uploads only ever landed directly in <inbox>/<category>.
            if (Directory.Exists(inbox))
                Assert.All(Directory.EnumerateFiles(inbox, "*", SearchOption.AllDirectories),
                    f => Assert.Equal(Path.GetFullPath(inbox), Path.GetDirectoryName(Path.GetDirectoryName(f))));
            // The process environment and working directory are untouched.
            Assert.Equal(environmentBefore, Environment());
            Assert.Equal(directoryBefore, Directory.GetCurrentDirectory());
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch { }
        }
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

    // The JSON Schema type of an argument; a nullable one ("string" + "null")
    // counts as its non-null type.
    private static string Type(JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type))
            return "(none)";
        return type.ValueKind == JsonValueKind.Array
            ? string.Join("|", type.EnumerateArray().Select(t => t.GetString()).Where(t => t != "null"))
            : type.GetString()!;
    }

    private static JsonElement Data(CliResult r)
    {
        using var doc = JsonDocument.Parse(r.Stdout.Trim().Split('\n')[^1]);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static Dictionary<string, string> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Convert.ToBase64String(File.ReadAllBytes(f)));

    private static string Text(string base64) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    private static SortedDictionary<string, string?> Environment()
    {
        var all = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            all[(string)e.Key] = (string?)e.Value;
        return all;
    }
}
