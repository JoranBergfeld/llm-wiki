using System.IO;
using System.Linq;
using System.Text;
using Wiki.Cli;
using Wiki.Core;

namespace Wiki.Services;

// `wiki persona read` result: the persona as four line arrays plus the
// three frontmatter fields. `Exists` separates "no PERSONA.md yet" from "a
// PERSONA.md with empty sections" for a caller that cares; both read as an
// empty persona. Frontmatter fields are null when absent or unparseable -
// `wiki lint` is where a malformed file gets reported, not here.
public sealed record PersonaView(
    bool Exists,
    string Path,
    string? SchemaVersion,
    string? LastDistilled,
    string? AuditEntry,
    string[] Values,
    string[] Principles,
    string[] ToneOfVoice,
    string[] Boundaries) : IHumanRenderable
{
    public string HumanSummary()
    {
        if (!Exists)
            return "No persona yet (PERSONA.md does not exist)";

        var sb = new StringBuilder();
        sb.Append("Persona");
        if (LastDistilled is not null) sb.Append($" (last distilled {LastDistilled})");
        AppendSection(sb, PersonaSection.Values, Values);
        AppendSection(sb, PersonaSection.Principles, Principles);
        AppendSection(sb, PersonaSection.ToneOfVoice, ToneOfVoice);
        AppendSection(sb, PersonaSection.Boundaries, Boundaries);
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, PersonaSection s, string[] lines)
    {
        sb.Append("\n\n").Append(PersonaSectionX.Heading(s));
        if (lines.Length == 0)
        {
            sb.Append("\n  (none)");
            return;
        }
        foreach (var line in lines)
            sb.Append("\n  - ").Append(line);
    }
}

// The `wiki persona` command group's service. This ticket (issue #48) adds
// only the read side; distill, propose and the interview commands land on
// the same service in later tickets. Only this group ever writes
// PERSONA.md - ingest has no path to it.
public sealed class PersonaService
{
    public static PersonaDoc Load(Vault v)
        => File.Exists(v.PersonaPath) ? PersonaDoc.Parse(File.ReadAllText(v.PersonaPath)) : PersonaDoc.Empty();

    public PersonaView Read(Vault v)
    {
        var exists = File.Exists(v.PersonaPath);
        var doc = Load(v);
        return new PersonaView(
            exists,
            PersonaDoc.FileName,
            doc.SchemaVersion,
            doc.LastDistilled,
            doc.AuditEntry,
            doc.Section(PersonaSection.Values).ToArray(),
            doc.Section(PersonaSection.Principles).ToArray(),
            doc.Section(PersonaSection.ToneOfVoice).ToArray(),
            doc.Section(PersonaSection.Boundaries).ToArray());
    }
}
