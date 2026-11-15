using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Ingest: `ingest_source` (spec tool catalogue: upload -> `source scan` ->
// `ingest resume`; decision #30 "Ingest"). Replaces `source add`/`source
// scan` over MCP. The upload lands in the server's inbox (IngestInbox) at
// <inboxRoot>/<category>/<filename>, then:
//
//   categoryDescription given and the category is unknown
//       -> `category list`, `category propose` (data.categoryProposal); no
//          scan - the upload waits in the inbox until the category exists,
//          and calling ingest_source again then registers it
//   otherwise
//       -> `source scan <inboxRoot>/<category> --category <category>`
//          (data.scan: every file's outcome), then `ingest resume` for each
//          source the scan registered and for the upload's own source when
//          it was a duplicate (data.plans)
//
// data.upload says where the upload landed and, after a scan, its outcome
// and source id. Landing is the tool's own work, not a CLI step: its
// refusals (inbox-not-configured, invalid-category-id, invalid-filename,
// invalid-upload, upload-too-large) are failure results with exitCode 1,
// like a command's validation failure, and nothing is written.
[McpServerToolType]
public sealed class IngestSourceTool(CliAdapter cli, IngestInbox inbox)
{
    [McpServerTool(Name = "ingest_source", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Ingest a raw source: the file lands in this server's inbox under the category, is registered at once " +
                 "(wiki source scan: hashed, deduplicated; data.scan has every file's outcome) and the deterministic " +
                 "ingest plan of each new source is returned (wiki ingest resume, data.plans). data.upload has the landed " +
                 "path, the upload's outcome (registered | skipped-duplicate | skipped-empty | rejected) and its sourceId. " +
                 "If the category does not exist and categoryDescription is given, a category proposal is filed instead " +
                 "(data.categoryProposal); call again once it is approved. Then drive the ingest with continue_ingest. " +
                 "Not all-or-nothing: if a later step fails, the landed upload and anything already registered stay " +
                 "(completedSteps); calling again is safe, since the scan deduplicates.")]
    public CallToolResult IngestSource(
        [Description("Category id the source registers under (lowercase kebab-case; one inbox directory per category)")]
        string category,
        [Description("File name the upload is stored under, e.g. 'notes.md' - a plain name, no directories")]
        string filename,
        [Description("The file's content, base64-encoded. Text only: binary content is rejected by the scan.")]
        string content,
        [Description("If the category does not exist yet: propose it with this one-line description instead of failing")]
        string? categoryDescription = null,
        [Description("With categoryDescription: why no existing category fits")]
        string? categoryRationale = null,
        CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);

        var landing = inbox.Land(category, filename, content);
        if (landing.Code is not null)
        {
            run.Refuse(landing.Code, landing.Message!, landing.Code == "invalid-filename" ? filename : null);
            return run.Result();
        }

        var upload = new JsonObject
        {
            ["path"] = landing.Path!.Replace('\\', '/'),
            ["category"] = category,
            ["filename"] = filename,
            ["bytes"] = landing.Bytes,
        };
        run.Note("upload", upload);

        if (categoryDescription is not null &&
            run.Step(new CliStep("categories", "category", "list")) &&
            !(run.DataOf("categories") as JsonArray ?? []).Any(c => (string?)c?["id"] == category))
        {
            run.Step(new CliStep("categoryProposal", "category", "propose")
                .Option("--description", categoryDescription)
                .Option("--rationale", categoryRationale)
                .Positional(category));
            return run.Result();
        }

        if (!run.Step(new CliStep("scan", "source", "scan")
                .Option("--category", category)
                .Positional(System.IO.Path.GetDirectoryName(landing.Path)!)))
            return run.Result();

        var entries = (run.DataOf("scan")?["entries"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var own = entries.FirstOrDefault(e => (string?)e["path"] == filename);
        upload["outcome"] = (string?)own?["outcome"];
        upload["sourceId"] = (string?)own?["id"];

        var planned = new List<string>();
        foreach (var e in entries)
        {
            var id = (string?)e["id"];
            if (id is not null && ((string?)e["outcome"] == "registered" || e == own) && !planned.Contains(id))
                planned.Add(id);
        }
        foreach (var id in planned)
        {
            if (run.Stopped)
                break;
            run.Step(new CliStep("plans", "ingest", "resume").Collect().Positional(id));
        }
        return run.Result();
    }
}
