# Security

## Reporting a vulnerability

Please report privately through GitHub's
[private vulnerability reporting](https://github.com/JoranBergfeld/llm-wiki/security/advisories/new)
rather than opening a public issue. I'll acknowledge within a week.

This is a personal project with no paid support and no SLA. That's the honest
expectation to set — reports are read and taken seriously, but fixes ship on
best effort.

## What this tool does with your data

`wiki` is a local CLI. It makes no network calls of its own: it reads and
writes the vault directory you point it at, and nothing else. Nothing is
uploaded, phoned home, or sent to a model — the LLM in "LLM wiki" is whatever
agent *you* run alongside it, and that agent talks to the CLI through your
shell, not the other way round.

One command touches the network, and only when asked: `wiki links check
--external` issues HTTP requests to the external URLs your own pages cite, to
see whether they still resolve. Without `--external` it is entirely offline.

## Scope

Worth reporting:

- A path that writes outside the vault root, or escapes it via a crafted
  slug, title, source path or wikilink target.
- Input that gets past validation and lands malformed on disk — the CLI being
  the only writer is the security property the whole design rests on.
- Anything in the install scripts or the release pipeline that could let a
  third party substitute the binary a user installs.

Out of scope: what an LLM agent chooses to write into page bodies. The CLI
enforces structure, not truthfulness — `wiki audit` and `wiki eval` exist
because content quality is a separate problem, addressed separately.

## Verifying what you installed

Release binaries are built by the CI workflow in this repository from a green
`main`. Two things let you check what you got.

**Integrity.** Every release carries a `SHA256SUMS` covering all four
archives. `install.sh` and `install.ps1` fetch it and verify automatically,
refusing to install on a mismatch; a release with no `SHA256SUMS` published
is reported and skipped rather than silently trusted. To check a manual
download yourself:

```bash
curl -fsSLO https://github.com/JoranBergfeld/llm-wiki/releases/download/latest/SHA256SUMS
sha256sum --ignore-missing -c SHA256SUMS
```

Note what this does and does not buy you: the archive and its checksum come
from the same host, so this catches corruption and truncation, not a
compromised release. It is integrity, not provenance.

**Provenance.** `wiki --version` reports the exact commit the binary was built
from:

```
$ wiki --version
1.0.0+7e608a39d00a804c7cbe8b9e1f53e9d816d85592
```

Cross-check that against the commit history, and against the CI run that
published the release, before trusting a binary you did not build yourself.
