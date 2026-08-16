using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wiki.Core;
using Wiki.Docs;
using Wiki.State;

namespace Wiki.Services;

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

// Read-only page queries: the `wiki page list/show/backlinks` and
// `wiki index show` surface, plus the inbound-link map they and LintService's
// orphan check share.
//
// Split out of PageService, which had grown to ~760 lines covering both
// mutation and query. The two halves have genuinely different shapes: every
// mutation here needs the clock/RNG seam, the validate-then-write discipline
// and the idmap/index/log write triple, while none of the queries touch any
// of it. Static, because with no clock and no writes there is nothing to
// inject - the seam PageService's constructor exists for is meaningless here.
public static class PageQuery
{
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
    public static IReadOnlyList<PageSummary> List(Vault v, PageType? type, PageStatus? status, bool orphans = false)
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
    public static IReadOnlyList<string> Backlinks(Vault v, string idOrName)
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
    public static IReadOnlyList<PageSummary> IndexShow(Vault v, PageType? type)
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
    public static PageView Show(Vault v, string idOrName, bool frontmatterOnly)
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
}
