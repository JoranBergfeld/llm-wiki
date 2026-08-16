using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wiki.Cli;
using Wiki.Core;
using Wiki.Docs;
using Wiki.State;

namespace Wiki.Services;

// One external URL's outcome. `Status` is a closed wire vocabulary:
//
//   ok          - the URL resolved (2xx or a redirect chain ending in one)
//   broken      - the URL definitively does not resolve: 404, 410, or a
//                 DNS/connection failure
//   unverified  - the probe could not establish either: 403, 429, a timeout,
//                 any 5xx. A soft state on purpose - see LinksService.
//
// `Pages` lists every page whose body carries this URL. Probing is per
// DISTINCT url, not per occurrence, so one link repeated across ten pages
// costs one request.
public sealed record ExternalLinkResult(
    string Url,
    string Status,
    int? HttpStatus,
    string? Detail,
    string[] Pages);

// `wiki links check` result.
public sealed record LinksCheckReport(
    bool Probed,
    int Urls,
    int Ok,
    int Broken,
    int Unverified,
    int Filed,
    ExternalLinkResult[] Results) : IHumanRenderable
{
    public string HumanSummary()
        => Probed
            ? $"Checked {Urls} distinct external URL(s): {Ok} ok, {Broken} broken, {Unverified} unverified; {Filed} issue(s) filed"
            : $"Found {Urls} distinct external URL(s). Pass --external to probe them.";
}

// `wiki links check [--external] [--timeout <ms>] [--concurrency <n>]`
// (issue #2): external URL liveness.
//
// WHY THIS IS NOT A LINT CHECK, and never will be. Every check in
// LintService is a pure function of the vault's bytes: same vault in, same
// findings out, offline, instantly. An HTTP liveness probe breaks all four -
// it needs the network, it is slow, and it is non-deterministic (429s,
// redirects, captive portals, sites that 403 anything without a browser UA).
//
// The real damage would be to the reflect loop. Issues merge on
// (kind, subject) and `occurrences` is the signal that an instructions
// deficiency is RECURRING (spec §12). If a finding can be produced by a flaky
// network, `occurrences` stops meaning "this problem persists" and starts
// meaning "the wifi flaked again" - which corrupts the one measurement the
// amendment workflow reads. It also cannot gate the ledger: the `linted`
// precondition compares `.wiki/lint.json` against a source's `integrated`
// timestamp, and making ingest completion depend on network reachability
// would strand sources mid-ingest for reasons that have nothing to do with
// the vault.
//
// So: separate command, opt-in, never a precondition for anything, never
// touching lint.json or the ledger, and never blocking a write.
//
// TWO MODES, and the network one is the one you have to ask for. Without
// `--external` this is a pure offline INVENTORY: extract every markdown link
// in the vault, report the distinct external URLs and which pages carry them,
// touch no socket. `--external` is what turns on probing. Making the network
// opt-in rather than the default means the command is safe to run anywhere,
// and the inventory alone answers "what does this vault depend on".
//
// WHAT COUNTS AS BROKEN, settled: 404 and 410 (the server said it is not
// there) and DNS/connection failures. Everything ambiguous - 403, 429,
// timeouts, 5xx - is `unverified`, reported but NEVER filed as an issue. A
// soft state is more honest than a false positive, and only definitive
// failures earn a place in the queue an agent works.
//
// RESULT CACHING is deliberately NOT implemented (see spec amendment Y).
// A TTL cache in `.wiki/` would be derived state that `reindex` needs a story
// for, and its correctness would hinge on wall-clock time - exactly the
// nondeterminism this command exists to keep out of the rest of the system.
// The rudeness it would have mitigated is handled the cheap way instead:
// probing is per distinct URL, so a link repeated across the vault costs one
// request, and `--concurrency` bounds the burst.
public sealed class LinksService
{
    private readonly Func<long> _nowUnixMs;
    private readonly Func<string, int, Task<ProbeOutcome>>? _probe;

    // `probe` is the network seam. Production leaves it null and gets the real
    // HttpClient path; tests inject a function so the suite never touches a
    // socket - which is the same reason every other service here takes a clock
    // seam.
    public LinksService(Func<long>? nowUnixMs = null, Func<string, int, Task<ProbeOutcome>>? probe = null)
    {
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _probe = probe;
    }

    public readonly record struct ProbeOutcome(string Status, int? HttpStatus, string? Detail);

    public LinksCheckReport Check(Vault v, bool external, int timeoutMs, int concurrency)
    {
        if (timeoutMs < 1)
            throw new ValidationException("invalid-timeout", $"--timeout must be >= 1 ms, got {timeoutMs}");
        if (concurrency < 1)
            throw new ValidationException("invalid-concurrency", $"--concurrency must be >= 1, got {concurrency}");

        // url -> the pages carrying it, sorted, deduped. The inventory half of
        // the command is fully deterministic even though the probing half
        // cannot be.
        var byUrl = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (slug, _, body) in PageStore.EnumerateWithBody(v))
        {
            foreach (var link in MarkdownLinks.Extract(body))
            {
                if (!MarkdownLinks.IsProbeable(link.Url)) continue;
                if (!byUrl.TryGetValue(link.Url, out var pages))
                {
                    pages = new SortedSet<string>(StringComparer.Ordinal);
                    byUrl[link.Url] = pages;
                }
                pages.Add(slug);
            }
        }

        if (!external)
        {
            var inventory = byUrl
                .Select(kv => new ExternalLinkResult(kv.Key, "unprobed", null, null, kv.Value.ToArray()))
                .ToArray();
            return new LinksCheckReport(false, inventory.Length, 0, 0, 0, 0, inventory);
        }

        var outcomes = ProbeAll(byUrl.Keys.ToArray(), timeoutMs, concurrency);

        var results = byUrl
            .Select(kv =>
            {
                var o = outcomes[kv.Key];
                return new ExternalLinkResult(kv.Key, o.Status, o.HttpStatus, o.Detail, kv.Value.ToArray());
            })
            .ToArray();

        // Only definitive failures are filed. Subject is the PAGE carrying the
        // link, matching how `dangling-link` subjects the containing page -
        // that is the thing someone has to edit.
        var utcIso = DateTimeOffset.FromUnixTimeMilliseconds(_nowUnixMs()).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var brokenByPage = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in results.Where(r => r.Status == "broken"))
        {
            foreach (var page in r.Pages)
            {
                if (!brokenByPage.TryGetValue(page, out var urls))
                {
                    urls = new List<string>();
                    brokenByPage[page] = urls;
                }
                urls.Add(r.HttpStatus is int code ? $"{r.Url} ({code})" : $"{r.Url} ({r.Detail})");
            }
        }

        var filed = 0;
        if (brokenByPage.Count > 0)
        {
            var issues = new Issues();
            issues.Load(v);
            foreach (var (page, urls) in brokenByPage)
            {
                issues.Upsert(IssueKind.BrokenExternalLink, page,
                    $"external link(s) that do not resolve: {string.Join(", ", urls)}", utcIso);
                filed++;
            }
            issues.Save(v);
        }

        LogFile.Append(v, utcIso, "links-check", "vault",
            $"urls={results.Length} broken={results.Count(r => r.Status == "broken")} " +
            $"unverified={results.Count(r => r.Status == "unverified")}");

        return new LinksCheckReport(
            true,
            results.Length,
            results.Count(r => r.Status == "ok"),
            results.Count(r => r.Status == "broken"),
            results.Count(r => r.Status == "unverified"),
            filed,
            results);
    }

    private Dictionary<string, ProbeOutcome> ProbeAll(string[] urls, int timeoutMs, int concurrency)
    {
        var results = new Dictionary<string, ProbeOutcome>(StringComparer.Ordinal);
        if (urls.Length == 0) return results;

        var probe = _probe ?? DefaultProbe;

        using var gate = new SemaphoreSlim(concurrency);
        var tasks = urls.Select(async url =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return (Url: url, Outcome: await probe(url, timeoutMs).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        // The CLI is synchronous end to end; this is the one place that fans
        // out, and it joins before returning. Blocking here keeps async from
        // leaking into every command signature for a single opt-in command.
        foreach (var (url, outcome) in Task.WhenAll(tasks).GetAwaiter().GetResult())
            results[url] = outcome;

        return results;
    }

    // HEAD first, falling back to GET on 405 (and on 501, which some servers
    // return for an unimplemented method). Redirects are followed by the
    // handler default, so a URL that ends at a 200 is `ok` regardless of how
    // many hops it took - a moved page is not a broken link.
    private static async Task<ProbeOutcome> DefaultProbe(string url, int timeoutMs)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
        };
        // Plenty of sites 403 anything that does not look like a browser.
        // Those land in `unverified` rather than `broken`, but sending a real
        // UA keeps the noise down in the first place.
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (compatible; llm-wiki links check)");

        try
        {
            var response = await SendAsync(http, HttpMethod.Head, url).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                response.Dispose();
                response = await SendAsync(http, HttpMethod.Get, url).ConfigureAwait(false);
            }

            using (response)
            {
                var code = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                    return new ProbeOutcome("ok", code, null);

                // Only the server saying "this is not here" is definitive.
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    return new ProbeOutcome("broken", code, response.ReasonPhrase);

                return new ProbeOutcome("unverified", code, response.ReasonPhrase);
            }
        }
        catch (TaskCanceledException)
        {
            // A timeout says nothing about whether the resource exists.
            return new ProbeOutcome("unverified", null, $"timed out after {timeoutMs} ms");
        }
        catch (HttpRequestException ex)
        {
            // DNS failure and connection refused ARE definitive - there is
            // nothing at that address to serve the page.
            var definitive = ex.InnerException is System.Net.Sockets.SocketException;
            return new ProbeOutcome(definitive ? "broken" : "unverified", null, ex.Message);
        }
        catch (UriFormatException ex)
        {
            return new ProbeOutcome("broken", null, ex.Message);
        }
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string url)
        => http.SendAsync(new HttpRequestMessage(method, url), HttpCompletionOption.ResponseHeadersRead);
}
