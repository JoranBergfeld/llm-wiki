using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #50: `wiki persona distill` (read side of a pass), `wiki persona
// propose` (one changeset, validated, queued; clears the candidate flags the
// pass considered) and the all-or-nothing review in `wiki review`.
// Observed only through the CLI seam: envelopes, exit codes, files in the
// vault and lines in the audit log.
public class PersonaChangesetTests
{
    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    // Init with the scaffolded `article` category endorsed (persona-source)
    // and `note` left alone.
    private static void Init(TempVault tv)
    {
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var path = Path.Combine(tv.Path, "wiki.yaml");
        var text = File.ReadAllText(path);
        var anchor = "description: \"Web articles and blog posts\"";
        Assert.Contains(anchor, text);
        File.WriteAllText(path, text.Replace(anchor, anchor + "\n    persona-source: true"));
    }

    private static string AddSource(TempVault tv, string category, string name)
    {
        var src = Path.Combine(tv.Path, name + ".md");
        File.WriteAllText(src, "Content of " + name);
        var r = tv.Run("source", "add", src, "--category", category, "--title", name, "--json");
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("id").GetString()!;
    }

    // A summary page citing a new source in `category`; returns (id, path).
    private static (string Id, string Path) Page(TempVault tv, string title, string category = "article", string? body = null)
    {
        var source = AddSource(tv, category, title.ToLowerInvariant().Replace(' ', '-') + "-src");
        var r = tv.RunStdin(body ?? "Body of " + title, "page", "upsert", "--type", "summary", "--title", title,
            "--summary", title + " summary", "--sources", source, "--stdin", "--json");
        Assert.Equal(0, r.ExitCode);
        return (Data(r).GetProperty("id").GetString()!, Data(r).GetProperty("path").GetString()!);
    }

    private static string Read(TempVault tv, string rel) => File.ReadAllText(Path.Combine(tv.Path, rel));

    [Fact]
    public void Distill_ListsCandidatePages_AndTheCurrentPersona()
    {
        using var tv = new TempVault(); Init(tv);
        var endorsed = Page(tv, "Craft note");
        Page(tv, "Plain note", category: "meeting-transcript");

        var r = tv.Run("persona", "distill", "--json");

        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        var candidate = Assert.Single(data.GetProperty("candidates").EnumerateArray());
        Assert.Equal(endorsed.Id, candidate.GetProperty("id").GetString());
        Assert.Equal("craft-note", candidate.GetProperty("slug").GetString());
        Assert.Equal(endorsed.Path, candidate.GetProperty("path").GetString());
        Assert.False(data.GetProperty("persona").GetProperty("exists").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("pending").ValueKind);
    }

    // ---- propose ----------------------------------------------------------

    private static string Add(string section, string text, params string[] pages)
        => "{\"op\":\"add\",\"section\":\"" + section + "\",\"text\":" + JsonSerializer.Serialize(text)
           + ",\"evidence\":[" + string.Join(",", pages.Select(p => "{\"page\":\"" + p + "\"}")) + "]}";

    private static string Changeset(string reason, string[] changes, params string[] considered)
        => "{\"reason\":" + JsonSerializer.Serialize(reason)
           + ",\"considered\":[" + string.Join(",", considered.Select(c => "\"" + c + "\"")) + "]"
           + ",\"changes\":[" + string.Join(",", changes) + "]}";

    private static CliResult Propose(TempVault tv, string changeset)
        => tv.RunStdin(changeset, "persona", "propose", "--stdin", "--json");

    private static string[] AuditLines(TempVault tv)
        => File.ReadAllText(Path.Combine(tv.Path, "audit", "audit.log")).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

    private static string[] Segments(string line) => line.Split(" | ");

    private static string? AuditField(string line, string key)
        => Segments(line).FirstOrDefault(s => s.StartsWith(key + "="))?[(key.Length + 1)..];

    private static string AuditDetail(TempVault tv, string reference)
    {
        Assert.StartsWith("@", reference);
        return File.ReadAllText(Path.Combine(tv.Path, "audit", "details", reference[1..] + ".txt"));
    }

    private static bool IsCandidate(TempVault tv, string rel) => Read(tv, rel).Contains("\npersona: candidate\n");

    [Fact]
    public void Propose_QueuesTheChangeset_ReadableInObsidian_AndClearsTheConsideredFlags()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");
        var c = Page(tv, "Craft three");

        var r = Propose(tv, Changeset("two notes say it",
            new[] { Add("values", "I value careful craft.", a.Id, "craft-two") }, c.Id));

        Assert.Equal(0, r.ExitCode);
        var changeset = Data(r).GetProperty("changeset");
        var id = changeset.GetProperty("id").GetString()!;
        Assert.Equal("open", changeset.GetProperty("status").GetString());

        // Evidence is resolved by the CLI: page id, path and a body hash.
        var evidence = changeset.GetProperty("changes")[0].GetProperty("evidence");
        Assert.Equal(2, evidence.GetArrayLength());
        Assert.Equal(b.Id, evidence[1].GetProperty("page").GetString());
        Assert.Equal(b.Path, evidence[1].GetProperty("path").GetString());
        Assert.Matches("^[0-9a-f]{64}$", evidence[1].GetProperty("hash").GetString());

        // Readable in Obsidian while pending.
        var pending = Read(tv, "persona/changeset.md");
        Assert.Contains(id, pending);
        Assert.Contains("I value careful craft.", pending);
        Assert.Contains("[[craft-one]]", pending);

        // The pass considered all three (two as evidence, one listed).
        Assert.False(IsCandidate(tv, a.Path));
        Assert.False(IsCandidate(tv, b.Path));
        Assert.False(IsCandidate(tv, c.Path));
        Assert.Empty(Data(tv.Run("persona", "distill", "--json")).GetProperty("candidates").EnumerateArray());

        // Nothing is applied before review.
        Assert.False(File.Exists(Path.Combine(tv.Path, "PERSONA.md")));

        var line = AuditLines(tv)[^1];
        Assert.Equal("persona.propose", Segments(line)[1]);
        Assert.Equal(id, Segments(line)[2]);
        Assert.Equal("+1 ~0 -0", AuditField(line, "lines"));
    }

    [Theory]
    [InlineData("I value [[craft]].", "persona-link")]
    [InlineData("I value craft, see https://example.com", "persona-link")]
    [InlineData("I value craft, as 01JC9Z6Q4X0V6M8T2K3B5N7P9R shows", "persona-id")]
    public void Propose_ChangesetThatWouldFailPersonaLint_IsRejected_AndNothingChanges(string text, string kind)
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");
        var auditBefore = AuditLines(tv).Length;

        var r = Propose(tv, Changeset("r", new[] { Add("values", text, a.Id, b.Id) }));

        Assert.Equal(1, r.ExitCode);
        var error = Assert.Single(r.Envelope.Errors);
        Assert.Equal("persona-lint", error.Code);
        Assert.Contains(kind, error.Message);
        Assert.False(File.Exists(Path.Combine(tv.Path, "persona", "changeset.md")));
        Assert.True(IsCandidate(tv, a.Path));
        Assert.Equal(auditBefore, AuditLines(tv).Length);
    }

    [Fact]
    public void Propose_ChangesetOverASectionCap_IsRejectedAsPersonaLint()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");
        var adds = Enumerable.Range(1, 11).Select(i => Add("values", $"I hold value {i}.", a.Id, b.Id)).ToArray();

        var r = Propose(tv, Changeset("r", adds));

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("persona-lint", r.Envelope.Errors[0].Code);
        Assert.Contains("persona-section-cap", r.Envelope.Errors[0].Message);
    }

    [Fact]
    public void Propose_NoteDistilledLineWithOneSupportingPage_IsRejected()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");

        var r = Propose(tv, Changeset("r", new[] { Add("principles", "I decide in writing.", a.Id) }));

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("persona-evidence", r.Envelope.Errors[0].Code);
        Assert.Contains("two supporting pages", r.Envelope.Errors[0].Message);
    }

    [Fact]
    public void Propose_ToneOfVoiceFromNotes_IsRejected()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");

        var r = Propose(tv, Changeset("r", new[] { Add("tone-of-voice", "I write plainly.", a.Id, b.Id) }));

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("persona-evidence", r.Envelope.Errors[0].Code);
        Assert.Contains("owner-stated", r.Envelope.Errors[0].Message);
    }

    [Fact]
    public void Propose_EvidenceFromAPageOutsidePersonaSourceCategories_IsRejected()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var plain = Page(tv, "Plain", category: "meeting-transcript");

        var r = Propose(tv, Changeset("r", new[] { Add("values", "I value craft.", a.Id, plain.Id) }));

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("evidence-not-endorsed", r.Envelope.Errors[0].Code);
    }

    // Amendment AV: a page that also cites a source outside every
    // persona-source category is not an endorsed note.
    [Fact]
    public void Propose_EvidenceFromAMixedSourcePage_IsRejected()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var endorsed = AddSource(tv, "article", "mixed-endorsed-src");
        var other = AddSource(tv, "meeting-transcript", "mixed-other-src");
        var mixed = tv.RunStdin("Body of Mixed", "page", "upsert", "--type", "summary", "--title", "Mixed",
            "--summary", "Mixed summary", "--sources", endorsed + "," + other, "--stdin", "--json");
        Assert.Equal(0, mixed.ExitCode);

        var r = Propose(tv, Changeset("r", new[] { Add("values", "I value craft.", a.Id, Data(mixed).GetProperty("id").GetString()!) }));

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("evidence-not-endorsed", r.Envelope.Errors[0].Code);
    }

    [Fact]
    public void Propose_WithoutAReason_IsRejected()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");

        var r = Propose(tv, Changeset(" ", new string[0], a.Id));

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("invalid-changeset", r.Envelope.Errors[0].Code);
    }

    [Fact]
    public void Propose_OnlyConsideredPages_QueuesNothing_ButClearsTheirFlags()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");

        var r = Propose(tv, Changeset("nothing persona-worthy", new string[0], a.Id));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(JsonValueKind.Null, Data(r).GetProperty("changeset").ValueKind);
        Assert.False(IsCandidate(tv, a.Path));
        Assert.False(File.Exists(Path.Combine(tv.Path, "persona", "changeset.md")));
    }

    [Fact]
    public void SecondProposal_SupersedesThePendingOne_AndTheAuditLogSaysSo()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");
        var first = Data(Propose(tv, Changeset("first", new[] { Add("values", "I value craft.", a.Id, b.Id) })))
            .GetProperty("changeset").GetProperty("id").GetString()!;

        var r = Propose(tv, Changeset("second", new[] { Add("values", "I value careful craft.", a.Id, b.Id) }));

        Assert.Equal(0, r.ExitCode);
        var second = Data(r).GetProperty("changeset").GetProperty("id").GetString()!;
        Assert.Equal(first, Data(r).GetProperty("superseded").GetString());
        Assert.Equal(second, Data(tv.Run("persona", "distill", "--json")).GetProperty("pending").GetProperty("id").GetString());
        Assert.Contains(second, Read(tv, "persona/changeset.md"));

        var line = AuditLines(tv)[^1];
        Assert.Equal(second, Segments(line)[2]);
        Assert.Equal(first, AuditField(line, "supersedes"));

        // The superseded one can no longer be decided.
        var approve = tv.Run("review", "approve", first, "--json");
        Assert.Equal(3, approve.ExitCode);
    }

    // ---- review -----------------------------------------------------------

    private static (string Id, (string Id, string Path) A, (string Id, string Path) B) Pending(TempVault tv, params string[] extraChanges)
    {
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");
        var changes = new[] { Add("values", "I value careful craft.", a.Id, b.Id) }.Concat(extraChanges).ToArray();
        var r = Propose(tv, Changeset("two notes say it", changes));
        Assert.Equal(0, r.ExitCode);
        return (Data(r).GetProperty("changeset").GetProperty("id").GetString()!, a, b);
    }

    [Fact]
    public void ReviewList_ShowsThePendingChangeset_WithADiffOfPersonaMd()
    {
        using var tv = new TempVault(); Init(tv);
        var (id, _, _) = Pending(tv);

        var r = tv.Run("review", "list", "--json");

        Assert.Equal(0, r.ExitCode);
        var row = Assert.Single(Data(r).EnumerateArray(), x => x.GetProperty("kind").GetString() == "persona");
        Assert.Equal(id, row.GetProperty("id").GetString());
        Assert.Contains("+ - I value careful craft.", row.GetProperty("diff").GetString());
        Assert.Equal(id, row.GetProperty("changeset").GetProperty("id").GetString());
    }

    [Fact]
    public void ReviewApprove_WritesPersonaMd_FrontmatterIncluded_AndLogsEvidenceWithHashes()
    {
        using var tv = new TempVault(); Init(tv);
        var (id, a, b) = Pending(tv);

        var r = tv.Run("review", "approve", id, "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("approved", Data(r).GetProperty("changeset").GetProperty("status").GetString());

        var persona = Data(tv.Run("persona", "read", "--json"));
        Assert.True(persona.GetProperty("exists").GetBoolean());
        Assert.Equal("1", persona.GetProperty("schemaVersion").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", persona.GetProperty("lastDistilled").GetString());
        Assert.Equal(new[] { "I value careful craft." },
            persona.GetProperty("values").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(File.Exists(Path.Combine(tv.Path, "persona", "changeset.md")));

        // A file the CLI wrote is lint-clean, evidence included.
        tv.Run("lint", "--json");
        var issues = Data(tv.Run("issues", "list", "--status", "open", "--json")).EnumerateArray()
            .Where(i => i.GetProperty("kind").GetString()!.StartsWith("persona-"));
        Assert.Empty(issues);

        var line = AuditLines(tv).Last(l => Segments(l)[1] == "persona.accept");
        // audit_entry names the audit-log entry that produced the file: the
        // accept line's own hash (amendment AY), not the changeset id.
        Assert.Equal(AuditField(line, "hash"), persona.GetProperty("auditEntry").GetString());
        Assert.Equal("PERSONA.md", Segments(line)[2]);
        Assert.Equal(id, AuditField(line, "changeset"));
        Assert.Equal("+1 ~0 -0", AuditField(line, "lines"));
        Assert.Equal("cli", AuditField(line, "decidedVia"));
        Assert.Equal("two notes say it", AuditField(line, "reason"));
        var evidence = AuditField(line, "evidence")!;
        if (evidence.StartsWith("@")) evidence = AuditDetail(tv, evidence);
        Assert.Matches(Regex(a.Path), evidence);
        Assert.Matches(Regex(b.Path), evidence);
        Assert.Contains("I value careful craft.", AuditDetail(tv, AuditField(line, "changeset-detail")!));
    }

    // PERSONA.md is written once its accept line is built (amendment AY), but
    // still inside the journaled command: an approve that dies before its
    // line lands is completed by the next writer, and audit_entry still names
    // the line that writer appends.
    [Fact]
    public void AnApproveInterruptedBeforeItsAuditLine_IsCompleted_AndAuditEntryNamesTheLine()
    {
        using var tv = new TempVault(); Init(tv);
        var (id, _, _) = Pending(tv);

        using (Interrupt.At("committed"))
            tv.Run("review", "approve", id, "--json");
        Assert.DoesNotContain(AuditLines(tv), l => Segments(l)[1] == "persona.accept");

        Assert.Equal(0, tv.Run("category", "add", "paper", "--description", "Papers", "--json").ExitCode);

        var line = AuditLines(tv).Single(l => Segments(l)[1] == "persona.accept");
        Assert.Equal(AuditField(line, "hash"),
            Data(tv.Run("persona", "read", "--json")).GetProperty("auditEntry").GetString());
    }

    // PERSONA.md files written before amendment AY carry the changeset id as
    // audit_entry; the evidence lookup still finds their accept by it.
    [Fact]
    public void APersonaMdWhoseAuditEntryIsTheChangesetId_StillFindsItsEvidence()
    {
        using var tv = new TempVault(); Init(tv);
        var (id, a, _) = Pending(tv);
        Assert.Equal(0, tv.Run("review", "approve", id, "--json").ExitCode);
        var path = Path.Combine(tv.Path, "PERSONA.md");
        var hash = Data(tv.Run("persona", "read", "--json")).GetProperty("auditEntry").GetString()!;
        File.WriteAllText(path, File.ReadAllText(path).Replace("audit_entry: " + hash, "audit_entry: " + id));
        var body = Path.Combine(tv.Path, a.Path);
        File.WriteAllText(body, File.ReadAllText(body).TrimEnd('\n') + "\nAn added paragraph.\n");

        tv.Run("lint", "--json");

        var kinds = Data(tv.Run("issues", "list", "--status", "open", "--json")).EnumerateArray()
            .Select(i => i.GetProperty("kind").GetString()).ToArray();
        Assert.Contains("persona-stale-evidence", kinds);
        Assert.DoesNotContain("persona-evidence", kinds);
    }

    private static string Regex(string path) => System.Text.RegularExpressions.Regex.Escape(path) + "@[0-9a-f]{64}";

    [Fact]
    public void ReviewReject_WithoutAReason_IsRefused()
    {
        using var tv = new TempVault(); Init(tv);
        var (id, _, _) = Pending(tv);

        var r = tv.Run("review", "reject", id, "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("reason-required", r.Envelope.Errors[0].Code);
        Assert.True(File.Exists(Path.Combine(tv.Path, "persona", "changeset.md")));
    }

    [Fact]
    public void ReviewReject_WithAReason_LeavesPersonaMdAlone_AndIsLogged()
    {
        using var tv = new TempVault(); Init(tv);
        var (id, _, _) = Pending(tv);

        var r = tv.Run("review", "reject", id, "--note", "too generic", "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("rejected", Data(r).GetProperty("changeset").GetProperty("status").GetString());
        Assert.False(File.Exists(Path.Combine(tv.Path, "PERSONA.md")));
        Assert.False(File.Exists(Path.Combine(tv.Path, "persona", "changeset.md")));
        Assert.Equal(JsonValueKind.Null, Data(tv.Run("persona", "distill", "--json")).GetProperty("pending").ValueKind);

        var line = AuditLines(tv)[^1];
        Assert.Equal("persona.reject", Segments(line)[1]);
        Assert.Equal(id, Segments(line)[2]);
        Assert.Equal("too generic", AuditField(line, "reason"));
        Assert.Equal("cli", AuditField(line, "decidedVia"));

        Assert.Equal(3, tv.Run("review", "approve", id, "--json").ExitCode);
    }

    [Fact]
    public void ReviewApprove_ChangeAndRemove_KeepEvidenceOfUntouchedLines()
    {
        using var tv = new TempVault(); Init(tv);
        var (first, a, b) = Pending(tv, Add("boundaries", "I keep client names private.", "craft-one", "craft-two"));
        Assert.Equal(0, tv.Run("review", "approve", first, "--json").ExitCode);

        var change = "{\"op\":\"change\",\"section\":\"values\",\"from\":\"I value careful craft.\",\"text\":\"I value craft over speed.\","
                     + "\"evidence\":[{\"page\":\"" + a.Id + "\"},{\"page\":\"" + b.Id + "\"}]}";
        var second = Data(Propose(tv, Changeset("sharper", new[] { change }))).GetProperty("changeset").GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("review", "approve", second, "--json").ExitCode);

        var persona = Data(tv.Run("persona", "read", "--json"));
        Assert.Equal("I value craft over speed.", persona.GetProperty("values")[0].GetString());
        Assert.Equal("I keep client names private.", persona.GetProperty("boundaries")[0].GetString());

        // The untouched boundary still has its evidence: lint stays clean.
        tv.Run("lint", "--json");
        Assert.DoesNotContain(Data(tv.Run("issues", "list", "--status", "open", "--json")).EnumerateArray(),
            i => i.GetProperty("kind").GetString() == "persona-evidence");

        var remove = "{\"op\":\"remove\",\"section\":\"boundaries\",\"from\":\"I keep client names private.\"}";
        var third = Data(Propose(tv, Changeset("not a boundary", new[] { remove }))).GetProperty("changeset").GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("review", "approve", third, "--json").ExitCode);
        Assert.Empty(Data(tv.Run("persona", "read", "--json")).GetProperty("boundaries").EnumerateArray());
        Assert.Equal("+0 ~0 -1", AuditField(AuditLines(tv)[^1], "lines"));
    }

    [Fact]
    public void Lint_PersonaLineWithoutEnoughSupport_IsAPersonaEvidenceError()
    {
        using var tv = new TempVault(); Init(tv);
        var (id, _, _) = Pending(tv);
        tv.Run("review", "approve", id, "--json");

        // A line slipped in by hand has no recorded evidence at all.
        var path = Path.Combine(tv.Path, "PERSONA.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("## Principles\n", "## Principles\n- I decide in writing.\n"));

        tv.Run("lint", "--json");
        var issue = Assert.Single(Data(tv.Run("issues", "list", "--status", "open", "--json")).EnumerateArray(),
            i => i.GetProperty("kind").GetString()!.StartsWith("persona-"));
        Assert.Equal("persona-evidence", issue.GetProperty("kind").GetString());
        Assert.Equal("PERSONA.md", issue.GetProperty("subject").GetString());
        Assert.Contains("I decide in writing.", issue.GetProperty("detail").GetString());
        Assert.Contains("two supporting pages", issue.GetProperty("detail").GetString());
    }
}
