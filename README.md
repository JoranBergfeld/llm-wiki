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

## Install

Grab a binary from the rolling [`latest`](https://github.com/JoranBergfeld/llm-wiki/releases/tag/latest)
prerelease — every green push to `main` publishes native-AOT builds for
`linux-x64`, `linux-arm64`, `win-x64` and `osx-arm64`. Unix targets are
`.tar.gz` (containing `wiki`), Windows is `.zip` (containing `wiki.exe`). No
runtime to install; put it on your `PATH`.

Or build from source (needs the [.NET 9 SDK](https://dotnet.microsoft.com/download)):

```bash
git clone https://github.com/JoranBergfeld/llm-wiki.git
cd llm-wiki
dotnet publish src/Wiki/Wiki.csproj -c Release -r linux-x64 -o publish
```

## Quickstart

```bash
# 1. Scaffold a vault. Also a valid Obsidian vault root.
wiki init ~/vaults/demo --name demo
export WIKI_VAULT=~/vaults/demo        # or pass --vault, or run from inside it

# 2. Categories are yours to define. Two ship in the scaffold; add more.
wiki category add paper --description "Research papers and reports"

# 3. Register a raw source. It is copied into raw/ under a new ULID, hashed,
#    deduped, and entered in the ledger as `registered`.
wiki source add ./notes.md --category article --title "Contoso platform review"
# → {"ok":true,"data":{"id":"01M05GXZ...","path":"raw/01M05GXZ....md",...}}

# 4. The agent writes the summary page. Bodies always arrive on stdin.
echo "Contoso shipped a billing engine in Q2." \
  | wiki page upsert --type summary \
      --title "Contoso platform review (summary)" \
      --summary "Key takeaways from the Contoso platform review" \
      --sources 01M05GXZ... --stdin --json
wiki ingest advance 01M05GXZ... --to summarized

# 5. …then the entities and concepts it touches.
echo "Contoso is a platform vendor. See [[contoso-platform-review-summary]]." \
  | wiki page upsert --type entity --title "Contoso" \
      --summary "Platform vendor evaluated in Q2" \
      --sources 01M05GXZ... --stdin --json
wiki ingest advance 01M05GXZ... --to integrated --touched 01M05GYAD...

# 6. Check the vault's health and close the loop.
wiki lint
wiki ingest advance 01M05GXZ... --to linted
```

Add `--json` to any command for the agent-facing envelope; without it you get
Spectre-rendered human output. Exit codes are identical either way:
`0` success · `1` your input was rejected · `2` environment/IO · `3` state
conflict (idempotent no-op).

Then open the vault in Obsidian and look at the graph.

### What's on disk afterwards

```
demo/
├── wiki.yaml            # your config: name, categories, review gate, lint thresholds
├── AGENTS.md            # the agent's instructions — conventions + playbooks
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
amendments to it (`wiki schema propose --section "<heading>" --stdin`) citing
recurring lint issues; you approve or reject. The agent never edits it directly.

## Commands

| Group | Commands |
|---|---|
| Vault | `init` · `reindex` · `category add\|list` |
| Sources | `source add\|list\|show\|impact\|retract` |
| Ingest | `ingest status\|advance\|resume` |
| Pages | `page upsert\|show\|list\|rename\|set-status\|backlinks` |
| Retrieval | `search` · `index show` |
| Health | `lint` · `issues list\|show\|resolve` |
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
