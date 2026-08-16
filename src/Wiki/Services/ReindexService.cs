using System;
using System.Collections.Generic;
using System.IO;
using Wiki.Cli;
using Wiki.Core;
using Wiki.Docs;
using Wiki.State;

namespace Wiki.Services;

// `wiki reindex` result.
public sealed record ReindexReport(int Pages, int Sources, int IdmapEntries) : IHumanRenderable
{
    public string HumanSummary() => $"Reindexed: {Pages} page(s), {Sources} source(s), {IdmapEntries} idmap entrie(s)";
}

// Rebuilds the derived caches (idmap.json, index.md, ledger.json's
// *structural* state - amendment A) from the markdown alone - `raw/*.md`
// source frontmatter and `wiki/**` page frontmatter are the only inputs;
// nothing here reads the existing idmap.json/index.md, and ledger.json is
// only read to merge-preserve HISTORY (Touched/IntegratedAt/RegisteredAt),
// never to seed the recomputed state itself. This makes reindex a recovery
// tool: delete the caches, run reindex, get them back (idmap byte-identical;
// ledger structurally-identical, history best-effort).
//
// Byte-identity: the fresh IdMap built here is Saved through the same
// IdMap.Save (Task 9) every other write path uses, which always serializes a
// freshly-populated, ordinal-sorted Dictionary<string,string> right before
// writing - so insertion order here (raw sources first, then pages) doesn't
// matter; the on-disk bytes are pinned by key sort order alone. Ledger.Save
// (Task 16) has the identical sort-on-save discipline, but ledger state is
// NOT byte-identity-checked (amendment A) - only the *structural* State field
// per source is asserted, since Touched/IntegratedAt/RegisteredAt are history
// reindex can't reconstruct from scratch.
public sealed class ReindexService
{
    public ReindexReport Rebuild(Vault v)
    {
        var idmap = new IdMap();

        var sourceIds = new List<string>();
        var sourceCount = 0;
        foreach (var (id, relPath) in EnumerateRawSources(v))
        {
            idmap.Put(id, relPath);
            sourceIds.Add(id);
            sourceCount++;
        }

        var pages = PageStore.Enumerate(v);
        foreach (var (slug, front) in pages)
        {
            idmap.Put(front.Id, PagePaths.Relative(v, slug, front));
        }

        idmap.Save(v);
        IndexFile.Regenerate(v, pages);

        RebuildLedger(v, sourceIds, pages);

        return new ReindexReport(pages.Count, sourceCount, idmap.All.Count);
    }

    // Recomputes STRUCTURAL ledger state for every raw source (amendment A):
    //   - `registered` - baseline; a source file exists in raw/ for the id
    //     (always true here, since sourceIds comes from EnumerateRawSources).
    //   - `summarized` - a `summary`-type page whose `sources` cites this id
    //     exists.
    //   - `integrated` - any `entity` OR `concept` page whose `sources` cites
    //     this id exists. This is the highest state markdown alone can prove;
    //     `linted` is deliberately never fabricated here (spec §10: it needs
    //     `.wiki/lint.json`'s lastRun compared against the ledger's own
    //     IntegratedAt, neither of which is a fact a page-frontmatter scan
    //     carries) - Ledger.Reconcile's merge (below) is what lets a source
    //     that really is `linted` stay `linted` across a reindex instead of
    //     being dragged back down to `integrated`.
    // One structural pass per source over the already-loaded `pages` list
    // (no extra directory scan) - Ledger.Reconcile does the merge-with-
    // existing/create-fresh split; see its doc comment for the exact rule.
    private static void RebuildLedger(Vault v, List<string> sourceIds, IReadOnlyList<(string Slug, PageFrontmatter Front)> pages)
    {
        var ledger = new Ledger();
        ledger.Load(v);

        foreach (var sourceId in sourceIds)
        {
            var structural = LedgerState.Registered;
            foreach (var (_, front) in pages)
            {
                if (Array.IndexOf(front.Sources, sourceId) < 0)
                    continue;

                if (front.Type == PageType.Summary && structural < LedgerState.Summarized)
                    structural = LedgerState.Summarized;
                else if (front.Type is PageType.Entity or PageType.Concept)
                    structural = LedgerState.Integrated; // highest structurally-derivable state; no need to keep scanning higher
            }

            ledger.Reconcile(sourceId, structural);
        }

        ledger.Save(v);
    }

    // raw/*.md carry source frontmatter (id, type: source, category, ...).
    // Walk order and the raw/assets/ exclusion live in SourceStore.
    private static IEnumerable<(string Id, string RelPath)> EnumerateRawSources(Vault v)
    {
        foreach (var (front, fullPath) in SourceStore.Enumerate(v))
            yield return (front.Id, Path.GetRelativePath(v.Root, fullPath).Replace('\\', '/'));
    }

}
