using System.IO;
using System.Text.Json;
using Wiki.Core;
using Wiki.Json;

namespace Wiki.State;

// The .wiki/lint.json store (amendment D): the single timestamp `wiki lint`
// (Task 22) writes on every run - compared against a source's `integrated`
// timestamp by the `linted` ledger precondition (IngestService, Task 17,
// CheckLintPrecondition). Same Load/Save instance-state shape as
// Issues/Ledger/IdMap for consistency, even though the payload is a single
// scalar field.
//
// LintStateData (below) originally lived inline in IngestService.cs - Task
// 17 needed *something* to deserialize `.wiki/lint.json` into ahead of this
// store existing, and left a note that Task 22 "may extend/relocate" it. It
// moves here now that LintState owns the file; IngestService keeps its
// `using Wiki.State;` and references the type unqualified exactly as before,
// so the move is a pure relocation with no behavior change at that call site.
public sealed class LintState
{
    public string? LastRun { get; private set; }

    public void Load(Vault v)
    {
        LastRun = null;
        var path = PathOf(v);
        if (!File.Exists(path))
            return;

        try
        {
            var data = JsonSerializer.Deserialize(File.ReadAllText(path), WikiJsonContext.Default.LintStateData);
            LastRun = data?.LastRun;
        }
        catch (JsonException)
        {
            // Corrupt lint.json degrades to "no lint run recorded" rather than
            // throwing - IngestService's `linted` precondition only wants a
            // yes/no on "is there a fresh lint timestamp", and a bad file on
            // disk is exactly the "no" case, not a crash. (Preserves the
            // try/catch-to-null behavior CheckLintPrecondition had inline
            // before this type existed.)
        }
    }

    public void Save(Vault v, string utcIso)
    {
        LastRun = utcIso;
        var data = new LintStateData { LastRun = utcIso };
        var json = JsonSerializer.Serialize(data, WikiJsonContext.Default.LintStateData);
        AtomicFile.Write(PathOf(v), json);
    }

    private static string PathOf(Vault v) => Path.Combine(v.StateDir, "lint.json");
}

// Wire shape for `.wiki/lint.json`. Deliberately minimal - amendment D
// specifies exactly one field. Already registered in WikiJsonContext; moving
// its definition here doesn't change that registration.
public sealed class LintStateData
{
    public string? LastRun { get; set; }
}
