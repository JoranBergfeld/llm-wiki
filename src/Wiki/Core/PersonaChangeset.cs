namespace Wiki.Core;

// A persona changeset (issue #50, design in #32/#33): one distillation pass's
// proposal, touching any number of lines across the four sections, reviewed
// all-or-nothing. The same shape is the `wiki persona propose` input (the
// agent fills reason, considered and changes), the stored record in
// .wiki/persona-changesets.json (the CLI adds id, status, createdAt and
// resolves every evidence reference) and the detail the audit log keeps.
//
// Plain settable classes rather than records so System.Text.Json source-gen
// (AOT) round-trips them without converters.
public sealed class PersonaChangesetData
{
    public string Id { get; set; } = "";

    // open | approved | rejected | superseded.
    public string Status { get; set; } = "";
    public string CreatedAt { get; set; } = "";

    // Why the pass proposes this; required on input. Recorded on the audit
    // line of the proposal and of the accept.
    public string Reason { get; set; } = "";

    // Page ids (input may give slugs) the pass looked at. Their candidate
    // flags, and those of every page cited as evidence, are cleared at
    // propose time.
    public string[] Considered { get; set; } = System.Array.Empty<string>();

    public PersonaChangeData[] Changes { get; set; } = System.Array.Empty<PersonaChangeData>();

    // Set when the changeset is decided or superseded.
    public string? Note { get; set; }
    public string? SupersededBy { get; set; }
}

// One line operation. `op` is add (append `text`), change (replace the line
// equal to `from` with `text`, in place) or remove (drop the line equal to
// `from`). add and change carry evidence for the new text; remove carries
// none, because removing a line retires its evidence (issue #33).
public sealed class PersonaChangeData
{
    public string Op { get; set; } = "";

    // Wire name: values | principles | tone-of-voice | boundaries.
    public string Section { get; set; } = "";
    public string? From { get; set; }
    public string? Text { get; set; }
    public PersonaEvidenceData[]? Evidence { get; set; }
}

// One evidence reference. `kind` discriminates; the rest is shaped so every
// kind points at one file, optionally at one place in it, with the content
// hash taken when the reference was recorded:
//
//   page          (this ticket)  page = page id, path = wiki/... ,         hash = sha256 of the page body
//   owner-stated  (issue #53)    path = the transcript file, anchor = the answer's anchor,
//                                hash = sha256 of that one answer's text
//
// On input only `kind` (default "page") and `page` (id or slug) are read for
// page evidence; path and hash are always computed by the CLI. Issue #53 adds
// the owner-stated branch in PersonaService.ResolveEvidence and the transcript
// lookup; no other part of the changeset, the store or the audit detail
// changes shape.
public sealed class PersonaEvidenceData
{
    public string Kind { get; set; } = "";
    public string? Page { get; set; }
    public string? Path { get; set; }
    public string? Anchor { get; set; }
    public string? Hash { get; set; }
}

// One line of the persona as accepted, with the evidence it rests on. The
// list of these for every line of PERSONA.md is stored as the `evidence`
// detail of each persona.accept audit line, so lint (and the stale-evidence
// check, issue #52) can answer "what supports this line?" from the one
// entry PERSONA.md's `audit_entry` names.
public sealed class PersonaLedgerLine
{
    public string Section { get; set; } = "";
    public string Text { get; set; } = "";
    public PersonaEvidenceData[] Evidence { get; set; } = System.Array.Empty<PersonaEvidenceData>();
}

public static class PersonaEvidenceKind
{
    public const string Page = "page";
    public const string OwnerStated = "owner-stated";
}
