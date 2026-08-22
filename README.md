# llm-wiki

[![CI](https://github.com/JoranBergfeld/llm-wiki/actions/workflows/ci.yml/badge.svg)](https://github.com/JoranBergfeld/llm-wiki/actions/workflows/ci.yml)

A local, CLI-operated **LLM wiki**: a directory of interlinked markdown files
that an LLM agent builds and maintains from immutable raw sources, and that you
browse in Obsidian.

`wiki` is a single native binary. It is the *only* thing that writes to the
vault.

---

## What is this?

Point an agent at a pile of raw material — meeting transcripts, articles,
papers — and it produces a knowledge base that compounds: one summary page per
source, entity pages for the things worth linking to, concept pages carrying
the cross-source thesis, and an overview tying it together. Everything is plain
markdown with YAML frontmatter and `[[wikilinks]]`, so Obsidian reads it
natively and so do you.

The pattern is Karpathy's
([gist](https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f)).
This implementation departs from it in one fundamental way:

> **The LLM never touches the filesystem. Every mutation goes through the
> `wiki` CLI, which enforces structure deterministically.**

## Why is it a thing?

Because LLMs drift. Left alone with a filesystem, an agent forgets the
conventions it agreed to three sessions ago, half-finishes a multi-file update
and loses the thread, and invents structure that looks plausible and isn't. The
knowledge base decays exactly as fast as it grows.

Splitting the work along the line each side is actually good at fixes that:

| Actor | Does |
|---|---|
| **LLM agent** | All the semantic work — reading sources, authoring prose, deciding create-vs-update, proposing amendments to its own instructions |
| **CLI** | All the bookkeeping — schema validation, ID assignment, index maintenance, logging, ledger state, lint |
| **Human** | Curates sources, owns the schema and config, approves reviews and amendments |
| **Obsidian** | Read-only viewer, by convention — graph, browse, search |

Concretely, the CLI makes the interesting failure modes either impossible or
detectable:

- **Malformed writes never land.** Frontmatter is a closed schema; unknown
  keys, bad enum values, dangling `[[links]]`, unknown source IDs, duplicate
  titles — all rejected before anything touches disk, with a machine-readable
  error code the agent can act on.
- **Interrupted work is resumable.** Ingest is a state machine per source. A
  fresh session with zero conversation context runs `wiki ingest status`, sees
  what's half-done, and finishes it.
- **Decay is tracked, not silently accumulated.** `wiki lint` files orphans,
  stale summaries and broken links as issues with occurrence counts — and a
  finding that survives several lints is evidence the *instructions* need
  amending, which is its own reviewed workflow.
- **Nothing derived is precious.** `.wiki/` is a cache. `wiki reindex` rebuilds
  it from the markdown alone.
- **Quality is measured, not assumed.** Lint checks the vault's *shape*, so a
  vault of well-linked lorem ipsum would pass it. Alongside it: every full-body
  update reports what it *removed*, so facts cannot quietly evaporate across
  re-integrations; `wiki eval` scores retrieval against your own golden
  questions; and `wiki audit` walks the agent through re-reading a page against
  its sources, cold, to check the claims actually hold.

## Install

Every green push to `main` publishes the same native-AOT build through three
channels. The install script is the one to reach for.

**Script (recommended).** Detects your platform, pulls the matching binary from
the rolling [`latest`](https://github.com/JoranBergfeld/llm-wiki/releases/tag/latest)
prerelease, and drops it in place. Re-run it to update — that is the whole
update story.

```bash
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/JoranBergfeld/llm-wiki/main/scripts/install.sh | sh
```

```powershell
# Windows
irm https://raw.githubusercontent.com/JoranBergfeld/llm-wiki/main/scripts/install.ps1 | iex
```

It installs to `~/.local/bin` (Unix) or `%LOCALAPPDATA%\Programs\wiki`
(Windows) — override with `WIKI_INSTALL_DIR` — and tells you if that directory
is not on your `PATH`. Set `WIKI_VERSION` to install a tag other than `latest`.

**Manual download.** Take the asset yourself from the same
[`latest`](https://github.com/JoranBergfeld/llm-wiki/releases/tag/latest)
release: native-AOT builds for `linux-x64`, `linux-arm64`, `win-x64` and
`osx-arm64`. Unix targets are `.tar.gz` (containing `wiki`), Windows is `.zip`
(containing `wiki.exe`). No runtime to install; put it on your `PATH`.

**Container.** A multi-arch image (`linux/amd64`, `linux/arm64`) carrying the
same binary. Mount your vault at `/vault`, which is where `WIKI_VAULT` already
points:

```bash
docker run --rm -v "$PWD:/vault" --user "$(id -u):$(id -g)" \
  ghcr.io/joranbergfeld/llm-wiki:latest lint
```

`:latest` follows `main`; every build is also tagged with its commit SHA.

**.NET global tool.** Published to the GitHub Packages NuGet feed. Two caveats,
so pick this only if you are already in the .NET toolchain: it ships the **IL
build, not the AOT one**, so it needs the .NET 9 runtime present, and GitHub
Packages requires authentication *even for public packages* — you need a PAT
with `read:packages`.

```bash
dotnet nuget add source https://nuget.pkg.github.com/JoranBergfeld/index.json \
  -n llm-wiki -u <your-github-username> -p <your-PAT> --store-password-in-clear-text
dotnet tool install -g LlmWiki.Cli --prerelease
```

**From source** (needs the [.NET 9 SDK](https://dotnet.microsoft.com/download)).
Pick the RID for your machine — CI publishes all four:

```
dotnet publish src/Wiki/Wiki.csproj -c Release -r linux-x64   -o publish
dotnet publish src/Wiki/Wiki.csproj -c Release -r linux-arm64 -o publish
dotnet publish src/Wiki/Wiki.csproj -c Release -r win-x64     -o publish
dotnet publish src/Wiki/Wiki.csproj -c Release -r osx-arm64   -o publish
```

Native AOT cannot cross-compile across operating systems, so build the target
you are on.

## Quickstart

Two things differ between shells: **setting an environment variable**, and
**passing a page body**. Everything else below is identical everywhere.

Point the CLI at a vault — or pass `--vault <path>` on each command, or just
run from inside the vault directory:

```bash
# bash / zsh
export WIKI_VAULT=~/vaults/demo
```

```powershell
# PowerShell (Windows, macOS, Linux)
$env:WIKI_VAULT = "$HOME\vaults\demo"
```

```
:: cmd.exe
set WIKI_VAULT=%USERPROFILE%\vaults\demo
```

`~` is a shell feature, not a CLI one: PowerShell and `cmd.exe` do not expand
it, so write the path out.

Then, in any shell:

```
:: 1. Scaffold a vault. Also a valid Obsidian vault root.
wiki init ./demo --name demo

:: 2. Categories are yours to define. Two ship in the scaffold; add more.
wiki category add paper --description "Research papers and reports"

:: 3. Register a raw source. It is copied into raw/ under a new ULID, hashed,
::    deduped, and entered in the ledger as `registered`.
wiki source add ./notes.md --category article --title "Contoso platform review"
:: → {"ok":true,"data":{"id":"01M05GXZ...","path":"raw/01M05GXZ....md",...}}

:: Or point it at an inbox directory and register everything in one go.
wiki source scan ./inbox --category article --dry-run
```

Now the agent writes pages. **Bodies go in a file, and you pass the path** —
`--body-file` is the portable way to do this, because it takes quoting,
escaping, encoding and length limits out of the hot path entirely. (Write
`summary.md` with whatever your editor or agent uses; it lives outside the
vault and is just input, like the file `source add` takes.)

```
wiki page upsert --type summary --title "Contoso platform review (summary)" --summary "Key takeaways from the Contoso platform review" --sources 01M05GXZ... --body-file ./summary.md --json
wiki ingest advance 01M05GXZ... --to summarized

wiki page upsert --type entity --title "Contoso" --summary "Platform vendor evaluated in Q2" --sources 01M05GXZ... --body-file ./contoso.md --json
wiki ingest advance 01M05GXZ... --to integrated --touched 01M05GYAD...

wiki lint
wiki ingest advance 01M05GXZ... --to linted
```

One line each on purpose: line continuations are the third shell-specific
thing (`\` in bash, a backtick in PowerShell, `^` in `cmd.exe`), and the
commands above are the same everywhere without them.

`--stdin` remains fully supported and is the right choice for a one-line body
in bash:

```bash
echo "Contoso shipped a billing engine in Q2." \
  | wiki page upsert --type summary --title "…" --summary "…" --stdin --json
```

In PowerShell that pipe is more awkward than it looks — `echo` is
`Write-Output` and pipes objects rather than bytes, a multi-line body needs a
here-string whose terminator sits at column 0, and `$`, backtick and `"` are
all live characters. Prefer `--body-file` there. Passing both `--stdin` and
`--body-file` is an error (`body-source-conflict`); pick one.

Add `--json` to any command for the agent-facing envelope; without it you get
Spectre-rendered human output. Exit codes are identical either way:
`0` success · `1` your input was rejected · `2` environment/IO · `3` state
conflict (idempotent no-op) · `4` a measurement came in under a threshold you
asked for (`wiki eval --fail-under` only).

Then open the vault in Obsidian and look at the graph.

### It is the same vault everywhere

The vault is plain markdown with LF line endings and forward-slashed internal
paths, so it is portable across operating systems — keep it in git, on a sync
folder, or on a USB stick and work on it from Windows, macOS and Linux
interchangeably. Obsidian reads it natively on all three, and the CLI writes
UTF-8 regardless of what the surrounding shell's code page happens to be.

### What's on disk afterwards

```
demo/
├── wiki.yaml            # your config: name, categories, review gate, lint thresholds
├── AGENTS.md            # the agent's instructions — conventions + playbooks
├── eval.yaml            # optional: your golden retrieval questions, for `wiki eval`
├── raw/                 # immutable sources, named by ULID
├── wiki/
│   ├── index.md         # CLI-generated routing catalog
│   ├── log.md           # CLI-generated append-only operation log
│   ├── overview.md      # top-level synthesis
│   ├── summaries/  entities/  concepts/
└── .wiki/               # derived cache — idmap, ledger, issues, lint (rebuildable)
```

`wiki/index.md` is what makes retrieval cheap — the agent routes through it
instead of reading page bodies to find out what's relevant:

```markdown
## Entities
- [[contoso]] — Contoso — Platform vendor evaluated in Q2 (sources: 1)

## Summaries
- [[contoso-platform-review-summary]] — Contoso platform review (summary) — Key takeaways… (sources: 1)
```

## Pointing an agent at it

`wiki init` scaffolds an `AGENTS.md` at the vault root containing the
conventions and the playbooks the agent follows — session start, retrieval,
tool selection, ingest, reflect. Any agent that reads `AGENTS.md` (Claude Code,
among others) picks it up automatically; it needs nothing but the `wiki` binary
on `PATH`.

That file is also the system's only learning surface. The agent can propose
amendments to it (`wiki schema propose --section "<heading>" --body-file <path>`) citing
recurring lint issues; you approve or reject. The agent never edits it directly.

## Commands

| Group | Commands |
|---|---|
| Vault | `init` · `reindex` |
| Categories | `category add\|list\|propose\|proposals\|approve\|reject` |
| Sources | `source add\|scan\|list\|show\|impact\|retract` |
| Ingest | `ingest status\|advance\|resume` |
| Pages | `page upsert\|show\|list\|rename\|set-status\|backlinks` |
| Retrieval | `search` · `index show` |
| Health | `lint` · `issues list\|show\|resolve` · `links check` |
| Quality | `eval` · `audit next\|record\|list` |
| Review | `review list\|approve\|reject` |
| Reflect | `schema propose\|proposals\|approve\|reject` |

`wiki <group> --help` documents every flag.

## Documentation

- **[Architecture](docs/architecture.md)** — layers, vault layout, the JSON
  contract, why the code looks the way it does
- **[Functional flow](docs/functional-flow.md)** — who does what: ingest,
  review gate, retraction, lint and the reflect loop, with diagrams
- **[Technical flow](docs/technical-flow.md)** — one invocation end to end,
  from `argv` to the bytes on disk, with diagrams
- **[Specification](docs/spec.md)** — the authoritative spec the implementation
  is built against, including the lettered amendments
- **[Contributing](CONTRIBUTING.md)** — build, test, and the conventions this
  repo actually holds itself to

## Licence

MIT — see [LICENSE.md](LICENSE.md).
