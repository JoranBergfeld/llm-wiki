# Persona evidence lives only in the audit log; tone of voice only from owner-stated text

Status: accepted (11 Nov 2026)

## Context

`PERSONA.md` holds the owner's values, principles, tone of voice and boundaries. Charting assumed "baseline weaving": ingest would tag every knowledge page with the values and principles it touches, and the persona would be distilled from ingested sources. Two facts broke that. First, the vault cannot tell who wrote a source, and notes the owner keeps may be LLM-drafted. Second, tagging an arbitrary page with "this touches your value X" is the agent inferring the owner's persona, which the owner rejected: the persona comes from him.

## Decision

- Knowledge pages carry no link to the persona. The only link is evidence in the audit log, from each persona line to what supports it.
- Evidence is either an endorsed note (a page from a source category marked `persona-source` in `wiki.yaml`) or `owner-stated` text (interview answers, direct statements). `owner-stated` is sufficient alone; distilled lines still need two supporting pages.
- Tone of voice is drawn only from `owner-stated` text.

## Considered alternatives

- **Page frontmatter field or wikilinks to persona lines.** Rejected: `PERSONA.md` has no IDs, line text changes through changesets, and the link would duplicate the audit log and drift from it.
- **Per-file authorship marker on sources.** Rejected: a new frontmatter key that only works if the owner remembers to set it, and it still cannot prove authorship.
- **Distilling tone from self-authored categories.** Rejected: one LLM-drafted note in the inbox pulls tone toward LLM style, and review does not reliably catch it.

## Consequences

- The page frontmatter schema does not change, and removing a persona line leaves nothing dangling.
- "Which pages support value X?" is a query over the audit log, not a page property.
- Tone of voice depends on the onboarding interview or direct statements existing; a vault with only notes has no tone section to distill.
- Turning off `persona-source` on a category makes evidence drawn from it stale (lint warning plus re-check proposal).
