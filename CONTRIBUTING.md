# Contributing

Thanks for looking. This document covers how to build and test the project and
the conventions the codebase actually holds itself to — the ones a reviewer
will notice if you skip them.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download). One SDK is enough to
  build every target — reference assemblies for the older ones restore as
  NuGet packages.
- To run the **full** test suite you need the 8.0, 9.0 and 10.0 runtimes,
  because both projects target `net8.0;net9.0;net10.0` and `dotnet test`
  executes the suite once per target. With a runtime missing, that target's
  pass rolls forward to the next one installed (`RollForward=LatestMajor`), so
  the run still succeeds — it just doesn't prove what it looks like it proves.
  CI installs all three for exactly that reason.
- For a native-AOT publish: a platform toolchain
  - **Linux:** `clang` and `zlib1g-dev`
  - **macOS:** Xcode command line tools; `openssl@3` and `brotli` (Homebrew
    keeps them keg-only, so export `LIBRARY_PATH` accordingly — see
    `.github/workflows/ci.yml`)
  - **Windows:** the Visual Studio C++ build tools

Native AOT cannot cross-compile across operating systems. You build for the OS
you're on.

Why three targets: a target framework is the **floor** of runtime support and
roll-forward only moves up, so a `net10.0` assembly cannot load on a 9.x
runtime however it's configured. Shipping the latest *and* not stranding
anyone on 8.x/9.x therefore needs more than one target. It only affects the
`dotnet tool` channel, which ships IL — the release binaries and Docker image
are self-contained AOT and need no runtime at all. Adding an API newer than
the lowest target is what would break this, and is the reason to drop `net8.0`
if it ever earns its keep.

## Build, test, run

```bash
dotnet build LlmWiki.sln                                   # every target
dotnet test tests/Wiki.Tests/Wiki.Tests.csproj -c Release  # the suite CI gates on, once per target
dotnet run --project src/Wiki -f net10.0 -- --help         # run the CLI

# -f is required by both `run` and `publish` now that the project
# multi-targets. Any of net8.0/net9.0/net10.0 works for local runs; the
# released binaries are built from the newest.

# native-AOT publish, as CI does it — substitute the RID for the OS you're on.
# CI publishes all four: linux-x64, linux-arm64, win-x64, osx-arm64.
dotnet publish src/Wiki/Wiki.csproj -c Release -f net10.0 -r linux-x64 -o publish
```

The commands above are the same in bash, PowerShell and `cmd.exe`. Where a
shell-specific construct is unavoidable (environment variables, line
continuations, piping a body into `--stdin`) the README gives both forms; in
your own scripts prefer `--vault <path>` and `--body-file <path>`, which read
identically everywhere.

Everything must build warning-free. AOT trim warnings in particular are not
noise — they mean something will fail at runtime in the published binary but
not under `dotnet run`.

## Repository layout

```
src/Wiki/            the CLI — see docs/architecture.md for the layer map
  Cli/               command tree, CommandContext, output paths
  Cli/Commands/      one file per command group
  Services/          one class per command group: preconditions + orchestration
  Core/              vault vocabulary: paths, config, frontmatter, slugs, links
  State/             .wiki/*.json stores
  Docs/              generated index.md and log.md
  Json/              envelope + source-generated serializer context
  Templates/         embedded wiki.yaml and AGENTS.md scaffolds
tests/Wiki.Tests/    xunit; Commands/ Core/ Docs/ State/ E2E/ Support/
docs/                spec + architecture + flow documentation
```

## The spec is authoritative

[`docs/spec.md`](docs/spec.md) is the contract this implementation is built
against, and it is referenced by section number throughout the code
(`spec §11`, `amendment K`). If you change behaviour the spec describes, change
the spec in the same PR.

Corrections to the spec are **appended as lettered amendments** in Appendix B
rather than edited into the body silently. An amendment states what the
original text said, why implementing it literally was wrong, and what the rule
is now. That history is why the codebase is legible months later; keep it.

## Code conventions

**Validation before writes, always.** Every mutating service method runs all
blocking validation, then a literal `--- Validation complete ---` comment, then
the writes. A rejected call must leave the vault byte-identical to how it found
it. Don't interleave.

**Error codes are an API.** `ValidationException` takes a stable kebab-case
code (`duplicate-title`, `unknown-source`) that agents branch on. Reuse an
existing code where the meaning matches; introduce a new one rather than
overloading one that means something else somewhere else.

**No `System.Console`.** Streams come in through `App.Main` and travel via
`CommandContext`. Spectre gets a local console instance writing to that stream,
never the process-global `AnsiConsole.Console` — the tests run in-process and
in parallel.

**Register every DTO in `WikiJsonContext`.** Native AOT has no reflection
fallback; an unregistered type fails at runtime, not at build time.

**Inject the clock and RNG.** Services take `Func<long> nowUnixMs` /
`Func<byte[]> randomBytes` defaulting to the real ones, so tests can pin ULIDs
and dates.

**Deterministic serialization.** Stores rebuild a sorted snapshot immediately
before writing. Enumerators sort ordinally. `wiki reindex` reproducibility
depends on both.

**Comments explain *why*.** The existing ones document the decision and the
failure mode that motivated it, not what the next line does. Match that. If you
delete a safety check, say why in a comment where it used to be — as
`AtomicFile` does for the guard that was removed.

## Tests

xunit, run in-process. `TempVault` scaffolds a throwaway vault and drives the
real `App.Main` with captured stdout, so a test exercises the actual parse →
validate → write path rather than a service in isolation:

```csharp
using var vault = new TempVault();
vault.Run("init", vault.Path, "--json");
var result = vault.RunStdin("body text", "page", "upsert", "--type", "entity",
                            "--title", "Contoso", "--summary", "…", "--stdin", "--json");
Assert.Equal(0, result.ExitCode);
Assert.True(result.Envelope.Ok);
```

What a change should come with:

- The `--json` envelope asserted for both the success and the rejection path —
  the envelope *is* the contract.
- Exit codes asserted, not just `ok`. 1 (rejected), 2 (IO), 3 (state conflict)
  are distinct promises.
- For anything touching derived state, a reindex assertion: idmap byte-identity
  and structural ledger state are rebuilt exactly; history is only
  merge-preserved.
- For lifecycle work, a case in `E2E/LifecycleTests.cs`.

## Commits and pull requests

Conventional-commit prefixes, matching the existing history: `feat:`, `fix:`,
`refactor:`, `test:`, `docs:`, `spec:`, `chore:`, `ci:`. The subject line says
what changed and, where it fits, why — `fix: human-mode errors render as
Spectre, not the JSON envelope (amendment P)`.

Line endings are pinned to LF in the working tree by `.gitattributes`. Don't
fight it.

Before opening a PR: the suite passes, the build is warning-free, and the spec
and docs match the behaviour you shipped.

## CI

Every push to `main` runs the tests; if they pass, it builds the native-AOT
binary for `linux-x64`, `linux-arm64`, `win-x64` and `osx-arm64` and republishes
the rolling [`latest`](https://github.com/JoranBergfeld/llm-wiki/releases/tag/latest)
prerelease at that commit. A newer commit supersedes an in-flight run so the
release always reflects the newest green `main`.

`osx-x64` is intentionally not built in CI — GitHub's Intel-Mac runners queue
long enough to stall the gated release. The RID still works if you build it
locally.

The same green build then fans out to three channels: the rolling release, a
multi-arch image on `ghcr.io/joranbergfeld/llm-wiki`, and the `LlmWiki.Cli`
global tool on the GitHub Packages NuGet feed. If you change the packaging,
change all three deliberately — `docker/Dockerfile` copies the binaries the
build job produced (so its base image must track the runners' Ubuntu version
or the AOT binary meets a glibc it was not linked against), and the `nuget`
job packs from `src/Wiki/Wiki.csproj` with a rolling `PackageVersion` that
leaves the assembly version alone.

The release job also publishes a `SHA256SUMS` covering every archive, which
both install scripts fetch and verify before installing. If you rename an
asset or add a RID, the checksum file follows automatically (it is generated
from the artifacts), but the scripts look their asset up by exact filename —
so a rename means touching `scripts/install.sh` and `scripts/install.ps1` too.

## Licence

By contributing you agree your contributions are licensed under the MIT
Licence — see [LICENSE.md](LICENSE.md).
