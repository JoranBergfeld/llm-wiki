using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Wiki.Cli;
using Wiki.Core;
using Wiki.Docs;
using Wiki.State;

namespace Wiki.Services;

public sealed record UpsertRequest(
    PageType Type,
    string Title,
    string? Id,
    string Summary,
    string[] Sources,
    string[] Tags,
    string Body,
    bool AllowDangling);

public sealed record UpsertResult(
    string Id,
    string Slug,
    string Path,
    string Status,
    string[] DanglingFiled) : IHumanRenderable
{
    public string HumanSummary() => $"Upserted [[{Slug}]] ({Status}) -> {Path}";
}

// `wiki page rename` result. LinksRewritten counts EVERY page whose body
// contained at least one `[[oldSlug]]`/`[[oldSlug|display]]` wikilink and got
// rewritten to `[[newSlug...]]`. The rewrite loop runs over all pages AFTER
// the move, so this includes the renamed page's own body if it self-linked
// to its old slug - not just the inbound links from OTHER pages. (A page that
// links to the same target twice still counts once: the counter is
// per-page-rewritten, not per-`[[...]]`-occurrence.)
public sealed record RenameResult(
    string Id,
    string OldSlug,
    string NewSlug,
    int LinksRewritten) : IHumanRenderable
{
    public string HumanSummary() => $"Renamed [[{OldSlug}]] -> [[{NewSlug}]] ({LinksRewritten} inbound link(s) rewritten)";
}

// `wiki page list` row shape. Deliberately flat/scalar (wire strings for
// Type/Status, not the enums) - it's a query result meant to be read straight
// off the wire, same spirit as UpsertResult. SourcesCount stands in for the
// full Sources array: list is a scanning/routing view, not a detail view -
// that's what `show` is for.
public sealed record PageSummary(
    string Id,
    string Slug,
    string Type,
    string Title,
    string Status,
    string Summary,
    int SourcesCount);

// `wiki page show` result: full frontmatter plus (optionally) the body.
// Body is null - and so omitted from JSON via WikiJsonContext's
// WhenWritingNull default - when the caller passed --frontmatter-only.
public sealed record PageView(
    string Id,
    string Slug,
    string Type,
    string Title,
    string Status,
    string Created,
    string Updated,
    string Summary,
    string[] Sources,
    string[] Tags,
    string? Body);

// `wiki page upsert`. This task (12) implements only the create path (no
// --id); Task 13 slots the update branch into Upsert() alongside it.
//
// Clock/RNG seam: mirrors WikiUlid.New's own contract (prod supplies
// unixMs+random) one level up. The constructor defaults to the real clock and
// RandomNumberGenerator so production code (PageCommand) just does
// `new PageService()`; tests inject fixed functions for deterministic
// ULID/created/updated values. Both the ULID timestamp and the created/updated
// date are derived from the SAME captured `nowMs` per call, so they can never
// disagree about "now" within one upsert.
public sealed class PageService
{
    private readonly Func<long> _nowUnixMs;
    private readonly Func<byte[]> _randomBytes;

    public PageService(Func<long>? nowUnixMs = null, Func<byte[]>? randomBytes = null)
    {
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _randomBytes = randomBytes ?? DefaultRandomBytes;
    }

    public UpsertResult Upsert(Vault v, VaultConfig cfg, UpsertRequest req)
        => req.Id is null ? Create(v, cfg, req) : Update(v, cfg, req);

    // `wiki page list`: scan every page in the vault (PageStore.Enumerate,
    // already deterministically sorted) and keep the ones matching both
    // filters, if given. Purely additive/read-only - no idmap, no disk write.
    //
    // `orphans` (Task 19) adds a third filter on top: status must be `active`
    // and the page must have zero inbound wikilinks (spec §11's orphan
    // definition). `pending-review` pages are excluded on purpose - they
    // aren't "orphans" yet, they just haven't been reviewed - and so is the
    // `overview` singleton, which is the vault's own entry point and is
    // expected to have no inbound links; flagging it every run would just be
    // noise. Building the inbound-link map is skipped entirely when
    // `orphans` is false, so plain `list` calls pay no extra cost.
    public IReadOnlyList<PageSummary> List(Vault v, PageType? type, PageStatus? status, bool orphans = false)
    {
        var inbound = orphans ? BuildInboundMap(v) : null;

        var result = new List<PageSummary>();
        foreach (var (slug, front) in PageStore.Enumerate(v))
        {
            if (type is not null && front.Type != type.Value) continue;
            if (status is not null && front.Status != status.Value) continue;

            if (orphans)
            {
                if (front.Status != PageStatus.Active || front.Type == PageType.Overview)
                    continue;
                if (inbound!.TryGetValue(slug, out var sources) && sources.Count > 0)
                    continue;
            }

            result.Add(new PageSummary(
                front.Id,
                slug,
                PageTypeX.ToWire(front.Type),
                front.Title,
                PageStatusX.ToWire(front.Status),
                front.Summary,
                front.Sources.Length));
        }
        // Returned as an array (not List<T>) so the runtime type boxed into
        // Envelope.Data matches WikiJsonContext's [JsonSerializable(typeof(PageSummary[]))]
        // registration - source-gen resolves Data's `object` property by the
        // boxed value's exact runtime type.
        return result.ToArray();
    }

    // `wiki page backlinks <id|name>`: the agent's graph-navigation
    // primitive - which pages' bodies link to this one. Resolves idOrName
    // the same way Show does (id -> idmap, else slug -> PageStore scan),
    // then looks the resolved slug up in the same inbound-link map `list
    // --orphans` builds. Read-only, no idmap/index/log write.
    public IReadOnlyList<string> Backlinks(Vault v, string idOrName)
    {
        var slug = ResolveSlug(v, idOrName);
        var inbound = BuildInboundMap(v);
        return inbound.TryGetValue(slug, out var sources) ? sources.ToArray() : Array.Empty<string>();
    }

    // `wiki index show [--type]`: the same routing data index.md carries -
    // grouped by type in the fixed Overview/Concept/Entity/Summary order,
    // archived pages excluded, sorted by title then slug within each group -
    // reusing IndexFile.GroupedEntries (Task 11's render model) so the JSON
    // view can never disagree with the file an agent would otherwise have
    // had to read. Emitted as PageSummary since the two shapes are
    // identical (id/slug/type/title/status/summary/sourcesCount) - no need
    // for a separate DTO or JSON registration.
    public IReadOnlyList<PageSummary> IndexShow(Vault v, PageType? type)
    {
        var pages = PageStore.Enumerate(v);
        var result = new List<PageSummary>();

        foreach (var (groupType, groupPages) in IndexFile.GroupedEntries(pages))
        {
            if (type is not null && groupType != type.Value) continue;

            foreach (var (slug, front) in groupPages)
            {
                result.Add(new PageSummary(
                    front.Id,
                    slug,
                    PageTypeX.ToWire(front.Type),
                    front.Title,
                    PageStatusX.ToWire(front.Status),
                    front.Summary,
                    front.Sources.Length));
            }
        }

        return result.ToArray();
    }

    // Resolves an id-or-name argument to the page's slug, without reading
    // its body (Backlinks only needs the slug to look up in the inbound-link
    // map). Mirrors Show's two-branch resolution (WikiUlid-shaped -> idmap;
    // else -> PageStore slug scan) so the two commands agree on what
    // "not-found" means, but stays a separate helper rather than a shared
    // refactor of Show - Show also needs the full file path to parse the
    // body/frontmatter, Backlinks doesn't.
    private static string ResolveSlug(Vault v, string idOrName)
    {
        if (WikiUlid.IsValid(idOrName))
        {
            var idmap = new IdMap();
            idmap.Load(v);
            var relPath = idmap.PathFor(idOrName);
            if (relPath is null || !relPath.StartsWith("wiki/", StringComparison.Ordinal))
                throw new ValidationException("not-found", $"no page found for id '{idOrName}'");
            var fullPath = System.IO.Path.Combine(v.Root, relPath);
            if (!System.IO.File.Exists(fullPath))
                throw new ValidationException("not-found", $"no page found for id '{idOrName}'");
            return System.IO.Path.GetFileNameWithoutExtension(fullPath);
        }

        var match = PageStore.Enumerate(v).FirstOrDefault(p => p.Slug == idOrName);
        if (match.Front is null)
            throw new ValidationException("not-found", $"no page found for slug '{idOrName}'");
        return match.Slug;
    }

    // Inbound-link map: every page's body run through Wikilinks.Extract
    // (which already skips code fences), inverted from "page -> targets it
    // links to" into "target slug -> slugs that link to it". Built fresh on
    // every call - the vault is small enough (M1/M2 scope) that a full body
    // scan per query is fine, and it keeps Backlinks/`list --orphans`
    // trivially correct with no cache-invalidation story to get wrong.
    // SortedSet dedups a page linking to the same target twice and gives a
    // deterministic (ordinal) order for free.
    //
    // `internal` (not `private`) so LintService's `orphan` check (Task 22)
    // reuses this exact inbound-link map instead of re-implementing it - both
    // types live in this assembly (Wiki.Services), so no InternalsVisibleTo
    // is needed.
    internal static Dictionary<string, SortedSet<string>> BuildInboundMap(Vault v)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (slug, _, body) in PageStore.EnumerateWithBody(v))
        {
            foreach (var link in Wikilinks.Extract(body))
            {
                // A page is never its own backlink: a `[[self]]` reference is
                // not an inbound link from anywhere else, so it mustn't keep a
                // page off the orphan list (a page linking ONLY to itself, with
                // nothing external pointing at it, IS orphaned) nor show up in
                // its own `backlinks` output.
                if (string.Equals(link.Target, slug, StringComparison.Ordinal))
                    continue;

                if (!map.TryGetValue(link.Target, out var sources))
                {
                    sources = new SortedSet<string>(StringComparer.Ordinal);
                    map[link.Target] = sources;
                }
                sources.Add(slug);
            }
        }
        return map;
    }

    // `wiki page show <id|name>`: resolve `idOrName` as an id (WikiUlid
    // shape -> idmap lookup) or, failing that, as a slug (PageStore scan for
    // an exact slug match). Either branch re-parses the resolved file fresh
    // off disk - read-only, no idmap write, no index/log touch.
    public PageView Show(Vault v, string idOrName, bool frontmatterOnly)
    {
        string fullPath;

        if (WikiUlid.IsValid(idOrName))
        {
            var idmap = new IdMap();
            idmap.Load(v);
            var relPath = idmap.PathFor(idOrName);
            // Same "not a page" cases as Upsert --id's not-found guard: no
            // idmap entry, a raw/ source id (valid entry, not a page), or a
            // stale entry whose file is gone - all collapse to not-found here.
            if (relPath is null || !relPath.StartsWith("wiki/", StringComparison.Ordinal))
                throw new ValidationException("not-found", $"no page found for id '{idOrName}'");
            fullPath = System.IO.Path.Combine(v.Root, relPath);
            if (!System.IO.File.Exists(fullPath))
                throw new ValidationException("not-found", $"no page found for id '{idOrName}'");
        }
        else
        {
            var match = PageStore.Enumerate(v).FirstOrDefault(p => p.Slug == idOrName);
            if (match.Front is null)
                throw new ValidationException("not-found", $"no page found for slug '{idOrName}'");

            fullPath = match.Front.Type == PageType.Overview
                ? System.IO.Path.Combine(v.WikiDir, "overview.md")
                : System.IO.Path.Combine(v.PageDir(match.Front.Type), match.Slug + ".md");
        }

        var doc = PageDoc.Parse(System.IO.File.ReadAllText(fullPath));
        var slug = System.IO.Path.GetFileNameWithoutExtension(fullPath);
        var front = doc.Front;

        return new PageView(
            front.Id,
            slug,
            PageTypeX.ToWire(front.Type),
            front.Title,
            PageStatusX.ToWire(front.Status),
            front.Created,
            front.Updated,
            front.Summary,
            front.Sources,
            front.Tags,
            frontmatterOnly ? null : doc.Body);
    }

    // `wiki page rename <id> <new-slug>`: renames a page's on-disk slug (the
    // filename) and rewrites every inbound `[[wikilink]]` across the vault so
    // links keep resolving after the move - that "aim heals" step is the
    // whole point of rename over a manual `mv`. Only the filename changes;
    // id/created/body content are untouched (the slug lives in the filename,
    // never in frontmatter, so there's no frontmatter field to edit here).
    //
    // The move is two file ops - AtomicFile.Write(new) then File.Delete(old)
    // - not atomic as a pair, but each op alone is crash-safe. A crash caught
    // between the two leaves both files on disk momentarily; `wiki reindex`
    // heals that by rebuilding idmap/index fresh from whatever page files
    // exist (spec §3 forward-repair), so this is acceptable per spec.
    public RenameResult Rename(Vault v, string id, string newSlug)
    {
        // --- Blocking validation: ALL of it runs before anything below touches disk. ---

        var idmap = new IdMap();
        idmap.Load(v);

        var relPath = idmap.PathFor(id);
        var fullPath = relPath is null ? null : System.IO.Path.Combine(v.Root, relPath);
        if (relPath is null || !relPath.StartsWith("wiki/", StringComparison.Ordinal) || !System.IO.File.Exists(fullPath))
            throw new ValidationException("not-found", $"unknown page id '{id}'");

        var doc = PageDoc.Parse(System.IO.File.ReadAllText(fullPath));
        var front = doc.Front;

        // The overview singleton is the vault's own fixed-path entry point
        // (`wiki/overview.md`) - it has no slug of its own to rename, and
        // giving it one would break every caller that expects to find it at
        // that well-known path. Reject outright rather than silently no-op.
        if (front.Type == PageType.Overview)
            throw new ValidationException("cannot-rename-overview", "the overview singleton cannot be renamed");

        var oldSlug = System.IO.Path.GetFileNameWithoutExtension(fullPath);

        // new-slug must already be a normalized kebab slug: Rename does not
        // silently reinterpret free text the way Create derives a slug from
        // --title (Slug.From collapsing punctuation/case). Requiring the
        // caller to pass an already-clean value means what you asked for is
        // exactly what lands - no surprise character-collapsing on a rename.
        if (string.IsNullOrEmpty(newSlug) || Slug.From(newSlug) != newSlug)
            throw new ValidationException("invalid-slug", $"'{newSlug}' is not a normalized kebab-case slug");

        if (newSlug == oldSlug)
            throw new ValidationException("slug-unchanged", $"'{newSlug}' is already this page's slug");

        // Slugs are globally unique across page types (Task 12 decision: the
        // Obsidian `[[slug]]` namespace has no per-type scoping), so the
        // collision check spans every page regardless of type.
        var existingPages = PageStore.Enumerate(v);
        if (existingPages.Any(p => p.Slug == newSlug))
            throw new ValidationException("slug-taken", $"slug '{newSlug}' is already in use");

        // --- Validation complete. Everything from here on is the write. ---

        var newPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fullPath)!, newSlug + ".md");

        AtomicFile.Write(newPath, doc.Serialize());
        System.IO.File.Delete(fullPath);

        var newRelPath = System.IO.Path.GetRelativePath(v.Root, newPath).Replace('\\', '/');
        idmap.Put(front.Id, newRelPath);
        idmap.Save(v);

        // Rewrite every inbound [[oldSlug]] wikilink across the vault. This
        // re-scans fresh off disk (not the `existingPages` snapshot used for
        // the collision check above) so it sees the renamed page at its NEW
        // path/slug rather than the old one.
        var rewrittenCount = 0;
        foreach (var (slug, pFront, body) in PageStore.EnumerateWithBody(v))
        {
            var rewritten = Wikilinks.Rewrite(body, oldSlug, newSlug);
            if (string.Equals(rewritten, body, StringComparison.Ordinal))
                continue;

            AtomicFile.Write(PagePaths.Full(v, slug, pFront), new PageDoc(pFront, rewritten).Serialize());
            rewrittenCount++;
        }

        var finalPages = PageStore.Enumerate(v);
        IndexFile.Regenerate(v, finalPages);

        var nowMs = _nowUnixMs();
        var utcIso = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
        LogFile.Append(v, utcIso, "rename", newSlug,
            $"id={front.Id} old={oldSlug} new={newSlug} links_rewritten={rewrittenCount}");

        return new RenameResult(front.Id, oldSlug, newSlug, rewrittenCount);
    }

    // `wiki page set-status <id> <status>`: the repair/workflow primitive
    // later tasks (review gate, retraction) reuse internally to move a page
    // between PageStatus values without touching body/summary/sources/tags.
    // Sets `updated` to today (same clock seam Create/Update use) since a
    // status change is itself an edit to the page's record. Index is
    // regenerated because status drives index inclusion (archived pages are
    // excluded entirely) and the `[pending-review]` marker.
    public void SetStatus(Vault v, string id, PageStatus status)
    {
        // --- Blocking validation: ALL of it runs before anything below touches disk. ---

        var idmap = new IdMap();
        idmap.Load(v);

        var relPath = idmap.PathFor(id);
        var fullPath = relPath is null ? null : System.IO.Path.Combine(v.Root, relPath);
        if (relPath is null || !relPath.StartsWith("wiki/", StringComparison.Ordinal) || !System.IO.File.Exists(fullPath))
            throw new ValidationException("not-found", $"unknown page id '{id}'");

        var doc = PageDoc.Parse(System.IO.File.ReadAllText(fullPath));
        var existingFront = doc.Front;

        var nowMs = _nowUnixMs();
        var today = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var front = new PageFrontmatter
        {
            Id = existingFront.Id,
            Type = existingFront.Type,
            Title = existingFront.Title,
            Status = status,
            Created = existingFront.Created,
            Updated = today,
            Summary = existingFront.Summary,
            Sources = existingFront.Sources,
            Tags = existingFront.Tags,
        };

        var serialized = new PageDoc(front, doc.Body).Serialize();
        // Frontmatter schema gate proper: must round-trip through the same
        // closed-schema parser real page files are read back with.
        PageDoc.Parse(serialized);

        // --- Validation complete. Everything from here on is the write. ---

        AtomicFile.Write(fullPath, serialized);

        var freshPages = PageStore.Enumerate(v);
        IndexFile.Regenerate(v, freshPages);

        var utcIso = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var slug = System.IO.Path.GetFileNameWithoutExtension(fullPath);
        LogFile.Append(v, utcIso, "set-status", slug, $"id={existingFront.Id} status={PageStatusX.ToWire(status)}");
    }

    // Full-body update: id/created/title/type are preserved from the page
    // already on disk; summary/sources/tags/body come from the request.
    // Status is preserved too UNLESS the review gate is on, in which case
    // every update lands back at pending-review regardless of what it was
    // before (spec §15) - a revision needs re-approval same as a fresh
    // create. Slug and file path never change here - renames are Task 20's
    // job, not this one's.
    private UpsertResult Update(Vault v, VaultConfig cfg, UpsertRequest req)
    {
        // --- Blocking validation: ALL of it runs before anything below touches disk. ---

        // idmap.Load is a read, not a write - safe to do mid-validation, and
        // the same loaded instance is reused below for Put+Save (mirrors Create).
        var idmap = new IdMap();
        idmap.Load(v);

        var relPath = idmap.PathFor(req.Id!);
        var fullPath = relPath is null ? null : System.IO.Path.Combine(v.Root, relPath);

        // A raw/ source is a valid idmap entry but not a page; a stale idmap
        // entry (file since deleted outside the CLI) resolves to nothing on
        // disk. Both are "not a page you can upsert" - same code either way.
        if (relPath is null || !relPath.StartsWith("wiki/", StringComparison.Ordinal) || !System.IO.File.Exists(fullPath))
            throw new ValidationException("not-found", $"unknown page id '{req.Id}'");

        var existingDoc = PageDoc.Parse(System.IO.File.ReadAllText(fullPath));
        var existingFront = existingDoc.Front;

        // A page's type is fixed at creation; update never migrates it
        // between directories. Silently keeping the stored type while
        // accepting a different --type would look like the flag did
        // something when it didn't, so reject the mismatch instead - the
        // safer of the two options (vs. silently ignoring --type).
        if (req.Type != existingFront.Type)
            throw new ValidationException("type-mismatch",
                $"--type '{PageTypeX.ToWire(req.Type)}' does not match existing page type '{PageTypeX.ToWire(existingFront.Type)}' for id '{req.Id}'");

        // Title is immutable on update: a page's title drives its slug and
        // identity, and changing it means rewriting every inbound wikilink -
        // that's Task 20's rename command, not upsert. --title is CLI-required
        // on every upsert, so silently discarding a differing one would look
        // like the flag did something. Reject the mismatch (same footgun class
        // as type-mismatch above). Trimmed both sides to ignore incidental
        // surrounding whitespace, not to normalize case (title identity is
        // case-sensitive, matching duplicate-title's storage).
        if (req.Title.Trim() != existingFront.Title.Trim())
            throw new ValidationException("title-mismatch",
                "the update title must match the existing page title; changing a page's title/identity is done via rename (a later command), not upsert");

        if (string.IsNullOrWhiteSpace(req.Summary))
            throw new ValidationException("summary-required", "--summary is required when updating a page");

        Scalar.GuardSingleLineQuotable(req.Summary, "summary", "frontmatter-schema");

        foreach (var sourceId in req.Sources)
        {
            var sourcePath = idmap.PathFor(sourceId);
            if (sourcePath is null || !sourcePath.StartsWith("raw/", StringComparison.Ordinal))
                throw new ValidationException("unknown-source", $"unknown source id '{sourceId}'");
        }

        // Title is immutable on update (not in the replace list, and renames
        // are Task 20's job), so there is no new title to collide with an
        // existing one - duplicate-title from Create simply doesn't apply
        // here and is intentionally not re-run. That also means a
        // self-update can never trip it.
        var slug = System.IO.Path.GetFileNameWithoutExtension(fullPath);

        var existingPages = PageStore.Enumerate(v);
        var existingSlugs = new HashSet<string>(existingPages.Select(p => p.Slug), StringComparer.Ordinal);

        var links = Wikilinks.Extract(req.Body);
        var danglingTargets = links
            .Select(l => l.Target)
            .Where(target => target != slug && !existingSlugs.Contains(target))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (danglingTargets.Length > 0 && !req.AllowDangling)
            throw new ValidationException("dangling-link",
                $"dangling wikilink target(s): {string.Join(", ", danglingTargets)}");

        var nowMs = _nowUnixMs();
        var today = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var front = new PageFrontmatter
        {
            Id = existingFront.Id,
            Type = existingFront.Type,
            Title = existingFront.Title,
            Status = cfg.ReviewGate ? PageStatus.PendingReview : existingFront.Status,
            Created = existingFront.Created,
            Updated = today,
            Summary = req.Summary,
            Sources = req.Sources,
            Tags = req.Tags,
        };

        var doc = new PageDoc(front, req.Body);
        var serialized = doc.Serialize();
        // Frontmatter schema gate proper: must round-trip through the same
        // closed-schema parser real page files are read back with.
        PageDoc.Parse(serialized);

        // --- Validation complete. Everything from here on is the write. ---

        // Review gate (spec §15): BEFORE the new body overwrites the page on
        // disk, stash the CURRENT (about-to-be-replaced) document as a shadow
        // copy under .wiki/review/<id>.prev.md - the one place the CLI keeps
        // a shadow copy. `review list` diffs against it; `review reject`
        // restores it. Only written when the gate is on; an update under a
        // gate-off vault never accrues shadow state.
        //
        // SaveIfAbsent, not Save (amendment K): a second un-reviewed update
        // must NOT overwrite the shadow, or `reject` would restore the first
        // un-reviewed edit instead of the last body a human actually
        // reviewed. approve/reject clear the shadow and so re-arm the capture.
        if (cfg.ReviewGate)
        {
            ReviewShadow.SaveIfAbsent(v, existingFront.Id, existingDoc);
        }

        AtomicFile.Write(fullPath, serialized);

        // Path is unchanged on update, so this Put is a same-value no-op -
        // kept anyway so idmap.Save always runs off one consistent
        // load-mutate-save cycle, same shape as Create's.
        idmap.Put(existingFront.Id, relPath);
        idmap.Save(v);

        var freshPages = PageStore.Enumerate(v);
        IndexFile.Regenerate(v, freshPages);

        var utcIso = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
        LogFile.Append(v, utcIso, "upsert", slug, $"update id={existingFront.Id} type={PageTypeX.ToWire(existingFront.Type)}");

        var filedDangling = FileDanglingIssues(v, slug, danglingTargets, req.AllowDangling, utcIso);
        return new UpsertResult(existingFront.Id, slug, relPath, PageStatusX.ToWire(front.Status), filedDangling);
    }

    private UpsertResult Create(Vault v, VaultConfig cfg, UpsertRequest req)
    {
        // --- Blocking validation: ALL of it runs before anything below touches disk. ---

        if (string.IsNullOrWhiteSpace(req.Summary))
            throw new ValidationException("summary-required", "--summary is required when creating a page");

        // Frontmatter schema gate: reject scalar values that would corrupt the
        // closed-schema quoting round-trip (a stray '"' or newline in title/summary).
        Scalar.GuardSingleLineQuotable(req.Title, "title", "frontmatter-schema");
        Scalar.GuardSingleLineQuotable(req.Summary, "summary", "frontmatter-schema");

        var existing = PageStore.Enumerate(v);

        // Overview is a singleton (`wiki/overview.md`). Create has no --id,
        // so there's no way to disambiguate "replace the existing overview"
        // from "make a second one" - a second create with a different title
        // would silently overwrite the file on disk while idmap.json kept
        // both ids, corrupting the id->path mapping. Block it here, before
        // any write; updating the overview is the --id path (Task 13).
        if (req.Type == PageType.Overview)
        {
            var overviewPath = System.IO.Path.Combine(v.WikiDir, "overview.md");
            var overviewExists = System.IO.File.Exists(overviewPath)
                || existing.Any(p => p.Front.Type == PageType.Overview);
            if (overviewExists)
                throw new ValidationException("overview-exists",
                    "an overview page already exists; update it with --id <id> instead of creating a new one");
        }

        foreach (var (existingSlug, existingFront) in existing)
        {
            if (existingFront.Type == req.Type && string.Equals(existingFront.Title, req.Title, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("duplicate-title",
                    $"a {PageTypeX.ToWire(req.Type)} page titled '{req.Title}' already exists ('{existingSlug}')");
        }

        // idmap.Load is a read, not a write - safe to do mid-validation. The
        // same loaded instance is reused below for Put+Save once every check
        // has passed, avoiding a second Load.
        var idmap = new IdMap();
        idmap.Load(v);
        foreach (var sourceId in req.Sources)
        {
            var path = idmap.PathFor(sourceId);
            if (path is null || !path.StartsWith("raw/", StringComparison.Ordinal))
                throw new ValidationException("unknown-source", $"unknown source id '{sourceId}'");
        }

        var existingSlugs = new HashSet<string>(existing.Select(p => p.Slug), StringComparer.Ordinal);

        // Overview is a singleton at a fixed path (`wiki/overview.md`), not a
        // slugged file under a per-type directory, so it skips title-derived
        // slug generation entirely. (Tests for this task only exercise
        // entity/concept/summary; overview support here is best-effort/deferred
        // per the task brief - see the report for the full note.)
        var slug = req.Type == PageType.Overview
            ? "overview"
            : Slug.Ensure(Slug.From(req.Title), existingSlugs.Contains);

        var links = Wikilinks.Extract(req.Body);
        var danglingTargets = links
            .Select(l => l.Target)
            .Where(target => target != slug && !existingSlugs.Contains(target))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (danglingTargets.Length > 0 && !req.AllowDangling)
            throw new ValidationException("dangling-link",
                $"dangling wikilink target(s): {string.Join(", ", danglingTargets)}");

        var nowMs = _nowUnixMs();
        var id = WikiUlid.New(nowMs, _randomBytes());
        var today = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var front = new PageFrontmatter
        {
            Id = id,
            Type = req.Type,
            Title = req.Title,
            Status = cfg.ReviewGate ? PageStatus.PendingReview : PageStatus.Active, // spec §15
            Created = today,
            Updated = today,
            Summary = req.Summary,
            Sources = req.Sources,
            Tags = req.Tags,
        };

        var doc = new PageDoc(front, req.Body);
        var serialized = doc.Serialize();
        // Frontmatter schema gate proper: must round-trip through the same
        // closed-schema parser real page files are read back with.
        PageDoc.Parse(serialized);

        var targetPath = req.Type == PageType.Overview
            ? System.IO.Path.Combine(v.WikiDir, "overview.md")
            : System.IO.Path.Combine(v.PageDir(req.Type), slug + ".md");

        // --- Validation complete. Everything from here on is the write. ---

        AtomicFile.Write(targetPath, serialized);

        var relPath = System.IO.Path.GetRelativePath(v.Root, targetPath).Replace('\\', '/');
        idmap.Put(id, relPath);
        idmap.Save(v);

        var freshPages = PageStore.Enumerate(v);
        IndexFile.Regenerate(v, freshPages);

        var utcIso = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
        LogFile.Append(v, utcIso, "upsert", slug, $"create id={id} type={PageTypeX.ToWire(req.Type)}");

        var filedDangling = FileDanglingIssues(v, slug, danglingTargets, req.AllowDangling, utcIso);
        return new UpsertResult(id, slug, relPath, PageStatusX.ToWire(front.Status), filedDangling);
    }

    // Spec §11.4 + amendment L: forward references permitted by
    // `--allow-dangling` are filed as `dangling-link` issues BY THE UPSERT,
    // not left for the next `wiki lint`. Deferring meant an agent could
    // upsert with the flag, read `wiki issues list`, and see nothing - while
    // the upsert's own `danglingFiled` field claimed the targets had been
    // filed.
    //
    // Subject is the page CONTAINING the link and the detail string matches
    // LintService.CheckDanglingLinks verbatim, on purpose: Issues.Upsert
    // merges on (kind, subject), so a link that stays dangling bumps
    // occurrences on the SAME record whether it was the upsert or a later
    // lint that saw it. Diverging on either would fork one problem into two
    // issues and halve the occurrence count the reflect loop reads.
    //
    // Called AFTER the page write and log append (it is a consequence of a
    // completed write, not a precondition for it) and only when the caller
    // opted in - a rejected upsert files nothing, and neither does a clean one.
    private static string[] FileDanglingIssues(
        Vault v, string slug, string[] danglingTargets, bool allowDangling, string utcIso)
    {
        if (!allowDangling || danglingTargets.Length == 0)
            return Array.Empty<string>();

        var issues = new Issues();
        issues.Load(v);
        issues.Upsert(IssueKind.DanglingLink, slug,
            $"dangling wikilink target(s): {string.Join(", ", danglingTargets)}", utcIso);
        issues.Save(v);

        return danglingTargets;
    }


    private static byte[] DefaultRandomBytes()
    {
        var bytes = new byte[10];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
