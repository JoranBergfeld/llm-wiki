# An example vault

This is a real vault, not a mock-up. Every command, every file and every error
message below was produced by running `wiki` against the three sources named
here, and the output is pasted as it came back.

The vault documents llm-wiki using llm-wiki. Its sources are this repository's
own `README.md`, `docs/architecture.md` and `docs/functional-flow.md`, so you
can read the raw material and the pages derived from it side by side.

It is small on purpose — three sources, eight pages. Enough to show the shape,
short enough to read end to end.

---

## 1. Scaffold, and register the sources

```
wiki init ./vault --name "llm-wiki docs"
export WIKI_VAULT=$PWD/vault

wiki category add documentation --description "Project documentation and specs"
```

Categories are yours to define; two ship in the scaffold and this vault adds a
third. Then each source is copied into `raw/` under a new ULID, hashed, deduped
and entered in the ledger as `registered`:

```
wiki source add ../README.md               --category documentation --title "llm-wiki README.md" --json
wiki source add ../docs/architecture.md    --category documentation --title "llm-wiki architecture.md" --json
wiki source add ../docs/functional-flow.md --category documentation --title "llm-wiki functional-flow.md" --json
```

```json
{"v":1,"ok":true,"data":{"id":"01M0YH3FFTX2QH4RCZ89GFB478","path":"raw/01M0YH3FFTX2QH4RCZ89GFB478.md","sha256":"5bee0575d1961de54635571e1c0eb5158924fad6a047a381b9db8526d510a5e6","category":"documentation"},"errors":[]}
```

The sha256 is what makes re-registering the same file a no-op rather than a
duplicate. `raw/` is immutable from here on: nothing but `source add` ever
writes there.

## 2. The agent writes pages

Entities and concepts first, so that links from later pages resolve. Bodies go
in a file and you pass the path — `--body-file` keeps quoting, escaping and
encoding out of the hot path:

```
wiki page upsert --type entity  --title "wiki CLI" --summary "…" --sources <readme>,<arch> --tags cli-enforcement,json-contract,validation --body-file ./b.md --json
wiki page upsert --type entity  --title "Vault"    --summary "…" --sources <readme>,<arch> --tags vault-layout,portability,derived-state --body-file ./b.md --json
wiki page upsert --type concept --title "Ingest state machine" --summary "…" --sources <flow>,<readme> --tags ingest-lifecycle,resumability,preconditions --body-file ./b.md --json
wiki page upsert --type concept --title "Division of labour"   --summary "…" --sources <readme>,<arch>,<flow> --tags design-rationale,drift-control,cli-enforcement --body-file ./b.md --json
```

```json
{"v":1,"ok":true,"data":{"id":"01M0YH3WC9E6HE49WYG47TZTJ0","slug":"wiki-cli","path":"wiki/entities/wiki-cli.md","status":"active","danglingFiled":[]},"errors":[]}
```

Then one summary per source, and the overview singleton. The ingest ledger is
advanced as each stage completes:

```
wiki ingest advance <readme> --to summarized
wiki ingest advance <readme> --to integrated --touched <wiki-cli>,<vault>,<division-of-labour>
wiki lint
wiki ingest advance <readme> --to linted
```

## 3. What ends up on disk

```
vault/
├── wiki.yaml
├── AGENTS.md
├── raw/
│   ├── 01M0YH3FFTX2QH4RCZ89GFB478.md      # README.md, verbatim
│   ├── 01M0YH3FP1HZJT40EV5VHX0R7M.md      # architecture.md, verbatim
│   └── 01M0YH3FVY2P29SZ0JHNZ4WGAS.md      # functional-flow.md, verbatim
├── wiki/
│   ├── index.md                            # generated routing catalog
│   ├── log.md                              # generated append-only log
│   ├── overview.md
│   ├── concepts/
│   │   ├── division-of-labour.md
│   │   └── ingest-state-machine.md
│   ├── entities/
│   │   ├── vault.md
│   │   └── wiki-cli.md
│   └── summaries/
│       ├── llm-wiki-architecture-summary.md
│       ├── llm-wiki-functional-flow-summary.md
│       └── llm-wiki-readme-summary.md
└── .wiki/                                  # derived cache, rebuildable
    ├── idmap.json  issues.json  ledger.json  lint.json
```

Three sources in, eight pages out. Open that directory in Obsidian and the
`[[wikilinks]]` below are a navigable graph with no plugins and no import step.

## 4. A page, in full

`wiki/entities/wiki-cli.md`, exactly as it sits on disk:

```markdown
---
id: 01M0YH3WC9E6HE49WYG47TZTJ0
type: entity
title: "wiki CLI"
status: active
created: 2026-08-26
updated: 2026-08-26
summary: "The native binary that is the only writer to a vault"
sources: [01M0YH3FFTX2QH4RCZ89GFB478, 01M0YH3FP1HZJT40EV5VHX0R7M]
tags: [cli-enforcement, json-contract, validation]
---
The single native binary that is the only writer to a vault. Every mutation —
creating a page, registering a source, advancing ingest state — goes through
it, which is what lets structure be enforced rather than remembered.

It validates before it writes: frontmatter is a closed schema, and unknown
keys, bad enum values, dangling links, unknown source ids and duplicate titles
are all rejected before anything touches disk. A rejected call leaves the vault
byte-identical to how it found it.

Every command takes `--json` and returns an envelope carrying a stable
kebab-case error code, so an agent can branch on the failure rather than parse
prose. Exit codes are part of that contract: 0 success, 1 rejected input,
2 environment or IO, 3 state conflict.
```

The frontmatter is a closed schema — every key above is one the CLI knows, and
an unknown one is rejected. `sources` are ULIDs that must resolve to registered
sources, which is what keeps a claim traceable to the material it came from.

## 5. `index.md` is why retrieval is cheap

Generated, never hand-edited. The agent routes through this instead of reading
page bodies to discover what is relevant:

```markdown
## Overview
- [[overview]] — Overview — Entry point: llm-wiki documented in its own format (sources: 3)

## Concepts
- [[division-of-labour]] — Division of labour — Why the agent does semantics and the CLI does bookkeeping (sources: 3)
- [[ingest-state-machine]] — Ingest state machine — The four-state, precondition-checked lifecycle every source moves through (sources: 2)

## Entities
- [[vault]] — Vault — The on-disk directory of markdown, sources and derived cache (sources: 2)
- [[wiki-cli]] — wiki CLI — The native binary that is the only writer to a vault (sources: 2)

## Summaries
- [[llm-wiki-readme-summary]] — llm-wiki README (summary) — The project's own framing: what it is, why the CLI is the only writer, how to install and drive it (sources: 1)
- [[llm-wiki-architecture-summary]] — llm-wiki architecture (summary) — Layer map, vault layout, and the JSON contract the CLI exposes to agents (sources: 1)
- [[llm-wiki-functional-flow-summary]] — llm-wiki functional flow (summary) — Ingest, review gate, retraction, lint and the reflect loop — who does what (sources: 1)
```

One line per page, carrying the summary the author wrote. An agent reads this
catalogue, picks a handful of candidates, and reads only those bodies — which
is the difference between a knowledge base that scales and one that has to be
re-read in full every session.

Two commands work the same territory. `wiki search` returns matching lines,
never whole bodies:

```
$ wiki search precondition --json
```

```json
{"hits":[
  {"kind":"page","path":"wiki/concepts/ingest-state-machine.md","title":"Ingest state machine","line":13,
   "matchLine":"linted — and each transition is precondition-checked by the [[wiki-cli]]."},
  {"kind":"source","path":"raw/01M0YH3FVY2P29SZ0JHNZ4WGAS.md","title":"llm-wiki functional-flow.md","line":98,
   "matchLine":"transition has a precondition the CLI checks before recording it, so the ledger"}
], "truncated":false, "scanned":11}
```

Note that it searches sources as well as pages, so you can always get back to
the raw material. And `wiki page backlinks` answers the other direction:

```
$ wiki page backlinks wiki-cli --json
["division-of-labour","ingest-state-machine","llm-wiki-architecture-summary","llm-wiki-readme-summary","overview"]
```

## 6. What the CLI refuses to write

This is the part that does not show up in a screenshot of the graph. Each of
these was a real attempt against this vault; each exited 1 and left the vault
byte-identical.

**A link to a page that does not exist:**

```
$ wiki page upsert --type concept --title "Retraction" … --body-file ./bad.md --json
{"v":1,"ok":false,"data":null,"errors":[{"code":"dangling-link","message":"dangling wikilink target(s): review-gate"}]}
```

**A title that collides with an existing page of the same type:**

```
{"v":1,"ok":false,"data":null,"errors":[{"code":"duplicate-title","message":"a entity page titled 'Vault' already exists ('vault')"}]}
```

**A source id that was never registered:**

```
{"v":1,"ok":false,"data":null,"errors":[{"code":"unknown-source","message":"unknown source id '01ZZZZZZZZZZZZZZZZZZZZZZZZ'"}]}
```

The ledger refuses out-of-order work on the same principle. Advancing a source
to `summarized` before any summary cites it fails, and the error names the
command that fixes it:

```
{"code":"precondition-summary","message":"no summary-type page cites source '01M05GXZ…' in its 'sources' list; write one first: wiki page upsert --type summary --sources 01M05GXZ… --stdin"}
```

Those codes are the API. An agent branches on `errors[].code`, corrects its
input and retries once — it never has to parse prose to work out what went
wrong, and it never gets a half-written page it has to clean up.

## 7. Nothing derived is precious

`.wiki/` is a cache. To prove it, this vault had the whole directory deleted
and rebuilt from the markdown alone:

```
$ sha256sum vault/.wiki/idmap.json vault/wiki/index.md
fc765f0861b326e7e6aaf5bc4769a6a4e7ea6622cac7838335588e0cec53f039  idmap.json
396ed5101a7a0bb525db7cccf7f947ec56b560fd57a7cf6885a744be37aecfcb  index.md

$ rm -rf vault/.wiki && wiki reindex --json
{"v":1,"ok":true,"data":{"pages":8,"sources":3,"idmapEntries":11},"errors":[]}

$ sha256sum vault/.wiki/idmap.json vault/wiki/index.md
fc765f0861b326e7e6aaf5bc4769a6a4e7ea6622cac7838335588e0cec53f039  idmap.json
396ed5101a7a0bb525db7cccf7f947ec56b560fd57a7cf6885a744be37aecfcb  index.md
```

Byte-identical, which is what deterministic serialization buys: stores rebuild
a sorted snapshot before writing, so a rebuild is not merely equivalent but
reproducible.

One honest caveat, visible in the same experiment. Reindex reconstructs
*structural* state from frontmatter, not *history*. The three sources come back
as `integrated` — derivable from the pages that cite them — but the timestamps,
the `--touched` lists and the `linted` state they had been advanced to are
gone, because nothing in the markdown records them:

```
$ wiki ingest status
│ 01M0YH3FFTX2QH4RCZ89GFB478 │ integrated │  │  │  │
```

So `.wiki/` is disposable, not worthless: delete it and you lose the audit
trail, not the wiki. `wiki/log.md` is the durable record, and it is authored
markdown rather than cache:

```markdown
## [2026-08-26T07:56:08Z] source-add | 01M0YH3FFTX2QH4RCZ89GFB478 | category=documentation sha256=5bee0575…
## [2026-08-26T07:56:21Z] upsert | wiki-cli | create id=01M0YH3WC9E6HE49WYG47TZTJ0 type=entity
## [2026-08-26T07:57:18Z] ingest-advance | 01M0YH3FFTX2QH4RCZ89GFB478 | to=summarized
```

## 8. Health

A vault this young and this deliberately built is clean:

```
$ wiki lint --json
{"v":1,"ok":true,"data":{"filed":0,"refreshed":0,"counts":[],…},"errors":[]}
```

Zero findings is the uninteresting case, and worth being honest about: lint
checks a vault's *shape*, so a vault of well-linked lorem ipsum would score
exactly the same. That is why the quality commands are separate — `wiki eval`
scores retrieval against golden questions you write, and `wiki audit` walks the
agent through re-reading a page against its sources, cold, to check the claims
actually hold.

What lint does catch is decay, with occurrence counts, so a finding that
survives several runs is evidence the *instructions* need amending rather than
the page. Drop the `--tags` from any page here and the next lint files a
`missing-tags` issue against it; remove the last inbound link to a page and it
files an `orphan`.

## Reproduce it

Everything above came from this repository's own docs, so you can rebuild it:

```bash
wiki init ./vault --name "llm-wiki docs"
export WIKI_VAULT=$PWD/vault
wiki category add documentation --description "Project documentation and specs"
wiki source add ./README.md --category documentation --title "llm-wiki README.md" --json
```

The page bodies are the part an agent writes. Point one at the vault — it picks
up `AGENTS.md` automatically — and ask it to ingest the registered sources.

See also: [architecture](architecture.md) for the layer map,
[functional flow](functional-flow.md) for the workflows, and
[spec](spec.md) for the authoritative contract.
