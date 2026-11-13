using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Wiki.Cli;
using Wiki.Core;

namespace Wiki.Services;

// `wiki persona interview guide`: the guide the interviewing agent follows.
// `Source` is `shipped` or `vault`; `Version` is what each transcript entry
// records as `guide:` - the shipped version, or `sha256:<hex>` of the vault's
// override file. `Path` is set only for the override.
public sealed record InterviewGuideView(string Source, string Version, string? Path, string Text) : IHumanRenderable
{
    public string HumanSummary() => Text;
}

// `wiki persona interview record`: where the entry went. `Created` is true
// when this call started the transcript. `AnswerHash` is what owner-stated
// evidence pointing at this entry records.
public sealed record InterviewRecordResult(
    string Transcript,
    string Path,
    string Anchor,
    bool Created,
    string Section,
    string Guide,
    string RecordedVia,
    string AnswerHash) : IHumanRenderable
{
    public string HumanSummary() => $"recorded {Path}#{Anchor} ({Section})" + (Created ? ", new transcript " + Transcript : "");
}

// A candidate transcript as `wiki persona distill` lists it: every entry in
// full, since the pass needs the answers and their anchors to cite them.
public sealed record PersonaTranscriptView(string Id, string Path, PersonaTranscriptEntryView[] Entries);

public sealed record PersonaTranscriptEntryView(string Anchor, string Section, string? Question, string Answer, string Hash);

// The onboarding interview (issue #53, decided in #40). The CLI stays
// deterministic: an external agent conducts the interview, and these
// commands only serve the guide and record what was said.
public sealed class InterviewService
{
    // Bump together with Templates/interview-guide.txt.
    public const string ShippedGuideVersion = "1";
    public const string GuideOverrideRelPath = "persona/interview-guide.md";

    private readonly Func<DateTimeOffset> _now;

    public InterviewService(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public static InterviewGuideView Guide(Vault v)
    {
        var path = Path.Combine(v.Root, GuideOverrideRelPath);
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            return new InterviewGuideView("vault", "sha256:" + InterviewTranscript.AnswerHash(text), GuideOverrideRelPath, text);
        }
        return new InterviewGuideView("shipped", ShippedGuideVersion, null, Templates.Templates.InterviewGuide.Replace("\r\n", "\n"));
    }

    public InterviewRecordResult Record(Vault v, InvocationOrigin origin, string sectionWire, string? question,
        string answer, string? transcriptId)
    {
        if (PersonaSectionX.FromWire(sectionWire) is null)
            throw new ValidationException("invalid-persona-section",
                $"unknown section '{sectionWire}'; expected values, principles, tone-of-voice or boundaries");
        if (string.IsNullOrWhiteSpace(answer))
            throw new ValidationException("empty-answer", "the answer is empty; record what the owner said, word for word");
        if (question is not null && string.IsNullOrWhiteSpace(question))
            question = null;

        string id;
        bool created;
        int next;
        if (transcriptId is not null)
        {
            id = NormaliseId(transcriptId);
            var existing = Load(v, id)
                ?? throw new ValidationException("not-found",
                    $"no interview transcript '{id}' ({InterviewTranscript.RelPath(id)}); omit --transcript to start a new one");
            if (!existing.Candidate)
                throw new StateConflictException("transcript-finished",
                    $"interview transcript '{id}' is finished (a distillation pass considered it) and is never appended to; omit --transcript to start a new one");
            created = false;
            next = existing.Entries.Select(e => int.Parse(e.Anchor[1..], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max() + 1;
        }
        else
        {
            var date = _now().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var n = 1;
            while (File.Exists(FullPath(v, InterviewTranscript.IdFor(date, n))))
                n++;
            id = InterviewTranscript.IdFor(date, n);
            created = true;
            next = 1;
        }

        var via = origin.Transport == Transport.Cli ? "cli" : "mcp";
        var entry = new InterviewEntry(
            "a" + next.ToString(CultureInfo.InvariantCulture),
            sectionWire,
            question,
            answer,
            Guide(v).Version,
            _now().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            via,
            origin.Transport switch { Transport.Stdio => "stdio", Transport.Http => "http", _ => null },
            origin.Transport == Transport.Http ? origin.TokenUsed : null);

        // --- Validation complete ---

        var text = (created ? InterviewTranscript.Header(id) : "") + InterviewTranscript.FormatEntry(entry);
        AtomicFile.Append(FullPath(v, id), text);

        return new InterviewRecordResult(id, InterviewTranscript.RelPath(id), entry.Anchor, created, sectionWire,
            entry.Guide, via, InterviewTranscript.AnswerHash(answer));
    }

    // Accepts the id (`2026-11-13`, `2026-11-13-v2`) or its vault path.
    public static string NormaliseId(string input)
    {
        var s = input.Trim().Replace('\\', '/');
        if (s.StartsWith(InterviewTranscript.Dir + "/", StringComparison.Ordinal))
            s = s[(InterviewTranscript.Dir.Length + 1)..];
        if (s.EndsWith(".md", StringComparison.Ordinal))
            s = s[..^3];
        if (!InterviewTranscript.IsId(s))
            throw new ValidationException("invalid-transcript",
                $"'{input}' is not an interview transcript id; expected <yyyy-mm-dd> or <yyyy-mm-dd>-v<n>");
        return s;
    }

    public static bool LooksLikeTranscript(string input)
    {
        try { NormaliseId(input); return true; }
        catch (ValidationException) { return false; }
    }

    public static string FullPath(Vault v, string id) => Path.Combine(v.Root, InterviewTranscript.RelPath(id));

    public static InterviewTranscript? Load(Vault v, string id)
    {
        var path = FullPath(v, id);
        return File.Exists(path) ? InterviewTranscript.Parse(id, File.ReadAllText(path)) : null;
    }

    public static IEnumerable<InterviewTranscript> All(Vault v)
    {
        var dir = Path.Combine(v.Root, InterviewTranscript.Dir);
        if (!Directory.Exists(dir))
            yield break;
        var files = Directory.EnumerateFiles(dir, "*.md").ToList();
        files.Sort(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (InterviewTranscript.IsId(id))
                yield return InterviewTranscript.Parse(id, File.ReadAllText(file));
        }
    }

    public static PersonaTranscriptView[] Candidates(Vault v)
        => All(v).Where(t => t.Candidate)
            .Select(t => new PersonaTranscriptView(t.Id, InterviewTranscript.RelPath(t.Id),
                t.Entries.Select(e => new PersonaTranscriptEntryView(e.Anchor, e.Section, e.Question, e.Answer,
                    InterviewTranscript.AnswerHash(e.Answer))).ToArray()))
            .ToArray();

    // Drops the candidate flag: the transcript is finished from here on.
    // Returns whether there was a flag to drop.
    public static bool ClearCandidate(Vault v, string id)
    {
        var path = FullPath(v, id);
        if (!File.Exists(path))
            return false;
        var cleared = InterviewTranscript.WithoutCandidateFlag(File.ReadAllText(path));
        if (cleared is null)
            return false;
        AtomicFile.Write(path, cleared);
        return true;
    }
}
