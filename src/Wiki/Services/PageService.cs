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
    private static Dictionary<string, SortedSet<string>> BuildInboundMap(Vault v)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (slug, _, body) in PageStore.EnumerateWithBody(v))
        {
            foreach (var link in Wikilinks.Extract(body))
            {
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
            // Same "not a page" cases as Upsert --id's unknown-id guard: no
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

    // Full-body update: id/created/title/type/status are preserved from the
    // page already on disk; summary/sources/tags/body come from the request.
    // Slug and file path never change here - renames are Task 20's job, not
    // this one's. `cfg` is unused today (mirrors Create; the review-gate
    // wiring in Task 23 will need it for both branches).
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
            throw new ValidationException("unknown-id", $"unknown page id '{req.Id}'");

        var existingFront = PageDoc.Parse(System.IO.File.ReadAllText(fullPath)).Front;

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

        GuardScalar(req.Summary, "summary");

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
            Status = existingFront.Status,
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

        var filedDangling = req.AllowDangling ? danglingTargets : Array.Empty<string>();
        return new UpsertResult(existingFront.Id, slug, relPath, PageStatusX.ToWire(front.Status), filedDangling);
    }

    private UpsertResult Create(Vault v, VaultConfig cfg, UpsertRequest req)
    {
        // --- Blocking validation: ALL of it runs before anything below touches disk. ---

        if (string.IsNullOrWhiteSpace(req.Summary))
            throw new ValidationException("summary-required", "--summary is required when creating a page");

        // Frontmatter schema gate: reject scalar values that would corrupt the
        // closed-schema quoting round-trip (a stray '"' or newline in title/summary).
        GuardScalar(req.Title, "title");
        GuardScalar(req.Summary, "summary");

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
            Status = PageStatus.Active, // review gate lands in Task 23; every create is `active` for now
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

        var filedDangling = req.AllowDangling ? danglingTargets : Array.Empty<string>();
        return new UpsertResult(id, slug, relPath, PageStatusX.ToWire(front.Status), filedDangling);
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
