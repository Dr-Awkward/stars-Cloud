# Galaxies legal documents

Three documents, all of them drafts, none of them reviewed by a lawyer.

**Every document in this folder is a draft pending legal review. None of them is
in force, none of them is legal advice, and none of them should be published,
linked from a sign-up flow, or relied on until a qualified lawyer has reviewed
and amended it.** They were written to an engineering brief standard: precise
about what the service actually does, honest about where the exposure sits, and
explicit about the questions counsel has to answer.

## The documents

| Document | What it is | State |
|---|---|---|
| [TERMS.md](TERMS.md) | Terms of service for the hosted game: who may use it, the minimum age of 16 and why an ad-supported service needs a real gate, acceptable use, the API and automated play, what happens to your games if you delete your account, the no-warranty and no-SLA position, suspension and termination, and how the GPL v2 engine affects you | Draft. 10 open questions for counsel at the end |
| [PRIVACY.md](PRIVACY.md) | The full privacy policy behind the short note on the site: every field collected and why, legal bases, the EU and UK consent platform for personalized ads, third parties, retention, the export path, the deletion path and what deletion genuinely does to a live game, children's data, international transfers | Draft. 12 open questions for counsel at the end |
| [CREDITS-AND-LICENSING.md](CREDITS-AND-LICENSING.md) | The credit and GPL obligation analysis: why "Nova is a clone of the Stars! source" is wrong, the three separate risks that must not be collapsed, a table of GPL v2 obligations and whether each attaches, and the recommendations on credit and naming | Draft, and **this one is the brief handed to the lawyer**. 16 itemized questions at the end |

## How to read the licensing document

`CREDITS-AND-LICENSING.md` is the brief for counsel, not a ruling. It is written
as an engineer's reading so a lawyer has something concrete to correct rather
than a blank page. Its obligations table carries an engineering confidence
column, which is not a legal opinion, and its section 7 is the itemized list of
questions that need written answers before launch. Treat nothing in it as
settled until those answers exist.

The three risks it separates (Stars! Nova's GPL v2, the Stars! name and
trademark, and game-design similarity) have different sources and different
mitigations. Collapsing them into one is the specific mistake the document
exists to prevent.

## Before launch

These four things gate a public launch, and none of them is done:

1. Counsel reviews and amends all three documents.
2. Counsel answers `CREDITS-AND-LICENSING.md` section 7 in writing, in
   particular the GPL boundary and the nominative fair use question on the
   Stars! name.
3. Every `<placeholder>` in the documents is filled: the legal entity,
   governing law, the contact mailboxes, retention numbers checked against the
   real system configuration.
4. The related engineering pieces land: `SECURITY.md`, the age confirmation at
   sign-up, the self-serve data export, the self-serve account deletion with the
   empire anonymization or AI handoff, the consent platform, and a tested
   restore from a per-turn snapshot.

## Related

- `Documentation/Cloud/GALAXIES-CLOUD-DESIGN.md`, section G.4, is where this
  licensing analysis originated, and section G.5 covers the ops and trust
  artifacts these documents depend on.
- `Documentation/Cloud/README.md` explains the cloud port and its current build
  state.

Contact for anything in this folder: coop@farehard.com.
