# Functional flow

What the system *does*, in terms of the people and processes involved: how a
raw file becomes wiki pages, how those pages get approved, what happens when a
source turns out to be wrong, and how the agent's own instructions improve over
time.

For code structure see [architecture](architecture.md); for what happens inside
one command see [technical flow](technical-flow.md).

---

## Who does what

```mermaid
flowchart TB
    subgraph human["Human"]
        H1["Curate sources"]
        H2["Own wiki.yaml + categories"]
        H3["Approve/reject pages"]
        H4["Approve/reject AGENTS.md amendments"]
        H5["Browse in Obsidian"]
    end
    subgraph agent["LLM agent"]
        A1["Read sources and pages"]
        A2["Author all prose"]
        A3["Decide new page vs. edit"]
        A4["Repair issues"]
        A5["Propose amendments"]
    end
    subgraph cli["wiki CLI"]
        C1["Validate every mutation"]
        C2["Assign IDs, maintain index/log"]
        C3["Track ledger + issues"]
        C4["Enforce the review gate"]
    end

    human -->|"source add, review, category"| cli
    agent -->|"page upsert, ingest advance, lint"| cli
    cli -->|"--json results, error codes"| agent
    cli -->|"markdown"| human
```

The division is not arbitrary. The agent does the work that requires judgement
and produces prose. The CLI does the work that requires never forgetting. The
human owns anything that changes the rules.

## The page types

Five types, a closed set. Lint rules, the ingest workflow and the
new-page-vs-edit heuristic are all defined in terms of them.

| Type | Lives in | Is |
|---|---|---|
| `source` | `raw/` | Immutable raw material plus registration metadata |
| `summary` | `wiki/summaries/` | One per source: takeaways, claims, references |
| `entity` | `wiki/entities/` | A distinct nameable thing — person, org, product, place |
| `concept` | `wiki/concepts/` | A theme synthesized across sources; carries the evolving thesis |
| `overview` | `wiki/overview.md` | Singleton top-level synthesis |

The heuristic the agent applies: **new page** when it is a distinct
entity/concept you would link to from elsewhere; **edit in place** when it is
an attribute or update of something that already exists.

Each page's `status` drives how the rest of the system treats it:

```mermaid
stateDiagram-v2
    [*] --> active: upsert, gate off
    [*] --> pending_review: upsert, gate on
    pending_review: pending-review
    needs_review: needs-review
    pending_review --> active: review approve
    pending_review --> archived: review reject (a create)
    pending_review --> active: review reject (an update — prior body + status restored)
    active --> needs_review: source retracted / lint flag
    needs_review --> active: repaired and re-upserted
    active --> archived: its source was retracted
```

`archived` pages are excluded from the index and from lint entirely.
`pending-review` pages *are* indexed — so the agent can route to and revise
them — but `AGENTS.md` forbids citing them in answers.

## Ingest: source in, wiki out

Ingest is a state machine per source, recorded in `.wiki/ledger.json`. Each
transition has a precondition the CLI checks before recording it, so the ledger
cannot claim work that didn't happen.

```mermaid
stateDiagram-v2
    [*] --> registered: wiki source add
    registered --> summarized: a summary page cites this source
    summarized --> integrated: entity/concept pages upserted, index consistent
    integrated --> linted: a lint ran at or after the integrate
    linted --> [*]
```

| State | Means | Precondition checked on advance |
|---|---|---|
| `registered` | File copied into `raw/`, hashed, frontmatter valid | entry state |
| `summarized` | Summary page exists | a `summary` page lists this source in `sources` |
| `integrated` | Affected entity/concept pages updated | `--touched id1,id2,…` supplied (may be empty), index verified consistent |
| `linted` | Health checked | `.wiki/lint.json`'s timestamp is at or after the `integrated` timestamp |

The full round trip:

```mermaid
sequenceDiagram
    autonumber
    actor H as Human
    actor A as Agent
    participant C as wiki CLI
    participant V as Vault

    H->>C: source add notes.md --category article --title "…"
    C->>V: copy to raw/<ULID>.md, hash, dedup
    C->>V: ledger: registered
    C-->>H: {id, path, sha256}

    A->>C: source show <id>
    C-->>A: frontmatter + raw body
    A->>A: read, decide what this affects
    A->>C: page upsert --type summary --sources <id> --stdin
    C->>V: wiki/summaries/<slug>.md + index + log
    A->>C: ingest advance <id> --to summarized

    A->>C: index show / search <terms>
    C-->>A: routing candidates (never full bodies)
    A->>C: page upsert --type entity|concept … (per affected page)
    C->>V: page files + index + log
    A->>C: ingest advance <id> --to integrated --touched <ids>

    A->>C: page upsert --type overview --id <id> (if warranted)
    A->>C: lint
    C->>V: issues.json, lint.json
    C-->>A: findings
    A->>C: ingest advance <id> --to linted
```

**The resume guarantee.** A fresh session with no conversation context runs
`wiki ingest status`, sees `01M05… : summarized`, runs `wiki ingest resume
01M05…`, and gets back exactly what remains:

```json
{"sourceId":"01M05GXZ…","current":"integrated","remainingStates":["linted"],
 "expectedArtifacts":["a 'wiki lint' run recorded in .wiki/lint.json newer than this source's 'integrated' timestamp"]}
```

This — not format validation — is the primary defence against an agent losing
the thread mid-task.

## Retrieval: routing, not scanning

The agent is forbidden from scanning page bodies to discover what's relevant.
That's the difference between a knowledge base that stays cheap to query and
one that gets more expensive with every page added.

```mermaid
flowchart LR
    Q["Question"] --> R{"Route"}
    R -->|"index show"| I["Catalog:<br/>slug, title, one-line summary"]
    R -->|"search terms"| S["Matching lines only<br/>pages + raw sources"]
    R -->|"page backlinks"| B["Inbound links"]
    I --> P["Pick ≤10 candidates"]
    S --> P
    B --> P
    P --> RD["page show / source show<br/>read only those bodies"]
    RD --> ANS["Answer<br/><i>never citing pending-review pages</i>"]
```

`index.md` is what makes this work: every page carries a required one-line
`summary` supplied at upsert time, and the index is regenerated from those on
every page mutation. An entry that doesn't route is useless, which is why
`--summary` is a blocking requirement rather than a nicety.

`wiki search` covers raw sources as well as pages, returns matching *lines*
rather than bodies, and reports `truncated` when `--limit` cut the scan short —
so a partial result is never mistaken for an exhaustive one.

## The review gate

Set `review_gate: true` in `wiki.yaml` and every page write lands as
`pending-review` until a human says otherwise.

```mermaid
sequenceDiagram
    autonumber
    actor A as Agent
    participant C as wiki CLI
    participant V as Vault
    actor H as Human

    A->>C: page upsert --id <id> (an update)
    C->>V: stash current doc to .wiki/review/<id>.prev.md<br/>(only if no shadow exists yet)
    C->>V: write new body, status = pending-review
    H->>C: review list
    C-->>H: pending pages + diff against the shadow
    alt approve
        H->>C: review approve <id>
        C->>V: status = active, shadow cleared
    else reject
        H->>C: review reject <id> --note "…"
        C->>V: restore shadow body AND its pre-gate status
        C->>V: file a `review-rejected` issue for the agent
    end
```

Two details matter and both were bugs once:

- The shadow is written **only when none exists**. A run of consecutive
  un-reviewed edits therefore keeps pointing at the last body a human actually
  signed off on — otherwise rejecting would restore an intermediate revision
  nobody approved, which is precisely what the gate exists to prevent.
  Approve/reject clear the shadow, re-arming the capture.
- Reject restores the page's **pre-gate status**, not an unconditional
  `active`. Rejecting an edit to a `needs-review` page must not silently clear
  its flag.

Rejecting a *create* has no shadow to restore, so the page is archived instead.
Either way an issue is filed, so the rejection reaches the agent as work rather
than as a message it might not read.

## Health: lint, issues, and the reflect loop

`wiki lint` never edits page content. It observes, and files what it finds as
issues merged on `(kind, subject)` — so a problem that survives several lint
runs accumulates `occurrences` on one record instead of spawning a new row each
time.

| Kind | Flags |
|---|---|
| `orphan` | Active page with zero inbound wikilinks |
| `dangling-link` | Wikilink target missing |
| `stale` | Summary older than `staleness_days` with newer sources on the same subjects |
| `coverage-gap` | A term mentioned across ≥3 pages with no page of its own |
| `index-drift` | Index entries ≠ actual pages (auto-fixed, but filed so the cause gets found) |
| `oversize` | Page over `max_page_lines` — a split candidate |
| `rename-drift` | Idmap path mismatch, i.e. someone renamed a file in Obsidian |
| `needs-review-backlog` / `pending-backlog` | Pages sitting in a review state over 14 days |
| `review-rejected` | A human rejected a pending page; the agent must revise |
| `retraction` | A cited source was retracted; the page needs repair |

Only `--fix-links` repairs anything, and only mechanical link targets after a
detected rename. Everything else is a tracked repair job.

The occurrence count is the point:

```mermaid
flowchart LR
    L["wiki lint"] --> I["issues.json<br/>occurrences++"]
    I --> OBS{"Same kind<br/>keeps recurring?"}
    OBS -->|"no"| FIX["Agent repairs<br/>the individual page"]
    OBS -->|"yes"| DR["Agent drafts a new full section<br/>for AGENTS.md, citing issue IDs"]
    DR --> PR["schema propose --section '…' --stdin"]
    PR --> HU{"Human reviews"}
    HU -->|"schema approve"| AP["CLI swaps that section<br/>in AGENTS.md verbatim, logs it"]
    HU -->|"schema reject"| RJ["Recorded, nothing applied"]
    AP --> BETTER["Next session starts<br/>with better instructions"]
```

A recurring finding is evidence that the *instructions* are deficient, not that
the agent made a one-off mistake. `AGENTS.md` is the system's only learning
surface — the operational memory that compounds — and amendments to it are
full-section replacements, never diffs: deterministic to apply, no patch engine
to go wrong. The agent may propose. Only the human applies.

## Retraction: when a source turns out to be wrong

```mermaid
flowchart TD
    R["wiki source retract &lt;id&gt; --reason '…'"] --> S1["Source frontmatter → retracted<br/>(--purge also strips the body, keeping a stub)"]
    S1 --> S2["Its summary page → archived"]
    S2 --> S3["Every other NON-archived citing page → needs-review<br/>+ a `retraction` issue naming the reason"]
    S3 --> S4["Index regenerated, log line written"]
    S4 --> W["Agent works the queue:<br/>issues list --kind retraction"]
    W --> W1["Rewrite the body, dropping claims<br/>that rested on the retracted source"]
    W1 --> W2["Drop the source id from `sources`, upsert"]
    W2 --> W3["Back to active (via review, if gated)"]
```

The raw file is not deleted by default — the archived summary is what the agent
reads to work out which claims rested on it. Already-archived citing pages are
left alone: flipping them back to `needs-review` would resurrect dead history
into active scope. Retracting an already-retracted source is a state conflict
(exit 3), not an error.

Deletion is thus a tracked repair job with a work queue, rather than
archaeology.

## Category and config changes

Categories are per-vault and entirely user-defined; the CLI never invents one.
An unknown category on `source add` is a blocking error telling the human to
add it first. Removing a category that registered sources still reference is a
blocking config error raised on *every* config-reading command — with one
carve-out: `wiki category …` itself is exempt, because `wiki category add
<missing-id>` is the intended repair, and a check that blocks its own fix is a
trap.
