using System;
using System.Collections.Generic;
using System.Linq;
using Wiki.Cli;
using Wiki.Core;

namespace Wiki.Services;

// One golden question's result. `Surfaced` is the ordered candidate set the
// router produced (capped at k); `Found`/`Missing` split the question's
// expectations against it. Recall is per-question so a report can be read
// question-by-question, not just as an aggregate.
public sealed record EvalQuestionResult(
    string Ask,
    string[] Expect,
    string[] Surfaced,
    string[] Found,
    string[] Missing,
    int RecallPercent);

// `wiki eval` result. `Score` is mean recall@k across every question, rounded
// to a whole percent - the vault-level number a human watches over time.
public sealed record EvalReport(
    int Questions,
    int K,
    int Score,
    int Passed,
    EvalQuestionResult[] Results) : IHumanRenderable
{
    public string HumanSummary()
        => $"Eval: recall@{K} = {Score}% across {Questions} question(s); {Passed} fully satisfied";
}

// `wiki eval [--k N] [--fail-under N]` (issue #11 part A): golden-question
// retrieval scoring.
//
// The gap it fills: every `wiki lint` check is deliberately non-semantic, so
// a vault of well-linked, correctly-sized lorem ipsum passes the full suite.
// That is the right design - determinism is why the CLI/LLM split works - but
// it left NO quality signal at all, deterministic or otherwise. This is a
// deterministic one, and it stays out of `lint` on purpose: lint's value is
// that "clean" means something precise and free, and mixing in a measurement
// that depends on a human-authored question file would spend that.
//
// THE METRIC FALLS OUT OF THE EXISTING DESIGN. The retrieval playbook budgets
// "at most 10 candidate pages", so the measure is recall@10: is the right page
// in the set the router hands the agent? `k` is configurable and defaults to
// 10 to match the playbook.
//
// HOW ROUTING IS SIMULATED. The playbook routes with `index show` (title +
// summary, the routing surface) and `search` (body text), then picks at most
// ten. So scoring mirrors that, deliberately dumbly (amendment F):
//
//   score(page) = 3 x (distinct question terms appearing in title+summary)
//               + 1 x (distinct question terms appearing in the body)
//
// Pages scoring zero are not candidates. Ties break on slug, ordinal, so the
// result is byte-stable for a given vault. Archived pages are excluded, the
// same exclusion `index.md` itself applies.
//
// The 3:1 weighting is the whole point rather than a tuning knob: `summary` is
// the most important text in the vault because it is what `index.md` routes
// on and the playbook forbids scanning bodies to find relevance. A vague
// summary does not degrade a page, it makes the page INVISIBLE - a retrieval
// bug, not a prose-style one - and weighting the routing surface above the
// body is what makes that failure show up in the score.
//
// What it catches: routing decay, `summary`-field rot, and semantic
// duplication (two near-duplicate pages split the evidence, so both twins
// surface and crowd out a real answer, or neither does).
//
// What it does not do: judge answer TEXT. That needs a model and gives up
// determinism - see `wiki audit` (issue #12).
public sealed class EvalService
{
    // Deliberately tiny and hard-coded rather than configurable: a stopword
    // list is a property of the metric's definition, and a per-vault one
    // would make two vaults' scores incomparable. Only words that carry no
    // routing signal in a question phrased as a question.
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "did", "do", "does", "for", "from",
        "has", "have", "how", "in", "is", "it", "of", "on", "or", "that", "the", "to", "was",
        "were", "what", "when", "where", "which", "who", "why", "with",
    };

    public EvalReport Run(Vault v, int k)
    {
        if (k < 1)
            throw new ValidationException("invalid-k", $"--k must be >= 1, got {k}");

        if (!System.IO.File.Exists(v.EvalPath))
            throw new ValidationException("eval-file-missing",
                $"no eval file at '{v.EvalPath}'. It is human-owned: create it with a 'version: 1' key and a " +
                "'questions:' list of '- ask: \"…\"' / 'expect: slug-a, slug-b' pairs.",
                v.EvalPath);

        var evalFile = EvalFile.Load(v.EvalPath);

        // Snapshot the routable page set once - every question scores against
        // the same vault state, so a report is internally consistent.
        var pages = PageStore.EnumerateWithBody(v)
            .Where(p => p.Front.Status != PageStatus.Archived)
            .ToArray();

        var results = new List<EvalQuestionResult>();
        var totalRecall = 0;
        var passed = 0;

        foreach (var q in evalFile.Questions)
        {
            var terms = Tokenize(q.Ask);

            var surfaced = pages
                .Select(p => (p.Slug, Score: ScorePage(terms, p.Front, p.Body)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Slug, StringComparer.Ordinal)
                .Take(k)
                .Select(x => x.Slug)
                .ToArray();

            var surfacedSet = new HashSet<string>(surfaced, StringComparer.Ordinal);
            var found = q.Expect.Where(surfacedSet.Contains).ToArray();
            var missing = q.Expect.Where(e => !surfacedSet.Contains(e)).ToArray();

            var recall = (int)Math.Round(found.Length * 100.0 / q.Expect.Length);
            totalRecall += recall;
            if (missing.Length == 0) passed++;

            results.Add(new EvalQuestionResult(q.Ask, q.Expect, surfaced, found, missing, recall));
        }

        var score = (int)Math.Round((double)totalRecall / evalFile.Questions.Count);
        return new EvalReport(evalFile.Questions.Count, k, score, passed, results.ToArray());
    }

    private static string[] Tokenize(string text)
    {
        var words = new List<string>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            // Single characters carry no routing signal and match everything.
            if (word.Length < 2) continue;
            if (Stopwords.Contains(word)) continue;
            words.Add(word);
        }
        return words.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int ScorePage(string[] terms, PageFrontmatter front, string body)
    {
        if (terms.Length == 0) return 0;

        var routing = front.Title + " " + front.Summary;
        var score = 0;
        foreach (var term in terms)
        {
            if (routing.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 3;
            else if (body.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 1;
        }
        return score;
    }
}
