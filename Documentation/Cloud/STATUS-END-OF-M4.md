# Galaxies build status: end of Milestone 4

Status: M0 through M4 are built. The whole solution compiles on net10.0 on Linux
(`Galaxies.slnx`) with zero warnings, and 119 tests pass with 1 skipped. Nothing
is deployed to GCP, and no real game has been played end to end.

This document is the handoff after M3 (built-in AI participants) and M4 (the
public launch gate). It follows the same rules as the M2 handoff: plain, direct,
no em dashes, honest about what is proven versus what merely compiles.

Read [STATUS-END-OF-M2.md](STATUS-END-OF-M2.md) first if you have not. Everything
it says about the engine port, the API, and the clock still holds.

## Where we are, honestly

The roadmap runs M0 to M7. M0 to M4 are now built as code:

- M0, prove the pipe. The engine is headless and reproducible.
- M1, the desktop client talks to the cloud. API, auth, command registry.
- M2, the clock. Control plane, scheduler, exactly-once turn guard.
- M3, AI participants. The AI is out of the GUI and runs as a cloud worker
  behind one open contract, and the single-AI-at-a-time file lock is gone.
- M4, the public launch gate. The lobby, account, and moderation surface, the
  marketing site, and the legal and operability artifacts.

What "end of M4" does not mean: it is not deployed, it has not run against real
Firestore, Cloud Tasks, Pub/Sub, or GCS, no participant has been called over a
real network with a real OIDC token, and no stranger has signed in. Those gaps
are environmental, not design gaps, and they are listed in full below.

## What was built in M3

### The headless AI assembly

`Ai/Nova.Ai.csproj` compiles the Stars! Nova AI headless against net10.0 and the
ported `Nova.Common`, with no UI reference anywhere.

The sources are linked from their original location under `Nova/Ai/` rather than
moved, matching the M0 decision to cut coupling in place and leave files on disk
for the eventual GUI port. One copy of the source, compiled two ways.

Two files are deliberately excluded:

- `Nova/Ai/AiRunner.cs`, the console entry point whose entire job was to take a
  `<race>.lock` file on a shared folder and read and write `.intel` and
  `.orders`. That is the single-AI-at-a-time limitation the cloud model removes,
  so it stays with the desktop build.
- `Nova/Client/ClientData.cs`, the WinForms client state. `Ai/ClientData.cs` is
  the headless replacement in the same `Nova.Client` namespace: the four members
  the AI actually uses, with the file and dialog paths cut away.

`AbstractAI` gained one seam, `Initialize(ClientData)`. The desktop
`Initialize(CommandArguments)` overload is untouched. The old file path is not
silently broken; it throws with a message pointing at the cloud seam.

One coupling had to be cut in the AI itself: `DefaultAIPlanner` built a
`ShipIcon` from a live `Bitmap`, which no longer exists in headless `Common`.

### The open participant contract

`AiContract/Galaxies.AiContract.csproj` is the contract as code, shared by the
runner and by first-party participants so there is exactly one definition of it.
It references only `Nova.Common` and never `Nova.Server`, because a participant
sits on the client side of the engine boundary.

It holds the `POST /v1/act` request and response types, the `empire_view`
transcoder, the order mapper, and the envelope.

### galaxies-ai, the dispatch runner

`AiService/` is the Cloud Run runner: `POST /v1/dispatch` for one seat-turn, the
three Pub/Sub event handlers, the Cloud Tasks enqueuer, the Firestore run lock,
the order submit client, the typed Firestore accessors, and the replay harness
endpoint. Everything ships dark behind flags that default to off.

### participants/nova-default

`Participants/NovaDefault/` is the first-party worker that answers `POST /v1/act`
by wrapping `DefaultAi`. Own folder, own Dockerfile, own cloudbuild, own service
account, container concurrency 1.

### galaxies-api additions

The internal AI orders route, the host control to add an AI opponent, seat
removal, and the abandoned-seat takeover wiring. See the route table below.

## What was built in M4

The launch-gate surface, all additive to the existing API:

| Area | Routes |
|---|---|
| Game browser | `GET /v1/games?scope=mine\|open\|public\|finished` |
| Settings | `GET` and `PATCH /v1/games/{id}/settings` |
| Membership | `POST /v1/games/{id}/leave`, `DELETE /v1/games/{id}/players/{empireId}` |
| Invites | `POST`, `GET`, `DELETE /v1/games/{id}/invites`, `POST /v1/invites/{token}/accept` |
| Game over | `GET /v1/games/{id}/summary` |
| Account | `GET /v1/account/export` (the DSAR bundle) |
| Moderation | `POST /v1/reports`, `GET /v1/admin/reports`, `POST /v1/admin/reports/{id}/resolve` |
| Bans | `POST` and `DELETE /v1/admin/users/{googleSub}/ban` |
| Lifecycle | `DELETE /v1/games/{id}` |

Plus per-account quotas (`GALAXIES_MAX_GAMES_PER_ACCOUNT`,
`GALAXIES_MAX_AI_SEATS_PER_GAME`), which the design doc called out as a real
exposure: without them one account can create thousands of games and move the
bill.

And off the API:

- `galaxies-web/marketing/`, the static Firebase Hosting site in the Vigil
  theme: the single-scroll landing page, support, privacy, and status pages, the
  hosting config with a strict CSP, the ships-dark `runtime-config.json` flag
  reader, `ads.txt`, and the auto-update appcast. The fixed dedication appears
  exactly once, in the landing page footer.
- `Documentation/Legal/`: terms of service, privacy policy, and the credits and
  licensing brief. Every one is explicitly a draft for counsel.
- `SECURITY.md`, a PR template, the four issue templates rewritten in house
  voice, and `.github/FUNDING.yml`.
- `Documentation/Cloud/DISASTER-RECOVERY.md`, `ANALYTICS-AND-KPIS.md`, and
  `ONBOARDING-SOLO-VS-AI.md`.

## Two engine bugs this work found

Both were latent in the M0 port and invisible until an AI started designing
ships. Both would have broken a real deployment.

1. **`ShipDesign.ToXml` dereferenced a null `Icon`.** A `ShipIcon` carries an
   image file identifier, and that identifier comes from `AllShipIcons`, which
   scans a graphics folder that does not exist in a server container. So `Icon`
   is null for every design created server side. The turn generator writes each
   empire's intel through `Intel.ToXml`, which reaches `ShipDesign.ToXml`, so
   this threw partway through writing an empire's intel and took the turn down
   over a missing picture. Fixed to persist the identifier when there is one and
   an empty string when there is not.

2. **The design could not be loaded back.** Writing an empty `<Icon>` element
   makes `mainNode.FirstChild` null on load, and the loader also resolved
   through the absent graphics database. Every design was silently dropped from
   the intel round trip. An AI restored from that intel would have had no ships
   it could build and would have played a much worse game with nothing failing
   loudly. Fixed in `ShipDesign(XmlNode)`, and `ShipIcon(string)` now tolerates a
   source that does not follow the icon numbering convention instead of throwing.

The second is the more dangerous kind of bug: it degrades play silently rather
than crashing.

## Decisions and why

These are additions to the M2 decision list. Argue with these if you disagree.

11. **The act request carries both a projection and the native intel.** The
    `empire_view` JSON is the language-neutral contract every participant can
    rely on. It is a projection, not a lossless serialization of a roughly
    24,000 line domain model. A first-party C# participant shares the engine
    assembly, so the request also carries the seat's own intel as engine XML,
    gzipped and base64 encoded inside the JSON envelope, exactly as the
    player-facing API already carries intel and orders. The built-in AI plays
    against the real object graph.

    The alternative, reconstructing an `EmpireData` from the JSON projection,
    would have been quietly lossy: the built-in opponent would get weaker in
    ways no test would catch, and golden replay would compare two degraded
    things to each other. The spec sanctions this path when it says the
    participant may reuse the `EmpireData(XmlNode)` and `ToXml` pair.

    Community and LLM participants ignore the native field and read the
    projection. `nova-default` returns an empty order list with a diagnostic
    note if it is ever handed a request with no native intel, rather than
    guessing.

12. **The seat seed is named `seat_seed`, not `seed`.** `ServerData.MasterSeed`
    is the one persisted per-game seed; the dispatch value is derived from it
    with the engine's own FNV-1a helper. Calling it `seed` invited the reading
    that a participant holds a second master value. This closes reconciliation
    item 3 in the specs README.

13. **Orders are built as engine commands, then serialized by the engine.** The
    order mapper turns each JSON order into a real `ICommand` and calls its
    `ToXml`, rather than hand-writing order XML. So the bytes an AI submits are
    the same shape a desktop player submits, and the turn generator cannot tell
    them apart. One validation path, not two.

14. **The API owns the takeover decision, galaxies-ai acts on it.** Consistent
    with decision 6 (the API owns all control-plane writes). The missed-turn
    ladder is evaluated before generation, handed-off seats are published in the
    `turn-generated` event's `handoffs` array, and the actual seat flip is gated
    separately from the publishing, so the behaviour is observable before it is
    armed.

## What is proven, and what is not

| Claim | How it is verified |
|---|---|
| The whole solution compiles headless on net10.0 Linux | `dotnet build` green, 0 warnings |
| The AI runs with no UI, no game folder, no lock file | `HeadlessAiTests`, 6 tests |
| The AI plays a real turn (scouts, researches, queues production) | Asserted; the fixture yields 5 orders |
| Two AI instances do not interfere | Asserted; this is what replaces the file lock |
| The built-in AI is deterministic | `GoldenReplayTests` reproduce byte for byte across repeated runs |
| Every order the AI emits passes the engine's own validation | Asserted against a fresh empire |
| Intel survives the native round trip, designs included | `EnvelopeTests`, pinned after the icon fix |
| A participant cannot act on a seat it was not handed | `OrderMapperTests`; a mismatched empire id is refused wholesale |
| A participant cannot order an object it does not own | Asserted for fleets, stars, and designs |
| Fleet keys survive decimal to hex and back | Asserted end to end through the engine's own registry |
| Orders serialize into the shape the engine reads | Asserted against `ROOT/Turn`, `ROOT/Id`, `ROOT/Orders` |
| Held orders on timeout, failure, or all-invalid | Code path exists and is unit covered at the mapper; not exercised against a live participant |
| Terraform is well-formed | `terraform validate` passes, `fmt` clean |
| Any service running on GCP | **Not proven.** Nothing is deployed |
| A participant called over real HTTP with a real OIDC token | **Not proven.** Compile-verified only |
| Firestore run locks, Cloud Tasks fan-out, Pub/Sub push | **Not proven.** Compile-verified only |
| The file and GCS pipe on a real saved game | **Not proven.** Still needs a 4.8-built fixture |
| Cross-architecture golden turns | **Not proven.** Still needs a 4.8 baseline |
| A full game played by two humans | **Not proven** |

The determinism result deserves a note, because it is better than expected. The
Nova AI draws no randomness at all: there is not one `Random` in `Nova/Ai/`. Its
determinism is structural rather than seed-dependent, so the golden gate works
today and does not have to wait on engine RNG seeding. That answers open question
1 in the galaxies-ai spec: do not block the golden gate on the turngen seeding
work. The seat seed is still threaded through the contract, because a community
or LLM participant will need it.

The honest limit on that claim: it is proven for the fixture in `Tests.Ai`, in
one process. A code path the fixture does not reach could still draw randomness,
though the absence of any `Random` in the assembly makes that unlikely.

## Things to look out for

Additions to the M2 list, roughly in order of how likely each is to bite.

1. **Nothing here has met the network.** Every GCP interaction (Firestore
   transactions, Cloud Tasks naming, Pub/Sub push envelopes, GCS reads, OIDC
   audiences) is compile-verified and unit-tested against in-memory doubles.
   The first deploy will find things. That is expected and is why the ACTIVATE
   runbook leads with negative tests.

2. **The engine still holds process-wide statics.** `GameSettings.Data` and
   `AllComponents` are global, which is why both the turn generator and the
   participant container run at concurrency 1. This is not a tuning choice. If
   you raise concurrency, seats will contaminate each other.

3. **The marketing site has an inline-script trap that has already bitten
   once.** `firebase.json` sets `script-src 'self'` with no `'unsafe-inline'`.
   The flag reader was originally an inline block, which needs a sha256 hash in
   the CSP, and that hash is bound to the exact bytes of the script. An edit
   invalidated it. The failure is nasty: the browser refuses the script, no
   flags load, the site pins to all-off, and the Firebase emulator does not send
   the headers block so local previews look healthy. It now lives in
   `marketing/scripts/flags.js` and is served from `'self'`, with no hash. Do
   not move it back inline.

4. **The site is missing binary assets on purpose.** Self-hosted fonts
   (Fraunces, IBM Plex Sans, IBM Plex Mono), the Open Graph card, and the
   installer the appcast points at are referenced but not committed. The build
   owes them before the site goes live.

5. **The AI seat model has two homes and they must stay consistent.** The
   canonical roster is `games/{gameId}/members/{empireId}`, where `Kind` is
   `Human` or `Ai`. `games/{gameId}/ai_seats/{empireId}` is additive metadata
   (pinned participant, version, difficulty, controller) keyed by the same pair.
   It is not a parallel roster and must never become one.

6. **The order mapper applies commands as it validates them.** This is
   deliberate: a design added and then produced in the same turn would otherwise
   fail validation on the production order, which is exactly the sequence the
   built-in AI emits. It does mean the `EmpireData` handed to the mapper is
   mutated, so do not reuse it afterwards expecting the pre-turn state.

7. **`minClientVersion` is now served from config.** The desktop upgrade gate is
   only as good as that value. Set it deliberately at deploy time; a wrong value
   either locks out working clients or lets stale ones submit unparseable orders.

## What to do first

The pre-deploy list from the M2 handoff is unchanged and still the highest-value
work. It has not been done:

1. Get a native Linux checkout or a clean CI runner, to kill the OneDrive cache
   incoherence and give CI a faithful build.
2. Capture a golden turn on .NET Framework 4.8 from a real two-player game and
   prove it reproduces on x64 Linux net10.0.
3. Deploy the M0 and M2 slice to `roybot` and run one turn end to end.
4. Seed the new-game RNG so the server can create games rather than only advance
   a fixture. `StarMapInitialiser`, `StarMapGenerator`, `NameGenerator`,
   `PointUtilities`, and `SpaceAllocator` still use unseeded `new Random()`.

Then, new with M3 and M4:

5. Deploy `ai-nova-default` and `galaxies-ai` dark and walk
   `ACTIVATE_galaxies_ai.md`. The three negative tests are the point: time a
   participant out and confirm the seat records held and the turn still
   generates; deliver a duplicate dispatch and confirm it is a no-op; confirm a
   dispatch for one empire can only read that empire's intel.
6. Commit the site's fonts and Open Graph card, then deploy the marketing site
   to a preview channel and run the WCAG audit against the real thing.
7. Send `Documentation/Legal/CREDITS-AND-LICENSING.md` to counsel. It is written
   as the brief, and it ends with the itemized list of questions that need an
   answer in writing before launch. This is a hard gate and no amount of code
   moves it.

## How to build and run

```
export PATH="$HOME/.dotnet:$PATH"
dotnet build Galaxies.slnx -c Release
dotnet test  Galaxies.slnx -c Release
```

Expect 119 passed and 1 skipped. The skip is
`calculateAdvantagePointsForStandardJoat`, a pre-existing upstream bug, not a
regression. See the M2 handoff.

The one test output that looks alarming and is not:
`Nova error: Unable to locate Nova! graphics components.` That is the graphics
database correctly failing to load in a headless test run, which is the whole
point of the port.
