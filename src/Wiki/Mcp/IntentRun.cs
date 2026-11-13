using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using ModelContextProtocol.Protocol;

namespace Wiki.Mcp;

// One intent tool call: run CLI steps in order, stop at the first one that
// does not exit 0, and turn the outcome into a tool result per the error
// contract (spec, Error contract):
//
//   all steps exit 0 -> isError false; structuredContent
//                       {v, ok:true, data:{<key>: <step data>, ...},
//                        errors:[], exitCode:0, completedSteps:[...]}
//   a step exits 1/2 -> isError true; structuredContent is that step's CLI
//                       envelope unchanged plus exitCode and completedSteps
//                       (the command paths of the steps before it); text is
//                       one `code: message` line per error
//   a step exits 3   -> the same shape with isError false: a state conflict
//                       or idempotent no-op is not a failure
//   cancelled        -> checked between steps only; the failure shape with
//                       code `cancelled` and no exitCode; no rollback
//
// Steps that already ran are never undone: each CLI step is atomic on its
// own, so after fixing what failed the agent re-runs the tool or the rest of
// the steps. A step marked CliStep.Collect() appends its data to an array
// under its key (one command per item); DataOf(key) hands a completed step's
// data to the steps built after it.
//
// Usage in a tool:
//   var run = cli.Begin(ct);
//   if (run.Step(new CliStep("page", "page", "show").Positional(page)) && backlinks)
//       run.Step(new CliStep("backlinks", "page", "backlinks").Positional(page));
//   return run.Result();
public sealed class IntentRun
{
    private readonly CliAdapter _cli;
    private readonly CancellationToken _cancellationToken;
    private readonly List<CliStepResult> _completed = [];
    private readonly List<(string Key, JsonNode? Data)> _notes = [];
    private CallToolResult? _stopped;

    internal IntentRun(CliAdapter cli, CancellationToken cancellationToken)
    {
        _cli = cli;
        _cancellationToken = cancellationToken;
    }

    // Runs the step unless the intent already stopped. True when it exited 0,
    // so the next step may run.
    public bool Step(CliStep step)
    {
        if (_stopped is not null)
            return false;

        if (_cancellationToken.IsCancellationRequested)
        {
            _stopped = Stop(Failure("cancelled", $"cancelled before '{step.CommandPath}'", path: null), exitCode: null, isError: true);
            return false;
        }

        var result = _cli.Run(step);
        if (result.ExitCode == 0)
        {
            _completed.Add(result);
            return true;
        }

        _stopped = Stop(result.Envelope, result.ExitCode, isError: result.ExitCode != 3);
        return false;
    }

    // A tool's own validation refusing the intent before (or between) CLI
    // steps - for checks on work the tool does itself, outside the CLI (e.g.
    // ingest_source landing an upload). Same result as a CLI step failing
    // validation: isError, the failure envelope with this code, exitCode 1.
    // Argument combinations the schema cannot express stay InvalidParams
    // protocol errors (ToolArgumentGuard.RequireFor); this is for values.
    public void Refuse(string code, string message, string? path = null)
    {
        if (_stopped is null)
            _stopped = Stop(Failure(code, message, path), exitCode: 1, isError: true);
    }

    // Data the tool produced itself (not a CLI step's), placed under
    // data.<key> of a successful result ahead of the step data. The node is
    // kept by reference, so the tool may fill it in after later steps ran.
    public void Note(string key, JsonNode? data) => _notes.Add((key, data));

    // True once a step failed or the intent was cancelled; no later step runs.
    public bool Stopped => _stopped is not null;

    // The data of the last completed step with this key (null when none ran),
    // so a later step can be built from an earlier step's output, e.g. the
    // source ids a scan returned. A clone: the tool result is unaffected.
    public JsonNode? DataOf(string key)
        => _completed.LastOrDefault(s => s.Step.Key == key)?.Envelope["data"]?.DeepClone();

    public CallToolResult Result()
    {
        if (_stopped is not null)
            return _stopped;

        var data = new JsonObject();
        foreach (var (key, note) in _notes)
            data[key] = note?.DeepClone();
        foreach (var step in _completed)
        {
            var stepData = step.Envelope["data"]?.DeepClone();
            if (!step.Step.Collected)
                data[step.Step.Key] = stepData;
            else if (data[step.Step.Key] is JsonArray items)
                items.Add(stepData);
            else
                data[step.Step.Key] = new JsonArray(stepData);
        }

        var structured = new JsonObject
        {
            ["v"] = Json.Envelope.Version,
            ["ok"] = true,
            ["data"] = data,
            ["errors"] = new JsonArray(),
            ["exitCode"] = 0,
            ["completedSteps"] = CompletedSteps(),
        };
        return ToolResult(structured, structured.ToJsonString(), isError: false);
    }

    private CallToolResult Stop(JsonObject envelope, int? exitCode, bool isError)
    {
        var structured = envelope.DeepClone().AsObject();
        if (exitCode is not null)
            structured["exitCode"] = exitCode;
        structured["completedSteps"] = CompletedSteps();

        var lines = (envelope["errors"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(e => $"{(string?)e["code"]}: {(string?)e["message"]}");
        return ToolResult(structured, string.Join("\n", lines), isError);
    }

    private static JsonObject Failure(string code, string message, string? path)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (path is not null)
            error["path"] = path;
        return new JsonObject
        {
            ["v"] = Json.Envelope.Version,
            ["ok"] = false,
            ["data"] = null,
            ["errors"] = new JsonArray(error),
        };
    }

    private JsonArray CompletedSteps()
        => new([.. _completed.Select(s => (JsonNode?)JsonValue.Create(s.Step.CommandPath))]);

    private static CallToolResult ToolResult(JsonObject structured, string text, bool isError) => new()
    {
        IsError = isError,
        // Round-tripped through text: JsonDocument parsing is AOT-safe, no
        // serializer contract needed for a JsonNode.
        StructuredContent = ToElement(structured),
        Content = [new TextContentBlock { Text = text }],
    };

    private static System.Text.Json.JsonElement ToElement(JsonObject node)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}
