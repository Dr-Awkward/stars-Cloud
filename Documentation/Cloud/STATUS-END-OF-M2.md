# Galaxies build status: end of Milestone 2

Status: M0, M1, and M2 are built. Everything compiles on net10.0 on Linux
(`Galaxies.slnx`), the test suites are green, and the API runs locally. Nothing is
deployed to GCP yet, and no real game has been played end to end. So this is the
end of Milestone 2 as buildable, tested code, and the start of the program's
pre-deploy phase.

This document is the handoff: what was built, why it was built that way, what to
watch for, and what the next two milestones need. It follows the house rules
(plain, direct, no em dashes, honest about limits).

## Where we are, honestly

The roadmap in GALAXIES-CLOUD-DESIGN.md runs M0 to M7. The three milestones that
retire the biggest risks are done as code:

- M0 "prove the pipe" was the load-bearing prerequisite: nothing containerizes
  until the engine is headless. It is now headless and reproducible.
- M1 "desktop client talks to the cloud" is the API, auth, and the command
  registry.
- M2 "the clock" is the control plane, the scheduler, and the exactly-once turn
  guard.

What "end of M2" does not yet mean: it is not deployed, it has not run against real
Firestore or Cloud Tasks, and it has not generated a turn from a real saved game.
Those gaps are environmental (see "What is proven" below), not design gaps, and
closing them is the first block of work, not part of M3.

## What is built

### M0, the headless engine (done and tested)

`Common` and `ServerState` are SDK-style net10.0 class libraries with no WinForms
and no `System.Drawing` on the turn-generation path.

- `Report` is now an `IReporter` sink with a static facade, so the hundreds of
  `Report.*` call sites are unchanged; the host installs a logging sink and
  `FatalError` throws `NovaFatalException` instead of aborting the thread.
- `FileSearcher`, `Files/Config`, and `Files/GameSettings` lost their file and
  folder dialogs; `AllComponents` loads `components.xml` synchronously through a
  no-op progress sink and a host-set path (`ComponentFilePathOverride`), replacing
  the WinForms `ProgressDialog` it used to spin.
- `ShipIcon`, `RaceIcon`, and `Component` keep their string identifiers (`Source`,
  `ImageFile`) and dropped the live `Bitmap` properties; the client presentation
  layer will resolve pixels later.
- Determinism: `ServerData.MasterSeed` is persisted and round-trips through the
  state XML (with a `FormatVersion`); `TurnGenerator`, `BattleEngine`, and
  `CheckForMinefields` seed their RNG from it using FNV-1a helpers in
  `Common/Determinism`; `IterateAllFleets` and command application iterate in a
  deterministic key order.

`ServerHost` is the turn-generation worker (`TurnService`, `IGameStore` with local
and GCS stores, a `Dockerfile`). Its `generate` endpoint returns the outcome the
API commits (new state path, empire ids, whether the game ended).

`Tests` runs on net10.0 with NUnit 3 and passes, including in-memory turn
generation and a determinism test that generates the same turn twice and asserts
the two results are identical.

### M1, the API (built, compiles, runs)

`Api/` is `galaxies-api`, an ASP.NET Core minimal-API service. It verified running
locally: `/healthz` returns 200, `/version` serves JSON, and an unauthenticated
`/v1/me` returns 401.

Implemented endpoints:

| Area | Endpoints |
|---|---|
| Meta | `GET /healthz`, `GET /readyz`, `GET /version` |
| Auth | `POST /v1/auth/google`, `POST /v1/auth/refresh`, `POST /v1/auth/logout`, `GET /v1/me`, `DELETE /v1/account` |
| Lobby | `POST /v1/games`, `GET /v1/games/{id}`, `GET /v1/games/{id}/players`, `POST /v1/games/{id}/join`, `POST /v1/games/{id}/start` |
| Orders | `PUT /v1/games/{id}/orders`, `GET /v1/games/{id}/orders`, `POST /v1/games/{id}/orders/submit`, `DELETE /v1/games/{id}/orders` |
| Intel and status | `GET /v1/games/{id}/intel`, `GET /v1/games/{id}/intel/{turnYear}`, `GET /v1/games/{id}/status` |
| Host controls (M2) | `POST /v1/games/{id}/force-generate`, `extend-deadline`, `pause`, `resume` |
| Internal (OIDC) | `POST /internal/deadline-fire`, `POST /internal/sweep` |

Auth is Google-brokered: `GoogleIdentityVerifier` validates the Google ID token,
the backend mints a first-party HS256 session JWT plus a rotating refresh token
(the refresh chain id lives on the user document, so logout revokes the chain).
`Authorization` holds the boundary rules R1 to R7 in one place; the golden rule is
that a caller's empire is resolved from session plus membership, never from the
request body.

`Common/Commands/CommandRegistry.cs` retires `OrderReader`'s hardcoded command
switch. The XML order path and the API's order validation both resolve command
types through the one registry, so a new command type is a registration, not a
switch edit.

`Client/` is the `ITurnTransport` seam with an `HttpTurnTransport` implementation:
the interface the desktop client's `IntelReader` and `OrderWriter` will use in
place of the shared folder.

### M2, the clock (built, unit-tested)

`ControlPlane/` is a shared library used by the API now and by the turngen commit
path later.

- Firestore model: `GameMeta` (games/{gameId}), `Member`
  (games/{gameId}/members/{empireId}, the locked seat model), `UserAccount`
  (users/{google_sub}).
- `Cadence` computes deadlines from the per-game "maximum time between turns".
- `Lifecycle` is the game state machine (Draft, Lobby, Active, Paused, Finished,
  Cancelled, Archived) with guarded transitions.
- `MissedTurn` is the HoldOrders ladder: reuse last orders, do nothing, or hand
  the seat to an AI after enough consecutive misses.
- `CloudTasksDeadlineScheduler` arms one Cloud Tasks entry per (gameId, turnYear),
  named `gen-{gameId}-{turnYear}` so a second enqueue is deduped, firing into
  `/internal/deadline-fire`.
- `PubSubTurnEventPublisher` publishes the `turn-generated` event with the locked
  field shape (`turnYear`, not `newTurnYear`).
- The exactly-once guard is the `FirestoreControlPlane` claim then commit: a worker
  wins a turn only if the game is on that turn year with no live lock, and commits
  only if its token still holds the lock. This is covered by a concurrency test
  where 12 simultaneous triggers for one turn produce exactly one winner.

`infra/terraform/m2_clock.tf` provisions the public API service, the deadline
queue, the one-minute backstop sweep, the fan-out topics, and the API and invoker
service accounts and secrets. `terraform validate` passes.

## The repository now

New and changed since the design phase (net-new line counts are approximate):

| Path | What it is | State |
|---|---|---|
| `Common/`, `ServerState/` | Ported headless engine (SDK-style net10.0) | Compiles, tested |
| `Common/Determinism/` | Seed derivation and seeded RNG | Compiles |
| `Common/Commands/CommandRegistry.cs` | Command dispatch registry | Compiles |
| `ServerHost/` | Turn-generation worker | Compiles |
| `Tests/`, `Tests.ControlPlane/` | Engine tests (63) and clock tests (11) | Green |
| `ControlPlane/` (about 1,000 lines) | Firestore control plane, scheduler, lock | Compiles, unit-tested |
| `Api/` (about 1,270 lines) | galaxies-api service | Compiles, runs locally |
| `Client/` (about 175 lines) | ITurnTransport client seam | Compiles |
| `infra/terraform/` | M0 buckets plus M2 clock and API | `terraform validate` passes |
| `Galaxies.slnx` | The headless solution | Builds |

## Decisions and why

These are the calls that shape everything downstream. If you disagree with one,
this is the list to argue with.

1. **.NET 10, and a locally-installed SDK.** The design already chose net10.0
   (current LTS). There was no dotnet on the machine, so it installs to `~/.dotnet`.
   That is a build-environment fact, not a project decision, but it is the first
   thing a new engineer will trip on (see "watch for").

2. **Reproduce the curated build, do not just glob.** The legacy `Common.csproj`
   listed 129 files by hand. The folder on disk holds more, including dead
   duplicates (`Scores.cs`, two of `Orders`/`Intel`/`GameSettings`,
   `ProductionItem.cs`) that the old build never compiled. The SDK default globs
   everything, so the new csprojs explicitly exclude those stale files. If you add
   a file, it is picked up automatically; if you see a duplicate-type error, a dead
   file is the likely cause.

3. **Cut the engine's coupling in place, defer the client split.** M0 needs the
   turn path headless, not a full client/presentation split. So the WinForms and
   live-bitmap files are excluded from the headless build and left on disk for the
   eventual GUI port, rather than moved into a new presentation assembly now. The
   one real turn-path coupling, `AllComponents` spinning a `ProgressDialog`, is cut
   to a no-op progress sink.

4. **`System.Drawing.Primitives` stays; `System.Drawing.Common` goes.** `Point`,
   `Size`, and `Rectangle` are in the box on net10.0 and are safe on Linux;
   `Bitmap` and `Image` are Windows-only and were the real problem. So the engine
   still uses primitive geometry, and only the bitmap use was removed. Do not add a
   `System.Drawing.Common` reference to make something compile; it will fail at
   runtime on Linux instead.

5. **Determinism is a safety net, built before the port could be trusted.** The
   seed is one persisted field (`MasterSeed`); per-turn, per-seat, and per-subsystem
   seeds derive from it with FNV-1a, never `string.GetHashCode` (which is randomized
   per process). The non-obvious half is iteration order: dictionary `.Values` order
   is not guaranteed, and the turn loop draws from the RNG, so unordered iteration
   was a silent divergence source. Both halves are fixed.

6. **The API owns the exactly-once lock; turngen is stateless compute.** The specs
   left a real collision: two services wanted to write the same game document with
   two vocabularies for "state". The reconciliation here is that `galaxies-api`
   owns all control-plane writes (claim, commit, lifecycle), and `galaxies-turngen`
   is a pure worker that loads state and orders from GCS, runs the engine, and
   writes results back. The API claims the lock, calls turngen, then commits. This
   is a deliberate choice; the AI and turngen specs still describe turngen doing its
   own commit, and that difference must be honored or re-decided before those
   services are wired.

7. **One game document, two axes.** The collision above is modelled as two distinct
   fields, `Lifecycle` (Draft to Archived) and `Generation` (Idle or Generating),
   with a single `GenerationLock` (token plus lease). This replaces turngen's
   separate `lockOwner`/`lockExpiry` scalars. Anyone writing the game document must
   use these names.

8. **XML inside a JSON envelope.** Orders and intel carry the existing engine XML
   as a string field in a small JSON body, so the desktop client's serialization is
   untouched. A native JSON projection for AI and the web client is a later
   convergence, not now.

9. **Structural order validation at the edge, semantic validation in the engine.**
   The API checks that orders are well-formed, target the caller's empire and the
   open turn, and carry known command types. It does not load the full universe to
   run each `ICommand.IsValid(empire)`; the engine already re-validates during
   `ParseCommands` and skips anything invalid. This keeps the API cheap and avoids
   loading a multi-megabyte state blob on every order write.

10. **GCS object paths follow the turngen scheme.** The specs drifted on paths; the
    canonical layout here is `games/{gameId}/{orders|intel}/{turnYear}/{empireId}.{ext}`
    and `games/{gameId}/state/{turnYear}.sstate`, matching what turngen and the
    stores already use.

## Things to look out for

Ordered roughly by how likely each is to bite.

1. **No dotnet on PATH; the repo lives on a Windows OneDrive mount over WSL.** Run
   `export PATH="$HOME/.dotnet:$PATH"` first. More important: the mount's file cache
   can be incoherent, so occasionally a file read or a search returns a stale copy
   while `git` and a fresh `dotnet build` see the true bytes. When a tool and the
   compiler disagree, trust the compiler and `git`. A native Linux checkout would
   remove this whole class of confusion and is worth doing for CI parity.

2. **The fixture and golden gap.** A real two-player `.sstate` plus `.orders`, and
   golden turns captured on .NET Framework 4.8, need a Windows build that this
   environment does not have. So the file and GCS pipe is proven by compilation plus
   the in-memory turn and determinism tests, not by generating a turn from a real
   saved game. The single most valuable next check (see next steps) is capturing a
   golden on 4.8 and proving it reproduces on x64 Linux, because floating-point in
   battle and movement math can differ across architecture. If it does differ, the
   plan is to re-baseline the golden on the Linux target and pin the engine version
   per game.

3. **Static singletons force one game per instance.** `GameSettings.Data` and
   `AllComponents` are process-wide statics. That is why `galaxies-turngen` runs at
   container concurrency 1, and it is not optional. It also bit the tests: a
   fixture that reused one `Star`/`Race` across methods contaminated later tests
   until the setup was made to build fresh instances. If you parallelize anything in
   the engine, this is where it breaks.

4. **The API-owns-the-lock reconciliation is a fork from the specs.** See decision
   6. It is coherent and tested, but the AI and turngen specs were written assuming
   turngen commits. Do not wire those services without reconciling this on paper
   first, or you will get two writers racing on the game document.

5. **One skipped test is a pre-existing upstream bug, not a regression.** `Tests`
   reports 62 passed, 1 skipped. The skip is `calculateAdvantagePointsForStandardJoat`,
   which asserts a value the upstream Stars! Nova code never produced correctly
   (see commit `5441c1f`). It is marked `[Ignore]` with that reason. Do not "fix" it
   by changing the number without understanding the advantage-point math.

6. **Refresh tokens are a simple version.** The refresh token is a signed JWT with a
   chain id compared against the user document. It works and revokes on logout, but
   it is not the full rotating-family reuse-detection scheme a hardened auth system
   wants. Revisit before the public launch gate.

7. **Package version pins matter.** `Google.Cloud.Firestore` pulls a minimum
   `Google.Apis.Auth`, so the API pins `1.72.0` to avoid a downgrade error. And the
   Cloud Tasks package defines its own `Task` type that clashes with
   `System.Threading.Tasks.Task`; the two files that touch it alias the framework
   type. Keep both in mind when bumping packages.

8. **Map generation on the server is still deferred.** M0 sidesteps new-game map
   generation with a fixture, so the four `new Random()` sites in `StarMapInitialiser`
   and the RNG in `StarMapGenerator`, `NameGenerator`, `PointUtilities`, and
   `SpaceAllocator` are not yet seeded. That is fine while games are created from a
   fixture, but server-side game creation (needed for a real lobby "start") must seed
   them for reproducible galaxies.

9. **`.slnx`, not `.sln`.** The .NET 10 CLI created a `Galaxies.slnx`. CI and any
   scripts must reference the `.slnx` name.

## How to build and run

```
export PATH="$HOME/.dotnet:$PATH"
dotnet build Galaxies.slnx -c Release
dotnet test  Galaxies.slnx -c Release

# Run the API locally (GCP clients are lazy, so healthz and version work with no
# credentials; game endpoints need Firestore and the buckets).
ASPNETCORE_URLS=http://127.0.0.1:8085 dotnet run --project Api/Galaxies.Api.csproj -c Release
```

## What is proven, and what is not

| Claim | How it is verified |
|---|---|
| Engine compiles headless on net10.0 Linux | `dotnet build` green |
| Engine generates a turn headless | In-memory turn tests pass |
| A turn is reproducible from a fixed seed | Determinism test passes |
| Exactly-once generation under concurrent triggers | 12-trigger concurrency test passes |
| Cadence, lifecycle, missed-turn ladder | Unit tests pass |
| API starts and enforces auth | Ran locally: healthz 200, /v1/me 401 |
| Terraform is well-formed | `terraform validate` passes |
| The file and GCS pipe on a real saved game | Not proven; needs a 4.8-built fixture |
| Cross-architecture golden turns | Not proven; needs a 4.8 baseline |
| Any service running on GCP | Not proven; needs a deploy |
| A full game played by two humans | Not proven; needs a deploy plus a client |

## Next: Milestone 3, built-in AI participants

Goal from the roadmap: solo-versus-AI and AI-fill seats through one open contract,
with the old single-AI file-lock limitation gone. This is where the AI-PARTICIPANTS
contract (`AI-PARTICIPANTS.md`) lands.

What M3 needs:

1. **Extract the AI into a headless worker assembly.** `Nova/Ai/*` currently lives
   inside the WinForms GUI project and drags WinForms in. Pull it into a UI-free
   assembly that references only pure `Common`, the same shape the engine port took.
   This is the M3 analogue of the M0 port and is the prerequisite for everything
   else here.
2. **The `POST /v1/act` participant contract and a host adapter.** Define the JSON
   act contract, and the adapter that translates a participant's JSON reply into
   `ICommand` objects (through the `CommandRegistry` that already exists) and runs
   `IsValid` before submission.
3. **AI dispatch and AI-as-client submission.** The `turn-generated` event already
   carries `aiEmpireIds`. Add the `galaxies-ai` push subscriber that, per AI empire,
   loads intel, runs the AI, and submits orders through the same authenticated
   channel humans use (the internal AI order route the API spec names). The
   single-slot generation worker never blocks on an AI call.
4. **Wire AI takeover to the missed-turn ladder.** `MissedTurn.Decide` already
   returns `HandToAi`; M3 acts on it, converting a long-absent seat to an AI.
5. **A replay and golden-game harness.** This doubles as the soak-test vehicle and
   the difficulty-ladder harness.

Hooks that already anticipate M3: `PlayerKind.Ai`, `Member.Kind`, the
`aiEmpireIds` field on the turn event, and `MissedTurnAction.HandToAi`. Endpoints
still to add: `POST /v1/games/{id}/players/ai` (host adds an AI), and the internal
AI order-submission route.

Suggested M3 order: extract the AI assembly first (nothing else compiles without
it), then the act contract and adapter, then dispatch, then takeover, then the
replay harness.

## Next: Milestone 4, the public launch gate

Goal from the roadmap: a stranger can discover, sign in, and legally play. This is
the milestone where the Hearthlight site, the legal gate, and money land.

What M4 needs:

1. **The marketing site and app skin** on Firebase Hosting in the Vigil theme, with
   the fixed dedication and the honest, low-pressure donations block.
2. **The rest of the lobby and account surface.** The core lobby is built; M4 adds
   the game browser and filters (`GET /games?scope=`), invites, the game-over
   summary, account export for a DSAR bundle, and the settings-edit endpoints. It
   also fills in the admin and moderation surface the full endpoint catalog lists.
3. **Money and consent.** AdSense (the owner has approved standard ads, so do not
   moralize about ad privacy; reword the "no ads" donate headline), the reworded
   donations block, and a consent platform.
4. **The legal gate.** Publish the client source, credit the Stars! team and Stars!
   Nova explicitly, keep the GPL v2 notices, and get counsel to sign off on the
   Stars! name and the GPL boundary before launch. This is a hard gate, and it is
   an engineering brief for a lawyer, not a ruling the code can make.
5. **Trust and operability.** `SECURITY.md`, terms of service, a privacy policy, an
   age gate, a tested disaster-recovery restore with a defined RPO and RTO, domain
   and TLS and custom-domain mapping, product analytics, per-account quotas, and a
   quick solo-versus-AI onboarding path.

M4 depends on M3 for the solo-versus-AI onboarding that makes a first visit
satisfying, which is why AI comes first.

## What to do first (bridge from "built" to "running")

Before M3, close the pre-deploy gap so the thing you built is a thing you can watch
run:

1. **Get a native Linux checkout** (or a clean CI runner) to kill the OneDrive cache
   confusion and give CI a faithful build.
2. **Capture a golden turn on .NET Framework 4.8** from a real two-player game, and
   prove it reproduces on x64 Linux net10.0. This is the highest-value check in the
   whole program, because it is where a silently different game would first show. If
   it diverges, re-baseline on Linux and pin the engine version per game.
3. **Deploy the M0 and M2 slice to `roybot`** and run one turn end to end: push the
   turngen and api images, `terraform apply`, upload a fixture, and drive a
   generation through the API's claim, turngen, commit path. That turns the
   exactly-once test from "passes in memory" into "works on Firestore and Cloud
   Tasks".
4. **Seed the new-game RNG** if you want the server to create games (rather than
   only advance a fixture), so galaxies are reproducible from their master seed.

Do those four, and M2 is not just built, it is live; then M3 has a running system to
add AI to.
