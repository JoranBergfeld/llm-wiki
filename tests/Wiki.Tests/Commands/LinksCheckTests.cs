using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Wiki.Core;
using Wiki.Services;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #2: `wiki links check [--external]`.
//
// Nothing here touches the network. The CLI-level tests exercise the offline
// inventory mode; the probing tests drive LinksService directly with an
// injected probe, which is exactly the seam that exists so a network feature
// can have a deterministic test suite.
public class LinksCheckTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    private static void CreatePage(TempVault tv, string title, string body)
    {
        var r = tv.RunStdin(body, "page", "upsert", "--type", "concept", "--title", title,
            "--summary", title + " summary", "--stdin", "--allow-dangling", "--json");
        Assert.Equal(0, r.ExitCode);
    }

    private static LinksService WithProbe(Dictionary<string, LinksService.ProbeOutcome> table)
        => new(nowUnixMs: () => 1_770_000_000_000,
               probe: (url, _) => Task.FromResult(
                   table.TryGetValue(url, out var o) ? o : new LinksService.ProbeOutcome("ok", 200, null)));

    [Fact]
    public void Check_WithoutExternal_IsAnOfflineInventory()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "Alpha", "See [the docs](https://example.com/docs) and [[beta]].");
        CreatePage(tv, "Beta", "Also [the docs](https://example.com/docs) plus [another](https://other.test/a).");

        var r = tv.Run("links", "check", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.False(Data(r).GetProperty("probed").GetBoolean());
        Assert.Equal(2, Data(r).GetProperty("urls").GetInt32());

        // Distinct URLs, each listing every page that carries it.
        var docs = Data(r).GetProperty("results").EnumerateArray()
            .Single(e => e.GetProperty("url").GetString() == "https://example.com/docs");
        Assert.Equal(new[] { "alpha", "beta" },
            docs.GetProperty("pages").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("unprobed", docs.GetProperty("status").GetString());

        // Offline mode writes nothing at all. (issues.json already exists -
        // creating Alpha with a forward reference to [[beta]] filed a
        // dangling-link issue - so the assertion is that it is UNCHANGED.)
        var issuesPath = Path.Combine(tv.Path, ".wiki", "issues.json");
        var before = File.ReadAllText(issuesPath);
        var logBefore = File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md"));
        tv.Run("links", "check", "--json");
        Assert.Equal(before, File.ReadAllText(issuesPath));
        Assert.Equal(logBefore, File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md")));
    }

    [Fact]
    public void Check_IgnoresWikilinksCodeFencesAndNonHttpTargets()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "Mixed", string.Join("\n", new[]
        {
            "A [[wikilink]] and a [display|target] thing.",
            "Mail [me](mailto:someone@example.com) or jump to [top](#top).",
            "A relative [file](./notes.md).",
            "```",
            "curl [fenced](https://fenced.test/should-not-count)",
            "```",
            "A real [link](https://real.test/page).",
        }));

        var r = tv.Run("links", "check", "--json");
        var urls = Data(r).GetProperty("results").EnumerateArray()
            .Select(e => e.GetProperty("url").GetString()).ToArray();

        Assert.Equal(new[] { "https://real.test/page" }, urls);
    }

    [Fact]
    public void Check_External_FilesOnlyDefinitiveFailures()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "Alpha", string.Join("\n", new[]
        {
            "[gone](https://example.test/gone)",
            "[forbidden](https://example.test/forbidden)",
            "[fine](https://example.test/fine)",
        }));

        var service = WithProbe(new()
        {
            ["https://example.test/gone"] = new("broken", 404, "Not Found"),
            ["https://example.test/forbidden"] = new("unverified", 403, "Forbidden"),
            ["https://example.test/fine"] = new("ok", 200, null),
        });

        var report = service.Check(Vault.At(tv.Path), external: true, timeoutMs: 1000, concurrency: 2);

        Assert.True(report.Probed);
        Assert.Equal(3, report.Urls);
        Assert.Equal(1, report.Ok);
        Assert.Equal(1, report.Broken);
        Assert.Equal(1, report.Unverified);

        // One issue, on the PAGE carrying the link, and only for the 404.
        Assert.Equal(1, report.Filed);
        var issues = ((JsonElement)tv.Run("issues", "list", "--kind", "broken-external-link", "--status", "open", "--json").Envelope.Data!)
            .EnumerateArray().ToArray();
        Assert.Single(issues);
        Assert.Equal("alpha", issues[0].GetProperty("subject").GetString());
        Assert.Contains("https://example.test/gone", issues[0].GetProperty("detail").GetString()!);
        Assert.DoesNotContain("forbidden", issues[0].GetProperty("detail").GetString()!);
    }

    // The collision amendment H documents, in its sharpest form: a page can
    // carry both a dangling WIKILINK and a broken external URL, and the two
    // findings must not overwrite each other's detail/occurrences.
    [Fact]
    public void BrokenExternalLink_DoesNotCollideWithDanglingLink()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "Alpha", "A [[missing-page]] and a [dead](https://example.test/gone) link.");

        var service = WithProbe(new()
        {
            ["https://example.test/gone"] = new("broken", 404, "Not Found"),
        });
        service.Check(Vault.At(tv.Path), external: true, timeoutMs: 1000, concurrency: 1);

        var all = ((JsonElement)tv.Run("issues", "list", "--status", "open", "--json").Envelope.Data!)
            .EnumerateArray().ToArray();

        var dangling = all.Single(e => e.GetProperty("kind").GetString() == "dangling-link");
        var broken = all.Single(e => e.GetProperty("kind").GetString() == "broken-external-link");

        Assert.Equal("alpha", dangling.GetProperty("subject").GetString());
        Assert.Equal("alpha", broken.GetProperty("subject").GetString());
        Assert.Contains("missing-page", dangling.GetProperty("detail").GetString()!);
        Assert.Contains("example.test/gone", broken.GetProperty("detail").GetString()!);
        Assert.NotEqual(dangling.GetProperty("id").GetString(), broken.GetProperty("id").GetString());
    }

    [Fact]
    public void Check_ProbesEachDistinctUrlOnce()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "Alpha", "[a](https://example.test/same)");
        CreatePage(tv, "Beta", "[b](https://example.test/same)");
        CreatePage(tv, "Gamma", "[c](https://example.test/same)");

        var probes = new List<string>();
        var service = new LinksService(
            nowUnixMs: () => 1_770_000_000_000,
            probe: (url, _) =>
            {
                lock (probes) probes.Add(url);
                return Task.FromResult(new LinksService.ProbeOutcome("ok", 200, null));
            });

        service.Check(Vault.At(tv.Path), external: true, timeoutMs: 1000, concurrency: 4);

        Assert.Single(probes);
    }

    [Fact]
    public void Check_NeverTouchesLintStateOrTheLedger()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "Alpha", "[dead](https://example.test/gone)");

        var service = WithProbe(new()
        {
            ["https://example.test/gone"] = new("broken", 404, "Not Found"),
        });
        service.Check(Vault.At(tv.Path), external: true, timeoutMs: 1000, concurrency: 1);

        // The `linted` ledger precondition reads .wiki/lint.json. A network
        // probe must never be able to write it, or ingest completion would
        // start depending on reachability.
        Assert.False(File.Exists(Path.Combine(tv.Path, ".wiki", "lint.json")));
        Assert.False(File.Exists(Path.Combine(tv.Path, ".wiki", "ledger.json")));
    }

    [Fact]
    public void Check_IsNotPartOfLint()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "Alpha", "[dead](https://example.test/gone)");

        var lint = tv.Run("lint", "--json");
        Assert.Equal(0, lint.ExitCode);
        var kinds = Data(lint).GetProperty("counts").EnumerateArray()
            .Select(e => e.GetProperty("kind").GetString()).ToArray();
        Assert.DoesNotContain("broken-external-link", kinds);
    }

    [Fact]
    public void Check_BadArguments_Rejected()
    {
        using var tv = new TempVault(); Init(tv);

        var badTimeout = tv.Run("links", "check", "--timeout", "0", "--json");
        Assert.Equal(1, badTimeout.ExitCode);
        Assert.Contains(badTimeout.Envelope.Errors, e => e.Code == "invalid-timeout");

        var badConcurrency = tv.Run("links", "check", "--concurrency", "0", "--json");
        Assert.Equal(1, badConcurrency.ExitCode);
        Assert.Contains(badConcurrency.Envelope.Errors, e => e.Code == "invalid-concurrency");
    }

    [Fact]
    public void Check_ExitsZeroEvenWithBrokenLinks()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "Alpha", "[dead](https://example.test/gone)");

        // A broken external URL is a filed finding, not a rejected input.
        var report = WithProbe(new() { ["https://example.test/gone"] = new("broken", 404, "Not Found") })
            .Check(Vault.At(tv.Path), external: true, timeoutMs: 1000, concurrency: 1);
        Assert.Equal(1, report.Broken);

        // The CLI surface agrees: the offline path exits 0 too.
        Assert.Equal(0, tv.Run("links", "check", "--json").ExitCode);
    }

    [Fact]
    public void MarkdownLinks_Extract_HandlesTitlesAndNesting()
    {
        // Documents the parser's deliberate limits: it matches the shape an
        // agent actually writes and DECLINES the exotic ones rather than
        // mis-parsing them into phantom findings. A link with a title after
        // the URL and an angle-bracket target are both skipped - declining to
        // probe is a non-event, a false "broken" in the issue queue is not.
        var links = MarkdownLinks.Extract("[a](https://x.test/1) [b](https://x.test/2 \"title\") [c](<https://x.test/3>)");
        Assert.Equal(new[] { "https://x.test/1" },
            links.Select(l => l.Url).Where(MarkdownLinks.IsProbeable).ToArray());
    }
}
