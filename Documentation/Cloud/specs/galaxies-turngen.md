# galaxies-turngen - Service Specification

**Service Name:** galaxies-turngen
**Port:** 8081 (local dev)
**Repository Path:** `galaxies-turngen/` (code currently scaffolded under `ServerHost/`; the move is a build-phase step, see §11)
**Build Phase:** M0 (formalized) plus the M1 stream refactor
**Status:** Scaffolded, not deployed. The M0 host, storage seam, and determinism helpers are committed under `ServerHost/`; they do not compile until the headless engine port subset (§4) lands. No cloud resources provisioned yet.
**Owner:** Farehard / Galaxies
**Classification:** Galaxies; Internal

---

## 1. Purpose & Scope

galaxies-turngen is the headless turn-generation worker: a private Cloud Run service that generates exactly one turn for one game, loading that game's authoritative universe and submitted orders from cloud storage, advancing it one year with the ported Stars! Nova engine (`TurnGenerator.Generate()`), and writing the new state plus each empire's fog-of-war intel back. It is the whole point of M0 ("prove the pipe"): storage in, one turn, per-empire storage out, in a Linux container with no UI and no filesystem dependency on a shared folder. It runs at container concurrency 1 and scales to zero, so one game occupies one instance and parallelism across games comes from Cloud Run scaling out single-slot instances, never from in-process threads. The per-game serialization guarantee lives in a Firestore `turnYear` plus lock transaction (§7), not in the container.

The service wraps the existing engine through seams it already exposes. `TurnGenerator` declares `protected virtual` `ReadOrders()`, `WriteIntel()`, `BackupTurn()`, `CleanupOrders()`, and `ParseCommands()`, and the test suite already subclasses it (`SimpleTurnGenerator`) to override those. galaxies-turngen follows the same pattern behind the `IGameStore` seam (§5). `Generate()` advances one year, runs `ScanStep` (an `ITurnStep`) to rebuild each empire's owned-versus-report split, and `IntelWriter` emits the per-empire `.intel` payload that is the de facto per-player wire protocol.

**Out of scope for v1 (M0/M1):**

- No authentication logic of its own beyond the platform OIDC gate. The generate endpoint is private; in M0 it is called by hand or by a test, in M2 by the trigger layer. Google ID token verification and first-party JWT minting belong to galaxies-api, not here.
- No scheduler, no deadlines, no "maximum time between turns" (that is M2, `gen-{gameId}-{turnYear}` Cloud Tasks plus the one-minute Cloud Scheduler sweep). M0 runs generation unlocked by hand; the Firestore lock transaction (§7) is specified here but wired to a live trigger in M2.
- No AI participants (M3). galaxies-turngen publishes `turn-generated`; it does not consume it.
- No new-game creation and therefore no server-side map generation on the turn path. `StarMapInitialiser` (the heaviest `System.Drawing` user) is exercised only at game creation; M0 sidesteps it with a pre-made two-player fixture (see §13).
- No email, no web client, no lobby.
- No serialization format migration. The hand-rolled `ToXml` / `XmlNode`-ctor XML stays the durable and wire format (see GALAXIES-CLOUD-DESIGN.md §A.3). Postgres is explicitly not used anywhere; Firestore is the single control plane (see GALAXIES-CLOUD-DESIGN.md §B.3).

---

## 2. High-Level Architecture

### 2.1 Components

- **Host (`galaxies-turngen/`)** - .NET 10, ASP.NET Core minimal API (`Microsoft.NET.Sdk.Web`), assembly `Nova.Server.Host`. Two entry modes in one binary (`Program.cs`): a private HTTP service (`POST /internal/games/{gameId}/generate`, `GET /healthz`, `GET /readyz`) and a one-shot CLI (`generate <gameId>`) for local runs against a folder. Stateless between requests; holds a large mutable object graph only for the duration of one generation.
- **TurnService (`Engine/TurnService.cs`)** - `GenerateTurnAsync(gameId)`. Loads state and orders, constructs `ServerData`, runs `new TurnGenerator(serverState).Generate()`, writes state and intel back through the store, returns the new turn year. Single method, no UI, no ambient filesystem assumptions beyond the scratch directory it owns.
- **IGameStore (`Storage/IGameStore.cs`)** - the seam that replaces the desktop game's shared folder. Two implementations: `LocalGameStore` (filesystem, for dev and tests, no cloud credentials) and `GcsGameStore` (three GCS buckets). M0 uses a hydrate/dehydrate scratch-directory shim; M1 refactors onto streams so the engine never touches a filesystem (§5).
- **Determinism (`Determinism/SeedDerivation.cs`, `NovaRandom.cs`)** - stable FNV-1a seed derivation from `ServerData.MasterSeed`, and a factory that hands the engine a seeded `Random`. These live in the host for the M0 scaffold and move into `Common` during the port so the engine calls them directly (§8).
- **Ported engine (`Common`, `ServerState`)** - the headless turn engine, referenced by project reference. `Common` is split so pure domain and XML serialization stay and all WinForms / `System.Drawing` live-bitmap code moves to a client-only presentation assembly (§4). `ServerState` drops its `ControlLibrary` reference. Neither may reference a `-windows` assembly.
- **GenerationLock (M2, specified in §7)** - the Firestore claim/commit transaction that makes generation exactly-once per `(gameId, turnYear)`. It wraps `GenerateTurnAsync` in the HTTP handler; the CLI path skips it.

### 2.2 Turn flow (main path)

1. A trigger arrives at `POST /internal/games/{gameId}/generate` carrying the `turnYear` it intends to advance (M0: by hand or a test; M2: Cloud Tasks with an OIDC token at the deadline).
2. **Claim (Firestore transaction A, §7).** Read the game doc. If `game.turnYear != trigger.turnYear`, the turn is already generated: ack and drop. If `state == "generating"` and `lockExpiry > now`, another worker owns it: ack and drop. Otherwise set `state="generating"`, `lockOwner=thisExecutionId`, `lockExpiry=now+lockTtl`, commit. Only the winner proceeds. (M0 runs without this step.)
3. `TurnService.GenerateTurnAsync(gameId)` downloads the game through `IGameStore` into a scratch working directory (M0 shim) or opens streams (M1).
4. Construct `ServerData`; `Restore()` rebuilds the object graph and `LinkServerStateReferences()` re-wires references by key. `StatePathName` and `GameFolder` are set to the scratch directory (M0) or left null (M1).
5. Seed the engine RNG from `serverState.MasterSeed` and `TurnYear` (§8), then `new TurnGenerator(serverState).Generate()`. Inside `Generate()`: `BackupTurn()`, `ReadOrders()` / `ParseCommands()` (via the `CommandRegistry`, replacing the hardcoded `OrderReader` switch), the turn steps including `ScanStep`, `WriteIntel()`, `CleanupOrders()`. The engine increments `TurnYear`.
6. `serverState.Save()` writes the new state; the store uploads the new authoritative state, every per-empire `.intel`, and the per-turn history archive, and deletes consumed orders.
7. **Commit (Firestore transaction B, §7).** Re-read; assert `turnYear` unchanged and `lockOwner == thisExecutionId`; set `turnYear += 1`, `state="idle"`, clear the lock, recompute `deadline`, point `currentStatePath` at the new blob. If the assertion fails, discard the just-written results (they land under the new turn year and are simply not adopted).
8. Publish `turn-generated` to Pub/Sub (`gameId`, `turnYear`, `empireIds`, `aiEmpireIds`, `gameEnded`). Return `{ "gameId", "turnYear" }`.

### 2.3 GCP Topology

| Setting | Value | Note |
|---|---|---|
| Platform | Cloud Run service (project `roybot`, region `us-central1`) | Not GKE; the workload is idle by design (see GALAXIES-CLOUD-DESIGN.md §B.1). |
| Container concurrency | **1** | One game per instance. The engine holds a big heap and relies on process-wide singletons (`GameSettings`) plus a per-`TurnGenerator` RNG; one slot sidesteps static cross-talk. |
| Scale | `min-instances=0`, `max-instances` capped (e.g. 20) | Scale to zero between turns. Parallelism across games is horizontal scale-out. |
| CPU | `--cpu-boost` on; CPU allocated during request only | Cold-start friendly; a generation is a few CPU-seconds. |
| Ingress (M2 target) | `internal`, OIDC invoker only | GCP-native OIDC identity tokens, NOT the Aries `X-Aries-Internal-Secret` HMAC. Cloud Tasks and Pub/Sub push mint OIDC tokens via `sa-invoker` holding `run.invoker`. |
| Ingress (M0 relaxation) | `--ingress=all --no-allow-unauthenticated` | Auth still required (a valid ID token with `run.invoker`), but laptop-reachable so the developer can hand-invoke while proving the pipe. Tightened to `internal` in M2 once the trigger layer invokes from inside the project. Tracked as an open item, §14. |
| Service account | `sa-turngen` | GCS read/write on state and intel, read on orders; Firestore read/write (lock/transaction); Pub/Sub publisher on `turn-generated`; Secret Manager accessor as needed. Least privilege (see GALAXIES-CLOUD-DESIGN.md §B.5). |
| Image | `us-central1-docker.pkg.dev/roybot/roybot-galaxies/galaxies-turngen:<tag>` | Multi-stage build to `mcr.microsoft.com/dotnet/aspnet:10.0`, non-root uid 10001. `PORT` supplied by Cloud Run, bound via `ASPNETCORE_URLS`. |
| VPC | None required for M0 | The service reaches GCS, Firestore, and Pub/Sub over Google APIs. No VPC connector is provisioned; revisit only if a private dependency demands it (§14). |

### 2.4 Repository Layout

Galaxies is one folder per microservice, each with its own `cloudbuild.yaml` and `Dockerfile`. galaxies-turngen owns the headless worker. The engine libraries (`Common`, `ServerState`) are shared and sit at the repo root; the turngen folder references them by project reference.

```
galaxies-turngen/                 # this service (moved from ServerHost/, see §11)
├── Program.cs                    # entry: HTTP service + `generate <gameId>` CLI
├── appsettings.json              # non-secret config skeleton (bucket names come from env)
├── Engine/
│   └── TurnService.cs            # GenerateTurnAsync(gameId)
├── Storage/
│   ├── IGameStore.cs             # the shared-folder replacement seam (§5)
│   ├── LocalGameStore.cs         # filesystem store (dev + tests)
│   └── GcsGameStore.cs           # GCS store (state / orders / intel buckets)
├── Determinism/
│   ├── SeedDerivation.cs         # stable FNV-1a seed rule (moves into Common, §8)
│   └── NovaRandom.cs             # seeded Random factory
├── Generation/                   # M2
│   └── GenerationLock.cs         # Firestore turnYear+lock claim/commit (§7)
├── Nova.Server.Host.csproj       # net10.0 web SDK; refs Common + ServerState
├── Dockerfile                    # multi-stage → aspnet:10.0, non-root
├── cloudbuild.yaml               # build, push to roybot-galaxies, deploy dark
├── spec.md                       # this document
└── questions.md                  # forward-looking dev-team forks (§14)

Common/                           # shared engine contract (net10.0, headless, §4)
ServerState/                      # headless turn engine (net10.0, no -windows refs)
Nova.Client.Presentation/         # NEW: receives the WinForms / Bitmap code removed from Common
components.xml                    # static component + tech database, shipped in the image

infra/terraform/                  # roybot infrastructure (image repo, 3 buckets, Firestore, SA, service)
├── main.tf
├── variables.tf
├── outputs.tf
└── versions.tf

.github/workflows/build.yml       # Linux build + `dotnet test` on ubuntu-latest
```

Each `cloudbuild.yaml` builds against its own folder context. The turngen `Dockerfile` copies `Common/`, `ServerState/`, the service folder, and `components.xml`, restores, and publishes; the copy list is kept tight so the layer cache survives code-only edits.

---

## 3. Configuration & Feature Flags

Every capability ships dark behind a `_TURNGEN_*` Cloud Build substitution that maps to an env var on the revision. With the master gate off, the generate mutation returns `403 {"disabled":true}` and the probes stay live (Cloud Run needs them to route). A substitution without the matching `--set-env-vars` entry on the revision is a silent no-op, so the flag plumbing is verified after every deploy (§12).

### 3.1 Switches (all ship OFF)

| Where (trigger) | Switch | Off state | On state |
|---|---|---|---|
| galaxies-turngen | `_TURNGEN_ENABLED` | `POST /internal/games/{id}/generate` returns `403 {"disabled":true}`; `/healthz` and `/readyz` stay live | generation runs |
| galaxies-turngen | `_TURNGEN_COMMIT_ENABLED` | dry-run: generation runs to completion and writes state + intel under the new `turnYear` history path, but Firestore commit transaction B is skipped, the `current` pointer is not swapped, `turnYear` is not advanced, and orders are not deleted (nothing is adopted) | commit transaction B plus the `current`-pointer swap and order cleanup go live |
| galaxies-turngen | `_TURNGEN_PUBLISH_ENABLED` | `turn-generated` is not published (no AI, no email fan-out) even after a committed turn | `turn-generated` published to Pub/Sub |
| galaxies-turngen | `_TURNGEN_LOCK_ENABLED` | claim/commit transactions are skipped (M0 hand-invocation, single caller) | Firestore `turnYear`+lock guard enforced (M2) |

`_TURNGEN_COMMIT_ENABLED=false` is the load-bearing dark mode for this service: it lets you run a real generation on a real game in the cloud and diff the produced state against the golden without ever adopting it. That is how a turn worker "ships dark" (an idle read-only service proves nothing; a worker proves itself by generating and then not committing).

### 3.2 Environment variables

| Env var | Default | Purpose |
|---|---|---|
| `GALAXIES_LOCAL_ROOT` | unset | If set, use `LocalGameStore` rooted here (dev/CLI). If unset, use `GcsGameStore`. |
| `GALAXIES_STATE_BUCKET` | (required in cloud) | `roybot-galaxies-state` |
| `GALAXIES_ORDERS_BUCKET` | (required in cloud) | `roybot-galaxies-orders` |
| `GALAXIES_INTEL_BUCKET` | (required in cloud) | `roybot-galaxies-intel` |
| `GALAXIES_SCRATCH_ROOT` | `${TMPDIR}/galaxies` | Working directory root for the M0 hydrate/dehydrate shim (§5). |
| `GALAXIES_FIRESTORE_PROJECT` | `roybot` | Control-plane project for the lock and metadata. |
| `GALAXIES_COMPONENTS_PATH` | `components.xml` | Headless content location (§4); the engine reads the component/tech database through a content locator, never a dialog or `nova.conf` registry walk. |
| `GALAXIES_LOCK_TTL_MINUTES` | `10` | `lockExpiry` window for the claim transaction (§7). |
| `ASPNETCORE_URLS` | `http://0.0.0.0:8080` | Cloud Run supplies `PORT`; local dev binds 8081. |
| `TURNGEN_ENABLED`, `TURNGEN_COMMIT_ENABLED`, `TURNGEN_PUBLISH_ENABLED`, `TURNGEN_LOCK_ENABLED` | `false` | Runtime flags fed by the `_TURNGEN_*` substitutions. |

Bucket names and secrets are never committed to `appsettings.json`; it carries only logging levels and empty placeholders.

---

## 4. Headless Engine Port Subset (the .NET 10 turn path)

None of the host code compiles until `Common` and `ServerState` build on `net10.0` on Linux with zero WinForms and zero `System.Drawing` on the turn-generation path. This is the real work of M0 and it is deliberately scoped down to "enough to generate a turn on an existing game." Full map generation stays out (it runs only at game creation, which M0 fixtures around). See GALAXIES-CLOUD-DESIGN.md §A.0 to §A.2.

### 4.1 Project partition

| Assembly | Fate | Target | Note |
|---|---|---|---|
| `Common` (Nova.Common) | Split. Pure domain and XML serialization stay; UI and live-bitmap files move out. | `net10.0` | Drop the `System.Windows.Forms`, `System.Drawing`, and SOAP formatter references. Becomes the shared engine contract. |
| `Nova.Client.Presentation` | New. Receives the code removed from `Common`. | `net10.0-windows` | `ProgressDialog.*`, the WinForms `Report` sink, the dialog bodies of `FileSearcher`, and all live `Bitmap` handling (`ShipIcon.Image`, `Component.ComponentImage`, icon sets). Client only; never on the server path. |
| `ServerState` (Nova.Server) | Headless engine. Remove the `ControlLibrary` project reference and the `SaveFileDialog` in `ServerData.Save`. | `net10.0` | Must not reference any `-windows` assembly. |
| `Nova.Server.Host` (this service) | Wraps `Generate()` as `TurnService`; owns storage and DI. | `net10.0` | References `Common` and `ServerState` only. |

### 4.2 De-WinForms / de-Drawing coupling points

| Location | Coupling | Replacement |
|---|---|---|
| `Common/Report.cs` | `MessageBox.Show` in `Error/Info/Fatal/Debug`; `Thread.CurrentThread.Abort()` (throws `PlatformNotSupportedException` on modern .NET) | Introduce `IReporter`; keep `Report` as a static facade with a settable `Report.Sink`. The host sets a logging sink writing structured JSON to Cloud Logging; `Thread.Abort` becomes a thrown `NovaFatalException`. Hard compile/runtime blocker. |
| `Common/Serializer.cs` | Dead `BinaryFormatter` plus the SOAP serialization reference | Delete outright. Will not compile on modern .NET without an obsolete opt-in, and is a known deserialization hole. Hard blocker, not a cleanup. |
| `Common/FileSearcher.cs`, `Common/Files/Config.cs` | `FolderBrowserDialog` / `OpenFileDialog`; `Microsoft.Win32` registry; `nova.conf` walking | Introduce `IContentLocator` returning `Stream`s for `components.xml`, race files, settings, resolved from the container image or config (`GALAXIES_COMPONENTS_PATH`). Never a dialog, never `GetNovaRoot()`. |
| `ServerState/Persistence/ServerData.cs` | `using System.Windows.Forms`; `SaveFileDialog` in `Save()`; 8-second file-lock retry loop in `Restore()` | `Save()`/`Restore()` operate on the injected `StatePathName` (host always sets it, so the dialog branch is dead). Delete the lock loop; GCS object generations give atomic writes with `ifGenerationMatch` preconditions instead of cooperative file locks. Remove the `ControlLibrary` reference. |
| `ServerState/NewGame/StarMapInitialiser.cs` | `using System.Drawing`; builds `ShipIcon(hull.ImageFile, (Bitmap)hull.ComponentImage)` for seed ships | Off the M0 turn path (game creation only). During the port, construct `ShipIcon` from the `Source` string; leave the live `Bitmap` deferred to the client. |
| `ServerState/BattleEngine.cs` | `using System.Drawing` (battle-grid geometry) | Battle math is on positions, not pixels. Replace `Point`/`Size` with `NovaPoint`/ints; drop the import. On the M0 turn path, so it must be clean. |
| `Common` domain: `ShipIcon.cs`, `Components/Component.cs`, icon sets, `NovaPoint` | Pervasive `System.Drawing` (mostly cosmetic; `NovaPoint` only constructs from `System.Drawing.Point`) | Data/reference split: keep string identifiers (`ShipIcon.Source`, `Component.ImageFile`) in `Common`; move live `Bitmap` properties and file loading to `Nova.Client.Presentation`. Drop `NovaPoint`'s `System.Drawing.Point` constructor. |

Do not add a `System.Drawing.Common` reference to make it compile. On .NET 6+ it is Windows-only and throws on Linux at runtime, so it would build green in CI and fail the first time icon or map code executes in the container. The data/reference split is the fix, not the NuGet package (see GALAXIES-CLOUD-DESIGN.md §A.2).

### 4.3 Order ingestion: CommandRegistry

`OrderReader.ReadPlayerTurn()` today dispatches command types through a hardcoded `switch` on the XML `Type` attribute (`research` / `waypoint` / `design` / `production` / `renamefleet`). Replace that switch with a `CommandRegistry` that maps type keys to `ICommand` factories. Orders are `ICommand` (Waypoint, Research, Design, Production, RenameFleet) carrying `IsValid` and `ApplyToState`; the registry is the extension seam that lets external and community AIs submit the same validated command shapes through galaxies-api without editing the engine. `ReadOrders()` and `ParseCommands()` remain the `protected virtual` seams the store overrides.

---

## 5. The IGameStore Seam

`IGameStore` replaces the desktop game's shared folder. The engine's four file seams map one-to-one onto storage operations: `ReadOrders()` reads the orders bucket, `WriteIntel()` writes the intel bucket, `BackupTurn()` archives to the state bucket, `CleanupOrders()` deletes consumed orders. Because the file boundary is the de facto protocol, the cloud port swaps the folder for buckets and leaves `TurnGenerator.Generate()` untouched.

### 5.1 M0 shim (hydrate / dehydrate)

The committed M0 interface is deliberately narrow:

```
Task DownloadGameAsync(string gameId, string workingDir, CancellationToken ct);
Task UploadResultsAsync(string gameId, string workingDir, int newTurnYear, CancellationToken ct);
```

`TurnService.GenerateTurnAsync` creates a per-run scratch directory (`gen-{gameId}-{guid}` under `GALAXIES_SCRATCH_ROOT`), calls `DownloadGameAsync` to pull the game's `state.sstate` and every submitted `{race}.orders` into it, points the engine's `GameFolder` and `StatePathName` at that directory, runs `Generate()` (which reads and writes the directory through its existing `GameFolder` seam), then calls `UploadResultsAsync` to push the new state, every `.intel`, and the per-year backup back, and deletes the scratch directory in a `finally`. It touches no engine internals. This is the whole M0 mechanism: prove the pipe without rewriting `OrderReader` or `IntelWriter`.

`LocalGameStore` (dev, tests) copies files under `{root}/games/{gameId}/`. `GcsGameStore` streams objects to and from the three buckets.

### 5.2 M1 stream target

M1 refactors `OrderReader` and `IntelWriter` off `serverState.GameFolder` (they hit `DirectoryInfo`/`File` directly today, as do `BackupTurn` and `CleanupOrders`). Their constructors take an `IGameStore` (or `Func<string,Stream>` factories) instead of a folder path, so no scratch directory exists and the engine never touches a filesystem. The target seam:

```
Stream OpenState(string gameId);                     // state.sstate
IEnumerable<Stream> OpenOrders(string gameId);       // every submitted .orders
Stream CreateIntel(string gameId, int year, string race);
void ArchiveTurn(string gameId, int year);           // replaces BackupTurn
void DeleteOrders(string gameId);                    // replaces CleanupOrders
void SaveState(string gameId, Stream xml);
```

A `CloudTurnGenerator : TurnGenerator` overrides the four `protected virtual` seams to delegate to `IGameStore`, mirroring the existing `SimpleTurnGenerator` test double. `TurnService.GenerateTurnAsync` keeps the same signature across the M0-to-M1 swap; the change is entirely behind the seam. See GALAXIES-CLOUD-DESIGN.md §A.5.

---

## 6. TurnService.GenerateTurnAsync

`GenerateTurnAsync(gameId, ct)` returns the new (advanced) turn year. Callers hold the per-game generation lock (§7) so it runs at most once per `(gameId, turnYear)`. In order:

1. Create the scratch working directory; `store.DownloadGameAsync(gameId, workingDir, ct)`.
2. `new ServerData()`; set `StatePathName` to `{workingDir}/{gameId}.sstate`; `Restore()` (rebuilds the graph, `LinkServerStateReferences()`); set `GameFolder` to `workingDir`. Record `yearBefore = serverState.TurnYear`.
3. Seed the RNG from `serverState.MasterSeed` and `TurnYear` (§8), then `new TurnGenerator(serverState).Generate()`. The engine increments `TurnYear`.
4. `serverState.Save()` (writes to `StatePathName`, no dialog because it is set). Record `yearAfter`.
5. `store.UploadResultsAsync(gameId, workingDir, yearAfter, ct)`; return `yearAfter`.
6. `finally`: delete the scratch directory; a failure to clean up is logged, not thrown.

The method emits structured log lines on entry and exit (`gameId`, `yearBefore`, `yearAfter`) so generation duration and the before/after year land as log-based metrics in Cloud Monitoring. All engine `Report.*` output flows to the same logging sink (§4.2), which is exactly why the legacy `MessageBox` had to go: unhandled engine exceptions must land in Error Reporting, not a dialog no one sees.

---

## 7. Exactly-Once Generation (Firestore turnYear + lock)

Two things can start a generation (everyone submitted, event-driven from galaxies-api; or the deadline fired, time-driven from Cloud Tasks), and exactly one generation per turn must happen. The guarantee is a Firestore transaction keyed on `(gameId, turnYear)`, plus two supporting layers. See GALAXIES-CLOUD-DESIGN.md §B.2.

The game control-plane document (`games/{gameId}`) holds:

```
turnYear (int), state ("idle"|"generating"), lockOwner (string),
lockExpiry (timestamp), deadline (timestamp), currentStatePath (gcs uri)
```

Every trigger carries the `turnYear` it means to advance. The worker:

1. **Claim (transaction A).** Read the doc. If `game.turnYear != trigger.turnYear`, the turn was already generated: ack and drop (stale/duplicate). If `state == "generating"` and `lockExpiry > now`, another worker owns it: ack and drop. Otherwise set `state="generating"`, `lockOwner=thisExecutionId`, `lockExpiry=now+GALAXIES_LOCK_TTL_MINUTES`, commit. Only the transaction winner proceeds.
2. **Work.** `GenerateTurnAsync` (§6): load, `Generate()`, write the new state blob and intel to GCS.
3. **Commit (transaction B).** Re-read; assert `turnYear` unchanged and `lockOwner == thisExecutionId`; set `turnYear += 1`, `state="idle"`, clear the lock, recompute `deadline`, point `currentStatePath` at the new blob. If the assertion fails (the lock expired and another worker took over), discard the just-written results: they were written under the new turn year and are simply never adopted (the `current` pointer is not swapped).

Three independent layers protect the property: Cloud Tasks name-based de-dup at enqueue (`gen-{gameId}-{turnYear}` collides on a second create), the Firestore `turnYear`/lock guard at execution, and GCS `ifGenerationMatch` preconditions on the authoritative state write so a duplicate worker cannot silently overwrite the adopted blob. Because `turnYear` increments monotonically and both triggers name the turn they mean to generate, any second trigger for the same turn finds `turnYear` already advanced and drops.

In M0 the whole transaction is gated off (`_TURNGEN_LOCK_ENABLED=false`): there is one hand-caller, so `GenerateTurnAsync` runs unlocked. The lock lands with M2 when the Cloud Tasks and Cloud Scheduler triggers go live. `GenerationLock` lives in `Generation/GenerationLock.cs` and wraps the HTTP handler only; the CLI path never locks.

---

## 8. Determinism

The engine cannot be verified while it is non-deterministic, so seeding lands with the port, not after. Today every stochastic subsystem constructs an unseeded `new Random()`, so turns are non-reproducible and untestable. See GALAXIES-CLOUD-DESIGN.md §A.4.

**Seed model.**
- `long MasterSeed` is added to `ServerData`, set at game creation and round-tripped through the existing `ToXml` and the `XmlNode` constructor (one `SaveData` line, one load case, and a bumped `FormatVersion` on the state root). Old saves lacking it load with a synthesized seed written back once.
- Per-turn seed: `hash(MasterSeed, turnYear)`. Per-seat seed: `hash(MasterSeed, turnYear, empireId)`. Per-subsystem seed: `hash(MasterSeed, turnYear, subsystemName)` so battles and minefields never share or reorder one sequence.
- The hash is a plain FNV-1a over the inputs (`SeedDerivation`). It deliberately does not use `string.GetHashCode`, which is per-process randomized on modern .NET and would break reproducibility across runs. This function must stay stable forever, because changing it re-rolls every game's randomness.

**`new Random()` sites to replace.** In `TurnGenerator` (constructor, currently `rand = new Random()`), plus `BattleEngine`, `CheckForMinefields`, `StarMapGenerator`, `NameGenerator`, the four instances in `StarMapInitialiser`, and `Common/PointUtilities` and `Common/SpaceAllocator`. Each takes a derived stream (`NovaRandom.ForTurn` / `ForSeat` / `ForSubsystem`). Prefer injecting an `IRandom` / `Random` over newing one inside each class, so tests can substitute a recorded sequence.

**The non-RNG trap: deterministic iteration.** Reproducibility also requires deterministic iteration order. `Generate()` iterates `serverState.AllEmpires.Values`, `IterateAllFleets()`, and `empire.OwnedStars.Values`; .NET dictionary enumeration order is not contractual. Wherever a loop body mutates shared state or draws from the RNG, iterate sorted by key (empire id, star key, fleet key). Unordered iteration is a silent source of turn-to-turn divergence that seeding alone does not fix.

`SeedDerivation` and `NovaRandom` sit in the host for the scaffold and move into `Common` during the port so both the host and the engine call one stable rule. The fog-of-war path is deterministic too: `ScanStep` rebuilds each `EmpireData` owned-versus-report split, and `IntelWriter` emits per-empire `.intel`; both iterate in sorted order so a re-run produces byte-identical intel per seat.

---

## 9. Data Model & Object Layout

galaxies-turngen reads and writes two stores: GCS for the universe graph and its per-empire payloads, Firestore for the small transactional control plane. No relational database is used anywhere (see GALAXIES-CLOUD-DESIGN.md §B.3); Postgres was considered and rejected for the graph.

### 9.1 GCS object layout

Buckets are private, uniform bucket-level access, public-access-prevention enforced. Intel and orders are never public and are served only through galaxies-api with per-empire authorization; galaxies-turngen writes them but never serves them.

| Data | Bucket | Object path (M0) | Note |
|---|---|---|---|
| Authoritative universe per turn | `roybot-galaxies-state` | `games/{gameId}/state/{turnYear}.sstate` | The per-turn object is the history; replaces the desktop `GameFolder/<year>/`. |
| Current-turn pointer | `roybot-galaxies-state` | `games/{gameId}/state/current.sstate` | What a trigger advances from; swapped only on commit (§7). Written with `ifGenerationMatch`. |
| Submitted orders | `roybot-galaxies-orders` | `games/{gameId}/orders/current/{race}.orders` | Read by the `ReadOrders()` seam; `OrderReader` validates turn year and empire id. Deleted on generation (`CleanupOrders`). |
| Per-empire intel | `roybot-galaxies-intel` | `games/{gameId}/intel/{turnYear}/{race}.intel` | Write-once per turn from `IntelWriter`; the per-player wire payload. |

`.sstate`, `.orders`, `.intel` are the extensions in `Common/GlobalDefinitions.cs`. M0 stores state uncompressed with a `current` pointer object (matching the committed `GcsGameStore`). Gzipping the XML (`{turnYear}.xml.gz`) to cut storage and egress is an M1-or-later optimization tracked in §14; the design (§B.3) prefers gzip once the format is stable. Bucket lifecycle rules (Standard for active games, transition to Coldline after 30 days and Archive after 365 for finished games) live in Terraform, not in this service.

### 9.2 Firestore control plane

Native mode, one store for everything including accounts (`users/{google_sub}`). galaxies-turngen touches only the game doc:

| Doc | Fields galaxies-turngen reads/writes | Use |
|---|---|---|
| `games/{gameId}` | `turnYear`, `state`, `lockOwner`, `lockExpiry`, `deadline`, `currentStatePath`, `masterSeedRef` (mirrored from the state XML for quick reference), `empireIds`, `aiEmpireIds` | The exactly-once lock (§7) and the pointer to the blob to load. Written transactionally on claim and commit. |

`MasterSeed` is authoritative in the state XML (§8); the Firestore mirror is a convenience for schedulers and never the source of truth.

---

## 10. Endpoint Catalog & Probes

| Method | Path | Purpose | Auth (M2 target) | Gated by |
|---|---|---|---|---|
| POST | `/internal/games/{gameId}/generate` | Generate one turn. Body/trigger carries the intended `turnYear`. Returns `{ "gameId", "turnYear" }` on success, `404` if the game state is missing, `409` if the claim finds the turn already generated or locked. | OIDC identity token, `run.invoker` | `_TURNGEN_ENABLED` (403 `{"disabled":true}` when off) |
| GET | `/healthz` | Liveness: process is up. Returns `200 "ok"`. | none (ingress-gated) | always live |
| GET | `/readyz` | Readiness: the content locator can open `components.xml` and the configured store is reachable (a GCS `HEAD` on the state bucket, or the local root exists). Returns `200 {"ready":true}` or `503 {"ready":false,"reason":...}`. | none (ingress-gated) | always live |
| CLI | `generate <gameId>` | One-shot local generation against `GALAXIES_LOCAL_ROOT`, prints the new year, exits. No auth, no lock, no Pub/Sub. | n/a (local) | n/a |

The probes must answer even when `_TURNGEN_ENABLED=false`, so Cloud Run can route to a dark revision. `/readyz` is the real dependency probe: a `503` there means the component database or a bucket is unreachable, which is the failure a cold container hits first.

---

## 11. Build Phases

Ordered, each step small enough to ship. M0 is the pipe; M1 removes the scratch directory. The determinism and golden-turn safety net (steps M0.2 and M0.3) is built before the framework port (M0.4) because the port is otherwise unverifiable.

| Step | Phase | Deliverable | Done when |
|---|---|---|---|
| M0.1 | M0 | Move the scaffold from `ServerHost/` to `galaxies-turngen/`; add its own `cloudbuild.yaml`. Create `Galaxies.sln` holding `Common`, `ServerState`, `galaxies-turngen`, `Tests`. | The solution loads; the folder is self-contained with `Dockerfile` + `cloudbuild.yaml`. |
| M0.2 | M0 | Determinism: add `ServerData.MasterSeed` (round-tripped, `FormatVersion` bumped); replace the `new Random()` sites (§8) with derived streams; fix deterministic iteration. | A seeded `Generate()` on the fixture produces byte-identical state across two runs on the same runtime. |
| M0.3 | M0 | Capture golden turns on .NET Framework 4.8 first (§13), commit them under `Tests/Fixtures/`. | The golden `.sstate` and per-empire `.intel` are committed. |
| M0.4 | M0 | Headless port subset (§4): SDK-style `net10.0` projects, drop `ControlLibrary`, delete `Serializer.cs` + SOAP, `IReporter` sink, `ServerData` de-dialog + de-lock-loop, the `System.Drawing` data/reference split, `IContentLocator`. | `Common`, `ServerState`, `galaxies-turngen` build on `ubuntu-latest`. |
| M0.5 | M0 | `TurnService` + `LocalGameStore` run the scratch-dir shim end to end. | `dotnet run -- generate fixture-2p` advances the fixture one year and writes one `.intel` per empire, locally. |
| M0.6 | M0 | `GcsGameStore`, `Dockerfile`, Terraform (image repo, three buckets, `sa-turngen`, the private service, Firestore). | The same container does the same thing on `roybot`, reading and writing GCS. |
| M0.7 | M0 | Golden-turn CI test on x64 Linux net10.0, including the floating-point reproduction spike (§13). | The golden test passes on Linux, or the golden was deliberately re-baselined on the target with the reason recorded. |
| M1.1 | M1 | `CommandRegistry` replaces the `OrderReader` `switch` (§4.3). | Orders parse through the registry; the golden still matches. |
| M1.2 | M1 | Stream-based `IGameStore` (§5.2): refactor `OrderReader` and `IntelWriter` off `GameFolder`; add `CloudTurnGenerator`. No scratch directory. | A cloud generation runs with no filesystem writes; the golden still matches. |
| M1.3 | M1 | Gzip state blobs (`{turnYear}.xml.gz`), add `ifGenerationMatch` on the state write. | Storage and egress drop; concurrent-writer test cannot clobber the adopted blob. |

M2 (the Cloud Tasks / Cloud Scheduler trigger, the `GenerationLock` transaction, ingress tightening to `internal`) and M3 (`galaxies-ai` consuming `turn-generated`) are separate milestones; this service exposes the seams they need (§7, §2.2 step 8) but does not build them.

---

## 12. Rollout (ships dark)

galaxies-turngen is the only service in M0, so the ordered deploy is short: provision infrastructure, build and push the image, deploy the service dark, then stage the flips. It ships dark: with `_TURNGEN_ENABLED=false` the generate mutation returns `403 {"disabled":true}` and only the probes answer. Values are pinned for copy-paste.

### §0 - Set these once per shell

```bash
gcloud config set project roybot
export REGION=us-central1
export REG=us-central1-docker.pkg.dev/roybot/roybot-galaxies
export STATE_BUCKET=roybot-galaxies-state
export ORDERS_BUCKET=roybot-galaxies-orders
export INTEL_BUCKET=roybot-galaxies-intel
export TG="$(gcloud run services describe galaxies-turngen --region="$REGION" --format='value(status.url)')"
```

### §1 - Infrastructure (Terraform, once)

```bash
cd infra/terraform
terraform init -backend-config="bucket=roybot-galaxies-tfstate"
terraform apply \
  -var="turngen_image=${REG}/galaxies-turngen:0.1.0"
# Provisions: Artifact Registry repo roybot-galaxies; the three buckets
# (uniform access, public-access-prevention enforced, versioning on state);
# Firestore (native mode); sa-turngen with least-privilege bindings; the private
# Cloud Run service. Create the tfstate bucket by hand before `init`.
```

### §2 - Build, push, deploy dark (ordered)

```bash
# 2.1 Build + push
gcloud auth configure-docker us-central1-docker.pkg.dev
docker build -f galaxies-turngen/Dockerfile -t "${REG}/galaxies-turngen:0.1.0" .
docker push "${REG}/galaxies-turngen:0.1.0"

# 2.2 Deploy dark. M0 uses ingress=all + require-auth so you can hand-invoke with
#     an identity token; M2 tightens to --ingress=internal. Every _TURNGEN_* flag
#     ships false.
gcloud run deploy galaxies-turngen \
  --region="$REGION" --image="${REG}/galaxies-turngen:0.1.0" \
  --no-allow-unauthenticated --ingress=all \
  --concurrency=1 --min-instances=0 --max-instances=20 --cpu-boost \
  --service-account="sa-turngen@roybot.iam.gserviceaccount.com" \
  --set-env-vars="GALAXIES_STATE_BUCKET=${STATE_BUCKET},GALAXIES_ORDERS_BUCKET=${ORDERS_BUCKET},GALAXIES_INTEL_BUCKET=${INTEL_BUCKET},GALAXIES_FIRESTORE_PROJECT=roybot,TURNGEN_ENABLED=false,TURNGEN_COMMIT_ENABLED=false,TURNGEN_PUBLISH_ENABLED=false,TURNGEN_LOCK_ENABLED=false"

# 2.3 Verify the flags actually landed on the revision (a substitution without the
#     matching env entry is a silent no-op):
gcloud run services describe galaxies-turngen --region="$REGION" \
  --format='value(spec.template.spec.containers[0].env)' | tr ',' '\n' | grep TURNGEN_

# 2.4 Probes answer even while dark:
TOKEN=$(gcloud auth print-identity-token --audiences="$TG")
curl -s "$TG/healthz" -H "Authorization: Bearer $TOKEN"          # -> "ok"
curl -s "$TG/readyz"  -H "Authorization: Bearer $TOKEN" | jq .   # -> {"ready":true}
# Dark generate is refused:
curl -s -o /dev/null -w "%{http_code}\n" -X POST \
  "$TG/internal/games/fixture-2p/generate" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"turnYear":2100}'                                          # -> 403
```

### §3 - Seed the fixture

```bash
# Upload the committed two-player fixture (§13) to the state and orders buckets.
gcloud storage cp Tests/Fixtures/fixture-2p/state.sstate \
  "gs://${STATE_BUCKET}/games/fixture-2p/state/current.sstate"
gcloud storage cp Tests/Fixtures/fixture-2p/orders/*.orders \
  "gs://${ORDERS_BUCKET}/games/fixture-2p/orders/current/"
```

### Flip 1 - arm generation in dry-run (commit still off)

Set `_TURNGEN_ENABLED=true`, leave `_TURNGEN_COMMIT_ENABLED=false`, and redeploy (or `gcloud run services update galaxies-turngen --region="$REGION" --update-env-vars=TURNGEN_ENABLED=true` for an immediate flip the next deploy wipes).

```bash
TOKEN=$(gcloud auth print-identity-token --audiences="$TG")
curl -s -X POST "$TG/internal/games/fixture-2p/generate" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"turnYear":2100}' | jq .           # -> {"gameId":"fixture-2p","turnYear":2101}

# Dry-run wrote history + intel under the NEW year but did NOT swap `current`:
gcloud storage ls "gs://${STATE_BUCKET}/games/fixture-2p/state/"    # 2101.sstate present
gcloud storage ls "gs://${INTEL_BUCKET}/games/fixture-2p/intel/2101/"  # one .intel per empire
gcloud storage cat "gs://${STATE_BUCKET}/games/fixture-2p/state/current.sstate" \
  | head -c 64                             # still the 2100 universe (not adopted)
```

Diff `2101.sstate` against the committed golden here: this is the real acceptance check, running a live cloud generation without adopting it.

### Flip 2 - arm commit

Set `_TURNGEN_COMMIT_ENABLED=true` and redeploy. Now a generation swaps `current`, advances `turnYear` in Firestore (once `_TURNGEN_LOCK_ENABLED` is on in M2), and deletes consumed orders.

```bash
TOKEN=$(gcloud auth print-identity-token --audiences="$TG")
curl -s -X POST "$TG/internal/games/fixture-2p/generate" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"turnYear":2101}' | jq .           # -> {"turnYear":2102}
gcloud storage cat "gs://${STATE_BUCKET}/games/fixture-2p/state/current.sstate" \
  | head -c 64                             # now the 2102 universe (adopted)
gcloud storage ls "gs://${ORDERS_BUCKET}/games/fixture-2p/orders/current/"  # empty (cleaned)
```

### Reserved - do not flip in v1

`_TURNGEN_PUBLISH_ENABLED` and `_TURNGEN_LOCK_ENABLED` stay `false` through M0/M1. Publishing `turn-generated` has no subscriber until galaxies-ai and galaxies-notifier exist (M3); the lock transaction has no trigger to serialize against until M2. Turning them on early publishes into the void and locks against a single hand-caller, respectively.

### Kill switch / rollback

`gcloud run services update galaxies-turngen --region="$REGION" --update-env-vars=TURNGEN_ENABLED=false` stops all generation immediately (generate returns `403`, probes stay live). GCS object versioning on the state bucket preserves every prior turn, so nothing is lost. All Terraform is additive; there is no down-migration.

---

## 13. Testing

### 13.1 The fixture

BUILT, and not the way this section originally described. It said to use the desktop New Game wizard on .NET Framework; that is unnecessary, because the wizard is only a front end for `Gameinitializer`, which lives in `ServerState` and runs headless. The fixture is generated on Linux by `Tests.ServerHost/FixtureBuilder.cs` (`[Explicit]`, never run by CI) and committed at `Tests/Fixtures/games/fixture-2p/state/current.sstate`: two empires, 17 stars, three fleets and three designs each, starting year 2100.

It uses the canonical layout in `GamePaths` (`ServerHost/Storage/IGameStore.cs`), not the flat `state.sstate` plus `orders/{race}.orders` this section used to specify. That old layout disagreed with what galaxies-api writes, and the disagreement was a live defect: the API wrote orders to `games/{gameId}/orders/{turnYear}/{empireId}.orders`, the game store read `games/{gameId}/orders/current/`, and turns generated with every submitted order silently discarded. Objects are keyed by empire id; the engine's race-name filenames exist only inside the scratch directory, and the store translates. See `Documentation/Cloud/M0.md` for the full layout.

The fixture carries no orders, because orders are per turn year and per empire and so belong to a run rather than to the game. `Tests.ServerHost/TurnRunTests.cs` writes them where it needs them, and asserts both that a submitted order reaches the generated turn and that an order tagged for another empire is refused.

### 13.2 Golden-turn regression

With the RNG seeded (§8), snapshot the fixed `ServerData` plus the fixed orders, run `Generate()`, and assert the resulting state XML (and each `.intel`) equals a committed golden document. The harness already exists: the `Tests` project's `SimpleTurnGenerator` subclasses `TurnGenerator` and overrides the file seams, feeding in-memory orders and suppressing file I/O. CI (`.github/workflows/build.yml`) restores, builds, and runs the suite on `ubuntu-latest`; it stays red until the port subset compiles, which is the correct signal.

### 13.3 The x86-to-x64 floating-point reproduction spike

This is the single most important M0 check, because it is where a silently different game would first show.

1. **Capture the golden on 4.8 first.** With the RNG seeded, run `Generate()` on the fixture on the current .NET Framework 4.8 build (x86) and commit the resulting `state.sstate` as the expected output. Do this before touching the framework, so any later diff is a regression introduced by the port.
2. **Reproduce on the target.** Run the same seeded generation on x64 Linux net10.0 and assert the output matches byte for byte.
3. **If it diverges,** the cause is almost certainly floating-point in battle or movement math differing across architecture and JIT (x86 80-bit intermediates versus x64 SSE, or FMA contraction). Re-baseline the golden on the x64 Linux target and pin the engine version per game (`FormatVersion` plus an engine-build stamp in the state XML), so a game always regenerates on the runtime that produced it. Record the re-baseline reason.

The exactly-once lock (§7) gets its own integration test in M2: fire both triggers (early-submit and deadline) concurrently for the same `(gameId, turnYear)` and assert exactly one generation commits and the other drops.

### 13.4 Determinism unit tests

Two seeded runs of `Generate()` on the fixture must produce identical output on the same runtime (guards the iteration-order fix, §8). `SeedDerivation` has its own tests pinning known `(masterSeed, turnYear[, empireId|subsystem])` inputs to fixed seeds, so the hash can never be changed by accident (changing it re-rolls every game).

---

## 14. Open Questions

Forward-looking forks for the dev team.

1. **State compression and format.** M0 stores `.sstate` uncompressed with a `current` pointer; the design prefers gzipped `{turnYear}.xml.gz` (§9.1, GALAXIES-CLOUD-DESIGN.md §B.3). When does gzip land (M1.3), and does the `current` pointer stay, or do we resolve the latest turn from Firestore `currentStatePath` alone and drop the pointer object? Compact binary is a later option; XML stays the format for the port.
2. **Ingress tightening.** M0 ships `--ingress=all --no-allow-unauthenticated` for hand-invocation. M2 must flip to `--ingress=internal`. Does that require a VPC connector and a smoke-runner VM (Aries pattern) for future smoke tests, or does invocation stay entirely inside the project (Cloud Tasks and Pub/Sub push with OIDC) so no VM is needed? Decide before M2 wires the trigger.
3. **Engine version pinning granularity.** If the floating-point spike (§13.3) forces a Linux re-baseline, do we pin the engine build per game for its whole life, or allow an explicit, audited "re-baseline this game to a new engine version" migration? A long-running game outliving several engine builds needs an answer.
4. **Lock TTL versus generation time.** `GALAXIES_LOCK_TTL_MINUTES` defaults to 10. A large late-game universe (up to 128 empires) may exceed that. Do we make the TTL adaptive (extend it mid-generation with a lock-heartbeat write), or size it to the worst case? An expired lock mid-work is the one path that discards a completed generation (§7).
5. **Concurrency-1 headroom.** Concurrency 1 is chosen because of `GameSettings` and other process-wide statics. If those statics are made instance-scoped during the port, is there value in concurrency greater than 1 per instance for very small games, or does the big-heap-per-generation cost keep 1 the right answer? Validate under load before relaxing it.
6. **`turn-generated` payload contract.** `gameEnded` comes from `VictoryCheck`. Is the payload (`gameId`, `turnYear`, `empireIds`, `aiEmpireIds`, `gameEnded`) sufficient for both galaxies-ai fan-out and galaxies-notifier, or does the notifier need deadline and unsubmitted-empire context that today rides `deadline-approaching` instead? Freeze the schema before M3 subscribes.
7. **Orders prefix naming.** `GcsGameStore` reads `orders/current/`, while the design writes `orders/{turnYear}/{empireId}.orders`. Reconcile: does galaxies-api write to a per-year prefix that turngen resolves from `turnYear`, or to a stable `current/` prefix that is cleared on commit? The two must agree on one convention before galaxies-api lands (M1/M2).