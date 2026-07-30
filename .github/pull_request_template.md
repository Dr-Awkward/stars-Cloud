Fill in what applies. A short, honest pull request that says what it does not
cover is worth more than a long one that implies it covers everything.

## What changed

Describe the change in a few sentences, from the reader's side. What is
different after this merges? Name the surfaces you touched (engine, API, control
plane, turn generator, client, AI seats, infrastructure, docs, site).

## Why

The problem, the bug, or the milestone item this serves. Link the issue if there
is one (`Closes #123`). If this is a decision rather than a fix, say what you
decided and what you rejected, so the next person does not relitigate it.

## How it was tested

Be specific about what actually ran, and be honest about what did not. Some of
this repository can only be exercised on Windows or against real cloud services,
so "compiles and unit-tested, not run end to end" is an acceptable answer. An
unverifiable claim is not.

- Tests run (which suites, on which runtime, passing or not):
- Manual verification (what you clicked, what you sent, what you saw):
- Not verified, and why:

## Risk and rollback

What breaks if this is wrong, and how to back it out. Note any data migration,
any change to persisted state or the state XML, any Terraform that touches live
infrastructure, and anything that changes turn generation output for games
already in flight.

## Checklist

- [ ] GPL v2 notices and per-file copyright headers are preserved. New files
      carry the header; moved and renamed files kept theirs; no notice was
      stripped, shortened, or reworded. This is a license obligation, not a
      style preference.
- [ ] Stars! Nova credit is intact anywhere this change touches attribution or
      the about/colophon surfaces.
- [ ] No em dashes and no en dashes in anything this change adds: copy,
      headings, comments, docs, test names, and the commit messages. Ranges are
      written as "10 to 120". No emoji or decorative status glyphs.
- [ ] Tests pass locally (`dotnet test` on `Galaxies.slnx`), and new behavior has
      a test that would fail without this change.
- [ ] This change does **not** touch the fog-of-war or authorization boundary.

      If it does, leave that box unchecked, check this one instead, and fill in
      the section below.

- [ ] This change **does** touch the fog-of-war or authorization boundary, and
      the verification below describes how it was checked.

### Fog-of-war and authorization boundary

Skip this section only if the box above is honestly checked. The boundary
includes: anything under authentication or session handling, membership and seat
resolution, the API's per-empire rules (R1 to R7), the derivation of the caller's
empire from the session, intel reads and the per-empire `EmpireData` split, order
ingestion and the command registry, spectator and history views, admin and
moderation paths, AI-participant credentials, and anything that serializes or
addresses a per-empire file.

The property being protected: a participant must only ever be able to read their
own empire's fog-of-war view. The server derives the caller's empire; a
client-supplied empire id or race name that disagrees is rejected, not corrected.

- What part of the boundary this touches:
- How it was verified (name the test, the request you sent as the wrong empire,
  and the response you got back):
- Whether an authorization test covers the negative case (a caller who should be
  refused actually is), and where that test lives:
- Anything a reviewer should look at especially hard:

## Anything else

Follow-ups you deliberately left out, known limits, screenshots if a surface
changed visually, and anything you want a second opinion on.
