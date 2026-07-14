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
