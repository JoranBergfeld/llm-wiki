# Glossary

## Persona

**Persona**
The owner's values, principles, tone of voice and boundaries, kept in `PERSONA.md` at the vault root. It comes from the owner; it is never inferred from material the owner did not endorse or state.
_Avoid_: profile, identity

**Persona line**
One first-person entry in one of the four persona sections.

**Evidence**
The material that supports a persona line, recorded in the audit log. Knowledge pages never link to the persona; evidence is the only link between them.
_Avoid_: persona link, weaving

**Endorsed note**
A page built from a source in a persona-source category. The owner endorses its content but may not have written it, so it can support values, principles and boundaries, never tone of voice.

**Persona-source category**
A source category the owner has marked as holding endorsed notes. Endorsement is per category, not per file.
_Avoid_: self-authored category

**Owner-stated**
Evidence the owner gave directly, such as onboarding interview answers or a dictated line. Sufficient on its own to support a persona line, and the only evidence for tone of voice.

**Onboarding interview**
A conversation in which an agent asks the owner about their values, principles, tone of voice and boundaries. The answers are owner-stated evidence. It can be re-run.
_Avoid_: questionnaire, survey

**Interview guide**
The versioned set of starter questions per persona section, plus the rules the interviewing agent follows. The agent may ask follow-ups beyond it.
_Avoid_: question bank, script

**Interview transcript**
The word-for-word record of one onboarding interview session: each question and the owner's answer. A dictated line outside an interview is a one-entry transcript. The owner may edit or delete it; deleting it retracts what was said. It is not a knowledge page.
_Avoid_: statement, answers file

**Persona candidate**
A page built from a persona-source category that a distillation pass has not yet considered.

**Persona changeset**
One distillation pass's proposed edits to the persona: any number of lines added, changed or removed across sections, each new line with its evidence. Reviewed all-or-nothing; at most one is pending, and a newer one supersedes it.
_Avoid_: persona proposal, persona diff

**Persona auto-accept**
The opt-in mode (`WIKI_PERSONA_AUTO_ACCEPT`, off by default) in which a valid persona changeset is accepted as soon as it is proposed, skipping human review but not lint or the evidence rules. Its accepts are audited as `decidedVia=auto-accept`.
_Avoid_: auto-approve, skip review
