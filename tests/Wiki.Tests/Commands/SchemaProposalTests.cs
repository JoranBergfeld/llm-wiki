using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 25: `wiki schema propose/proposals/approve/reject` - the reflect
// loop's amendment surface (spec §13, Appendix B amendment C: full-section
// replacement, never a unified diff). Tests run against the real AGENTS.md
// template `wiki init` scaffolds (src/Wiki/Templates/agents-md.txt), which
// has these real sections:
//
//   # Wiki Agent Instructions          (h1, not a valid --section target)
//   ## Conventions                     (h2, no subsections)
//   ## Playbooks                       (h2, LAST top-level section, has ### children)
//     ### Session start                (h3, not last - a sibling follows)
//     ### Retrieval (answering questions)
//     ### Ingest
//     ### Reflect                      (h3, LAST heading in the file - to EOF)
public class SchemaProposalTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string AgentsPath(TempVault tv) => Path.Combine(tv.Path, "AGENTS.md");

    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static CliResult Propose(TempVault tv, string section, string body, string? rationale = null)
    {
        var args = new System.Collections.Generic.List<string> { "schema", "propose", "--section", section, "--stdin", "--json" };
        if (rationale is not null) { args.Add("--rationale"); args.Add(rationale); }
        return tv.RunStdin(body, args.ToArray());
    }

    // -------------------- propose --------------------

    [Fact]
    public void Propose_KnownSection_CreatesOpenProposal()
    {
        using var tv = new TempVault(); Init(tv);

        var r = Propose(tv, "Session start", "1. New session bootstrap step.\n", "tighten session-start playbook");
        Assert.Equal(0, r.ExitCode);

        var data = Data(r);
        Assert.Equal("Session start", data.GetProperty("section").GetString());
        Assert.Equal("open", data.GetProperty("status").GetString());
        Assert.Equal("tighten session-start playbook", data.GetProperty("rationale").GetString());
        Assert.Equal("1. New session bootstrap step.\n", data.GetProperty("newText").GetString());
        Assert.True(data.GetProperty("id").GetString()!.Length == 26);
    }

    [Fact]
    public void Propose_UnknownSection_UnknownSectionError()
    {
        using var tv = new TempVault(); Init(tv);

        var before = File.ReadAllText(AgentsPath(tv));
        var r = Propose(tv, "Not A Real Heading", "whatever\n");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-section");

        // Nothing should have landed - AGENTS.md untouched, no proposal filed.
        Assert.Equal(before, File.ReadAllText(AgentsPath(tv)));
        Assert.Equal(0, Data(tv.Run("schema", "proposals", "--json")).GetArrayLength());
    }

    [Fact]
    public void Propose_TopLevelTitle_IsNotAValidSectionTarget()
    {
        // The h1 document title is intentionally out of scope for amendment
        // C - only '##'/'###' headings are eligible section anchors.
        using var tv = new TempVault(); Init(tv);

        var r = Propose(tv, "Wiki Agent Instructions", "whatever\n");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-section");
    }

    [Fact]
    public void Propose_MissingRationale_DefaultsToEmptyString()
    {
        using var tv = new TempVault(); Init(tv);

        var r = Propose(tv, "Conventions", "- New convention line.\n");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", Data(r).GetProperty("rationale").GetString());
    }

    // -------------------- proposals --------------------

    [Fact]
    public void Proposals_ListsOpenProposal()
    {
        using var tv = new TempVault(); Init(tv);
        var created = Data(Propose(tv, "Session start", "new body\n", "r"));
        var id = created.GetProperty("id").GetString();

        var r = tv.Run("schema", "proposals", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal(id, data[0].GetProperty("id").GetString());
        Assert.Equal("open", data[0].GetProperty("status").GetString());
    }

    [Fact]
    public void Proposals_FilterByStatus()
    {
        using var tv = new TempVault(); Init(tv);
        var openId = Data(Propose(tv, "Session start", "a\n")).GetProperty("id").GetString()!;
        var rejectedId = Data(Propose(tv, "Conventions", "b\n")).GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("schema", "reject", rejectedId, "--json").ExitCode);

        var open = tv.Run("schema", "proposals", "--status", "open", "--json");
        Assert.Equal(1, Data(open).GetArrayLength());
        Assert.Equal(openId, Data(open)[0].GetProperty("id").GetString());

        var rejected = tv.Run("schema", "proposals", "--status", "rejected", "--json");
        Assert.Equal(1, Data(rejected).GetArrayLength());
        Assert.Equal(rejectedId, Data(rejected)[0].GetProperty("id").GetString());
    }

    [Fact]
    public void Proposals_InvalidStatus_ValidationError()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("schema", "proposals", "--status", "bogus", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "invalid-proposal-status");
    }

    // -------------------- approve --------------------

    [Fact]
    public void Approve_UnknownId_NotFound()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("schema", "approve", "01AAAAAAAAAAAAAAAAAAAAAAAA", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void Approve_ReplacesSectionBody_PreservesRestOfFile()
    {
        using var tv = new TempVault(); Init(tv);
        var before = File.ReadAllText(AgentsPath(tv));

        var id = Data(Propose(tv, "Session start", "1. Run `wiki ingest status`.\n2. Run `wiki issues list --status open`.\n", "tighten wording")).GetProperty("id").GetString()!;

        var r = tv.Run("schema", "approve", id, "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("approved", Data(r).GetProperty("status").GetString());

        var after = File.ReadAllText(AgentsPath(tv));
        Assert.NotEqual(before, after);

        // Heading kept verbatim; new body present.
        Assert.Contains("### Session start\n1. Run `wiki ingest status`.\n2. Run `wiki issues list --status open`.\n", after);

        // The old Session-start body is gone...
        Assert.DoesNotContain("finish interrupted work before anything else", after);

        // ...but everything else survives untouched: the h1 preamble, the
        // whole Conventions section, and every OTHER Playbooks subsection
        // (proving the replacement stopped at the very next heading and
        // didn't bleed into siblings).
        Assert.Contains("## Conventions", after);
        Assert.Contains("- Page types: summary (one per source), entity (nameable thing), concept", after);
        Assert.Contains("### Retrieval (answering questions)", after);
        Assert.Contains("Never scan bodies to discover relevance.", after);
        Assert.Contains("### Ingest", after);
        Assert.Contains("### Reflect", after);
        Assert.Contains("Never edit this file directly.", after);

        // And proposals persists the decision across a fresh invocation.
        var show = tv.Run("schema", "proposals", "--status", "approved", "--json");
        Assert.Equal(1, Data(show).GetArrayLength());
    }

    [Fact]
    public void Approve_LastSection_ReplacesToEndOfFile()
    {
        // "Reflect" is the LAST heading in the whole file - its body must
        // replace everything to EOF, with nothing left dangling after it.
        using var tv = new TempVault(); Init(tv);

        var id = Data(Propose(tv, "Reflect", "Draft an amendment whenever an issue kind recurs 3+ times.\n")).GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("schema", "approve", id, "--json").ExitCode);

        var after = File.ReadAllText(AgentsPath(tv));
        Assert.EndsWith("### Reflect\nDraft an amendment whenever an issue kind recurs 3+ times.\n", after);

        // Earlier sections/subsections are all still present and unchanged.
        Assert.Contains("## Conventions", after);
        Assert.Contains("### Session start", after);
        Assert.Contains("### Ingest", after);
        Assert.DoesNotContain("Never edit this file directly.", after);
    }

    [Fact]
    public void Approve_SectionWithSubsections_DoesNotStopAtFirstChildHeading()
    {
        // "Playbooks" is a '##' section whose body contains four '###'
        // children (Session start / Retrieval / Ingest / Reflect). Replacing
        // it must swallow ALL of them, not stop at the first '###' - proving
        // the locator's "equal-OR-HIGHER level" rule (a deeper heading does
        // NOT end the section).
        using var tv = new TempVault(); Init(tv);

        var replacement = "A single flat playbook paragraph with no subsections at all.\n";
        var id = Data(Propose(tv, "Playbooks", replacement)).GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("schema", "approve", id, "--json").ExitCode);

        var after = File.ReadAllText(AgentsPath(tv));
        Assert.Contains("## Playbooks\n" + replacement, after);

        Assert.DoesNotContain("### Session start", after);
        Assert.DoesNotContain("### Retrieval", after);
        Assert.DoesNotContain("### Ingest", after);
        Assert.DoesNotContain("### Reflect", after);

        // Conventions (the other top-level section) is untouched.
        Assert.Contains("## Conventions", after);
        Assert.Contains("Never cite pages with status pending-review.", after);

        // Playbooks was also the last top-level section, so this doubles as
        // the "replace to EOF" case at the '##' level.
        Assert.EndsWith("## Playbooks\n" + replacement, after);
    }

    [Fact]
    public void Approve_AgentsMdSectionUnknownAtApproveTime_UnknownSectionError()
    {
        // Propose validates the section exists; approve re-checks, because
        // AGENTS.md may have drifted since (a human hand-edit here, or
        // another approved proposal renaming the heading).
        using var tv = new TempVault(); Init(tv);
        var id = Data(Propose(tv, "Session start", "new body\n")).GetProperty("id").GetString()!;

        // Simulate drift: rewrite AGENTS.md so "Session start" no longer exists.
        File.WriteAllText(AgentsPath(tv), "# Wiki Agent Instructions\n\n## Conventions\nsomething\n");

        var r = tv.Run("schema", "approve", id, "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-section");

        // The proposal is still open (approve failed before any state write).
        var proposals = tv.Run("schema", "proposals", "--status", "open", "--json");
        Assert.Equal(1, Data(proposals).GetArrayLength());
    }

    [Fact]
    public void Approve_AlreadyApproved_StateConflict()
    {
        using var tv = new TempVault(); Init(tv);
        var id = Data(Propose(tv, "Conventions", "- one rule\n")).GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("schema", "approve", id, "--json").ExitCode);

        var second = tv.Run("schema", "approve", id, "--json");
        Assert.Equal(3, second.ExitCode);
        Assert.Contains(second.Envelope.Errors, e => e.Code == "state-conflict");
    }

    [Fact]
    public void Approve_AlreadyRejected_StateConflict()
    {
        using var tv = new TempVault(); Init(tv);
        var id = Data(Propose(tv, "Conventions", "- one rule\n")).GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("schema", "reject", id, "--json").ExitCode);

        var r = tv.Run("schema", "approve", id, "--json");
        Assert.Equal(3, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "state-conflict");
    }

    // -------------------- reject --------------------

    [Fact]
    public void Reject_UnknownId_NotFound()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("schema", "reject", "01AAAAAAAAAAAAAAAAAAAAAAAA", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void Reject_MarksRejected_DoesNotTouchAgentsMd()
    {
        using var tv = new TempVault(); Init(tv);
        var before = File.ReadAllText(AgentsPath(tv));

        var id = Data(Propose(tv, "Conventions", "- a totally different rule set\n")).GetProperty("id").GetString()!;
        var r = tv.Run("schema", "reject", id, "--note", "not aligned with current conventions", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("rejected", Data(r).GetProperty("status").GetString());
        Assert.Equal("not aligned with current conventions", Data(r).GetProperty("note").GetString());

        var after = File.ReadAllText(AgentsPath(tv));
        Assert.Equal(before, after);
    }

    [Fact]
    public void Reject_AlreadyRejected_StateConflict()
    {
        using var tv = new TempVault(); Init(tv);
        var id = Data(Propose(tv, "Conventions", "- x\n")).GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("schema", "reject", id, "--json").ExitCode);

        var r = tv.Run("schema", "reject", id, "--json");
        Assert.Equal(3, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "state-conflict");
    }

    [Fact]
    public void Reject_AlreadyApproved_StateConflict()
    {
        using var tv = new TempVault(); Init(tv);
        var id = Data(Propose(tv, "Conventions", "- x\n")).GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("schema", "approve", id, "--json").ExitCode);

        var r = tv.Run("schema", "reject", id, "--json");
        Assert.Equal(3, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "state-conflict");
    }

    [Fact]
    public void Approve_AppendsLogEntry()
    {
        using var tv = new TempVault(); Init(tv);
        var id = Data(Propose(tv, "Conventions", "- x\n")).GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("schema", "approve", id, "--json").ExitCode);

        var log = File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md"));
        Assert.Contains("schema-approve", log);
        Assert.Contains(id, log);
    }
}
