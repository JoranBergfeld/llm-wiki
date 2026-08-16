using System.Collections.Generic;
using System.Text.RegularExpressions;
using Wiki.Cli;
using Wiki.Core;
using Wiki.State;

namespace Wiki.Services;

// `wiki category add` result.
public sealed record CategoryAddResult(string Id, string Description) : IHumanRenderable
{
    public string HumanSummary() => $"Added category '{Id}'";
}

// `wiki category list` row shape.
public sealed record CategoryData(string Id, string Description);

// `wiki category propose/proposals/approve/reject` row shape - the wire view
// of a CategoryProposal, mirroring how ProposalData surfaces a Proposal.
public sealed record CategoryProposalView(
    string Id,
    string CategoryId,
    string Description,
    string Rationale,
    string[] Sources,
    string Status,
    string CreatedAt,
    string? Note) : IHumanRenderable
{
    public string HumanSummary() => $"Category proposal {Id} for '{CategoryId}' is {Status}";
}

// Backs `wiki category add/list` (spec §5). This is the ONLY place category
// ids ever get written to wiki.yaml - there is no code path from source-add
// or ingest into this service (spec §5's "the CLI never adds categories on
// its own" guarantee lives structurally: nothing but CategoryService.Add
// ever calls AtomicFile.Write(vault.ConfigPath, ...)).
public sealed class CategoryService
{
    private static readonly Regex KebabId = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    private readonly System.Func<long> _nowUnixMs;
    private readonly System.Func<byte[]> _randomBytes;

    // Clock/RNG seam, only needed by the proposal verbs below (Add/List are
    // pure functions of the config file). Defaults to the real clock and
    // RandomNumberGenerator so production code just does `new
    // CategoryService()`; tests inject fixed functions for deterministic
    // proposal ids and timestamps. Same shape as SchemaService.
    public CategoryService(System.Func<long>? nowUnixMs = null, System.Func<byte[]>? randomBytes = null)
    {
        _nowUnixMs = nowUnixMs ?? (() => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _randomBytes = randomBytes ?? DefaultRandomBytes;
    }

    // Adds a category by a TARGETED textual insertion into wiki.yaml, not a
    // full re-serialize of the parsed VaultConfig. VaultConfig.Load discards
    // comments and exact formatting as it parses (see its own doc comment);
    // round-tripping through it and writing back out would silently strip
    // every human `# comment` in the file. Instead this reads the raw file
    // text, locates the `categories:` block, and inserts a new `- id: ... /
    // description: ...` pair immediately after the last existing category
    // item (before whatever comes next - `lint:`, EOF, or a blank line) -
    // every other line, comment, and key in the file is left byte-identical.
    public CategoryAddResult Add(Vault v, VaultConfig cfg, string id, string description)
    {
        // --- Blocking validation: ALL of it runs before anything below touches disk. ---

        if (!KebabId.IsMatch(id))
            throw new ValidationException("invalid-category-id", $"category id '{id}' must be lowercase kebab-case");

        if (cfg.HasCategory(id))
            throw new ValidationException("duplicate-category", $"category '{id}' already exists in wiki.yaml");

        Scalar.GuardSingleLineQuotable(description, "description", "invalid-description");

        var text = System.IO.File.ReadAllText(v.ConfigPath);
        var updated = InsertCategory(v.ConfigPath, text, id, description);

        // --- Validation complete. Everything from here on is the write. ---

        AtomicFile.Write(v.ConfigPath, updated);

        // Confirm the edited file still round-trips through the real parser
        // before returning success - a malformed insertion must never be
        // left on disk silently.
        VaultConfig.Load(v.ConfigPath);

        return new CategoryAddResult(id, description);
    }

    // Spec §5 / amendment N: "removing a category that sources still
    // reference is a blocking config error". Enforced here rather than inside
    // VaultConfig.Load, which parses a file path and has no vault to scan.
    //
    // Called from CommandContext.LoadConfig, so it gates every command that
    // reads config - which is the mutation surface (`page upsert`,
    // `source add`, `lint`, `ingest advance`) plus `category`. The `category`
    // command deliberately bypasses it: `wiki category add <dropped-id>` is
    // the repair, and a check that blocks its own fix is a trap.
    //
    // Retracted sources count. Their raw/ file is still on disk carrying the
    // category (retraction flips `status`, it does not delete the record), so
    // the reference is real and the category is still load-bearing.
    //
    // Cost is one frontmatter scan of raw/*.md per config-reading invocation -
    // the same scan `source list` already does on every call.
    public static void EnsureCategoriesCoverSources(Vault v, VaultConfig cfg)
    {
        // Which sources reference each missing category - collected rather
        // than short-circuited so the error can name them. A human who
        // deleted a category needs to know what they broke, not just that
        // they broke something.
        var missing = new SortedDictionary<string, List<string>>(System.StringComparer.Ordinal);

        // Tolerant read: this runs on every config-reading command, so an
        // unparseable stray file under raw/ must not brick the CLI with a
        // frontmatter error unrelated to what the caller asked for. Strict
        // readers (dedup, reindex) still surface those loudly.
        foreach (var (front, _) in SourceStore.Enumerate(v, skipUnparseable: true))
        {
            if (cfg.HasCategory(front.Category))
                continue;

            if (!missing.TryGetValue(front.Category, out var ids))
            {
                ids = new List<string>();
                missing[front.Category] = ids;
            }
            ids.Add(front.Id);
        }

        if (missing.Count == 0)
            return;

        var parts = new List<string>();
        foreach (var (category, ids) in missing)
            parts.Add($"'{category}' (referenced by {ids.Count} source(s): {string.Join(", ", ids)})");

        var first = System.Linq.Enumerable.First(missing.Keys);
        throw new ValidationException("category-in-use",
            $"wiki.yaml is missing {parts.Count} category/categories that registered sources still reference: " +
            $"{string.Join("; ", parts)}. Restore it with " +
            $"'wiki category add {first} --description \"...\"', or retract and purge the sources that use it.",
            v.ConfigPath);
    }

    // --- The proposal channel (issue #9) -------------------------------------
    //
    // Categories stay the human's: if the agent could mint them directly the
    // taxonomy would drift into article / articles / blog-post / blogpost
    // within a week, which is precisely the decay this project exists to
    // prevent. Nothing below changes that - Approve is the human's action and
    // it is the only route from a proposal to a wiki.yaml write.
    //
    // What this closes is an ASYMMETRY. The vault already has two
    // propose-then-approve channels (pages -> the review gate; AGENTS.md ->
    // `wiki schema propose`). Categories had neither: the agent hit
    // `unknown-category` and the only instruction was "report it", leaving the
    // human to work out unaided what the new category should be and why.
    //
    // Has `category` earned a proposal workflow? The issue asks that first,
    // and the answer moved while these issues were being worked. Before
    // `wiki source scan` (amendment T), `category` was consumed by source
    // frontmatter, `source list --category`, and amendment N's in-use check -
    // thin. Scan makes a category the routing key for an entire inbox
    // directory: one inbox, one category, and that mapping is the human's
    // editorial decision made once instead of per file. That is a real job,
    // so the gate stays and deserves a proper propose channel rather than
    // being replaced by open-set tags.
    //
    // Clock/RNG seam mirrors SchemaService exactly.
    public CategoryProposalView Propose(
        Vault v, VaultConfig cfg, string id, string description, string rationale, string[] sources)
    {
        // --- Blocking validation: ALL of it runs before anything below touches disk. ---

        // Same two gates `Add` applies, applied at PROPOSE time: a human must
        // never be handed a proposal that cannot possibly be approved.
        // Approve re-checks, because wiki.yaml may have changed in between -
        // the same "validate at both ends" shape SchemaService uses.
        if (!KebabId.IsMatch(id))
            throw new ValidationException("invalid-category-id", $"category id '{id}' must be lowercase kebab-case");

        if (cfg.HasCategory(id))
            throw new ValidationException("duplicate-category", $"category '{id}' already exists in wiki.yaml");

        Scalar.GuardSingleLineQuotable(description, "description", "invalid-description");

        // The cited sources are the evidence the review turns on, so a typo'd
        // id must not silently become an empty citation. Same `unknown-source`
        // code and same idmap check `page upsert --sources` uses.
        var idmap = new IdMap();
        idmap.Load(v);
        foreach (var sourceId in sources)
        {
            var path = idmap.PathFor(sourceId);
            if (path is null || !path.StartsWith("raw/", System.StringComparison.Ordinal))
                throw new ValidationException("unknown-source", $"unknown source id '{sourceId}'");
        }

        // --- Validation complete. Everything from here on is the write. ---

        var nowMs = _nowUnixMs();
        var utcIso = ToIso(nowMs);
        var proposalId = WikiUlid.New(nowMs, _randomBytes());

        var store = new CategoryProposals();
        store.Load(v);
        var created = store.Add(proposalId, id, description, rationale ?? "", sources, utcIso);
        store.Save(v);

        Docs.LogFile.Append(v, utcIso, "category-propose", proposalId,
            $"category={id} sources={sources.Length}");

        return ToView(created);
    }

    public IReadOnlyList<CategoryProposalView> ListProposals(Vault v, string? status)
    {
        var store = new CategoryProposals();
        store.Load(v);
        var result = new List<CategoryProposalView>();
        foreach (var p in store.List(status))
            result.Add(ToView(p));
        return result.ToArray();
    }

    // Approving performs exactly the `category add` the human would otherwise
    // have typed - not a second, parallel way of writing wiki.yaml. Add()
    // re-runs every gate against the CURRENT config, so a category that has
    // since been added by hand fails here with `duplicate-category` and the
    // proposal is left open for the human to reject.
    public CategoryProposalView Approve(Vault v, VaultConfig cfg, string proposalId)
    {
        var store = new CategoryProposals();
        store.Load(v);
        var proposal = store.Get(proposalId)
            ?? throw new ValidationException("not-found", $"no category proposal found for id '{proposalId}'");

        if (proposal.Status != "open")
            throw new StateConflictException("state-conflict",
                $"category proposal '{proposalId}' is already '{proposal.Status}'; nothing to do");

        Add(v, cfg, proposal.CategoryId, proposal.Description);

        var updated = store.SetStatus(proposalId, "approved", null);
        store.Save(v);

        Docs.LogFile.Append(v, ToIso(_nowUnixMs()), "category-approve", proposalId,
            $"category={proposal.CategoryId}");

        return ToView(updated);
    }

    public CategoryProposalView Reject(Vault v, string proposalId, string? note)
    {
        var store = new CategoryProposals();
        store.Load(v);
        var proposal = store.Get(proposalId)
            ?? throw new ValidationException("not-found", $"no category proposal found for id '{proposalId}'");

        if (proposal.Status != "open")
            throw new StateConflictException("state-conflict",
                $"category proposal '{proposalId}' is already '{proposal.Status}'; nothing to do");

        var updated = store.SetStatus(proposalId, "rejected", note);
        store.Save(v);

        Docs.LogFile.Append(v, ToIso(_nowUnixMs()), "category-reject", proposalId, note ?? "(no note)");

        return ToView(updated);
    }

    private static CategoryProposalView ToView(CategoryProposal p)
        => new(p.Id, p.CategoryId, p.Description, p.Rationale, p.Sources, p.Status, p.CreatedAt, p.Note);

    private static string ToIso(long unixMs)
        => System.DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] DefaultRandomBytes()
    {
        var bytes = new byte[10];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    public IReadOnlyList<CategoryData> List(VaultConfig cfg)
    {
        var result = new List<CategoryData>();
        foreach (var c in cfg.Categories)
            result.Add(new CategoryData(c.Id, c.Description));
        return result.ToArray();
    }

    // Splits on the raw file text (preserving \r\n vs \n and every comment),
    // finds the top-level `categories:` line, walks forward over `- id: ... /
    // description: ...` pairs (same 2-line-per-item shape VaultConfig.Load
    // itself expects), and inserts the new pair right after the last one -
    // i.e. right before whatever line ends the block (a blank line, `lint:`,
    // or EOF). Mirrors the 2-space/4-space indent style the wiki-yaml
    // template and VaultConfig.Load both use.
    private static string InsertCategory(string configPath, string text, string id, string description)
    {
        var hasCrLf = text.Contains("\r\n");
        var normalized = text.Replace("\r\n", "\n");
        var lines = new List<string>(normalized.Split('\n'));

        var categoriesLine = FindTopLevelKeyLine(lines, "categories:");
        if (categoriesLine < 0)
            throw new ValidationException("config", "wiki.yaml has no top-level 'categories:' key to insert into", configPath);

        var insertAt = categoriesLine + 1;
        var i = categoriesLine + 1;
        while (i < lines.Count)
        {
            if (!IsListItemStart(lines[i]) || !VaultConfig.StripInlineComment(lines[i]).Trim().StartsWith("- id:", System.StringComparison.Ordinal))
                break;
            if (i + 1 >= lines.Count || !IsIndentedContinuation(lines[i + 1]) ||
                !VaultConfig.StripInlineComment(lines[i + 1]).Trim().StartsWith("description:", System.StringComparison.Ordinal))
                break;
            i += 2;
            insertAt = i;
        }

        var newItem = new[]
        {
            $"  - id: {id}",
            $"    description: \"{description}\"",
        };
        lines.InsertRange(insertAt, newItem);

        var joined = string.Join("\n", lines);
        return hasCrLf ? joined.Replace("\n", "\r\n") : joined;
    }

    // An un-indented "key:" line (optionally followed by a trailing comment
    // or more content) - the same "top-level scalar" shape VaultConfig.Load
    // matches against for `version`/`name`/etc.
    private static int FindTopLevelKeyLine(List<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                continue;
            if (VaultConfig.StripInlineComment(line).Trim() == key)
                return i;
        }
        return -1;
    }

    private static bool IsListItemStart(string line)
    {
        var trimmed = line.TrimStart();
        return line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && trimmed.StartsWith("- ", System.StringComparison.Ordinal);
    }

    // A continuation line for a list item's second field: indented, but not
    // itself the start of a new `- ` item.
    private static bool IsIndentedContinuation(string line)
    {
        if (line.Length == 0) return false;
        if (line[0] != ' ' && line[0] != '\t') return false;
        var trimmed = line.Trim();
        return trimmed.Length > 0 && !trimmed.StartsWith("- ", System.StringComparison.Ordinal);
    }


}
