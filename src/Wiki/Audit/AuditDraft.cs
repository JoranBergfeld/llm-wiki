using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Wiki.Audit;

// One field a command contributed to its audit line, in the order added.
// IsDetail: the value is stored in a detail file and the line carries its
// hash instead (`key=@<sha256>`).
public sealed record AuditField(string Key, string Value, bool IsDetail);

// What a mutating command wants recorded on its audit line, filled in by the
// command handler through `ctx.Audit` and turned into a line by AuditStep
// once the command returns (issue #44).
//
// Every field is optional: with nothing set, the line is
//   <utc> | <command.path> | <positional args or -> | exit=<n> | via=<origin> | prev=… | hash=…
// Later tickets enrich it rather than inventing a second writer. A persona
// accept, for example:
//   ctx.Audit.Action = "persona.accept";
//   ctx.Audit.Subject = "PERSONA.md";
//   ctx.Audit.Field("lines", "+1 -0")
//            .Field("evidence", "[[page-a]]@3f2a…,owner-stated@9c1e…")
//            .Field("decidedVia", "cli")
//            .Field("reason", reason)
//            .Detail("changeset", fullDiffAndReasoning);
//
// Field values may hold any text: `|`, `%`, line breaks and a leading `@` are
// percent-escaped on the line. A value longer than AuditLine.MaxInlineValue
// moves to a detail file on its own, so a caller never has to decide.
public sealed class AuditDraft
{
    private static readonly Regex KeyShape = new("^[a-z][A-Za-z0-9-]*$", RegexOptions.Compiled);

    // Written by the step itself; a command may not set them.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "exit", "error", "via", "prev", "hash" };

    private readonly List<AuditField> _fields = new();
    private Action<string>? _writeOnSeal;

    // Dotted action name. Defaults to the command path ("page upsert" ->
    // "page.upsert").
    public string? Action { get; set; }

    // What the change was about: a page slug, a proposal id, PERSONA.md.
    // Defaults to the command's positional arguments, comma-separated.
    public string? Subject { get; set; }

    // The vault the change landed in, when the command creates it: `init`
    // runs before any wiki.yaml exists, so the boundary cannot resolve the
    // vault up front and must be told afterwards where the log belongs.
    public string? VaultRoot { get; set; }

    public IReadOnlyList<AuditField> Fields => _fields;

    // A vault write that must name the audit line recording it: PERSONA.md's
    // `audit_entry` is the hash of its accept line (amendment AY). The hash
    // covers the line's timestamp and prev, so it exists only once AuditStep
    // builds the line after the command returns. AuditStep then calls this
    // with the hash, before sealing the line into the write journal, so the
    // write is journaled and recovered with the rest of the command. At most
    // one per command.
    public Action<string>? SealedWrite => _writeOnSeal;

    public void WriteOnSeal(Action<string> write)
    {
        if (_writeOnSeal is not null)
            throw new InvalidOperationException("a command registers at most one write on seal");
        _writeOnSeal = write;
    }

    public AuditDraft Field(string key, string value) => Add(key, value, isDetail: false);

    // Always stored in a detail file referenced by hash, whatever its size:
    // for content that is large by nature (a changeset's diff and reasoning).
    public AuditDraft Detail(string key, string content) => Add(key, content, isDetail: true);

    private AuditDraft Add(string key, string value, bool isDetail)
    {
        // Programming errors, not user input: fail loudly in the command that
        // made them rather than write an unparseable line.
        if (!KeyShape.IsMatch(key))
            throw new ArgumentException($"audit field key '{key}' must match {KeyShape}", nameof(key));
        if (Reserved.Contains(key))
            throw new ArgumentException($"audit field key '{key}' is reserved", nameof(key));
        foreach (var f in _fields)
            if (f.Key == key)
                throw new ArgumentException($"audit field key '{key}' was already added", nameof(key));

        _fields.Add(new AuditField(key, value ?? "", isDetail));
        return this;
    }
}
