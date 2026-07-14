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
