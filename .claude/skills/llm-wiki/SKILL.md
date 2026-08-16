---
name: llm-wiki
description: Operate an llm-wiki vault through the `wiki` CLI - ingest raw sources into wiki pages, answer questions from the vault, and keep it healthy. Use when a vault is present (wiki.yaml + AGENTS.md), when the user mentions the wiki/vault/ingest/lint, or when running unattended on a loop or schedule to advance outstanding wiki work.
---

# Operating an llm-wiki vault

You do the semantic work: reading sources, authoring prose, deciding
create-vs-update. The CLI does the bookkeeping: IDs, schema validation, indexes,
ledger state, lint. Stay on your side of that line.

## Invariants

1. **Never write to vault files directly.** No Write, Edit, `mv`, `rm`, or
   shell redirection against anything under the vault. Every mutation goes
   through `wiki`. Editing a file by hand bypasses validation, ID assignment,
   indexing, and the log — it corrupts the vault silently.
   Reading is unrestricted: prefer `wiki source show` / `wiki page show` for
   sources and pages, and read `AGENTS.md` and `wiki.yaml` directly.
2. **Always pass `--json`.** Parse the envelope. Human output is for humans.
3. **Bodies arrive on stdin.** Never as an argument.
4. **One unit of work per tick** — see The Tick.

## Preflight

Locate the vault, in this order: `--vault <path>` → `$WIKI_VAULT` → auto-detect
from the working directory. Confirm with any read command; `no-vault` means none
of the three resolved, so stop and ask where the vault is.

Then **read the vault's `AGENTS.md`** (`wiki page show` does not serve it — read
the file; it is documentation, not vault content). It carries the conventions
this specific vault runs on, and it is amendable at runtime through the
proposal loop, so it can differ from what you saw last session. It is the
authority on conventions; this skill is the authority on mechanics.

## The envelope

Every command returns the same four keys:

```json
{"v":1,"ok":true,"data":{...},"errors":[]}
```

On failure `data` is `null` and `errors[]` is populated. Branch on the exit
code, then on `errors[].code`:

| Exit | Meaning | What you do |
|---|---|---|
| 0 | Success | Continue. |
| 1 | Your input was rejected | Read `errors[].code`, correct it, retry **once**. Never retry blind. |
| 2 | Environment/IO problem | Do not retry. Report to the human and stop. |
| 3 | State conflict | The world is already how you asked. Treat as a no-op and move on — this is not an error. |
| 4 | A measurement came in under a threshold you asked for | Only `wiki eval --fail-under`. The report is still in `data`. Not an error; read the score. |

## The Tick

Run this ladder on every wake-up. It assumes zero conversation context — the
vault's state is the only memory you need. Take the **first** rung that has
work, do that one unit, then stop.

1. **Finish interrupted work.** `wiki ingest status --json` lists every source
   not yet `linted`. Non-empty? Take the oldest entry and run
   `wiki ingest resume <source-id> --json` — it reports the remaining states and
   the exact artifacts each one expects. Do that. Unfinished work always
   outranks new work.
2. **Start new work.** All sources linted? `wiki source list --status active
   --json` for anything registered but absent from the ledger's active set —
   begin its ingest at step 1 of the Ingest playbook. Nothing outstanding, and
   the human has configured an inbox? `wiki source scan <inbox-dir> --category
   <id> --json` first; anything it registers becomes this tick's work.
3. **Repair.** `wiki issues list --status open --json`. Take the highest
   `occurrences` count and fix it (see Issue kinds).
4. **Detect.** No open issues? `wiki lint --json` to file fresh findings. If
   `filed` or `refreshed` is non-zero, the next tick handles them.
5. **Reflect.** A clean lint, but an issue kind keeps recurring across ticks?
   Draft an amendment: `wiki schema propose --section "<exact heading>"
   --rationale "<cite issue ids>" --stdin`. A human approves it. Never edit
   `AGENTS.md` directly.
6. **Nothing to do.** Say so plainly and stop. Do not invent work — a quiet
   vault is a healthy one.

**Why one unit per tick:** each tick stays bounded and context-sized, and a
crash costs one source rather than a session. `ingest resume` makes the next
tick pick up exactly where this one died.

**Never leave a ledger half-advanced.** If you cannot complete a state
transition, stop before advancing rather than advancing and hoping.

## Ingest playbook

A source moves `registered → summarized → integrated → linted`, one step at a
time, each precondition-checked. Skipping a state fails with
`precondition-order`.

**Step 0 — registration.** The human decides *what* enters the vault. The
*typing* is mechanical and can be yours:

- The human registers one file at a time with `wiki source add <file>
  --category <id> --title "…"`.
- If they have named an **inbox directory**, you register it in bulk:
  `wiki source scan <inbox-dir> --category <id> --json`. It copies each
  not-yet-registered file into `raw/`, hashes and dedups it, and enters the
  ledger. Content is hash-deduped, so re-running is a clean no-op — safe on
  every tick. Use `--dry-run` the first time you point it at a new directory.

Only ever scan a directory the human has explicitly named as an inbox. If no
inbox is configured and nothing is registered, you have no ingest work — do
not go looking for files to add.

`scan` exits 0 even when individual files were rejected: read `entries[]` for
per-file outcomes (`registered`, `skipped-duplicate`, `skipped-empty`,
`rejected` with a `code`). Report the rejections; do not retry them blind.

**Choosing a category needs no new command.** `wiki category list --json` plus
the source content is enough to pick an existing one — do that first. Only when
nothing fits, propose one and stop:

```bash
wiki category propose <id> --description "…" \
  --rationale "why nothing existing fits" \
  --sources <the source ids that fit nothing> --json
```

A human approves or rejects it (`wiki category approve|reject`). You may never
run `wiki category add`. Cite the source ids — they are the evidence that makes
the decision reviewable instead of an argument about a name in the abstract.

**Step 1 — read it.** `wiki source show <source-id> --json`.

**Step 2 — summarize.** One summary page per source.

```bash
wiki page upsert --type summary --title "…" --summary "…" \
  --sources <source-id> --stdin --json
wiki ingest advance <source-id> --to summarized --json
```

**Step 3 — integrate.** Find what this source affects via `wiki index show
--json` and `wiki search <terms> --json`. For each entity/concept: new page if
it is a distinct thing other pages would link to, edit if it is an attribute or
update of an existing one. Extend `--sources` with the new source id. Then:

```bash
wiki ingest advance <source-id> --to integrated --touched <id1,id2,…> --json
```

`--touched` is required here and may be empty if the source genuinely changed
nothing beyond its summary.

**Step 4 — lint and close.** Update `overview.md` if the top-level picture
changed, then:

```bash
wiki lint --json
wiki ingest advance <source-id> --to linted --json
```

### Updating an existing page

`page upsert --id <id>` **replaces the entire body**. Read the current body
with `wiki page show <id> --json` first and send back the complete new text.
Sending only your addition silently destroys the rest of the page.

Every update reports what it **removed** in `data.contentLoss`:
`removedLinks`, `removedSources`, `lossPercent`, and the old/new line counts.
Read it in the same tick that produced it. If you dropped a link or a source
you did not mean to drop, upsert again with the complete text — that is far
cheaper than the alternative, which is a fact silently disappearing from a page
that still looks fine. Above the vault's threshold the CLI files a
`content-loss` issue (`issueFiled: true`). If the removal was deliberate — a
split, a retraction repair — resolve that issue with a note saying so.

### Wikilinks

First mention of any entity in a body must be a `[[wikilink]]`. Links to pages
that do not exist yet are rejected with `dangling-link`. Two ways forward:

- Create the target page first (preferred when you are about to anyway).
- Pass `--allow-dangling` — the write lands and the CLI files a
  `dangling-link` issue listing the targets in `data.danglingFiled`. **You now
  owe those pages.** Create them, then close the issue.

## Retrieval playbook

Answering a question from the vault:

1. `wiki index show --json` (add `--type` to narrow) and/or
   `wiki search <terms> --json` to route.
2. Select **at most 10** candidate pages.
3. `wiki page show <id-or-name> --json` on only those.

Never scan bodies to discover relevance — that is what the index and search are
for. `search` returns matching lines only, never full bodies. Use
`--frontmatter-only` when you just need to check status or sources.

**Never cite a page whose status is `pending-review`.**

## Issue kinds

`wiki lint` files findings with an occurrence count; it does **not** close
them. After fixing one, close it yourself:
`wiki issues resolve <issue-id> --note "…" --json`.

| Kind | Fix |
|---|---|
| `dangling-link` | Create the missing target pages, then resolve. |
| `orphan` | Link the page from somewhere real, or archive it via `page set-status`. |
| `stale` | Re-integrate the source whose summary drifted. |
| `coverage-gap` | A source has no summary page — finish its ingest. |
| `oversize` | Page over ~400 lines: split it into linked pages. |
| `index-drift` | `wiki reindex --json` rebuilds from markdown alone. |
| `rename-drift` | Someone renamed a file in Obsidian: `wiki lint --fix-links --json`. |
| `needs-review-backlog`, `pending-backlog` | Human-gated — report, don't act. |
| `review-rejected` | Read the rejection, rewrite the page, resubmit. |
| `retraction` | A cited source was retracted: revise each citing page to drop it. |
| `content-loss` | An update dropped a large share of a page's links/sources. Restore what should not have gone, or resolve with a note if the removal was deliberate. |

## What needs a human

Do these **never**; surface them and stop:

- `wiki source add` — one-off registration is theirs. You may run `wiki source
  scan` against an inbox directory they have configured; you may not decide
  what belongs in the vault, and you may not scan a directory nobody named.
- `wiki review approve` / `reject` — the review gate is theirs.
- `wiki schema approve` / `reject` — you may *propose*, they dispose.
- `wiki category add` — categories are schema. You may `wiki category
  propose`; they dispose.
- `wiki source retract` — destructive and cascading.

Running unattended, collect these into one clear report at the end of the tick
rather than pausing mid-work.

## Command reference

Every command accepts `--json` and `--vault`.

| Intent | Command |
|---|---|
| What is unfinished? | `wiki ingest status [<source-id>]` |
| What remains for this source? | `wiki ingest resume <source-id>` |
| Record ingest progress | `wiki ingest advance <source-id> --to <state> [--touched <ids>]` |
| Route to candidate pages | `wiki index show [--type <t>]` |
| Find a term | `wiki search <terms> [--kind page\|source] [--type <t>] [--regex] [--limit N]` |
| Read a page | `wiki page show <id-or-name> [--frontmatter-only]` |
| List pages | `wiki page list [--type <t>] [--status <s>] [--orphans]` |
| What links here? | `wiki page backlinks <id-or-name>` |
| Write/replace a page | `wiki page upsert --type <t> --title "…" --summary "…" [--id <id>] [--sources <ids>] [--tags <t>] [--allow-dangling] --stdin` |
| Change page status | `wiki page set-status <id> <status>` |
| Rename a page | `wiki page rename <id> <new-slug>` (rewrites inbound links) |
| Read a raw source | `wiki source show <id>` |
| List sources | `wiki source list [--status <s>] [--category <c>]` |
| Register a configured inbox | `wiki source scan <dir> --category <id> [--dry-run]` |
| Who cites this source? | `wiki source impact <id>` |
| Check vault health | `wiki lint [--fix-links]` |
| Score retrieval quality | `wiki eval [--k N] [--fail-under N]` (needs a human-owned `eval.yaml`) |
| Outstanding repairs | `wiki issues list [--kind <k>] [--status open\|resolved]` |
| Close a repair | `wiki issues resolve <id> --note "…"` |
| Rebuild derived state | `wiki reindex` |
| List categories | `wiki category list` |
| Propose a new category | `wiki category propose <id> --description "…" --rationale "…" --sources <ids>` |
| Propose an AGENTS.md change | `wiki schema propose --section "<heading>" --rationale "…" --stdin` |

Enums: page type `summary | entity | concept | overview` · page status `active |
pending-review | needs-review | archived` · ledger state `registered |
summarized | integrated | linted` · source status `active | retracted`.

## Error codes

| Code | Meaning and fix |
|---|---|
| `no-vault` | No vault resolved. Stop; ask for the path. |
| `precondition-order` | You skipped a ledger state. Run `ingest resume` and take the next one. |
| `precondition-summary` / `precondition-index` / `precondition-lint` | The artifact that state requires does not exist yet. Produce it, then advance. |
| `state-conflict` (exit 3) | Already done. Move on. |
| `dangling-link` | Create the targets, or re-run with `--allow-dangling` and own the filed issue. |
| `duplicate-title` | A page with this title exists. Update it with `--id` instead of creating. |
| `duplicate-source` | Identical content already registered. Not an error — reuse the existing id. Line endings do not matter; the hash is newline-insensitive. |
| `source-not-text` | The file is binary (PDF, image, `.docx`) or not valid UTF-8. Nothing was registered. Convert it to text first — do not retry as-is. |
| `body-source-conflict` | You passed both `--stdin` and `--body-file`. Pick one. |
| `summary-required` | Pass `--summary`. It is the routing description the index shows. |
| `frontmatter-schema` | Unknown key or bad enum value. Correct it against the enums above. |
| `not-found` / `id-or-name` | Bad id or slug. Re-route via `index show` or `search`. |
| `overview-exists` | Only one overview page. Update it with `--id`. |
| `invalid-category-id` | Not a kebab-case id. Fix the id. |
| `unknown-category` | Category is not in `wiki.yaml`. Pick an existing one from `category list`, or `wiki category propose` a new one and stop. |
| `duplicate-category` | The category already exists. Use it. |
| `unknown-command` | You invented a command. Check the reference above. |
| `io-error` (exit 2) | Environment problem. Do not retry; report. |

## Recovery

- Lost the thread mid-ingest → `wiki ingest resume <source-id>`.
- Index or idmap looks wrong → `wiki reindex`. `.wiki/` is a cache; it rebuilds
  from the markdown alone. Nothing derived is precious.
- Someone edited the vault in Obsidian → `wiki lint --fix-links`, then work the
  issues it files.
