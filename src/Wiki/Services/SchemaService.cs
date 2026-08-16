using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Wiki.Core;
using Wiki.Docs;
using Wiki.State;

namespace Wiki.Services;

// The reflect loop's amendment surface (spec §13, amendment C):
// `wiki schema propose/proposals/approve/reject`. Full-section replacement
// only - no unified-diff engine (AOT + cross-platform hazard per the spec).
// "Humans own the schema" (design principle 4): Propose only stores a
// proposal; Approve is the one path that ever writes AGENTS.md, and it only
// runs when a human invokes it.
//
// AGENTS.md is not a protected path the way raw/, index.md and log.md are:
// it isn't a CLI-generated artifact but human/agent-authored content the CLI
// amends on explicit human approval, so Approve() writes it directly.
//
// Clock/RNG seam mirrors Issues/ReviewService/IngestService: defaults to the
// real clock and RandomNumberGenerator so production code just does
// `new SchemaService()`; tests inject fixed functions for deterministic ids
// and timestamps.
public sealed class SchemaService
{
    private readonly Func<long> _nowUnixMs;
    private readonly Func<byte[]> _randomBytes;

    public SchemaService(Func<long>? nowUnixMs = null, Func<byte[]>? randomBytes = null)
    {
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _randomBytes = randomBytes ?? DefaultRandomBytes;
    }

    // Validates the named section EXISTS in AGENTS.md at propose time (so
    // the human is never handed a proposal that can't possibly apply), then
    // stores it as an open proposal. This is a "validate at both ends"
    // design: Approve() re-checks below, because AGENTS.md may have changed
    // (human hand-edit, or a different approved proposal) between propose
    // and approve.
    public Proposal Propose(Vault v, string sectionHeading, string newText, string rationale)
    {
        if (string.IsNullOrWhiteSpace(sectionHeading))
            throw new ValidationException("invalid-section", "--section must be a non-empty heading text");

        var agentsText = ReadAgents(v);
        SectionLocator.EnsureExists(agentsText, sectionHeading);

        var nowMs = _nowUnixMs();
        var utcIso = ToIso(nowMs);
        var id = WikiUlid.New(nowMs, _randomBytes());

        var store = new Proposals();
        store.Load(v);
        var created = store.Add(id, sectionHeading, newText, rationale ?? "", utcIso);
        store.Save(v);

        return created;
    }

    public IReadOnlyList<Proposal> List(Vault v, string? status = null)
    {
        var store = new Proposals();
        store.Load(v);
        return store.List(status);
    }

    // Locates the proposal's named section in the CURRENT AGENTS.md (it may
    // have drifted since Propose validated it) and replaces its body
    // verbatim with the proposal's NewText, keeping the heading line and
    // every other section untouched. Sets the proposal's status to
    // "approved" and logs the amendment. Nothing about this is a
    // diff/patch apply - it's SectionLocator.Replace, full stop.
    public void Approve(Vault v, string proposalId)
    {
        var store = new Proposals();
        store.Load(v);
        var proposal = store.Get(proposalId)
            ?? throw new ValidationException("not-found", $"no proposal found for id '{proposalId}'");

        if (proposal.Status != "open")
            throw new StateConflictException("state-conflict",
                $"proposal '{proposalId}' is already '{proposal.Status}'; nothing to do");

        var agentsText = ReadAgents(v);
        var updated = SectionLocator.Replace(agentsText, proposal.Section, proposal.NewText);
        AtomicFile.Write(v.AgentsPath, updated);

        store.SetStatus(proposalId, "approved", null);
        store.Save(v);

        var utcIso = ToIso(_nowUnixMs());
        LogFile.Append(v, utcIso, "schema-approve", proposalId, $"section=\"{proposal.Section}\"");
    }

    // Marks the proposal rejected (+ optional note); AGENTS.md is never
    // touched - design principle 4, "the LLM may propose; it may never
    // apply", and a rejected proposal never applies either.
    public void Reject(Vault v, string proposalId, string? note)
    {
        var store = new Proposals();
        store.Load(v);
        var proposal = store.Get(proposalId)
            ?? throw new ValidationException("not-found", $"no proposal found for id '{proposalId}'");

        if (proposal.Status != "open")
            throw new StateConflictException("state-conflict",
                $"proposal '{proposalId}' is already '{proposal.Status}'; nothing to do");

        store.SetStatus(proposalId, "rejected", note);
        store.Save(v);

        var utcIso = ToIso(_nowUnixMs());
        LogFile.Append(v, utcIso, "schema-reject", proposalId, note ?? "(no note)");
    }

    private static string ReadAgents(Vault v)
    {
        if (!File.Exists(v.AgentsPath))
            throw new ValidationException("agents-missing", $"'{v.AgentsPath}' does not exist; run 'wiki init' first", v.AgentsPath);
        return File.ReadAllText(v.AgentsPath);
    }

    private static string ToIso(long unixMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static byte[] DefaultRandomBytes()
    {
        var bytes = new byte[10];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
