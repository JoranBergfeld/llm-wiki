using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Persona: `read_persona` (spec tool catalogue: `persona read`, plus
// `persona interview guide` when asked, issue #40). An agent reads the
// persona before adjusting it, and the guide before running the onboarding
// interview.
[McpServerToolType]
public sealed class ReadPersonaTool(CliAdapter cli)
{
    [McpServerTool(Name = "read_persona", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Read the owner's persona (PERSONA.md: values, principles, tone of voice, boundaries; data.persona). " +
                 "A vault without PERSONA.md has an empty persona. With includeGuide, also the onboarding interview " +
                 "guide - starter questions per section and the rules for recording answers word for word (data.guide).")]
    public CallToolResult ReadPersona(
        [Description("Also return the interview guide (the vault's persona/interview-guide.md when present, else the shipped one)")]
        bool includeGuide = false,
        CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);
        if (run.Step(new CliStep("persona", "persona", "read")) && includeGuide)
            run.Step(new CliStep("guide", "persona", "interview", "guide"));
        return run.Result();
    }
}
