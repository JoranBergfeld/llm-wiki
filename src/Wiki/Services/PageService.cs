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

    // Task 13 replaces this body with the full-body update path (spec §11 /
    // §9 review-shadow handling). Left as an explicit, typed stub rather than
    // falling through so Upsert's create/update split is obvious from here.
    private static UpsertResult Update(Vault v, VaultConfig cfg, UpsertRequest req)
        => throw new ValidationException("not-implemented", "page upsert --id (update path) is implemented in a later task");

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
