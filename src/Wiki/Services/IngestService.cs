using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Wiki.Cli;
using Wiki.Core;
using Wiki.Docs;
using Wiki.Json;
using Wiki.State;

namespace Wiki.Services;

// `wiki ingest resume` result (spec §10's "resume guarantee"): where a source
// currently sits in the ledger state machine, plus what's left to reach
// `linted` and a human/machine description of each remaining step's expected
// artifact. `Current` stays the domain LedgerState (not a wire string) per
// the task's interface contract - IngestCommand converts to ResumePlanView
// (below) at the JSON boundary, same split as LedgerEntry/LedgerEntryData.
public sealed record ResumePlan(
    string SourceId,
    LedgerState Current,
    string[] RemainingStates,
    string[] ExpectedArtifacts);

// Wire shape for `ResumePlan.Current` - the only field that isn't already a
// plain string. Registered in WikiJsonContext; built by IngestCommand, never
// by IngestService itself (keeps IngestService's public contract matching
// the brief exactly).
public sealed record ResumePlanView(
    string SourceId,
    string Current,
    string[] RemainingStates,
    string[] ExpectedArtifacts);

// `wiki ingest advance` success result. Advance() itself is void per the
// brief's interface - this is purely a JSON-envelope/human-summary shape the
// command builds once Advance() returns without throwing.
public sealed record IngestAdvanceResult(string SourceId, string State) : IHumanRenderable
{
    public string HumanSummary() => $"Advanced {SourceId} -> {State}";
}

// Minimal read-only shape for `.wiki/lint.json`'s `lastRun` field (amendment
// D). `wiki lint` (Task 22) is the writer and owns the full shape; this is
// just enough to read the timestamp back for the `linted` precondition
// below. Deliberately not over-built - Task 22 may extend/relocate this.
public sealed class LintStateData
{
    public string? LastRun { get; set; }
}

// The ingest state-machine CLI backing service (spec §10): validates every
// ledger transition's precondition table before recording it, and answers
// "where am I / what's left" for a source with zero conversation context
// (the resume guarantee). Ledger itself (Task 16) only knows how to *record*
// a transition; this is where the preconditions that gate *whether* a
// transition is allowed live.
//
// Clock seam: mirrors PageService/SourceService - defaults to the real
// clock so production code (IngestCommand) just does `new IngestService()`;
// tests inject a fixed function for deterministic `integratedAt` timestamps.
public sealed class IngestService
{
    private readonly Func<long> _nowUnixMs;

    public IngestService(Func<long>? nowUnixMs = null)
    {
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // `wiki ingest status [<source-id>]`. With an id: that single entry (or
    // unknown-source if it's never been registered). Without: every entry
    // NOT in `linted` state - a linted source has nothing left to track.
    public IReadOnlyList<LedgerEntry> Status(Vault v, string? sourceId)
    {
        var ledger = new Ledger();
        ledger.Load(v);

        if (sourceId is not null)
        {
            var entry = ledger.Get(sourceId)
                ?? throw new ValidationException("unknown-source", $"unknown source id '{sourceId}'");
            return new[] { entry };
        }

        var result = new List<LedgerEntry>();
        foreach (var entry in ledger.All())
        {
            if (entry.State != LedgerState.Linted)
                result.Add(entry);
        }
        return result;
    }

    // `wiki ingest advance <source-id> --to <state>`. Every precondition
    // check below runs before Ledger.Advance/Save - "nothing lands on a
    // failed precondition" (Global Constraints), same discipline as
    // PageService/SourceService's validate-then-write split.
    public void Advance(Vault v, VaultConfig cfg, string sourceId, LedgerState to, string[] touched)
    {
        var ledger = new Ledger();
        ledger.Load(v);

        var entry = ledger.Get(sourceId)
            ?? throw new ValidationException("unknown-source", $"unknown source id '{sourceId}'");

        // Idempotent no-op: re-advancing to the state a source is already in
        // is a safe resume artifact (an agent replaying `ingest advance`
        // after a dropped connection, say), not a mistake - exit 3, not a
        // crash (spec §8 exit code table).
        if (to == entry.State)
            throw new StateConflictException("state-conflict",
                $"source '{sourceId}' is already in state '{LedgerStateX.ToWire(to)}'; nothing to do");

        // Ordering: the chain is strictly linear (registered -> summarized ->
        // integrated -> linted), so the only valid forward transition from
        // any state is the very next one. Anything else - skipping ahead,
        // or naming an earlier state that isn't the current one - is
        // out-of-order. (The current-state case is handled above, first, so
        // it never falls through to this check.)
        if ((int)to != (int)entry.State + 1)
            throw new ValidationException("precondition-order",
                $"cannot advance source '{sourceId}' from '{LedgerStateX.ToWire(entry.State)}' to '{LedgerStateX.ToWire(to)}'; " +
                "states advance one step at a time (registered -> summarized -> integrated -> linted)");

        switch (to)
        {
            case LedgerState.Summarized:
                CheckSummaryPrecondition(v, sourceId);
                break;
            case LedgerState.Integrated:
                CheckIndexConsistent(v);
                break;
            case LedgerState.Linted:
                CheckLintPrecondition(v, entry);
                break;
            // LedgerState.Registered can never be `to` here: entry.State has
            // no predecessor for the `+1` check above to satisfy, so it
            // always trips precondition-order first.
        }

        var nowMs = _nowUnixMs();
        var utcIso = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

        // --- Validation complete. Everything from here on is the write. ---

        ledger.Advance(sourceId, to, touched, utcIso);
        ledger.Save(v);

        LogFile.Append(v, utcIso, "ingest-advance", sourceId, $"to={LedgerStateX.ToWire(to)}");
    }

    // `wiki ingest resume <source-id>`: the "fresh session, zero context"
    // recovery primitive from spec §10. Purely read-only.
    public ResumePlan Resume(Vault v, string sourceId)
    {
        var ledger = new Ledger();
        ledger.Load(v);
        var entry = ledger.Get(sourceId)
            ?? throw new ValidationException("unknown-source", $"unknown source id '{sourceId}'");

        var remainingStates = new List<string>();
        var artifacts = new List<string>();
        for (var s = entry.State + 1; s <= LedgerState.Linted; s++)
        {
            remainingStates.Add(LedgerStateX.ToWire(s));
            artifacts.Add(ArtifactDescription(s));
        }

        return new ResumePlan(sourceId, entry.State, remainingStates.ToArray(), artifacts.ToArray());
    }

    private static string ArtifactDescription(LedgerState to) => to switch
    {
        LedgerState.Summarized =>
            "a 'summary'-type page whose 'sources' list includes this source id " +
            "(wiki page upsert --type summary --sources <id> --stdin)",
        LedgerState.Integrated =>
            "entity/concept pages updated to reflect this source, with wiki/index.md current " +
            "(wiki page upsert ... --sources <id>,... then ingest advance --to integrated --touched id1,id2,...)",
        LedgerState.Linted =>
            "a 'wiki lint' run recorded in .wiki/lint.json newer than this source's 'integrated' timestamp",
        _ => throw new ValidationException("invalid-ledger-state", $"no expected artifact for state '{to}'"),
    };

    // `summarized` precondition (spec §10): a summary-type page citing this
    // source in its `sources` list must exist. Reuses PageStore.Enumerate,
    // same scan every other page-set check in the codebase uses.
    private static void CheckSummaryPrecondition(Vault v, string sourceId)
    {
        foreach (var (_, front) in PageStore.Enumerate(v))
        {
            if (front.Type == PageType.Summary && Array.IndexOf(front.Sources, sourceId) >= 0)
                return;
        }

        throw new ValidationException("precondition-summary",
            $"no summary-type page cites source '{sourceId}' in its 'sources' list; " +
            $"write one first: wiki page upsert --type summary --sources {sourceId} --stdin");
    }

    // `integrated` precondition (spec §10): "index verified consistent".
    // Pragmatic cheap check per the task brief: re-render the index from the
    // current page set (the same IndexFile.Render every upsert already calls
    // to regenerate wiki/index.md) and compare it byte-for-byte against what
    // is actually on disk. Since every page mutation already regenerates
    // index.md, this only trips if something wrote to index.md outside the
    // CLI (the one thing §9 says must never happen) - exactly the drift this
    // precondition exists to catch, without a heavier diff/merge engine.
    private static void CheckIndexConsistent(Vault v)
    {
        var expected = IndexFile.Render(PageStore.Enumerate(v));
        var actual = File.Exists(v.IndexPath) ? File.ReadAllText(v.IndexPath) : "";
        if (expected != actual)
            throw new ValidationException("precondition-index",
                "wiki/index.md does not match a freshly rendered index (index drift); " +
                "run 'wiki reindex' or investigate before integrating");
    }

    // `linted` precondition (spec §10, amendment D): a lint run newer than
    // this entry's `integratedAt` timestamp must exist, tracked in
    // `.wiki/lint.json`'s `lastRun` field. `wiki lint` (Task 22) is the only
    // writer of that file; until it exists, this precondition can never
    // pass - which is exactly the "documents the amendment-D wiring ahead of
    // Task 22" behavior the brief asks for.
    private static void CheckLintPrecondition(Vault v, LedgerEntry entry)
    {
        var lintPath = Path.Combine(v.StateDir, "lint.json");
        if (!File.Exists(lintPath))
            throw new ValidationException("precondition-lint",
                $"no lint run recorded yet ('{lintPath}' does not exist); run 'wiki lint' first");

        LintStateData? data;
        try
        {
            data = JsonSerializer.Deserialize(File.ReadAllText(lintPath), WikiJsonContext.Default.LintStateData);
        }
        catch (JsonException)
        {
            data = null;
        }

        if (string.IsNullOrEmpty(data?.LastRun) ||
            !DateTimeOffset.TryParse(data.LastRun, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lastRun) ||
            entry.IntegratedAt is null ||
            !DateTimeOffset.TryParse(entry.IntegratedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var integratedAt) ||
            lastRun <= integratedAt)
        {
            throw new ValidationException("precondition-lint",
                $"no lint run newer than this source's 'integrated' timestamp ({entry.IntegratedAt}) is recorded " +
                $"in '{lintPath}'; run 'wiki lint' again after integrating");
        }
    }
}
