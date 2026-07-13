# LLM Wiki CLI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `wiki`, a single-binary CLI that owns every mutation to a markdown "LLM Wiki" vault so an LLM agent can author prose without ever touching the filesystem.

**Architecture:** One .NET 9 console app compiled to native AOT. All state is markdown files in a vault; `.wiki/*.json` is a rebuildable cache. Commands parse via System.CommandLine, render human output via Spectre.Console, and emit a stable `--json` envelope for the agent. Every mutation is: validate → write-temp-then-rename → append log → refresh derived caches. No rollback; forward-repair only.

**Tech Stack:** .NET 9 (`PublishAot=true`, single-file, self-contained), System.CommandLine (parsing), Spectre.Console (rendering), System.Text.Json source-generated contexts, hand-rolled ULID + YAML frontmatter + wikilink regex (AOT-safe, zero reflection), xUnit for tests.

## Global Constraints

Copied verbatim from `docs/spec.md`. Every task's requirements implicitly include this section.

- **AOT-clean, zero reflection at runtime.** No reflection-based YAML/JSON. System.Text.Json uses source-generated `JsonSerializerContext`. Frontmatter is hand-parsed. Fix every trim/AOT warning; do not suppress.
- **Filesystem is source of truth.** `.wiki/*.json` is a cache. `wiki reindex` rebuilds idmap byte-identically and recomputes structural ledger state; history (issue occurrences/first_seen/last_seen, ledger `--touched` audit, `.wiki/lint.json` timestamp, review shadow copies) is merge-preserved best-effort, never byte-identity-guaranteed (Appendix B, amendments A/D).
- **LLM writes prose, never files.** Page bodies arrive only via `--stdin` or `--body-file`, never as shell args. The CLI refuses any write path under `raw/` except `source add`, and any edit to `index.md`/`log.md` except by the CLI.
- **`--json` on every command.** Envelope: `{"ok": bool, "data": <any>, "errors": [{"code","message","path"}]}`, versioned. Exit codes: `0` success · `1` blocking validation (nothing written) · `2` environment/IO error · `3` state conflict (idempotent no-op reported).
- **Closed vocabulary.** Page types (`summary|entity|concept|overview`), source status (`active|retracted`), page status (`active|pending-review|needs-review|archived`), ledger states (`registered|summarized|integrated|linted`), issue kinds (fixed list §11), and frontmatter keys are fixed enums. Only category `id`s and page content are free.
- **IDs are ULIDs**, stored in frontmatter `id`, permanent. Filenames are slugs. Wikilinks: `[[slug]]` or `[[slug|display]]` only; standard markdown links for external URLs only.
- **Required frontmatter `summary`** on every wiki page (amendment B), supplied via `--summary`.
- **Every file the CLI writes** uses write-temp-then-rename within the same directory. Cross-file atomicity is explicitly not provided.
- **Timestamps** are UTC ISO-8601 (`2026-07-13T14:02:11Z`); dates are `YYYY-MM-DD`.
- **Vault resolution:** `--vault <path>` flag → `WIKI_VAULT` env → walk up from CWD for `wiki.yaml`.
- **Build matrix:** `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`, `linux-arm64`.
- **Local toolchain (this machine):** .NET 9 SDK is Homebrew `dotnet@9` (keg-only). `dotnet`, `DOTNET_ROOT`, and `LIBRARY_PATH` (openssl@3 + brotli lib dirs, required for the AOT native link on macOS) are exported from `~/.zshenv`, so every shell already has them — no per-command env setup needed. Verified: `net9.0` `PublishAot=true` builds and runs a native binary that uses `System.Security.Cryptography.SHA256`. Harmless "built for newer macOS version" linker warnings are expected; IL2xxx/IL3xxx trim/AOT warnings are not — fix those.

**Note on task fidelity:** Tasks 1-18 (foundation + M1 + core M2) carry complete TDD code because they set every pattern the rest reuse. Tasks 19+ specify exact files, interfaces, behavior, and representative test code; where the implementation is a mechanical repeat of an earlier task's pattern, that is stated rather than re-transcribed. Follow the referenced earlier task for the shape.

---

## File Structure

```
llm-wiki/
├── LlmWiki.sln
├── src/Wiki/
│   ├── Wiki.csproj                     # AOT config, System.CommandLine, Spectre.Console
│   ├── Program.cs                      # command tree wiring, top-level exception→exit-code
│   ├── Core/
│   │   ├── Ulid.cs                     # 26-char Crockford ULID gen + validate
│   │   ├── Frontmatter.cs             # closed-schema parse/serialize for source + wiki pages
│   │   ├── PageDoc.cs                  # frontmatter + body value type; round-trips a file
│   │   ├── VaultConfig.cs             # wiki.yaml load + validate
│   │   ├── Vault.cs                    # resolution, path model, dir constants
│   │   ├── AtomicFile.cs               # write-temp-then-rename, refuse-path guards
│   │   ├── Wikilinks.cs               # regex extraction (skip code fences)
│   │   ├── Slug.cs                     # title → kebab slug, collision suffixing
│   │   └── Enums.cs                    # PageType, PageStatus, SourceStatus, LedgerState, IssueKind
│   ├── State/
│   │   ├── IdMap.cs                    # .wiki/idmap.json  (id ↔ path)
│   │   ├── Ledger.cs                   # .wiki/ledger.json (per-source state machine)
│   │   ├── Issues.cs                   # .wiki/issues.json (lifecycle + occurrences)
│   │   ├── LintState.cs                # .wiki/lint.json  (last-lint timestamp)
│   │   └── ReviewShadow.cs             # .wiki/review/<id>.prev.md
│   ├── Docs/
│   │   ├── IndexFile.cs                # wiki/index.md generation
│   │   └── LogFile.cs                  # wiki/log.md append
│   ├── Services/
│   │   ├── PageService.cs              # upsert/show/list/rename/set-status/backlinks
│   │   ├── SourceService.cs            # add/list/show/impact/retract
│   │   ├── IngestService.cs            # status/advance/resume + precondition checks
│   │   ├── LintService.cs              # all advisory checks → Issues
│   │   ├── ReviewService.cs            # gate integration + list/approve/reject
│   │   ├── SchemaService.cs            # AGENTS.md section proposals
│   │   ├── SearchService.cs            # plain-text/regex over frontmatter+bodies
│   │   └── ReindexService.cs           # rebuild caches from markdown scan
│   ├── Json/
│   │   ├── Envelope.cs                 # {ok,data,errors} + WikiError
│   │   └── WikiJsonContext.cs          # [JsonSerializable] source-gen registrations
│   ├── Cli/
│   │   ├── OutputMode.cs               # --json vs Spectre rendering switch
│   │   └── Commands/*.cs               # one file per command group (init, page, source, ...)
│   └── Templates/
│       ├── agents-md.txt               # AGENTS.md scaffold (Appendix A)
│       └── wiki-yaml.txt               # wiki.yaml scaffold
└── tests/Wiki.Tests/
    ├── Wiki.Tests.csproj
    ├── Support/TempVault.cs            # spins a temp vault, runs the command tree in-proc
    ├── Support/CliResult.cs            # captures exit code + parsed envelope
    ├── Core/*.cs                       # unit tests per Core type
    ├── Commands/*.cs                   # per-command golden + behavior tests
    └── E2E/LifecycleTests.cs           # full init→ingest→lint→retract→repair + reindex property
```

---

## FOUNDATION

### Task 1: Solution scaffold, AOT project, JSON envelope, in-proc test harness

**Files:**
- Create: `LlmWiki.sln`, `src/Wiki/Wiki.csproj`, `src/Wiki/Program.cs`, `src/Wiki/Json/Envelope.cs`, `src/Wiki/Json/WikiJsonContext.cs`, `src/Wiki/Cli/OutputMode.cs`
- Create: `tests/Wiki.Tests/Wiki.Tests.csproj`, `tests/Wiki.Tests/Support/CliResult.cs`, `tests/Wiki.Tests/Support/TempVault.cs`
- Test: `tests/Wiki.Tests/Commands/EnvelopeTests.cs`

**Interfaces:**
- Produces: `Envelope { bool Ok; object? Data; WikiError[] Errors; }`; `WikiError { string Code; string Message; string? Path; }`; `record CliResult(int ExitCode, Envelope Envelope, string Stdout)`; `TempVault : IDisposable { string Path; CliResult Run(params string[] args); }`. `int App.Main(string[] args, TextWriter stdout, TextReader stdin)` — the entrypoint the harness calls in-proc.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Wiki.Tests/Commands/EnvelopeTests.cs
public class EnvelopeTests
{
    [Fact]
    public void UnknownCommand_EmitsErrorEnvelope_Exit1()
    {
        using var v = new TempVault();
        var r = v.Run("nonesuch", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.False(r.Envelope.Ok);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-command");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Wiki.Tests --filter EnvelopeTests`
Expected: FAIL to compile — `App`, `TempVault`, `CliResult` not defined.

- [ ] **Step 3: Write minimal implementation**

`src/Wiki/Wiki.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <AssemblyName>wiki</AssemblyName>
    <RootNamespace>Wiki</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.CommandLine" Version="2.0.0-*" />
    <PackageReference Include="Spectre.Console" Version="0.49.*" />
  </ItemGroup>
</Project>
```

`src/Wiki/Json/Envelope.cs`:
```csharp
namespace Wiki.Json;

public sealed class WikiError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Path { get; set; }
}

public sealed class Envelope
{
    public const int Version = 1;
    public int V { get; set; } = Version;
    public bool Ok { get; set; }
    public object? Data { get; set; }
    public WikiError[] Errors { get; set; } = System.Array.Empty<WikiError>();

    public static Envelope Success(object? data) => new() { Ok = true, Data = data };
    public static Envelope Failure(params WikiError[] errors) => new() { Ok = false, Errors = errors };
}
```

`src/Wiki/Json/WikiJsonContext.cs` (grows every task that adds a DTO):
```csharp
using System.Text.Json.Serialization;
namespace Wiki.Json;

// Compact single-line output: the envelope is a machine interface (one JSON blob per line,
// JSONL-friendly) and the test harness parses the last stdout line. Do NOT set WriteIndented.
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(WikiError))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
public partial class WikiJsonContext : JsonSerializerContext { }
```

`src/Wiki/Cli/OutputMode.cs`:
```csharp
namespace Wiki.Cli;
public static class OutputMode
{
    // Emit the envelope as JSON. Human rendering (Spectre) is added per-command later.
    public static void Emit(System.IO.TextWriter w, Wiki.Json.Envelope env)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(env,
            Wiki.Json.WikiJsonContext.Default.Envelope);
        w.WriteLine(json);
    }
}
```

`src/Wiki/Program.cs` — expose an in-proc `App.Main` so tests never shell out:
```csharp
using Wiki.Json;
using Wiki.Cli;
namespace Wiki;

public static class App
{
    // Real process entrypoint delegates here.
    public static int Main(string[] args) => Main(args, System.Console.Out, System.Console.In);

    public static int Main(string[] args, System.IO.TextWriter stdout, System.IO.TextReader stdin)
    {
        try
        {
            // Task 8+ replaces this stub with the System.CommandLine tree.
            var cmd = args.Length > 0 ? args[0] : "";
            var env = Envelope.Failure(new WikiError { Code = "unknown-command",
                Message = $"unknown command '{cmd}'" });
            OutputMode.Emit(stdout, env);
            return 1;
        }
        catch (System.Exception ex)
        {
            OutputMode.Emit(stdout, Envelope.Failure(new WikiError { Code = "io-error", Message = ex.Message }));
            return 2;
        }
    }
}
```

`tests/Wiki.Tests/Support/CliResult.cs` + `TempVault.cs`:
```csharp
using System.Text.Json;
namespace Wiki.Tests.Support;

public sealed record CliResult(int ExitCode, Wiki.Json.Envelope Envelope, string Stdout);

public sealed class TempVault : System.IDisposable
{
    public string Path { get; }
    public TempVault()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wiki-test-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path);
    }
    public CliResult Run(params string[] args)
    {
        var full = new System.Collections.Generic.List<string>(args);
        if (!full.Contains("--vault")) { full.Add("--vault"); full.Add(Path); }
        var sw = new System.IO.StringWriter();
        var exit = Wiki.App.Main(full.ToArray(), sw, new System.IO.StringReader(""));
        var line = sw.ToString().Trim().Split('\n')[^1];
        var env = JsonSerializer.Deserialize(line, Wiki.Json.WikiJsonContext.Default.Envelope)!;
        return new CliResult(exit, env, sw.ToString());
    }
    public CliResult RunStdin(string stdin, params string[] args)
    {
        var full = new System.Collections.Generic.List<string>(args);
        if (!full.Contains("--vault")) { full.Add("--vault"); full.Add(Path); }
        var sw = new System.IO.StringWriter();
        var exit = Wiki.App.Main(full.ToArray(), sw, new System.IO.StringReader(stdin));
        var line = sw.ToString().Trim().Split('\n')[^1];
        var env = JsonSerializer.Deserialize(line, Wiki.Json.WikiJsonContext.Default.Envelope)!;
        return new CliResult(exit, env, sw.ToString());
    }
    public void Dispose() { try { System.IO.Directory.Delete(Path, true); } catch { } }
}
```

`tests/Wiki.Tests/Wiki.Tests.csproj` references `src/Wiki/Wiki.csproj`, `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Wiki.Tests --filter EnvelopeTests`
Expected: PASS.

- [ ] **Step 5: Verify AOT publishes clean on the host**

Run: `dotnet publish src/Wiki -r osx-arm64 -c Release`
Expected: succeeds with zero `IL2xxx`/`IL3xxx` warnings. If any appear, fix now — do not suppress.

- [ ] **Step 6: Commit**

```bash
git add LlmWiki.sln src/ tests/
git commit -m "feat: AOT project skeleton, JSON envelope, in-proc test harness"
```

---

### Task 2: ULID generation and validation

**Files:**
- Create: `src/Wiki/Core/Ulid.cs`
- Test: `tests/Wiki.Tests/Core/UlidTests.cs`

**Interfaces:**
- Produces: `static class WikiUlid { string New(long unixMs, System.ReadOnlySpan<byte> random); bool IsValid(string s); }`. `New` takes time+randomness as params (deterministic, testable, and the spec bans ambient `Date.now`-style calls in generators used by golden tests — production callers pass `DateTimeOffset.UtcNow` + `RandomNumberGenerator`).

- [ ] **Step 1: Write the failing test**

```csharp
public class UlidTests
{
    [Fact]
    public void New_Is26CrockfordChars_MonotonicTimePrefix()
    {
        var rnd = new byte[10];
        var a = WikiUlid.New(0, rnd);
        var b = WikiUlid.New(1, rnd);
        Assert.Equal(26, a.Length);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{26}$", a);
        Assert.True(string.CompareOrdinal(a, b) < 0); // later time sorts later
    }

    [Theory]
    [InlineData("01J9ZKM3E8W1R2X3Y4Z5A6B7C8", true)]
    [InlineData("not-a-ulid", false)]
    [InlineData("01J9ZKM3E8W1R2X3Y4Z5A6B7CI", false)] // I is not in the alphabet
    public void IsValid_ChecksLengthAndAlphabet(string s, bool expected)
        => Assert.Equal(expected, WikiUlid.IsValid(s));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Wiki.Tests --filter UlidTests`
Expected: FAIL — `WikiUlid` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Wiki.Core;
public static class WikiUlid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32
    public static string New(long unixMs, System.ReadOnlySpan<byte> random)
    {
        System.Span<char> c = stackalloc char[26];
        long t = unixMs;
        for (int i = 9; i >= 0; i--) { c[i] = Alphabet[(int)(t & 31)]; t >>= 5; }
        // 80 bits of randomness → 16 base32 chars
        System.Span<byte> r = stackalloc byte[10];
        random[..10].CopyTo(r);
        int bit = 0;
        for (int i = 10; i < 26; i++)
        {
            int v = 0;
            for (int k = 0; k < 5; k++)
            {
                int b = bit + k;
                int val = (r[b / 8] >> (7 - b % 8)) & 1;
                v = (v << 1) | val;
            }
            c[i] = Alphabet[v]; bit += 5;
        }
        return new string(c);
    }
    public static bool IsValid(string s)
    {
        if (s is null || s.Length != 26) return false;
        foreach (var ch in s) if (Alphabet.IndexOf(ch) < 0) return false;
        return true;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Wiki.Tests --filter UlidTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Wiki/Core/Ulid.cs tests/Wiki.Tests/Core/UlidTests.cs
git commit -m "feat: AOT-safe ULID generate + validate"
```

---

### Task 3: Enums and slug

**Files:**
- Create: `src/Wiki/Core/Enums.cs`, `src/Wiki/Core/Slug.cs`
- Test: `tests/Wiki.Tests/Core/SlugTests.cs`, `tests/Wiki.Tests/Core/EnumsTests.cs`

**Interfaces:**
- Produces: enums `PageType {Summary,Entity,Concept,Overview}`, `PageStatus {Active,PendingReview,NeedsReview,Archived}`, `SourceStatus {Active,Retracted}`, `LedgerState {Registered,Summarized,Integrated,Linted}`, `IssueKind {...9 kinds...}` — each with `Parse(string)`/`ToWire()` producing the spec's kebab wire strings (`pending-review`, not `PendingReview`). `static class Slug { string From(string title); string Ensure(string slug, System.Func<string,bool> exists); }` — `Ensure` appends `-2`, `-3` on collision.

- [ ] **Step 1: Write the failing test**

```csharp
public class SlugTests
{
    [Theory]
    [InlineData("Contoso", "contoso")]
    [InlineData("Contoso platform review — 2026", "contoso-platform-review-2026")]
    [InlineData("  A/B  Test!! ", "a-b-test")]
    public void From_ProducesKebab(string title, string expected)
        => Assert.Equal(expected, Slug.From(title));

    [Fact]
    public void Ensure_SuffixesOnCollision()
    {
        var taken = new System.Collections.Generic.HashSet<string> { "contoso", "contoso-2" };
        Assert.Equal("contoso-3", Slug.Ensure("contoso", taken.Contains));
    }
}

public class EnumsTests
{
    [Fact] public void PageStatus_RoundTripsKebabWire()
    {
        Assert.Equal("pending-review", PageStatusX.ToWire(PageStatus.PendingReview));
        Assert.Equal(PageStatus.PendingReview, PageStatusX.Parse("pending-review"));
        Assert.Throws<Wiki.Core.ValidationException>(() => PageStatusX.Parse("bogus"));
    }
}
```

- [ ] **Step 2: Run** `dotnet test tests/Wiki.Tests --filter "SlugTests|EnumsTests"` → FAIL (undefined).

- [ ] **Step 3: Write minimal implementation.** `Enums.cs` defines the enums plus a static helper class per enum (`PageStatusX`, `PageTypeX`, etc.) with `ToWire`/`Parse` over a hardcoded string↔value table (no reflection). Also define `Core/ValidationException.cs` (`class ValidationException : System.Exception { public string Code; public string? Path; }`) — the single exception type all blocking validation throws; `App.Main` maps it to exit 1 + an error envelope. `Slug.From` lowercases, replaces any run of non-`[a-z0-9]` with `-`, trims leading/trailing `-`. `Slug.Ensure` loops appending `-N`.

- [ ] **Step 4: Run** the filter → PASS.

- [ ] **Step 5: Commit** `feat: closed-vocabulary enums, ValidationException, slug`.

---

### Task 4: Frontmatter parse/serialize (closed schema) and PageDoc

**Files:**
- Create: `src/Wiki/Core/Frontmatter.cs`, `src/Wiki/Core/PageDoc.cs`
- Test: `tests/Wiki.Tests/Core/FrontmatterTests.cs`

**Interfaces:**
- Produces:
  - `record PageDoc(PageFrontmatter Front, string Body)` with `string Serialize()` and `static PageDoc Parse(string fileText)`.
  - `class PageFrontmatter { string Id; PageType Type; string Title; PageStatus Status; string Created; string Updated; string Summary; string[] Sources; string[] Tags; }`
  - `class SourceFrontmatter { string Id; string Title; string Category; string Added; string Sha256; string Origin; SourceStatus Status; }` (type is fixed `source`).
  - Both parse via `Frontmatter.ReadBlock(text) → (Dictionary<string,string> scalars, Dictionary<string,string[]> lists, string body)` then a typed mapper that **rejects unknown keys and missing required keys** by throwing `ValidationException` with `code = "frontmatter-schema"`.

- [ ] **Step 1: Write the failing test**

```csharp
public class FrontmatterTests
{
    const string Valid = """
        ---
        id: 01J9ZKM3E8W1R2X3Y4Z5A6B7C8
        type: entity
        title: "Contoso"
        status: active
        created: 2026-07-13
        updated: 2026-07-13
        summary: "The vendor under review"
        sources: [01J9ZKM1E8W1R2X3Y4Z5A6B7C8]
        tags: []
        ---
        Body about [[contoso-deal]].
        """;

    [Fact]
    public void Parse_RoundTrips_BytePreservingBody()
    {
        var doc = PageDoc.Parse(Valid);
        Assert.Equal(PageType.Entity, doc.Front.Type);
        Assert.Single(doc.Front.Sources);
        Assert.Contains("[[contoso-deal]]", doc.Body);
        Assert.Equal(Valid.Trim(), doc.Serialize().Trim()); // stable serialization
    }

    [Fact]
    public void Parse_UnknownKey_Throws()
    {
        var bad = Valid.Replace("tags: []", "tags: []\nbogus: 1");
        var ex = Assert.Throws<ValidationException>(() => PageDoc.Parse(bad));
        Assert.Equal("frontmatter-schema", ex.Code);
    }

    [Fact]
    public void Parse_MissingRequiredSummary_Throws()
    {
        var bad = Valid.Replace("summary: \"The vendor under review\"\n", "");
        Assert.Throws<ValidationException>(() => PageDoc.Parse(bad));
    }
}
```

- [ ] **Step 2: Run** `--filter FrontmatterTests` → FAIL.

- [ ] **Step 3: Write minimal implementation.** `Frontmatter.ReadBlock`: require the file to start with `---\n`, read to the next `---` line, parse each `key: value` line. Values: quoted strings unquote; `[a, b]` becomes a list (empty `[]` → empty array); bare scalars stay strings. Serialize back in a **fixed key order** (id, type, title, status, created, updated, summary, sources, tags) so round-trips and golden files are stable; always quote `title`/`summary`, always render `sources`/`tags` as `[...]`. The typed mappers hold the allowed-key set and required-key set as `static readonly string[]`; unknown → throw, missing-required → throw. `SourceFrontmatter` mapper enforces `type: source` and its own key set. Validate `id` via `WikiUlid.IsValid`, `type`/`status` via the enum `Parse`.

- [ ] **Step 4: Run** → PASS.

- [ ] **Step 5: Commit** `feat: closed-schema frontmatter parse/serialize + PageDoc`.

---

### Task 5: Vault resolution, path model, atomic writes, refuse-path guards

**Files:**
- Create: `src/Wiki/Core/Vault.cs`, `src/Wiki/Core/AtomicFile.cs`
- Test: `tests/Wiki.Tests/Core/VaultTests.cs`, `tests/Wiki.Tests/Core/AtomicFileTests.cs`

**Interfaces:**
- Produces:
  - `class Vault { string Root; string RawDir; string WikiDir; string StateDir; string ConfigPath; string IndexPath; string LogPath; string AgentsPath; static Vault Resolve(string? flag, System.Func<string,string?> env, string cwd); string PageDir(PageType t); }`
  - `static class AtomicFile { void Write(string path, string content); void GuardWritable(Vault v, string path); }` — `GuardWritable` throws `ValidationException code=protected-path` for any path under `raw/` or equal to `index.md`/`log.md` (callers that are allowed pass through a dedicated internal method, not `GuardWritable`).

- [ ] **Step 1: Write the failing test**

```csharp
public class AtomicFileTests
{
    [Fact]
    public void Write_CreatesFile_AndIsAtomicViaTempRename()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var p = System.IO.Path.Combine(dir, "x.md");
        AtomicFile.Write(p, "hello");
        Assert.Equal("hello", System.IO.File.ReadAllText(p));
        Assert.Empty(System.IO.Directory.GetFiles(dir, "*.tmp")); // temp cleaned up
    }

    [Fact]
    public void GuardWritable_RejectsRawAndGeneratedDocs()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        var v = Wiki.Core.Vault.Resolve(tv.Path, _ => null, tv.Path);
        Assert.Throws<ValidationException>(() => AtomicFile.GuardWritable(v, System.IO.Path.Combine(v.RawDir, "a.md")));
        Assert.Throws<ValidationException>(() => AtomicFile.GuardWritable(v, v.IndexPath));
    }
}

public class VaultTests
{
    [Fact]
    public void Resolve_WalksUpForWikiYaml()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        System.IO.File.WriteAllText(System.IO.Path.Combine(tv.Path, "wiki.yaml"), "version: 1");
        var nested = System.IO.Path.Combine(tv.Path, "a", "b");
        System.IO.Directory.CreateDirectory(nested);
        var v = Wiki.Core.Vault.Resolve(null, _ => null, nested);
        Assert.Equal(tv.Path, v.Root);
    }
}
```

- [ ] **Step 2: Run** `--filter "AtomicFileTests|VaultTests"` → FAIL.

- [ ] **Step 3: Write minimal implementation.** `Vault.Resolve`: flag wins; else env var; else walk up from `cwd` until a dir containing `wiki.yaml` (throw `ValidationException code=no-vault` if none). Path properties compose from `Root`. `PageDir` maps `Summary→wiki/summaries`, `Entity→wiki/entities`, `Concept→wiki/concepts`, `Overview→wiki` (file `overview.md`). `AtomicFile.Write`: write to `path + ".<guid>.tmp"` in the same dir, `File.Move(tmp, path, overwrite:true)`, delete tmp on failure. `GuardWritable`: normalize both paths, throw if under `RawDir` or equals `IndexPath`/`LogPath`.

- [ ] **Step 4: Run** → PASS.

- [ ] **Step 5: Commit** `feat: vault resolution, path model, atomic writes, refuse-path guards`.

---

### Task 6: VaultConfig (wiki.yaml) load + validate

**Files:**
- Create: `src/Wiki/Core/VaultConfig.cs`
- Test: `tests/Wiki.Tests/Core/VaultConfigTests.cs`

**Interfaces:**
- Produces: `class VaultConfig { int Version; string Name; bool ReviewGate; List<Category> Categories; int StalenessDays; int MaxPageLines; static VaultConfig Load(string yamlPath); bool HasCategory(string id); }`; `record Category(string Id, string Description)`. Load throws `ValidationException code=config` on: version≠1, duplicate/non-kebab category id, missing required keys.

- [ ] **Step 1: Write the failing test**

```csharp
public class VaultConfigTests
{
    const string Yaml = """
        version: 1
        name: "work"
        review_gate: true
        categories:
          - id: meeting-transcript
            description: "Customer meeting transcripts"
          - id: article
            description: "Web articles"
        lint:
          staleness_days: 90
          max_page_lines: 400
        """;

    [Fact] public void Load_ParsesCategoriesAndFlags()
    {
        var p = WriteTmp(Yaml);
        var c = VaultConfig.Load(p);
        Assert.True(c.ReviewGate);
        Assert.True(c.HasCategory("article"));
        Assert.Equal(90, c.StalenessDays);
    }

    [Fact] public void Load_RejectsNonKebabCategory()
    {
        var p = WriteTmp(Yaml.Replace("meeting-transcript", "Meeting_Transcript"));
        Assert.Throws<ValidationException>(() => VaultConfig.Load(p));
    }
}
```
(`WriteTmp` writes to a temp file and returns the path.)

- [ ] **Step 2: Run** `--filter VaultConfigTests` → FAIL.

- [ ] **Step 3: Write minimal implementation.** Hand-parse this small, known YAML shape line by line: top-level scalars, a `categories:` list of `- id:` / `description:` pairs, a `lint:` block with two ints. No YamlDotNet. Validate kebab via regex `^[a-z0-9]+(-[a-z0-9]+)*$`, uniqueness via a set.

- [ ] **Step 4: Run** → PASS.

- [ ] **Step 5: Commit** `feat: wiki.yaml load + validation`.

---

### Task 7: Wikilink extraction

**Files:**
- Create: `src/Wiki/Core/Wikilinks.cs`
- Test: `tests/Wiki.Tests/Core/WikilinksTests.cs`

**Interfaces:**
- Produces: `static class Wikilinks { IReadOnlyList<Link> Extract(string body); string Rewrite(string body, string oldSlug, string newSlug); }`; `record Link(string Target, string? Display)`. Skips fenced code blocks (```). Regex `\[\[([^\]|]+)(\|[^\]]+)?\]\]`.

- [ ] **Step 1: Write the failing test**

```csharp
public class WikilinksTests
{
    [Fact] public void Extract_FindsTargets_IgnoresCodeFences_HandlesDisplay()
    {
        var body = "See [[contoso]] and [[deal-x|the deal]].\n```\n[[not-a-link]]\n```\n";
        var links = Wikilinks.Extract(body);
        Assert.Equal(2, links.Count);
        Assert.Equal("contoso", links[0].Target);
        Assert.Equal("the deal", links[1].Display);
    }

    [Fact] public void Rewrite_RenamesTargetPreservingDisplay()
    {
        var body = "[[contoso]] and [[contoso|Contoso Inc]]";
        Assert.Equal("[[acme]] and [[acme|Contoso Inc]]", Wikilinks.Rewrite(body, "contoso", "acme"));
    }
}
```

- [ ] **Step 2: Run** `--filter WikilinksTests` → FAIL.

- [ ] **Step 3: Write minimal implementation.** Split body into lines, track a `inFence` toggle on lines starting with ```` ``` ````, run the regex only on non-fence lines. `Rewrite` matches links whose target equals `oldSlug` and swaps the target segment.

- [ ] **Step 4: Run** → PASS.

- [ ] **Step 5: Commit** `feat: wikilink extraction + rename rewrite`.

---

## MILESTONE 1 — Skeleton (wiki usable by hand)

### Task 8: `wiki init` + command tree wiring + `--vault`/`--json` globals

**Files:**
- Create: `src/Wiki/Cli/Commands/InitCommand.cs`, `src/Wiki/Templates/agents-md.txt`, `src/Wiki/Templates/wiki-yaml.txt`; rewrite `src/Wiki/Program.cs` to build the System.CommandLine root with global `--vault` and `--json` options and a `CommandContext` passed to handlers.
- Test: `tests/Wiki.Tests/Commands/InitTests.cs`

**Interfaces:**
- Produces: `class CommandContext { string? VaultFlag; bool Json; TextWriter Out; TextReader In; Vault ResolveVault(); VaultConfig LoadConfig(); void EmitOk(object? data); }` (EmitOk renders JSON when `Json`, else Spectre). `wiki init <path> [--name X] [--review-gate]` scaffolds: `wiki.yaml`, `AGENTS.md`, `raw/`, `raw/assets/`, `wiki/summaries|entities|concepts`, empty `wiki/index.md`, empty `wiki/log.md`, `.wiki/`. Templates embed via `[EmbeddedResource]` or `File.ReadAllText` of copied-to-output `.txt` (use `<EmbeddedResource>` + a source-gen-free `GetManifestResourceStream`).

- [ ] **Step 1: Write the failing test**

```csharp
public class InitTests
{
    [Fact] public void Init_ScaffoldsVault_Idempotently()
    {
        using var tv = new TempVault();
        var r = tv.Run("init", tv.Path, "--name", "work", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki.yaml")));
        Assert.True(File.Exists(Path.Combine(tv.Path, "AGENTS.md")));
        Assert.True(Directory.Exists(Path.Combine(tv.Path, "wiki", "entities")));
        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki", "index.md")));
        // re-init on existing vault is a state conflict, not a crash
        var r2 = tv.Run("init", tv.Path, "--json");
        Assert.Equal(3, r2.ExitCode);
    }
}
```

- [ ] **Step 2: Run** `--filter InitTests` → FAIL.

- [ ] **Step 3: Write minimal implementation.** Replace `Program.cs` stub with a `RootCommand` that adds global options and subcommands (start with `init`; later tasks append their command groups to a shared `RootCommand` builder). Map `ValidationException`→exit 1 envelope, IO→exit 2, a new `StateConflictException`→exit 3. `init` refuses if `wiki.yaml` already exists (throw StateConflict). Write templates verbatim; `wiki.yaml` from `wiki-yaml.txt` with `{{name}}`/`{{review_gate}}` substituted; `AGENTS.md` from Appendix A of the spec verbatim.

- [ ] **Step 4: Run** → PASS. Also re-run the Task-1 `EnvelopeTests` to confirm the tree still returns `unknown-command` for garbage.

- [ ] **Step 5: Commit** `feat: wiki init + command tree + global options`.

---

### Task 9: IdMap cache

**Files:**
- Create: `src/Wiki/State/IdMap.cs`; add `IdMap` DTO to `WikiJsonContext`.
- Test: `tests/Wiki.Tests/State/IdMapTests.cs`

**Interfaces:**
- Produces: `class IdMap { void Load(Vault v); string? PathFor(string id); string? IdFor(string relPath); void Put(string id, string relPath); void Remove(string id); void Save(Vault v); IReadOnlyDictionary<string,string> All; }`. Serialized as `{ "<id>": "<forward-slash relpath>" }`. Paths normalized to `/`.

- [ ] **Step 1: Write the failing test** — put two ids, save, reload into a fresh `IdMap`, assert `PathFor` and reverse `IdFor` resolve; assert stored paths use `/`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** with a `Dictionary<string,string>` + source-gen JSON (register `Dictionary<string,string>` already in context). Normalize `\` → `/` on put.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: idmap cache`.

---

### Task 10: LogFile append

**Files:**
- Create: `src/Wiki/Docs/LogFile.cs`
- Test: `tests/Wiki.Tests/Docs/LogFileTests.cs`

**Interfaces:**
- Produces: `static class LogFile { void Append(Vault v, string utcIso, string op, string subject, string detail); }` writing `## [<utcIso>] <op> | <subject> | <detail>\n`. Uses the internal allowed-write path (bypasses `GuardWritable`).

- [ ] **Step 1: Write the failing test** — append two lines, assert file contains both in order and the exact `## [ts] op | subj | detail` shape.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** append via `File.AppendAllText` (append is safe without temp-rename; note this in a comment).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: append-only log.md`.

---

### Task 11: IndexFile generation

**Files:**
- Create: `src/Wiki/Docs/IndexFile.cs`
- Test: `tests/Wiki.Tests/Docs/IndexFileTests.cs`

**Interfaces:**
- Produces: `static class IndexFile { void Regenerate(Vault v, IEnumerable<PageFrontmatter> pages); string Render(IEnumerable<PageFrontmatter> pages); }`. Groups by type, one line: `- [[slug]] — <title> — <summary> (sources: N)`; excludes `archived`; includes `pending-review` (amendment E) with a ` [pending-review]` marker. Deterministic ordering: type order (overview, concept, entity, summary), then title asc.

- [ ] **Step 1: Write the failing test** — feed three fake frontmatters (one archived), assert archived excluded, pending marked, grouping headers present, ordering stable. Golden-compare the whole string.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** the renderer; slug comes from idmap/path (pass slug alongside frontmatter — extend signature to `IEnumerable<(string Slug, PageFrontmatter Front)>`).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: index.md routing-file generation`.

---

### Task 12: `wiki page upsert` — create path + blocking validation

**Files:**
- Create: `src/Wiki/Services/PageService.cs`, `src/Wiki/Cli/Commands/PageCommand.cs`; add page DTOs to `WikiJsonContext`.
- Test: `tests/Wiki.Tests/Commands/PageUpsertCreateTests.cs`

**Interfaces:**
- Produces: `class PageService { UpsertResult Upsert(Vault v, VaultConfig cfg, UpsertRequest req); }`; `record UpsertRequest(PageType Type, string Title, string? Id, string Summary, string[] Sources, string[] Tags, string Body, bool AllowDangling)`; `record UpsertResult(string Id, string Slug, string Path, string Status, string[] DanglingFiled)`. CLI: `wiki page upsert --type <t> --title "…" --summary "…" [--id <id>] [--sources a,b] [--tags x,y] [--allow-dangling] --stdin`. Body via stdin/`--body-file`.
- Blocking validations enforced here (spec §11): frontmatter schema; missing `--summary` (`code=summary-required`); unknown source id in `--sources` (`code=unknown-source`, check idmap for a `source` id); dangling wikilink not in same batch and not `--allow-dangling` (`code=dangling-link`, error lists each); duplicate title within type case-insensitive without `--id` (`code=duplicate-title`).

- [ ] **Step 1: Write the failing test**

```csharp
public class PageUpsertCreateTests
{
    CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t");

    [Fact] public void Create_WritesFile_UpdatesIndexAndIdmap_LogsOp()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.RunStdin("Body text.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "The vendor", "--json");
        Assert.Equal(0, r.ExitCode);
        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        Assert.True(File.Exists(file));
        Assert.Contains("[[contoso]]", File.ReadAllText(Path.Combine(tv.Path, "wiki", "index.md")));
        Assert.Contains("upsert", File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md")));
    }

    [Fact] public void Create_MissingSummary_Rejected_NothingWritten()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.RunStdin("Body", "page", "upsert", "--type", "entity", "--title", "X", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "summary-required");
        Assert.False(File.Exists(Path.Combine(tv.Path, "wiki", "entities", "x.md")));
    }

    [Fact] public void Create_DanglingLink_Rejected_UnlessAllowed()
    {
        using var tv = new TempVault(); Init(tv);
        var bad = tv.RunStdin("See [[ghost]].", "page", "upsert", "--type", "concept",
            "--title", "T", "--summary", "s", "--json");
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains(bad.Envelope.Errors, e => e.Code == "dangling-link");
        var ok = tv.RunStdin("See [[ghost]].", "page", "upsert", "--type", "concept",
            "--title", "T", "--summary", "s", "--allow-dangling", "--json");
        Assert.Equal(0, ok.ExitCode); // filed as issue in M3; for now just permitted
    }
}
```

- [ ] **Step 2: Run** `--filter PageUpsertCreateTests` → FAIL.

- [ ] **Step 3: Write minimal implementation.** No `--id` → create: generate ULID (prod passes `UtcNow`+RNG), slug from title via `Slug.Ensure` against existing slugs in the target dir, set `created=updated=today`, `status` = `active` (review gate handled in M3 Task 25 — for now always active), assemble `PageFrontmatter`, run validations, `AtomicFile.Write` the `PageDoc`, `IdMap.Put`+`Save`, regenerate index from a fresh scan of all page dirs, `LogFile.Append`. Dangling check: extract links, a target is satisfied if a page with that slug exists (idmap/dir scan) or is created in this call; else error unless `--allow-dangling`. `EmitOk(new UpsertResult(...))`.

- [ ] **Step 4: Run** → PASS.

- [ ] **Step 5: Commit** `feat: page upsert (create) with blocking validation, index+idmap+log`.

---

### Task 13: `wiki page upsert` — update path (`--id`)

**Files:**
- Modify: `src/Wiki/Services/PageService.cs`
- Test: `tests/Wiki.Tests/Commands/PageUpsertUpdateTests.cs`

**Interfaces:**
- Consumes: `PageService.Upsert` from Task 12.
- Produces: same `UpsertResult`. With `--id`: load existing page by idmap, preserve `id`/`created`, set `updated=today`, replace body + `summary` + `sources` + `tags` from the request, re-validate, rewrite file. Slug/filename unchanged on update. `code=unknown-id` if the id isn't in idmap.

- [ ] **Step 1: Write the failing test** — create a page, capture its id, upsert with `--id` and a new body+summary, assert file body changed, `created` unchanged, `updated` today, index summary line updated.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** the `--id` branch in `Upsert`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: page upsert (full-body update)`.

---

### Task 14: `wiki page show` / `wiki page list`

**Files:**
- Modify: `src/Wiki/Services/PageService.cs`, `src/Wiki/Cli/Commands/PageCommand.cs`
- Test: `tests/Wiki.Tests/Commands/PageShowListTests.cs`

**Interfaces:**
- Produces: `PageView Show(Vault v, string idOrName, bool frontmatterOnly)`; `IReadOnlyList<PageSummary> List(Vault v, PageType? type, PageStatus? status, bool orphansOnly)`. `wiki page show <id|name> [--frontmatter-only]`, `wiki page list [--type] [--status] [--orphans]`. `--orphans` needs backlinks (Task 19) — for now implement list without `--orphans`, add the flag as a no-op error `code=not-implemented` until Task 19, OR fold orphan support into Task 19. Choose: **defer `--orphans` to Task 19**; list here supports `--type`/`--status`.

- [ ] **Step 1: Write the failing test** — create two pages of different types; `list --type entity` returns one; `show <slug>` returns frontmatter+body; `show <id>` resolves same; `show --frontmatter-only` omits body.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** show (resolve id via idmap, name via slug→path) and list (scan dirs, filter). Human rendering: Spectre table for list, panel for show.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: page show + list`.

---

### Task 15: `wiki reindex` (idmap + structural rebuild) + property test

**Files:**
- Create: `src/Wiki/Services/ReindexService.cs`, `src/Wiki/Cli/Commands/ReindexCommand.cs`
- Test: `tests/Wiki.Tests/Commands/ReindexTests.cs`

**Interfaces:**
- Produces: `class ReindexService { ReindexReport Rebuild(Vault v); }` — scans `raw/` + `wiki/` for frontmatter, rewrites `idmap.json` byte-identically, regenerates `index.md`, and (once ledger exists in M2) recomputes structural ledger state merge-preserving history. In M1 it rebuilds idmap + index only.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] public void Reindex_ReproducesIdmapByteIdentically()
{
    using var tv = new TempVault(); tv.Run("init", tv.Path, "--name", "t");
    tv.RunStdin("a", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s");
    tv.RunStdin("b", "page", "upsert", "--type", "concept", "--title", "Deal", "--summary", "s");
    var before = File.ReadAllText(Path.Combine(tv.Path, ".wiki", "idmap.json"));
    File.Delete(Path.Combine(tv.Path, ".wiki", "idmap.json"));
    var r = tv.Run("reindex", "--json");
    Assert.Equal(0, r.ExitCode);
    var after = File.ReadAllText(Path.Combine(tv.Path, ".wiki", "idmap.json"));
    Assert.Equal(before, after); // byte-identical (amendment A)
}
```

- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** the scan+rebuild. Deterministic ordering of idmap keys (sort by id) so byte-identity holds regardless of filesystem enumeration order — apply the same sort in Task 9's `Save`.
- [ ] **Step 4: Run** → PASS. **M1 DONE: the wiki is usable by hand.**
- [ ] **Step 5: Commit** `feat: wiki reindex (idmap+index) with byte-identity property`.

---

## MILESTONE 2 — Lifecycle

### Task 16: `wiki source add` (copy, sha256, dedup, ledger register)

**Files:**
- Create: `src/Wiki/Services/SourceService.cs`, `src/Wiki/State/Ledger.cs`, `src/Wiki/Cli/Commands/SourceCommand.cs`
- Test: `tests/Wiki.Tests/Commands/SourceAddTests.cs`

**Interfaces:**
- Produces:
  - `class Ledger { LedgerEntry? Get(string sourceId); void Register(string sourceId); void Advance(string sourceId, LedgerState to, string[] touched, string utcIso); IReadOnlyList<LedgerEntry> All(); void Load(Vault v); void Save(Vault v); }`; `class LedgerEntry { string SourceId; LedgerState State; string[] Touched; string? IntegratedAt; }`.
  - `class SourceService { SourceAddResult Add(Vault v, VaultConfig cfg, string file, string category, string title, string? origin); }`.
- CLI: `wiki source add <file> --category <id> --title "…" [--origin "…"]`. Blocking: unknown category (`code=unknown-category`, message says add it first); duplicate sha256 (`code=duplicate-source`, lists existing id).

- [ ] **Step 1: Write the failing test**

```csharp
public class SourceAddTests
{
    [Fact] public void Add_CopiesToRaw_WritesFrontmatter_RegistersLedger()
    {
        using var tv = new TempVault(); tv.Run("init", tv.Path, "--name", "t");
        var src = Path.Combine(tv.Path, "input.md"); File.WriteAllText(src, "# transcript\nhello");
        var r = tv.Run("source", "add", src, "--category", "meeting-transcript",
            "--title", "Contoso mtg", "--json");
        Assert.Equal(0, r.ExitCode);
        var raws = Directory.GetFiles(Path.Combine(tv.Path, "raw"), "*.md");
        Assert.Single(raws);
        Assert.Contains("type: source", File.ReadAllText(raws[0]));
        // ledger shows registered
        var st = tv.Run("ingest", "status", "--json"); // Task 17 — may fail until then
    }

    [Fact] public void Add_UnknownCategory_Rejected()
    {
        using var tv = new TempVault(); tv.Run("init", tv.Path, "--name", "t");
        var src = Path.Combine(tv.Path, "i.md"); File.WriteAllText(src, "x");
        var r = tv.Run("source", "add", src, "--category", "nope", "--title", "T", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-category");
    }
}
```
(The `ingest status` line is illustrative; keep the ledger assertion by reading `.wiki/ledger.json` directly until Task 17.)

- [ ] **Step 2: Run** `--filter SourceAddTests` → FAIL.
- [ ] **Step 3: Implement.** Category check against `cfg`. Compute sha256 of file content; scan existing source frontmatters for a matching hash → dedup error. Generate ULID, copy file to `raw/<id>.md` **with a prepended source frontmatter block** (category, added=today, sha256, origin default `"manual"`, status active). `Ledger.Register`. Log. Note: the raw file gets frontmatter prepended — that's the one write under `raw/` the CLI performs, via the internal allowed-write method, not `GuardWritable`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: source add with sha256 dedup + ledger registration`.

---

### Task 17: Ledger commands — `ingest status/advance/resume`

**Files:**
- Create: `src/Wiki/Services/IngestService.cs`, `src/Wiki/Cli/Commands/IngestCommand.cs`
- Test: `tests/Wiki.Tests/Commands/IngestTests.cs`

**Interfaces:**
- Produces: `class IngestService { IReadOnlyList<LedgerEntry> Status(Vault v, string? sourceId); void Advance(Vault v, VaultConfig cfg, string sourceId, LedgerState to, string[] touched); ResumePlan Resume(Vault v, string sourceId); }`; `record ResumePlan(string SourceId, LedgerState Current, string[] RemainingStates, string[] ExpectedArtifacts)`. CLI per spec §8.
- Precondition checks on `Advance` (spec §10): `summarized` requires a `summary` page whose `sources` include the id (`code=precondition-summary`); `integrated` records `--touched` and verifies index consistency; `linted` requires `.wiki/lint.json` timestamp newer than the entry's `integratedAt` (`code=precondition-lint`).

- [ ] **Step 1: Write the failing test**

```csharp
public class IngestTests
{
    (TempVault, string) Seeded()
    {
        var tv = new TempVault(); tv.Run("init", tv.Path, "--name", "t");
        var src = Path.Combine(tv.Path, "i.md"); File.WriteAllText(src, "hello");
        var add = tv.Run("source", "add", src, "--category", "meeting-transcript", "--title", "M", "--json");
        var id = // read source id from envelope data
            ((System.Text.Json.JsonElement)add.Envelope.Data!).GetProperty("id").GetString()!;
        return (tv, id);
    }

    [Fact] public void Advance_ToSummarized_RequiresSummaryPage()
    {
        var (tv, id) = Seeded();
        var early = tv.Run("ingest", "advance", id, "--to", "summarized", "--json");
        Assert.Equal(1, early.ExitCode);
        Assert.Contains(early.Envelope.Errors, e => e.Code == "precondition-summary");
        tv.RunStdin("Summary body", "page", "upsert", "--type", "summary",
            "--title", "M summary", "--summary", "s", "--sources", id, "--json");
        var ok = tv.Run("ingest", "advance", id, "--to", "summarized", "--json");
        Assert.Equal(0, ok.ExitCode);
        tv.Dispose();
    }

    [Fact] public void Resume_ListsRemainingStates()
    {
        var (tv, id) = Seeded();
        var r = tv.Run("ingest", "resume", id, "--json");
        var data = (System.Text.Json.JsonElement)r.Envelope.Data!;
        Assert.Equal("registered", data.GetProperty("current").GetString());
        Assert.Contains("summarized", data.GetProperty("remainingStates").EnumerateArray()
            .Select(x => x.GetString()));
        tv.Dispose();
    }
}
```

- [ ] **Step 2: Run** `--filter IngestTests` → FAIL.
- [ ] **Step 3: Implement.** `Status` with no id returns all entries not in `linted`. `Advance` enforces the precondition table, updates the entry, logs. `Resume` computes remaining states from current position and names the expected artifact per state (summary page; touched entity/concept pages; a lint run). Requires reading `--touched` (comma split). Add `--touched` recording on `integrated`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: ingest status/advance/resume with ledger preconditions`.

---

### Task 18: `wiki search`

**Files:**
- Create: `src/Wiki/Services/SearchService.cs`, `src/Wiki/Cli/Commands/SearchCommand.cs`
- Test: `tests/Wiki.Tests/Commands/SearchTests.cs`

**Interfaces:**
- Produces: `class SearchService { IReadOnlyList<Hit> Search(Vault v, string terms, PageType? type, int limit); }`; `record Hit(string Id, string Path, string Title, int Line, string MatchLine)`. Plain-text (default) or regex; searches frontmatter + body; **never returns full bodies** — only the matching line. CLI: `wiki search <terms> [--type] [--limit N] [--regex]`.

- [ ] **Step 1: Write the failing test** — create two pages, one containing "platform"; `search platform` returns one hit with the matching line and correct 1-based line number; `--limit` caps results.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement.** Scan page files line by line, case-insensitive contains (or `Regex` when `--regex`), collect hits, cap at limit. Return title from frontmatter, id from idmap.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: plain/regex search returning match lines only`.

---

### Task 19: `wiki page backlinks`, `wiki page list --orphans`, `wiki index show`

**Files:**
- Modify: `src/Wiki/Services/PageService.cs`; Create `src/Wiki/Cli/Commands/IndexCommand.cs`
- Test: `tests/Wiki.Tests/Commands/BacklinksTests.cs`

**Interfaces:**
- Produces: `IReadOnlyList<string> Backlinks(Vault v, string idOrName)` (slugs of pages whose body links to the target); wire `list --orphans` to "active page with zero backlinks"; `IndexShow(Vault v, PageType?)` returns the index entries as structured JSON (same data as `index.md`, without a file read for the agent).
- Consumes: `Wikilinks.Extract` (Task 7), `PageService.List` (Task 14).

- [ ] **Step 1: Write the failing test** — page A links to B; `backlinks B` returns `[A]`; a third page C with no inbound links shows under `list --orphans`; `index show --type entity --json` returns entries with slug/title/summary/sources-count.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** by building an inbound-link map from a full body scan (extract links per page, invert). `--orphans` filters active pages absent from the map's value set. `index show` reuses the Task 11 render model but emits JSON.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: backlinks, orphan listing, index show (json routing)`.

---

### Task 20: `wiki page rename`, `set-status`; source `list/show/impact`

**Files:**
- Modify: `src/Wiki/Services/PageService.cs`, `src/Wiki/Services/SourceService.cs`, command files.
- Test: `tests/Wiki.Tests/Commands/RenameTests.cs`, `tests/Wiki.Tests/Commands/SourceQueryTests.cs`

**Interfaces:**
- Produces: `RenameResult Rename(Vault v, string id, string newSlug)` — moves the file, rewrites every inbound `[[wikilink]]` via `Wikilinks.Rewrite`, updates idmap, regenerates index, logs. `void SetStatus(Vault v, string id, PageStatus s)`. `SourceService`: `List(status?,category?)`, `Show(id)`, `Impact(id)` (pages whose `sources` include id).

- [ ] **Step 1: Write the failing test** — A links to `[[contoso]]`; rename contoso→acme; assert file moved, A's body now `[[acme]]`, idmap updated, index shows `[[acme]]`. Separate test: `source impact <id>` lists the summary page citing it.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** rename (guard new slug not taken; scan all bodies for inbound links to old slug; move via atomic write of new path + delete old; note the move is two file ops, non-atomic — acceptable per §3, reindex heals). `impact` scans page frontmatters' `sources`.
- [ ] **Step 4: Run** → PASS. **M2 DONE.**
- [ ] **Step 5: Commit** `feat: page rename+set-status, source list/show/impact`.

---

## MILESTONE 3 — Health

### Task 21: Issues store + `issues list/show/resolve`

**Files:**
- Create: `src/Wiki/State/Issues.cs`, `src/Wiki/Cli/Commands/IssuesCommand.cs`
- Test: `tests/Wiki.Tests/State/IssuesTests.cs`

**Interfaces:**
- Produces: `class Issues { void Load(Vault v); Issue Upsert(IssueKind kind, string subject, string detail, string utcIso); void Resolve(string issueId, string? note); IReadOnlyList<Issue> List(IssueKind? kind, string? status); void Save(Vault v); }`; `class Issue { string Id; IssueKind Kind; string Subject; string Detail; string FirstSeen; string LastSeen; int Occurrences; string Status; }`. `Upsert` **merges** on `(kind,subject)`: existing open issue → bump `lastSeen`+`occurrences`, don't duplicate.

- [ ] **Step 1: Write the failing test** — upsert same (kind,subject) twice → one issue, occurrences 2, firstSeen preserved; `resolve` sets status resolved; `list --status open` excludes it.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** the merge-keyed store with source-gen JSON (register `Issue`, `Issue[]`). Issue id = ULID.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: issues store with occurrence merging + commands`.

---

### Task 22: `wiki lint` + `.wiki/lint.json`, `--fix-links`

**Files:**
- Create: `src/Wiki/Services/LintService.cs`, `src/Wiki/State/LintState.cs`, `src/Wiki/Cli/Commands/LintCommand.cs`
- Test: `tests/Wiki.Tests/Commands/LintTests.cs`

**Interfaces:**
- Produces: `class LintService { LintReport Run(Vault v, VaultConfig cfg, bool fixLinks); }`. Runs each check (spec §11 advisory table) → `Issues.Upsert`, writes `.wiki/lint.json` `{ lastRun: utcIso }`. Checks for v1 (amendment F keeps fuzzy ones dumb): `orphan`, `dangling-link`, `index-drift` (+ auto-fix), `oversize` (line count > `max_page_lines`), `rename-drift` (idmap path ≠ actual), `stale` (summary older than `staleness_days` with a newer source sharing an entity/concept — timestamp+shared-source heuristic), `coverage-gap` (capitalized multi-word token appearing as non-wikilink text in ≥3 bodies with no page), `needs-review-backlog`/`pending-backlog` (>14 days). `--fix-links` repairs only mechanical link targets after renames.

- [ ] **Step 1: Write the failing test** — build a vault with one orphan page and one oversize page (>`max_page_lines`); run lint; assert two issues filed of the right kinds and `.wiki/lint.json` written; re-run lint; assert occurrences bumped, not duplicated. Second test: a page with a dangling `[[ghost]]` (via `--allow-dangling`) files a `dangling-link` issue; `--fix-links` after a rename repairs the target.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** each check as a small private method returning `(kind,subject,detail)` tuples; keep the fuzzy ones intentionally simple. Auto-fix index-drift by regenerating index but still filing the issue (so drift cause is investigated).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: lint checks → issues, lint.json timestamp, --fix-links`.

---

### Task 23: Review gate — upsert integration + `review list/approve/reject` + shadow copy

**Files:**
- Create: `src/Wiki/Services/ReviewService.cs`, `src/Wiki/State/ReviewShadow.cs`, `src/Wiki/Cli/Commands/ReviewCommand.cs`; Modify `PageService.Upsert`.
- Test: `tests/Wiki.Tests/Commands/ReviewGateTests.cs`

**Interfaces:**
- Produces: `class ReviewService { IReadOnlyList<PendingView> List(Vault v); void Approve(Vault v, string pageId); void Reject(Vault v, string pageId, string? note); }`. When `cfg.ReviewGate`: `Upsert` lands `status=pending-review`; on update it first saves the previous body to `.wiki/review/<id>.prev.md`. `approve` → `active`, clears shadow. `reject` → restores shadow (updates) or `archived` (creates), files an issue, clears shadow. Lint excludes pending from orphan (Task 22 already checks status).

- [ ] **Step 1: Write the failing test** — init with `--review-gate`; upsert a page → status pending-review, appears in `review list`; `approve` → active; second page, update it, `reject` → previous body restored (or archived for a create); an issue filed on reject.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement.** Thread `cfg.ReviewGate` into `Upsert`'s status decision; write/read shadow via `ReviewShadow`; wire the three commands. `review list` shows a diff (Spectre) between shadow and current for updates.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: review gate + list/approve/reject + shadow diffs`.

---

### Task 24: `wiki source retract` cascade + `--purge`

**Files:**
- Modify: `src/Wiki/Services/SourceService.cs`; Create `src/Wiki/Cli/Commands/RetractCommand.cs` (or fold into SourceCommand).
- Test: `tests/Wiki.Tests/Commands/RetractTests.cs`

**Interfaces:**
- Produces: `RetractResult Retract(Vault v, string id, string reason, bool purge)` implementing spec §14: source frontmatter → `retracted` (+reason/timestamp); its summary page → `archived`; every other page citing the id → `needs-review` + a `retraction` issue per page; index regenerated; logged. `--purge` deletes the raw file after, keeping a metadata stub.

- [ ] **Step 1: Write the failing test** — add source, summarize it, create a concept citing it; retract; assert source status retracted, summary archived, concept needs-review, one retraction issue per affected page; `--purge` removes the raw file but leaves a stub.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** the cascade in order; use `SetStatus` + `Issues.Upsert(kind=Retraction)`.
- [ ] **Step 4: Run** → PASS. **M3 DONE.**
- [ ] **Step 5: Commit** `feat: source retraction cascade + purge`.

---

## MILESTONE 4 — Reflect + hardening

### Task 25: Schema proposals (full-section replacement) — `schema propose/proposals/approve/reject`

**Files:**
- Create: `src/Wiki/Services/SchemaService.cs`, `src/Wiki/Cli/Commands/SchemaCommand.cs`; `.wiki/proposals.json` store.
- Test: `tests/Wiki.Tests/Commands/SchemaProposalTests.cs`

**Interfaces:**
- Produces (amendment C): `class SchemaService { Proposal Propose(Vault v, string sectionHeading, string newText, string rationale); IReadOnlyList<Proposal> List(Vault v); void Approve(Vault v, string proposalId); void Reject(Vault v, string proposalId, string? note); }`; `class Proposal { string Id; string Section; string NewText; string Rationale; string Status; string CreatedAt; }`. `approve` locates the named `##`/`###` section in `AGENTS.md` and replaces its body verbatim (up to the next heading of equal-or-higher level), logs the amendment. No diff engine.

- [ ] **Step 1: Write the failing test** — `schema propose --section "Retrieval" --stdin` (rationale via `--rationale`); `schema proposals` lists it as open; `schema approve <id>` swaps that AGENTS.md section's text; `schema reject` marks rejected without touching the file. Include a test that an unknown section heading errors `code=unknown-section`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** a small markdown section locator (find heading line, capture until next heading of `<=` level) and verbatim replace via allowed-write (AGENTS.md is not a guarded path). Persist proposals as source-gen JSON.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: AGENTS.md section-replacement proposals`.

---

### Task 26: `wiki category add/list`, AGENTS.md template finalized, human rendering pass

**Files:**
- Create: `src/Wiki/Cli/Commands/CategoryCommand.cs`; finalize `src/Wiki/Templates/agents-md.txt` to spec Appendix A verbatim; add Spectre rendering to any command still JSON-only.
- Test: `tests/Wiki.Tests/Commands/CategoryTests.cs`

**Interfaces:**
- Produces: `wiki category add <id> --description "…"` (blocking `code=duplicate-category` if exists; edits `wiki.yaml` preserving other keys), `wiki category list`. Confirm the CLI **never** auto-adds a category (spec §5) — there is no code path from ingest to category creation; add a test asserting `source add` with an unknown category never mutates `wiki.yaml`.

- [ ] **Step 1: Write the failing test** — `category add paper --description "…"` adds to `wiki.yaml`; `category list` shows three; adding a duplicate errors; after a failed `source add --category nope`, `wiki.yaml` is byte-unchanged.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** category add by rewriting `wiki.yaml` through a structured re-serialize of `VaultConfig` (round-trip must preserve the file; add a round-trip test in Task 6 if not already). Ensure human output uses Spectre tables/trees across commands.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: category add/list, finalized AGENTS.md template, Spectre rendering`.

---

### Task 27: End-to-end lifecycle test + reindex-with-ledger property + AOT matrix smoke

**Files:**
- Create: `tests/Wiki.Tests/E2E/LifecycleTests.cs`
- Modify: `src/Wiki/Services/ReindexService.cs` (recompute structural ledger state)

**Interfaces:**
- Consumes: every command.
- Produces: one scripted test running init → source add → summary upsert → advance summarized → entity/concept upserts → advance integrated --touched → lint → advance linted → retract → repair; asserting exit codes and final state. Plus: after a full ingest, delete `.wiki/` entirely, run `reindex`, assert idmap byte-identical and structural ledger state (`registered/summarized/integrated`) recomputed correctly, while confirming history fields are absent/reset (amendment A — the property is structural-only).

- [ ] **Step 1: Write the failing E2E test** (full script above with assertions at each step).
- [ ] **Step 2: Run** `--filter LifecycleTests` → FAIL (reindex doesn't recompute ledger yet).
- [ ] **Step 3: Implement** ledger recomputation in `ReindexService`: `registered` if a source file exists; `summarized` if a summary page cites it; `integrated` if any entity/concept page cites it. Do not fabricate `linted` (needs lint history) — leave at the highest structurally-derivable state. Merge-preserve any surviving `issues.json`/`lint.json` rather than wiping.
- [ ] **Step 4: Run** → PASS. Then run `dotnet publish -r linux-x64 -c Release` and `-r win-x64` to smoke the AOT matrix; fix any warnings.
- [ ] **Step 5: Commit** `test: end-to-end lifecycle + structural-reindex property; feat: ledger recompute on reindex`. **M4 DONE.**

---

## Self-Review Notes (author checklist, resolved)

- **Spec coverage:** every §8 command maps to a task — init(8), reindex(15/27), category(26), source add/list/show/impact/retract(16,20,24), ingest status/advance/resume(17), page upsert/show/list/rename/set-status/backlinks(12,13,14,19,20), search(18), index show(19), lint(22), issues(21), review(23), schema(25). Frontmatter/validation §7/§11 in tasks 4/12. Amendments A-F in tasks 15/27(A), 4/12(B), 25(C), 17/22(D), 11/19(E), 22(F).
- **Deferred deliberately:** `--orphans` moved from Task 14 to Task 19 (needs backlinks); `linted` reindex recomputation intentionally not fabricated (documented in Task 27).
- **Type consistency:** `PageService.Upsert(UpsertRequest)→UpsertResult`, `Ledger.Advance(id,state,touched,iso)`, `Issues.Upsert(kind,subject,detail,iso)` used identically across tasks.
- **Placeholder scan:** the one `not-implemented` is an explicit, tested interim behavior (Task 14), removed in Task 19.
