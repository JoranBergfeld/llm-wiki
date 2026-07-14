using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Wiki.Cli;
using Wiki.Core;
using Wiki.Docs;
using Wiki.State;

namespace Wiki.Services;

// `wiki source add` result.
public sealed record SourceAddResult(
    string Id,
    string Path,
    string Sha256,
    string Category) : IHumanRenderable
{
    public string HumanSummary() => $"Registered source {Id} ({Category}) -> {Path}";
}

// `wiki source list` row shape - a scanning/routing view, same spirit as
// PageSummary: wire strings for Status (not the enum), no body.
public sealed record SourceSummary(
    string Id,
    string Title,
    string Category,
    string Status,
    string Added,
    string Sha256);

// `wiki source show <id>` result: full source frontmatter plus (optionally)
// the raw body. Body is null - and so omitted from JSON via WikiJsonContext's
// WhenWritingNull default - when the caller passed --frontmatter-only, same
// convention as PageView.
public sealed record SourceView(
    string Id,
    string Title,
    string Category,
    string Status,
    string Added,
    string Sha256,
    string Origin,
    string? Body);

// `wiki source impact <id>` row: one page whose frontmatter `sources` array
// cites this source id - the provenance query an agent runs before
// retracting/editing a source to see what would be affected.
public sealed record SourceImpactEntry(
    string Id,
    string Slug,
    string Title,
    string Type,
    string Status);

// `wiki source retract <id>` result (spec §14). ArchivedSummaries is
// (almost always one, but not schema-enforced) the summary-type page(s)
// citing this source id, each flipped to `archived`. AffectedPages is every
// OTHER citing page, flipped to `needs-review` with a `retraction` issue
// filed per page - that's the punch list `wiki issues list --kind
// retraction` hands the agent. Purged mirrors the `--purge` flag the caller
// passed, so a JSON consumer doesn't have to separately re-check.
public sealed record RetractResult(
    string Id,
    string Status,
    string[] ArchivedSummaries,
    string[] AffectedPages,
    bool Purged) : IHumanRenderable
{
    public string HumanSummary() =>
        $"Retracted source {Id}: {ArchivedSummaries.Length} summary page(s) archived, " +
        $"{AffectedPages.Length} page(s) flagged needs-review" + (Purged ? ", raw content purged" : "");
}

// Registers an immutable raw source: validates the category, hashes the
// input file's content for integrity + dedup, writes raw/<id>.md (source
// frontmatter + the original content as the body - the ONE sanctioned write
// under raw/), registers it in the idmap and the ingest ledger, and appends
// a log entry. Every check in Add() runs before any write - a rejection
// leaves the vault byte-identical to how it was called (same "nothing lands"
// discipline as PageService.Create/Update).
//
// Clock/RNG seam: mirrors PageService exactly. Constructor defaults to the
// real clock and RandomNumberGenerator so production code (SourceCommand)
// just does `new SourceService()`; tests inject fixed functions for
// deterministic ULID/added/registered-at values. Both the ULID timestamp and
// the added/registered timestamps are derived from the SAME captured
// `nowMs`, so they can never disagree about "now" within one Add() call.
public sealed class SourceService
{
    private readonly Func<long> _nowUnixMs;
    private readonly Func<byte[]> _randomBytes;

    public SourceService(Func<long>? nowUnixMs = null, Func<byte[]>? randomBytes = null)
    {
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _randomBytes = randomBytes ?? DefaultRandomBytes;
    }

    public SourceAddResult Add(Vault v, VaultConfig cfg, string file, string category, string title, string? origin)
    {
        // --- Blocking validation: ALL of it runs before anything below touches disk. ---

        if (!cfg.HasCategory(category))
            throw new ValidationException(
                "unknown-category",
                $"unknown category '{category}'; add it first with 'wiki category add {category} --description \"...\"'");

        if (!File.Exists(file))
            throw new ValidationException("source-file-not-found", $"source file '{file}' does not exist", file);

        // Frontmatter schema gate: reject a title that would corrupt the
        // closed-schema quoting round-trip (a stray '"' or newline) - same
        // rationale/shape as PageService.GuardScalar for page title/summary.
        GuardScalar(title, "title");

        var resolvedOrigin = string.IsNullOrWhiteSpace(origin) ? "manual" : origin;
        GuardScalar(resolvedOrigin, "origin");

        var content = File.ReadAllText(file);
        var sha256 = ComputeSha256Hex(content);

        var existingId = FindExistingSourceIdBySha(v, sha256);
        if (existingId is not null)
            throw new ValidationException(
                "duplicate-source",
                $"source content already registered as '{existingId}' (matching sha256 '{sha256}')");

        var nowMs = _nowUnixMs();
        var id = WikiUlid.New(nowMs, _randomBytes());
        var today = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var front = new SourceFrontmatter
        {
            Id = id,
            Title = title,
            Category = category,
            Added = today,
            Sha256 = sha256,
            Origin = resolvedOrigin,
            Status = SourceStatus.Active,
        };

        var serialized = front.ToBlock() + "\n" + content;

        // Frontmatter schema gate proper: must round-trip through the same
        // closed-schema parser real raw/ files are read back with (mirrors
        // PageDoc.Parse(serialized) in PageService.Create/Update).
        var (roundTripScalars, roundTripLists, _) = Frontmatter.ReadBlock(serialized);
        SourceFrontmatter.FromRaw(roundTripScalars, roundTripLists);

        var targetPath = Path.Combine(v.RawDir, id + ".md");

        // --- Validation complete. Everything from here on is the write. ---

        // This is the one sanctioned write under raw/: called directly, NOT
        // through AtomicFile.GuardWritable (that guard is the caller-invoked
        // policy check every other command must pass before writing user
        // content; source-add IS the allowed producer of raw/<id>.md).
        AtomicFile.Write(targetPath, serialized);

        var relPath = Path.GetRelativePath(v.Root, targetPath).Replace('\\', '/');

        var idmap = new IdMap();
        idmap.Load(v);
        idmap.Put(id, relPath);
        idmap.Save(v);

        var utcIso = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var ledger = new Ledger();
        ledger.Load(v);
        ledger.Register(id, utcIso);
        ledger.Save(v);

        LogFile.Append(v, utcIso, "source-add", id, $"category={category} sha256={sha256}");

        return new SourceAddResult(id, relPath, sha256, category);
    }

    // `wiki source list [--status] [--category]`: enumerate raw/*.md,
    // parsing each one's SOURCE frontmatter, keeping the ones matching both
    // filters if given. Read-only - raw/ is immutable and this never writes
    // anything, matching PageService.List's shape/discipline exactly.
    public IReadOnlyList<SourceSummary> List(Vault v, SourceStatus? status, string? category)
    {
        var result = new List<SourceSummary>();
        foreach (var (_, front) in EnumerateSources(v))
        {
            if (status is not null && front.Status != status.Value) continue;
            if (category is not null && !string.Equals(front.Category, category, StringComparison.Ordinal)) continue;

            result.Add(new SourceSummary(
                front.Id,
                front.Title,
                front.Category,
                SourceStatusX.ToWire(front.Status),
                front.Added,
                front.Sha256));
        }
        // Array (not List<T>) so the runtime type boxed into Envelope.Data
        // matches WikiJsonContext's [JsonSerializable(typeof(SourceSummary[]))]
        // registration, same reasoning as PageService.List.
        return result.ToArray();
    }

    // `wiki source show <id>`: the source's full frontmatter, plus its raw
    // body unless --frontmatter-only. Read-only. `not-found` if the id isn't
    // a registered source - same code PageService.Show uses for an
    // unresolvable page id/slug, since this is the same kind of "does this
    // thing exist" lookup, just for the raw/ side of the vault.
    public SourceView Show(Vault v, string id, bool frontmatterOnly)
    {
        var (front, body, _) = ResolveSource(v, id);
        return new SourceView(
            front.Id,
            front.Title,
            front.Category,
            SourceStatusX.ToWire(front.Status),
            front.Added,
            front.Sha256,
            front.Origin,
            frontmatterOnly ? null : body);
    }

    // `wiki source impact <id>`: the provenance query - every PAGE whose
    // frontmatter `sources` array cites this source id. Read-only: scans
    // page frontmatter only (PageStore.Enumerate), never touches raw/ or
    // writes anything. `not-found` if the id isn't a registered source
    // (mirrors Show's guard) so a typo'd id fails clearly rather than
    // silently returning an empty list.
    public IReadOnlyList<SourceImpactEntry> Impact(Vault v, string id)
    {
        var (front, _, _) = ResolveSource(v, id);

        var result = new List<SourceImpactEntry>();
        foreach (var (slug, pageFront) in PageStore.Enumerate(v))
        {
            if (Array.IndexOf(pageFront.Sources, front.Id) < 0)
                continue;

            result.Add(new SourceImpactEntry(
                pageFront.Id,
                slug,
                pageFront.Title,
                PageTypeX.ToWire(pageFront.Type),
                PageStatusX.ToWire(pageFront.Status)));
        }
        return result.ToArray();
    }

    // `wiki source retract <id> --reason "…" [--purge]` (spec §14). Runs the
    // retraction cascade in the exact order the spec lists:
    //
    //   1. Source frontmatter -> retracted. The closed source schema
    //      (id/type/title/category/added/sha256/origin/status) has no field
    //      for reason/timestamp - adding one would be an unapproved
    //      frontmatter key, which Frontmatter.ValidateKeys would then reject
    //      on every future read of this exact file. So the reason lives in
    //      the log line and in each filed issue's detail instead; the
    //      timestamp is the log line's own `utcIso`.
    //   2. Every page whose `sources` cites this id AND is itself a
    //      `summary`-type page -> `archived` via SetStatus. No issue filed
    //      for these - the archived summary IS the human-readable record of
    //      what the retracted source said (spec §14's own words: "readable
    //      in the archived summary").
    //   3. Every OTHER citing page -> `needs-review` via SetStatus, plus a
    //      `retraction` issue filed per page (Issues.Upsert, kind=Retraction)
    //      carrying the reason - the punch list `wiki issues list --kind
    //      retraction` hands the agent (spec §14's repair loop).
    //   4. Index regenerated (each SetStatus call above already does this -
    //      archived pages drop out of index.md, so there is nothing left to
    //      regenerate beyond what the per-page calls already did) and a
    //      `retract` log line written.
    //   5. `--purge`: AFTER 1-4, the raw file's body is replaced with an
    //      empty body while its (now-retracted) frontmatter is kept intact -
    //      the file at the SAME path is rewritten via AtomicFile.Write, so
    //      the id keeps resolving through the idmap and Show/Impact/a repeat
    //      `retract` guard all keep working. This is the "metadata stub":
    //      id/type/title/category/added/sha256/origin/status survive,
    //      the raw content that prompted the compliance request does not.
    //      Without --purge the raw file (frontmatter + full body) is left
    //      exactly as step 1 wrote it.
    //
    // Already-retracted is rejected outright (own error code, no write) -
    // spec §14 doesn't say what a second retract on the same id should do,
    // and silently re-running the cascade would re-archive an
    // already-archived summary, re-file/bump the same retraction issues a
    // second time on unrelated pages, and (worse, under --purge) overwrite
    // an already-purged stub with a no-op write. Rejecting is the safer
    // default; a caller who really wants a fresh reason recorded can `page
    // set-status` by hand.
    public RetractResult Retract(Vault v, string id, string reason, bool purge)
    {
        // --- Blocking validation: ALL of it runs before anything below touches disk. ---

        if (string.IsNullOrWhiteSpace(reason))
            throw new ValidationException("reason-required", "--reason is required to retract a source");
        GuardScalar(reason, "reason");

        var (front, body, fullPath) = ResolveSource(v, id);

        if (front.Status == SourceStatus.Retracted)
            throw new ValidationException("already-retracted", $"source '{id}' is already retracted");

        // Snapshot every citing page BEFORE any write - same "gather first,
        // mutate after validation" discipline as every other service here.
        var citingPages = new List<(string Slug, PageFrontmatter Front)>();
        foreach (var (slug, pageFront) in PageStore.Enumerate(v))
        {
            if (Array.IndexOf(pageFront.Sources, front.Id) >= 0)
                citingPages.Add((slug, pageFront));
        }

        var retractedFront = new SourceFrontmatter
        {
            Id = front.Id,
            Title = front.Title,
            Category = front.Category,
            Added = front.Added,
            Sha256 = front.Sha256,
            Origin = front.Origin,
            Status = SourceStatus.Retracted,
        };
        var serialized = retractedFront.ToBlock() + "\n" + body;
        // Frontmatter schema gate proper: must round-trip through the same
        // closed-schema parser real raw/ files are read back with.
        var (roundTripScalars, roundTripLists, _) = Frontmatter.ReadBlock(serialized);
        SourceFrontmatter.FromRaw(roundTripScalars, roundTripLists);

        // --- Validation complete. Everything from here on is the write. ---

        var nowMs = _nowUnixMs();
        var utcIso = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

        // Step 1: source frontmatter -> retracted.
        AtomicFile.Write(fullPath, serialized);

        // Steps 2 + 3: cascade over every citing page.
        var pageService = new PageService(_nowUnixMs);
        var archivedSummaries = new List<string>();
        var affectedPages = new List<string>();

        var issues = new Issues();
        issues.Load(v);

        foreach (var (slug, pageFront) in citingPages)
        {
            if (pageFront.Type == PageType.Summary)
            {
                pageService.SetStatus(v, pageFront.Id, PageStatus.Archived);
                archivedSummaries.Add(slug);
            }
            else
            {
                pageService.SetStatus(v, pageFront.Id, PageStatus.NeedsReview);
                var detail = $"source '{id}' was retracted (reason: {reason}); page cites it and needs repair " +
                    "(rewrite the body to drop claims resting on it, remove the id from 'sources', upsert)";
                issues.Upsert(IssueKind.Retraction, slug, detail, utcIso);
                affectedPages.Add(slug);
            }
        }
        issues.Save(v);

        // Step 4: log line. (Index regeneration already happened inside each
        // SetStatus call above; there is no page-independent index state left
        // to regenerate here.)
        LogFile.Append(v, utcIso, "retract", id,
            $"reason=\"{reason}\" archived_summaries={archivedSummaries.Count} affected_pages={affectedPages.Count}" +
            (purge ? " purge=true" : ""));

        // Step 5: --purge - rewrite the raw file at the same path with the
        // retracted frontmatter but an empty body (the metadata stub).
        if (purge)
        {
            var stub = retractedFront.ToBlock() + "\n";
            AtomicFile.Write(fullPath, stub);
        }

        return new RetractResult(
            front.Id,
            SourceStatusX.ToWire(SourceStatus.Retracted),
            archivedSummaries.ToArray(),
            affectedPages.ToArray(),
            purge);
    }

    // Resolves a source id via the idmap to its parsed frontmatter + raw
    // body + full path. Shared by Show and Impact - both need "does this id
    // resolve to a real raw/ source" as their first guard.
    private static (SourceFrontmatter Front, string Body, string FullPath) ResolveSource(Vault v, string id)
    {
        var idmap = new IdMap();
        idmap.Load(v);

        var relPath = idmap.PathFor(id);
        if (relPath is null || !relPath.StartsWith("raw/", StringComparison.Ordinal))
            throw new ValidationException("not-found", $"no source found for id '{id}'");

        var fullPath = Path.Combine(v.Root, relPath);
        if (!File.Exists(fullPath))
            throw new ValidationException("not-found", $"no source found for id '{id}'");

        var (scalars, lists, body) = Frontmatter.ReadBlock(File.ReadAllText(fullPath));
        var front = SourceFrontmatter.FromRaw(scalars, lists);
        return (front, body, fullPath);
    }

    // Scans raw/*.md (TopDirectoryOnly - raw/assets/ is a subdirectory and is
    // never visited), sorted for deterministic order, same shape as
    // FindExistingSourceIdBySha / ReindexService.EnumerateRawSources.
    private static IEnumerable<(string Id, SourceFrontmatter Front)> EnumerateSources(Vault v)
    {
        if (!Directory.Exists(v.RawDir))
            yield break;

        var files = new List<string>(Directory.EnumerateFiles(v.RawDir, "*.md", SearchOption.TopDirectoryOnly));
        files.Sort(StringComparer.Ordinal);

        foreach (var f in files)
        {
            var (scalars, lists, _) = Frontmatter.ReadBlock(File.ReadAllText(f));
            var front = SourceFrontmatter.FromRaw(scalars, lists);
            yield return (front.Id, front);
        }
    }

    // Scans raw/*.md (TopDirectoryOnly - raw/assets/ is a subdirectory and is
    // never visited) looking for a source frontmatter whose sha256 matches.
    // Sorted for deterministic scan order, matching ReindexService's
    // EnumerateRawSources / PageStore's directory sorts.
    private static string? FindExistingSourceIdBySha(Vault v, string sha256)
    {
        if (!Directory.Exists(v.RawDir))
            return null;

        var files = new List<string>(Directory.EnumerateFiles(v.RawDir, "*.md", SearchOption.TopDirectoryOnly));
        files.Sort(StringComparer.Ordinal);

        foreach (var f in files)
        {
            var (scalars, lists, _) = Frontmatter.ReadBlock(File.ReadAllText(f));
            var existingFront = SourceFrontmatter.FromRaw(scalars, lists);
            if (string.Equals(existingFront.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                return existingFront.Id;
        }
        return null;
    }

    // Hashes the file's text content (read via File.ReadAllText, same as
    // every other file this codebase touches - it's all string-based, no
    // byte-level handling elsewhere). Consistency matters more than matching
    // some external hash here: the CLI is the only writer/reader of this
    // hash, so as long as every registration hashes the same way, dedup and
    // integrity both hold.
    private static string ComputeSha256Hex(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void GuardScalar(string value, string field)
    {
        foreach (var c in value)
        {
            if (c == '"' || c == '\n' || c == '\r')
                throw new ValidationException("frontmatter-schema", $"'{field}' may not contain quotes or newlines");
        }
    }

    private static byte[] DefaultRandomBytes()
    {
        var bytes = new byte[10];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
