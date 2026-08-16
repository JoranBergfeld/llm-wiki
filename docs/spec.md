# LLM Wiki — Implementation Specification

**Status:** Draft v1.0 · **Date:** 2026-07-13
**Purpose:** Complete specification for a local, CLI-operated LLM Wiki based on Karpathy's LLM Wiki pattern (gist `karpathy/442a6bf555914893e9891c11519de94f`). This document is the input to Claude Code for implementation.

---

## 1. Overview

An LLM Wiki is a persistent, compounding knowledge base: a directory of interlinked markdown files that an LLM agent builds and maintains from immutable raw sources, and that a human browses in Obsidian. This implementation departs from the reference pattern in one fundamental way:

> **The LLM never touches the filesystem. Every mutation goes through a CLI (`wiki`) that enforces structure deterministically.**

The LLM does all semantic work — summarizing, synthesizing, deciding which pages a new source affects. The CLI does all bookkeeping — schema validation, ID assignment, index maintenance, logging, state tracking. The division exists because LLMs drift: they forget conventions, half-finish multi-file updates, and invent structure. The CLI makes those failure modes impossible or detectable.

### Roles

| Actor | Responsibilities |
|---|---|
| **Human** | Curates sources, approves reviews, owns the schema/config, edits categories, browses in Obsidian |
| **LLM agent** (Claude Code) | Reads sources and pages, authors all prose, decides page create-vs-update, proposes schema amendments |
| **CLI** | Validates and executes every mutation, maintains index/log/ledger/issues, runs lint, enforces the review gate |
| **Obsidian** | Read-only viewer (by convention): graph view, browsing, search for the human |

### One binary, many vaults

The same binary serves multiple independent vaults (e.g., one for work, one for personal use). All behavior that differs between contexts — source categories, review gate, conventions — is **data in the vault's config**, not code. Sync (OneDrive, Obsidian Sync, git, nothing) is the user's responsibility and out of scope.

---

## 2. Goals and non-goals

### Goals (v1)

1. Deterministic CLI covering the full lifecycle: init, source registration, ingest, page mutation, lint, review, retraction, schema amendment.
2. Resumable ingest via a state ledger — an interrupted session can be completed by a fresh session with zero context.
3. Dynamic, human-defined source categories per vault; fixed global page types.
4. Per-vault review gate (config flag) for human approval before pages become active.
5. Obsidian-compatible output: `[[wikilinks]]`, YAML frontmatter, plain markdown.
6. Machine-readable CLI output (`--json`) so the agent parses results instead of prose.
7. All derived state rebuildable from the markdown alone (`wiki reindex`).
8. A reflect loop: lint findings → tracked issues → human-approved amendments to the agent instructions file.

### Non-goals (v1)

- No MCP server (the agent shells out to the CLI).
- No embedded database; no vector or FTS search (agentic grep + index routing suffices; SQLite FTS5 is a v2 candidate).
- No git integration or transactional rollback — **forward repair only** (see §3).
- No filing of query answers back as wiki pages.
- No PDF/image/URL ingestion (markdown and plain text only).
- No scheduled/hooked lint (manual command only).
- No sync, no multi-user concurrency, no web UI, no telemetry.

---

## 3. Design principles

1. **Filesystem is the source of truth.** Structural derived state (the ID map, page/index consistency, and the *structural* ledger state a source is in) must be reconstructible by scanning the vault. Derived state is a cache. **Exception (amendment A):** issue history (`first_seen`/`occurrences`/`last_seen`), the ledger `--touched` audit list, the last-lint timestamp, and review shadow copies are *historical* state that markdown does not contain. `wiki reindex` rebuilds the idmap byte-identically and recomputes structural ledger state; it merge-preserves history best-effort and never claims byte-identity for it (see Appendix B).
2. **The LLM writes prose, never files.** Page bodies enter the system only via CLI stdin/arguments. The CLI writes files.
3. **Forward repair, not rollback.** Multi-file operations are not atomic. Instead: every mutation is idempotent, the ledger records completed steps, `wiki ingest resume` finishes interrupted work, and lint detects any residual inconsistency. There is no undo.
4. **Humans own the schema.** Categories and instructions change only through human action (config edit or explicit approval of a proposal). The LLM may propose; it may never apply.
5. **Blocking validation at the write boundary; advisory validation at lint.** Malformed writes never land. Semantic decay (orphans, staleness) is detected, filed as issues, and repaired forward.
6. **Closed vocabulary for structure.** Page types, ledger states, issue kinds, and frontmatter keys are fixed enumerations. Only category names and page content are free.

---

## 4. Vault layout

```
my-vault/                        # also the Obsidian vault root
├── wiki.yaml                    # vault config (human-owned)
├── AGENTS.md                    # agent instructions: conventions + playbooks (§13)
├── raw/                         # immutable sources
│   ├── 01J9ZKM3....md           # source files, named by source ID
│   └── assets/                  # attachments (Obsidian attachment folder)
├── wiki/
│   ├── index.md                 # routing catalog (CLI-generated, §9)
│   ├── log.md                   # append-only operation log (CLI-generated)
│   ├── overview.md              # top-level synthesis (LLM-authored via CLI)
│   ├── summaries/               # one page per source
│   ├── entities/                # people, orgs, products, places…
│   └── concepts/                # topics, themes, cross-source synthesis
└── .wiki/                       # derived state — NEVER synced-critical, always rebuildable
    ├── idmap.json               # id → relative path
    ├── ledger.json              # ingest state machine per source (§10)
    └── issues.json              # lint findings with lifecycle (§12)
```

Notes:
- `.wiki/` should be excluded from Obsidian indexing (dot-folder, automatic) and may be excluded from sync; `wiki reindex` regenerates it entirely.
- `raw/` files are never modified after registration. The CLI enforces this by refusing any write path under `raw/` except `wiki source add`.

---

## 5. Configuration — `wiki.yaml`

Human-edited (directly or via `wiki category` commands). Read by the CLI on every invocation; validated on read, with hard failure and a precise error on any violation.

```yaml
version: 1
name: "work"                     # vault display name
review_gate: true                # pages land as pending-review when true
categories:                      # source categories — fully user-defined
  - id: meeting-transcript
    description: "Customer meeting transcripts"
  - id: article
    description: "Web articles and blog posts"
  - id: paper
    description: "Research papers and reports"
lint:
  staleness_days: 90             # advisory: summaries older than this with newer related sources
  max_page_lines: 400            # advisory: pages larger than this flagged for splitting
```

Rules:
- `categories[].id`: lowercase kebab-case, unique. Referenced by every source.
- Removing a category that sources still reference is a blocking config error, enforced on every config load (amendment N). `wiki category …` is exempt so the human can repair the config with the CLI rather than by hand.
- Adding categories: `wiki category add <id> --description "…"` or direct file edit. **The CLI never adds categories on its own; there is no code path by which ingest creates one.** An unknown category on `wiki source add` is a blocking error instructing the human to add it first.

---

## 6. Identity model

- **ID:** every source and page gets a ULID at creation, stored in frontmatter as `id`. IDs are permanent and are what all provenance and CLI references use.
- **Filename:** human-readable slug (`entities/contoso.md`), the handle Obsidian and `[[wikilinks]]` resolve against.
- **ID map:** `.wiki/idmap.json` maps id → path; rebuilt by `wiki reindex` from frontmatter scans.
- **Renames:** `wiki page rename <id> <new-name>` moves the file and rewrites all inbound `[[wikilinks]]`. If the human renames in Obsidian directly, the next `wiki lint` detects the path/idmap mismatch via the frontmatter ID, repairs the idmap, and files an issue listing any now-broken inbound links (repairable via `wiki lint --fix-links`).
- **Wikilinks:** `[[filename]]` or `[[filename|display]]` only. Standard markdown links are allowed for external URLs only.

---

## 7. Page types (fixed, global)

The page type set is **closed**. Lint rules, ingest workflow, and the new-page-vs-edit heuristic are all defined in terms of these types. New types require a new version of this spec, not configuration.

| Type | Directory | Purpose | Created during |
|---|---|---|---|
| `source` | `raw/` | Immutable raw material + registration metadata | `wiki source add` |
| `summary` | `wiki/summaries/` | One per source: key takeaways, claims, references | Ingest step 2 |
| `entity` | `wiki/entities/` | A distinct nameable thing (person, org, product, project, place) referenced from elsewhere | Ingest step 3 |
| `concept` | `wiki/concepts/` | A topic/theme synthesized across sources; carries the evolving thesis | Ingest step 3 |
| `overview` | `wiki/overview.md` | Singleton top-level synthesis | Ingest step 4 (updated) |

Heuristic the agent instructions encode (from production experience in the gist thread): **new page** when it is a distinct entity/concept you would link to from elsewhere; **edit in place** when it is an attribute or update of an existing one.

### Frontmatter schemas

Common (all wiki pages):

```yaml
id: 01J9ZKM3E8W1R2X3Y4Z5A6B7C8   # ULID, CLI-assigned, immutable
type: entity                      # enum: summary | entity | concept | overview
title: "Contoso"
status: active                    # enum: active | pending-review | needs-review | archived
created: 2026-07-13
updated: 2026-07-13               # CLI-maintained on every write
summary: "One-line routing description"  # REQUIRED; supplied via --summary; feeds index.md (§9)
sources: [01J9ZKM1..., 01J9ZKM2...]  # source IDs this page cites — provenance backbone
tags: []                          # optional, free-form, for Obsidian/Dataview
```

Source files (`raw/`):

```yaml
id: 01J9ZKM1...
type: source
title: "Contoso platform review — meeting 2026-07-10"
category: meeting-transcript      # must exist in wiki.yaml
added: 2026-07-13
sha256: "…"                       # content hash at registration (integrity + dedup)
origin: "manual"                  # free-text provenance note (URL, 'clipper', 'manual', …)
status: active                    # active | retracted
```

Blocking validation (§11) rejects any write whose frontmatter deviates from these schemas.

### `status` semantics

- `active` — normal.
- `pending-review` — written under a review gate; excluded from orphan lint; listed by `wiki review list`; the agent must not cite pending pages when answering queries (encoded in AGENTS.md).
- `needs-review` — flagged by retraction cascade or lint; content may be stale/unsupported.
- `archived` — kept for history; excluded from index and lint.

---

## 8. CLI specification

### Binary and global conventions

- Binary name: `wiki`. Vault resolution: `--vault <path>` flag, else `WIKI_VAULT` env var, else walk up from CWD looking for `wiki.yaml`. **All three branches require a `wiki.yaml` at the resolved root** (amendment M) — an explicit path that isn't a vault is an error, never an empty vault.
- **`--json` on every command** emits a stable, versioned JSON envelope: `{"ok": bool, "data": …, "errors": [{"code": "…", "message": "…", "path": "…"}]}`. This is the primary agent interface; human-facing output uses Spectre.Console rendering — **on the failure path as well as the success path** (amendment P). Exit codes are identical in both modes.
- Exit codes: `0` success · `1` blocking validation failure (input rejected, nothing written) · `2` environment/IO error · `3` state conflict (e.g., resuming a ledger step already done — safe, idempotent no-op reported).
- Page bodies always arrive via **stdin** (`--stdin`) or `--body-file <path>`; never as shell arguments (quoting/size hazards).
- Every mutating command appends one line to `wiki/log.md` in the format `## [2026-07-13T14:02:11Z] <op> | <subject> | <one-line detail>` (grep-parseable, per the gist).

### Command reference

```
wiki init <path> [--name X] [--review-gate]     Scaffold vault: wiki.yaml, AGENTS.md template, dirs, empty index/log
wiki reindex                                    Rebuild .wiki/* entirely from markdown scan

# Config
wiki category add <id> --description "…"        Human-only; blocking error if id exists
wiki category list

# Sources
wiki source add <file> --category <id> --title "…" [--origin "…"]
                                                Copies file into raw/ named by new ULID, writes frontmatter,
                                                computes sha256 (dedup: identical hash → error listing existing id),
                                                registers ledger entry in state `registered`
wiki source list [--status …] [--category …]
wiki source show <id>
wiki source impact <id>                         All pages whose `sources` include <id>
wiki source retract <id> [--reason "…"]         §14 cascade

# Ingest (§10)
wiki ingest status [<source-id>]                Ledger state; without args: everything not `linted` (i.e. all incomplete ingests — amendment G)
wiki ingest advance <source-id> --to <state>    CLI validates preconditions before recording transition
wiki ingest resume <source-id>                  Prints exactly what remains (states + expected artifacts), --json

# Pages
wiki page upsert --type <t> --title "…" [--id <id>] [--sources id1,id2] [--tags …] --stdin
                                                Create (no --id) or full-body update (--id). All blocking
                                                validation (§11) runs before write. Updates index.md + idmap.
wiki page show <id|name> [--frontmatter-only]
wiki page list [--type …] [--status …] [--orphans]
wiki page rename <id> <new-slug>                Moves file, rewrites inbound wikilinks
wiki page set-status <id> <status>              Gate/lint workflows use this internally; exposed for repair
wiki page backlinks <id|name>                   Inbound links — the agent's graph-navigation primitive

# Retrieval support (deterministic map-first navigation, §13)
wiki search <terms> [--type …] [--limit N]      Plain-text/regex search over frontmatter + bodies of BOTH wiki
                [--regex] [--kind page|source]   pages and raw/ sources (amendment O); returns kind, id, path,
                                                title, matching line — never full bodies. The result reports
                                                `truncated` when --limit cut the scan short.
wiki index show [--type …]                      Emit index.md entries as JSON (routing without file read)

# Lint & issues (§12)
wiki lint [--fix-links]                         Run all advisory checks; file/refresh issues; print report
wiki issues list [--kind …] [--status open]
wiki issues show <issue-id>
wiki issues resolve <issue-id> [--note "…"]

# Review gate (§9)
wiki review list
wiki review approve <page-id>                   pending-review → active
wiki review reject <page-id> [--note "…"]       pending-review → archived; files an issue for the agent

# Reflect loop (§13)
wiki schema propose --section "<heading>" --stdin Full-section replacement (amendment C): LLM submits the new full
                                                text of one AGENTS.md section (identified by its heading); stored as proposal.
                                                No unified-diff engine — the CLI swaps the named section verbatim on approve.
wiki schema proposals
wiki schema approve <proposal-id>               Human applies the diff; logs amendment
wiki schema reject <proposal-id> [--note "…"]
```

---

## 9. Index and log

- **`wiki/index.md`** is CLI-generated on every page mutation (never hand- or LLM-edited; a lint check verifies it matches reality). Format: grouped by page type, one line per page: `- [[slug]] — <title> — <one-line description> (sources: N)`. The one-line description is supplied by the LLM at `page upsert` time via `--summary "…"` (add this flag to upsert; it is stored in frontmatter as `summary` and is **required** — blocking — because the index is only useful if every entry routes).
- **`wiki/log.md`** is append-only, CLI-written, grep-parseable (format in §8). It is an audit narrative, not recovery state — recovery state is the ledger.

The index is a **routing file**: the agent reads it (or `wiki index show`) to pick candidate pages, then reads at most ~10 page bodies. This rule lives in AGENTS.md (§13). `pending-review` pages **are** listed in the index (so the agent can route to and revise them) but carry their `status` in the entry; AGENTS.md forbids *citing* them in query answers.

---

## 10. Ingest workflow and state ledger

Ingest is a state machine per source, recorded in `.wiki/ledger.json`. States are a closed enum; the CLI validates preconditions on every transition.

```
registered ──▶ summarized ──▶ integrated ──▶ linted
```

| State | Meaning | Precondition checked by CLI on advance |
|---|---|---|
| `registered` | Source in `raw/`, frontmatter valid | (entry state) |
| `summarized` | Summary page exists | A `summary` page with this source in `sources` exists |
| `integrated` | Entity/concept pages updated, index current | Agent supplies `--touched id1,id2,…` (may be empty; recorded in ledger for audit); index verified consistent |
| `linted` | Post-ingest lint ran clean or issues filed | A lint run newer than the `integrated` timestamp exists. The last-lint timestamp is stored in `.wiki/lint.json` (written by every `wiki lint`); the ledger records the `integrated` timestamp for comparison. |

The canonical agent flow (encoded in AGENTS.md, enforced by ledger preconditions):

1. Human: `wiki source add …` → `registered`.
2. Agent reads the source (`wiki source show`, then reads the raw file — reading is unrestricted; only writing is mediated).
3. Agent writes summary: `wiki page upsert --type summary --sources <src-id> --stdin` → `wiki ingest advance <src> --to summarized`.
4. Agent consults `wiki index show` + `wiki search` to find affected entities/concepts, upserts each (bodies via stdin, `--sources` extended with the new source ID), creating new pages per the heuristic in §7 → `wiki ingest advance <src> --to integrated --touched …`.
5. Agent updates `overview.md` if warranted, then runs `wiki lint` → `linted`.

**Resume guarantee:** a fresh session with zero conversation context runs `wiki ingest status`, sees e.g. `01J9… : summarized`, runs `wiki ingest resume 01J9…`, and receives a machine-readable description of remaining steps. This — not format validation — is the primary defense against the LLM losing track.

---

## 11. Validation: blocking vs advisory

### Blocking (write rejected, exit 1, nothing lands)

1. Frontmatter schema violation (missing/unknown keys, bad enum values, malformed ULID).
2. Unknown `category` on source add.
3. Unknown source ID in a page's `sources` list.
4. `[[wikilink]]` in a submitted body whose target resolves to no existing page **and** is not among pages created in the same upsert batch → error lists each dangling link. (The agent may pass `--allow-dangling` to permit forward references; these are then filed automatically as `dangling-link` issues rather than silently ignored — **filed by the upsert itself, at write time, not deferred to the next lint** (amendment L). The upsert's `danglingFiled` envelope field names the targets actually filed.)
5. Any write path under `raw/` other than via `source add`; any edit to `index.md`/`log.md` other than by the CLI itself.
6. Duplicate source content hash; duplicate page title within a type (case-insensitive) without explicit `--id` (i.e., accidental near-duplicate creation).
7. Missing `--summary` on page creation.
8. Ledger transition whose precondition fails.

### Advisory (lint findings → issues)

| Kind | Check |
|---|---|
| `orphan` | Page (status `active`) with zero inbound wikilinks |
| `dangling-link` | Wikilink target missing (from `--allow-dangling` or Obsidian-side edits) |
| `stale` | Summary older than `staleness_days` with newer sources touching the same entities/concepts |
| `coverage-gap` | Term appearing in ≥3 pages' bodies as a wikilink-less proper mention with no page of its own (heuristic; agent refines candidates) |
| `index-drift` | index.md entry set ≠ actual page set (auto-fixed, but filed so drift cause is investigated) |
| `oversize` | Page exceeds `max_page_lines` (candidate for split/compression) |
| `rename-drift` | idmap path mismatch (Obsidian-side rename) |
| `needs-review-backlog` | Pages in `needs-review` older than 14 days |
| `pending-backlog` | Pages in `pending-review` older than 14 days |

Lint never edits page content. `--fix-links` repairs only mechanical link targets after renames. Everything else is filed as an issue for the agent/human.

---

## 12. Issues

`.wiki/issues.json`; each issue: `{id, kind, subject(page/source id), detail, first_seen, last_seen, occurrences, status: open|resolved}`. Re-running lint refreshes `last_seen`/`occurrences` on persisting findings instead of duplicating them. **`occurrences` is the reflect-loop signal**: a finding that survives multiple lints indicates an instructions deficiency, not a one-off mistake.

---

## 13. Agent instructions file and the reflect loop (`AGENTS.md`)

`AGENTS.md` at vault root is loaded by the agent at session start. It is the system's **only learning surface** — the operational memory that compounds. `wiki init` scaffolds it with two mandated sections:

### Section 1 — Conventions
Page-type definitions and the new-vs-edit heuristic, prose style, linking rules (every entity mention in a body should be a wikilink on first occurrence), what belongs in overview.md, vault-specific domain guidance (human-authored).

### Section 2 — Playbooks (the meta-learning target)
Explicit procedures for *how the agent operates*, including at minimum:

- **Retrieval playbook:** never scan page bodies to discover relevance. Route via `wiki index show` / `wiki search` / `wiki page backlinks`, select ≤10 candidates, then read only those bodies. Never cite `pending-review` pages in answers.
- **Tool-selection rules:** which CLI command for which intent (a table); always parse `--json`; on exit 1, read `errors[].code` and correct the input — never retry blind; on session start with pending work, run `wiki ingest status` first.
- **Ingest playbook:** the §10 sequence, verbatim.

### The reflect loop

1. `wiki lint` runs; issues accumulate `occurrences`.
2. When the human (or the agent, during a session) observes recurring issue kinds or operational flailing, the agent drafts an amendment: `wiki schema propose --section "<heading>" --stdin` with the full replacement text for that one AGENTS.md section, plus a rationale referencing issue IDs. (Full-section replacement, not a unified diff — deterministic to apply, no patch engine.)
3. Human reviews: `wiki schema approve|reject <id>`. Approved diffs are applied by the CLI and logged.

The LLM can propose changes to its own conventions *and* to its own playbooks (retrieval strategy, tool usage) — that is the meta-learning you asked for — but the human always applies. **Explicit v1 limitation:** there is no automated measurement of retrieval or answer quality; the loop's trigger is recurring lint evidence plus human judgment. Automated eval traces are a v2 investigation.

---

## 14. Source retraction

`wiki source retract <id> --reason "…"`:

1. Source frontmatter: `status: retracted` (+ reason, timestamp). The raw file is **not deleted** by default; `--purge` deletes the file after step 3 (for the compliance/deletion-request case) while retaining the metadata stub.
2. The source's summary page → `status: archived`.
3. Every other page whose `sources` include the ID → `status: needs-review`, and a `retraction` issue is filed per page with the reason.
4. Index regenerated; log entry written.

The agent then works through `wiki issues list --kind retraction`: for each page, rewrite the body removing claims that rested on the retracted source (readable in the archived summary), drop the source ID from `sources`, upsert, and restore `status: active` via review flow if the gate is on. Deletion is thus a tracked repair job, not archaeology.

---

## 15. Review gate

When `review_gate: true`:

- Every `page upsert` (create or update) lands with `status: pending-review`. For updates, the CLI writes the new body to the page but preserves the **last reviewed** body under `.wiki/review/<page-id>.prev.md` so `wiki review list` can show a diff; `reject` restores it. The shadow is written only when none exists, so a run of consecutive un-reviewed updates keeps pointing at the last body a human actually signed off on (amendment K). (This is the one place the CLI keeps a shadow copy; it is derived state, cleared on approve/reject.)
- `reject` on an update restores the shadow body **and the status the page held before the gate captured it**, not an unconditional `active` (amendment K) — rejecting an edit to a `needs-review` page must not silently clear its needs-review flag.
- `wiki review list` shows pending pages with diffs; `approve` activates, `reject` restores/archives and files an issue.
- Lint excludes `pending-review` from orphan checks; AGENTS.md forbids citing them.

When `false`, pages land `active` directly. The flag can be flipped at any time; it affects only future writes.

---

## 16. Implementation notes (.NET)

- **Target:** .NET 9, `PublishAot=true`, single-file, self-contained. Build matrix: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`, `linux-arm64`.
- **CLI framework:** Spectre.Console for rendering (tables, trees, diff display). For command parsing, verify **Spectre.Console.Cli** AOT/trimming compatibility at implementation time — it historically relies on reflection for command binding; if warnings persist, use `System.CommandLine` for parsing and Spectre.Console purely for output. Do not fight the trimmer.
- **YAML:** frontmatter schemas here are small and closed — prefer a hand-rolled or minimal mapping over reflection-based YamlDotNet defaults (AOT hazard). If YamlDotNet is used, use its static/source-generated context.
- **Markdown:** Markdig for parsing (wikilink extraction via a custom inline parser or regex over the raw text — wikilinks are simple enough that regex `\[\[([^\]|]+)(\|[^\]]+)?\]\]` on non-code-fence lines is acceptable and avoids AST complexity).
- **JSON:** `System.Text.Json` with source-generated contexts (AOT requirement).
- **IDs:** ULID (e.g., the `Ulid` NuGet package or a 30-line implementation to stay dependency-light).
- **File writes:** write-temp-then-rename within the same directory for every file the CLI produces (crash safety per file; cross-file atomicity is explicitly not provided — see §3).
- **Paths:** treat vault paths as case-sensitive internally; normalize to forward slashes in idmap; be deliberate about OneDrive/NTFS vs ext4 vs APFS casing when checking duplicates.
- **Testing:** golden-file tests for every command's `--json` output; an end-to-end test that scripts the full lifecycle (init → add → ingest all states → lint → retract → repair) against a temp vault; property test that `wiki reindex` from markdown reproduces the **idmap** byte-identically and the **structural ledger state** exactly (per-source registered/summarized/integrated derived from page scans). History (issue occurrences, ledger `--touched` audit, last-lint timestamp) is explicitly **out** of the byte-identity property — reindex merge-preserves it (amendment A).

### Suggested milestones for Claude Code

1. **M1 — Skeleton:** `init`, `reindex`, config load/validation, frontmatter parse/validate, `page upsert/show/list`, blocking validation, index + log generation. *(The wiki is usable manually.)*
2. **M2 — Lifecycle:** sources, ledger (`ingest status/advance/resume`), search, backlinks, rename.
3. **M3 — Health:** lint + issues, review gate, retraction cascade.
4. **M4 — Reflect:** schema proposals, AGENTS.md template finalized, end-to-end test with a real Claude Code session against a demo vault.

---

## 17. v2 candidates (explicitly deferred)

SQLite FTS5 hybrid search (`.wiki/search.db`, rebuildable) · MCP server wrapping the CLI · PDF/URL/image ingestion · scheduled lint (CI or timer) · query-answer filing as a distinct page type · retrieval-quality traces/evals to feed the reflect loop · multi-vault cross-linking.

---

## Appendix A — AGENTS.md template (scaffolded by `wiki init`)

The authoritative copy lives at `src/Wiki/Templates/agents-md.txt` (embedded resource); this reproduces it. The **Tool selection** table and the fully-written **Ingest** playbook are mandatory per §13 — an earlier draft left Ingest as the placeholder `(§10 sequence of the spec, verbatim.)`, which shipped an empty playbook into every scaffolded vault.

```markdown
# Wiki Agent Instructions
You maintain this wiki exclusively through the `wiki` CLI. You never create,
edit, move, or delete files in this vault directly. Always pass --json and
parse the result. On exit code 1, read errors[].code, fix your input, retry once.

## Conventions
- Page types: summary (one per source), entity (nameable thing), concept
  (cross-source theme), overview (singleton).
- New page vs edit: NEW when it's a distinct entity/concept other pages would
  link to; EDIT when it's an attribute or update of an existing one.
- First mention of any entity in a body must be a [[wikilink]].
- Keep pages under 400 lines; propose splits via lint issues.
- Never cite pages with status pending-review.

## Playbooks

### Session start
1. `wiki ingest status` — finish interrupted work before anything else.
2. `wiki issues list --status open` — know the outstanding repairs.

### Retrieval (answering questions)
1. `wiki index show --json` and/or `wiki search <terms>` to route.
2. Select at most 10 candidate pages.
3. Read only those bodies.
Never scan bodies to discover relevance.

### Tool selection
| Intent | Command |
|---|---|
| What work is unfinished? | `wiki ingest status` |
| What do I do next for this source? | `wiki ingest resume <source-id>` |
| Route to candidate pages | `wiki index show [--type <t>]` |
| Find a term across the vault | `wiki search <terms> [--kind page\|source] [--regex]` |
| What links here? | `wiki page backlinks <id\|name>` |
| Read one page | `wiki page show <id\|name> [--frontmatter-only]` |
| Read a raw source | `wiki source show <id>` |
| Which pages cite this source? | `wiki source impact <id>` |
| Write or replace a page body | `wiki page upsert --type <t> --title "…" [--id <id>] --summary "…" --sources <ids> --stdin` |
| Record ingest progress | `wiki ingest advance <source-id> --to <state> [--touched <ids>]` |
| Check vault health | `wiki lint` |
| See outstanding repairs | `wiki issues list --status open [--kind <k>]` |
| Close a repair | `wiki issues resolve <issue-id> --note "…"` |
| Amend these instructions | `wiki schema propose --section "<heading>" --stdin` |

Always pass `--json`. Exit codes: 0 success · 1 your input was rejected — read
`errors[].code`, correct it, retry once, never retry blind · 2 environment/IO
problem, do not retry · 3 state conflict, the world is already how you asked,
treat as a no-op and move on.

### Ingest
1. The human registers the source:
   `wiki source add <file> --category <id> --title "…"` → state `registered`.
2. Read it: `wiki source show <source-id>` (reading is unrestricted; only
   writing is mediated).
3. Write the summary page, then record the step:
   `wiki page upsert --type summary --title "…" --summary "…" --sources <source-id> --stdin`
   `wiki ingest advance <source-id> --to summarized`
4. Find the entities/concepts this source affects via `wiki index show` and
   `wiki search`; upsert each one — new page or edit per the heuristic above —
   extending `--sources` with the new source id. Then:
   `wiki ingest advance <source-id> --to integrated --touched <id1,id2,…>`
5. Update `overview.md` if the source changes the top-level picture, then:
   `wiki lint`
   `wiki ingest advance <source-id> --to linted`

Never skip a state: transitions advance one step at a time and each is
precondition-checked. If you lose the thread mid-ingest, `wiki ingest resume
<source-id>` reports exactly what remains.

### Reflect
If an issue kind recurs across lints, draft the new full text for the relevant
AGENTS.md section and submit via `wiki schema propose --section "<heading>" --stdin`
with rationale citing issue IDs. Never edit this file directly.
```

---

## Appendix B — Validation amendments (2026-07-13)

Resolved during a spec-validation pass before implementation. These override the original text where they conflict.

- **A. Reindex scope relaxed.** Byte-identical reindex applies to the idmap and to *structural* ledger state (the state each source is in, derivable from page scans) only. Issue history (`first_seen`/`occurrences`/`last_seen`), the ledger `--touched` audit list, the last-lint timestamp (`.wiki/lint.json`), and review shadow copies are *historical* state markdown does not contain; reindex merge-preserves them best-effort and makes no byte-identity claim. §3, §16 updated.
- **B. `summary` frontmatter key added.** §9's required `--summary` stores frontmatter key `summary`; it is now part of the §7 common schema (previously omitted, which would have made the closed-vocabulary validator reject every page write).
- **C. Schema proposals are full-section replacements, not unified diffs.** `wiki schema propose --section "<heading>" --stdin` submits the new full text of one named AGENTS.md section; the CLI swaps that section verbatim on approve. No patch/diff engine (AOT + cross-platform hazard). §8, §13 updated.
- **D. Last-lint timestamp home.** Stored in `.wiki/lint.json`, written by every `wiki lint`; the `linted` ledger precondition compares it against the ledger's `integrated` timestamp. §10 updated.
- **E. Pending-review pages are indexed.** They appear in `index.md`/`wiki index show` with their `status` so the agent can route to and revise them; AGENTS.md still forbids *citing* them in answers. §9 updated.
- **F. Fuzzy lints stay dumb in v1.** `coverage-gap`, `stale`, and `oversize` use deliberately simple deterministic heuristics (capitalized multi-word token frequency; timestamp + shared-source comparison; line count). No NLP. Refinement is a v2 candidate.
- **G. `ingest status` (no args) lists everything not `linted`.** The §8 table originally said "not `integrated`", but a source at `integrated` still needs a lint pass to finish; showing only non-`integrated` sources would hide that outstanding work. "Not `linted`" = all incomplete ingests, which is what the resume/status guarantee is for.
- **J. `linted` precondition is "lint ran at-or-after integration", not strictly after.** §10's `linted` row says "A lint run newer than the `integrated` timestamp exists". Timestamps are second-granularity, and the canonical agent flow is integrate-then-lint back-to-back — so an integrate and a lint landing in the same wall-clock second (realistic when an agent scripts commands) would fail a strict `lastRun > integratedAt` check even though the lint genuinely ran after integration, and would keep failing for up to a second with no hint why. So the precondition rejects only `lastRun < integratedAt` (i.e. accepts `lastRun >= integratedAt`). Worst case (a lint in the same second but fractionally before the integrate) is negligible and self-corrects on the next lint.
- **I. Retraction cascade skips `archived` citing pages; already-retracted is a state conflict.** §14 step 3 says "every other page whose `sources` include the ID → `needs-review`", but flipping an `archived` page (which §7 defines as excluded from index and lint) back to `needs-review` resurrects dead history into active scope, contradicting the archive's purpose. So the cascade flags only NON-archived citing pages (`active`/`pending-review`/`needs-review`) → `needs-review` + a `retraction` issue; `archived` citers are left untouched. Also: `wiki source retract` on an already-retracted source is a state conflict (exit 3, idempotent no-op reported via `StateConflictException`), NOT a blocking input error (exit 1) — consistent with re-advancing a ledger to its current state.
- **H. `IssueKind` is a closed enum broader than the §11 lint table.** The §11 table lists the 9 *advisory lint* kinds. But the system also files issues from non-lint workflows: review `reject` (§15) and source `retract` (§14, which literally references `wiki issues list --kind retraction`). So the closed `IssueKind` vocabulary is those 9 lint kinds PLUS `review-rejected` (a human rejected a pending page; the agent must revise) and `retraction` (a cited source was retracted; the page needs repair). These MUST be distinct kinds — reusing a lint kind like `pending-backlog` for a rejection collides on the Issues merge key `(kind, subject)` and silently corrupts the unrelated lint record's detail/occurrences (the reflect-loop signal). `review-rejected` is added in Task 23; `retraction` in Task 24.
- **K. The review shadow holds the last REVIEWED body, and reject restores the prior status.** §15 said `page upsert` under the gate "preserves the previous body" — implemented literally, that overwrote `.wiki/review/<id>.prev.md` on *every* gated update. Two updates before a review therefore left the shadow holding the first *un-approved* edit, and rejecting restored that: the page landed `active` carrying content no human ever approved, which is precisely the outcome the gate exists to prevent. So the shadow is written only when none already exists — the first gated update after a review captures the reviewed body, and subsequent updates leave it alone. `approve`/`reject` clear it, re-arming the capture. Second half: `reject` on an update hardcoded `status: active`, silently clearing the flag on a page that was `needs-review` before the edit. The pre-gate status is now captured alongside the shadow body and restored with it; `active` is only the fallback when no captured status exists (a shadow written by an older build).
- **L. `--allow-dangling` files its issues at write time.** §11.4 already said permitted forward references "are then filed automatically as `dangling-link` issues rather than silently ignored", but the upsert only *returned* the targets in its `danglingFiled` envelope field and left the actual filing to whenever `wiki lint` next ran. The field name asserted something untrue, and an agent that upserts with `--allow-dangling` and then reads `wiki issues list` saw nothing. Upsert now calls `Issues.Upsert(DanglingLink, <page-slug>, …)` itself, using the same `(kind, subject)` merge key lint uses — so a link that stays dangling accumulates occurrences on ONE record across both paths instead of forking into two.
- **M. An explicit vault path must be a vault.** §8's resolution order (`--vault` → `WIKI_VAULT` → walk up from CWD) only validated the walk-up branch, which stops at a `wiki.yaml` by construction. The two explicit branches accepted any string: `wiki page list --vault ./typo --json` returned `{"ok":true,"data":[]}` with exit 0, making "this vault is empty" and "this path is not a vault" indistinguishable to the agent that is supposed to trust `ok`. Both explicit branches now require `wiki.yaml` at the resolved root and raise `no-vault` (exit 1) otherwise. `wiki init` is exempt — it is the command that *creates* the `wiki.yaml`, so it takes its target path as an argument rather than through resolution.
- **N. The category-in-use rule is enforced, on config load.** §5's "removing a category that sources still reference is a blocking config error" had no implementation: `VaultConfig.Load` validated syntax, kebab-case and duplicates but never looked at `raw/`. Enforcement now lives in the config-load path used by commands (not in `VaultConfig.Load` itself, which parses a file and knows nothing about a vault), so a config that has lost a referenced category fails every command that reads it, per "blocking config error". Two carve-outs keep it usable: `wiki category …` is exempt, because `wiki category add <missing-id>` is the intended repair and a check that blocks its own fix is a trap; and the error names the offending category, the sources referencing it, and the exact `wiki category add` line to run. Cost is one frontmatter scan of `raw/*.md` per invocation of a config-reading command, which is the same scan `source list` already does.
- **O. `wiki search` covers `raw/` sources, and reports truncation.** §8 scoped search to "frontmatter + bodies" without saying whose; the implementation read wiki pages only, so the agent's one text-search primitive was blind to the raw material the wiki is built from — with no way to find which source mentioned a term except reading files directly, which §13's retrieval playbook forbids. Search now scans pages and sources both; each `Hit` carries `kind: "page" | "source"`, and `--kind` filters to one or the other (`--type` continues to filter page types and implies `--kind page`). Separately, `--limit` used to stop the scan silently, so a truncated result was indistinguishable from an exhaustive one; the result is now an object `{hits, truncated, scanned}` rather than a bare array.
- **P. Human-mode errors render as Spectre, not JSON.** §8 says "human-facing output uses Spectre.Console rendering", and success paths did — but every failure path emitted the raw JSON envelope regardless of `--json`, so an interactive user got `{"v":1,"ok":false,…}` with `'`-escaped quotes. Failures now render as a Spectre error line (code, message, and `path` when present) unless `--json` was passed. The envelope, the error codes, and the exit codes are unchanged in both modes — this is presentation only, and `--json` output is byte-for-byte what it was.

## Appendix C — Portability and content amendments (2026-08-16)

Resolved while working the issues filed on 2026-08-16. Same standing as Appendix B: these override the original text where they conflict.

- **Q. Source hashing is newline-insensitive, and legacy hashes are tolerated rather than migrated.** (issue #5) `source add` hashed the raw result of `File.ReadAllText` with no normalisation, so the same document produced a different `sha256` depending on whether it arrived with CRLF or LF — and `duplicate-source` therefore missed on a vault shared between machines through git, on a source fetched once on Windows and once on Linux, and on any inbox pipeline that rewrites line endings. Content-addressed dedup that is sensitive to invisible whitespace is not content-addressed. Line endings (CRLF *and* lone CR) now normalise to LF before hashing, and the normalised form is what gets written to `raw/` — `raw/` is immutable *content*, not a byte-for-byte forensic copy, and the vault is already committed to markdown-on-disk portability. **Migration:** existing vaults are not rewritten. Rewriting every source's frontmatter to fix what is effectively a cache field is a much bigger hammer than the problem, and `wiki reindex` explicitly rebuilds `.wiki/` from the markdown alone — `sha256` lives in `raw/` frontmatter, not in the cache, so reindex is the wrong lever. Instead the dedup scan is legacy-tolerant: for each existing source it compares the candidate hash against the stored `sha256` *and*, if that misses, against a hash of the stored body computed the new way. Sources registered by an older build therefore keep deduping correctly without their bytes changing. Cost is one hash per `raw/` file on `source add`, a rare and already-IO-bound command. The forward-slash path normalisation that was copy-pasted at six call sites moved onto `Vault.RelativePath` in the same pass — a separator convention is a property of the vault format.
- **R. `source add` rejects non-text content.** (issue #4) `SourceService.Add` read every registered file with `File.ReadAllText` and no check that the bytes were text, so handing it a PDF, a `.docx` or an image *succeeded*: the mojibake was hashed, wrapped in source frontmatter, written to `raw/`, and entered in the ledger as `registered`. Nothing downstream caught it — the agent read the garbage back through `wiki source show` and dutifully wrote a summary page from it, and `wiki lint` could not see the problem because it is semantic, not structural. Registration now rejects with `source-not-text` (exit 1, nothing lands) when the content contains a NUL byte in the first 8 KB or fails a strict UTF-8 decode. Deliberately a **content** check rather than an extension allowlist: an extension tells you nothing useful, and a `.md` file containing a pasted PDF blob should still be rejected. A UTF-8 BOM is stripped as an encoding artefact of the producer rather than treated as content. This is a precondition for bulk registration (amendment T), not a nicety alongside it.
- **S. The CLI fixes its own stream encoding, and the two body sources are mutually exclusive.** (issues #6, #7) Nothing set `Console.OutputEncoding`/`InputEncoding`, so on Windows the standard streams used the console code page: a body piped in on `--stdin` containing an accented name, a curly quote, CJK or an em dash could be decoded wrong and stored corrupted (the write succeeds; the corruption is silent and permanent), and the `--json` envelope could be emitted in a non-UTF-8 encoding, which is a contract violation for everything parsing it. The CLI is the only thing that writes to the vault, so it guarantees the encoding rather than inheriting it: the real process entrypoint wraps the raw standard streams in explicit UTF-8 (no BOM — a BOM in front of a JSON envelope breaks strict parsers) and best-effort sets the console's output code page for display. `Main(string[], TextWriter, TextReader)` — the in-proc test overload — is untouched, which is what keeps this testable. Separately, `--body-file` (already accepted by `page upsert` and `schema propose`) now shares one resolver with `--stdin`: the file is read as UTF-8 explicitly rather than inheriting a default, and supplying **both** flags is a `body-source-conflict` validation error rather than a silent preference for one of them.
