namespace Wiki.Cli;

// Implemented by command result DTOs (Envelope.Data payloads) that want a
// one-line human-readable summary for the non-JSON output path. Keeps
// CommandContext.EmitOk generic across every command's result type instead of
// pattern-matching on concrete DTOs.
public interface IHumanRenderable
{
    string HumanSummary();
}
