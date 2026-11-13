using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Persona: `record_owner_statement` (issue #40; wraps `wiki persona interview
// record`). An assistant runs the onboarding interview, or records a line the
// owner dictates, one answer per call. The answer goes to the CLI on stdin so
// it reaches the transcript byte for byte, whatever it looks like.
//
// There is deliberately no argument saying how the answer was recorded:
// `recordedVia: mcp`, the transport and the token flag come from the
// server's InvocationOrigin (bound into the CliAdapter), so a client cannot
// pass an MCP answer off as typed at the CLI. Trust risk (an agent inventing
// the owner's answers) is named in #40; a deployment narrows this tool away
// with --exclude-tools.
[McpServerToolType]
public sealed class RecordOwnerStatementTool(CliAdapter cli)
{
    [McpServerTool(Name = "record_owner_statement", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Record one thing the owner said about themselves, word for word, in an interview transcript " +
                 "(wiki persona interview record, data.record). Without `transcript` a new transcript is started and its " +
                 "id returned (data.record.transcript); pass that id to append the next answer of the same interview. " +
                 "Never paraphrase or suggest an answer: record exactly what the owner said (see read_persona includeGuide). " +
                 "The transcript is a candidate for the next distill_persona pass.")]
    public CallToolResult RecordOwnerStatement(
        [Description("The persona section the answer speaks to: values | principles | tone-of-voice | boundaries")]
        [AllowedValues("values", "principles", "tone-of-voice", "boundaries")]
        string section,
        [Description("The owner's answer, word for word")]
        string answer,
        [Description("The question as asked; omit for a line the owner dictated")]
        string? question = null,
        [Description("Append to this transcript (its id, e.g. 2026-11-13-v2); omit to start a new one")]
        string? transcript = null,
        CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);
        run.Step(new CliStep("record", "persona", "interview", "record")
            .Option("--section", section)
            // `--opt=` (empty) does not parse (#57); a blank question or
            // transcript means the same as omitting it.
            .Option("--question", string.IsNullOrWhiteSpace(question) ? null : question)
            .Option("--transcript", string.IsNullOrWhiteSpace(transcript) ? null : transcript)
            .Flag("--stdin", true)
            .Stdin(answer));
        return run.Result();
    }
}
