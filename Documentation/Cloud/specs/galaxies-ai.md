# galaxies-ai - Service Specification

**Service Name:** galaxies-ai (AI participant runner and registry)
**Port:** 8082 (local dev)
**Repository Path:** `galaxies-ai/` (runner + registry); participant workers under `participants/`
**Build Phase:** M3 (built-in Nova AI) and M6 (open registry + LLM). Every M6-only clause is tagged **(M6)**; everything else is M3.
**Status:** Planned; docs only, no code yet
**Owner:** Farehard / Galaxies
**Classification:** Galaxies; Internal

---

## 1. Purpose & Scope

galaxies-ai is the service and worker layer that runs AI participants and dispatches them for AI seats and abandoned-seat takeovers. It is the cloud replacement for the old single-process `Nova/Ai/AiRunner.cs` path, which opened `File.OpenWrite(raceName + ".lock")` against a shared folder and could therefore run only one AI at a time. In the cloud there is no shared file, no lock, and no folder: each seat-turn is an isolated invocation that receives its fog-of-war view in a request body and returns orders in a response body, so N AI seats run as N concurrent invocations across one or many games.

The service does three jobs. First, it hosts and calls **participants**: the built-in Stars! Nova AI wrapped as a `/v1/act` worker (M3), community container plug-ins (M6), and an LLM-driven participant backed by Claude on Vertex (M6). All three implement one language-neutral HTTP contract (see §4 and `AI-PARTICIPANTS.md` §F.1). Second, it owns the **dispatch path**: a per-seat-turn worker that projects the seat's own intel to an `empire_view`, calls the seat's pinned participant, and submits the returned orders through the same orders pipeline a human client uses, with a held-orders fallback on timeout or failure so a slow or broken AI can never stall a game. Third (M6), it owns the **registry**: a Firestore catalog of participant manifests that the lobby lists and games pin per seat.

An AI participant is not special. It consumes exactly one empire's view and returns a list of orders, exactly as a human client does through the GUI. galaxies-ai therefore sits on the client side of the engine boundary: it never links the turn engine, never touches `ServerData`, and cannot read another seat's owned data.

**Out of scope for v1 (M3):**

- Community container plug-ins, the open manifest registry, lobby selection of anything but the built-in AI, and per-game version pinning of third-party participants. All M6.
- The LLM-driven participant (`claude-strategist`), the per-game LLM token budget, and degrade-to-Nova. All M6.
- The difficulty ladder / win-rate matrix. M6.
- The in-process C# assembly participant variant (kind b, assembly form). Not built in v1 at all; community and LLM AIs are always containers, never in-process, so a crash or infinite loop can never touch the turn engine. See §16.
- Engine RNG seeding. galaxies-ai consumes `game.seed` but does not seed the engine; engine determinism is a turngen prerequisite (see §10.3 and `AI-PARTICIPANTS.md` §F.5.3).
- Adding a sixth order type. The `CommandRegistry` that replaces the hardcoded `OrderReader` switch is a turngen concern; galaxies-ai adds one JSON arm per order type but does not own the engine switch (see §4.4).

---

## 2. High-Level Architecture

### 2.1 Components

- **galaxies-ai runner** (`galaxies-ai/`) - ASP.NET Core on .NET 10, Cloud Run, ingress=internal, scale to zero. Stateless. Owns the dispatch worker, the `empire_view` transcoder, the Firestore registry, the order-submission client, the LLM token budget (M6), and the test/evaluation harness. This is the only component in this spec that is a first-class Galaxies service.
- **participants/nova-default** (`participants/nova-default/`, M3) - a thin .NET 10 worker that answers `POST /v1/act` by wrapping `DefaultAi : AbstractAI` and its sub-AIs (`DefaultAIPlanner`, `DefaultPlanetAI`, `DefaultFleetAI`). It deserializes `empire_view` into an `EmpireData` (reusing the `EmpireData(XmlNode)` / `ToXml` pair or a JSON shim), builds a `ClientData`, calls `DoMove()`, and serializes `clientState.Commands` to `orders[]`. It threads the per-seat seed into the AI's `new Random()`. Its own folder, own `cloudbuild.yaml`, own `Dockerfile`, own service account.
- **participants/claude-strategist** (`participants/claude-strategist/`, M6) - a worker that answers `POST /v1/act` by summarizing `empire_view` into a compact digest, prompting Claude on Vertex AI, and emitting orders through one strict `submit_orders` tool. See §11.
- **participants/sample-community** (`participants/sample-community/`, M6) - a template community participant, shipped as documentation and a golden reference image, not run in production games.
- **Firestore (native mode)** - the one control-plane store for everything (see `GALAXIES-CLOUD-DESIGN.md` §D). galaxies-ai owns the `ai_participants`, per-game `ai_seats`, `ai_runs`, `ai_budget`, and `ai_memory` collections (see §6). No Cloud SQL; Postgres is not used and must not be reintroduced.
- **GCS buckets** - `roybot-galaxies-intel` (the per-seat intel that becomes `empire_view`), `roybot-galaxies-orders` (where AI orders land, same as human orders), `roybot-galaxies-state` (durable run artifacts under an `ai-runs/` prefix for replay and golden tests). All three are private, uniform-access, public-access-prevention enforced; intel and orders are never public and are reachable only through the API with per-empire authorization.
- **galaxies-api (additive)** - gains one internal, OIDC-gated route so the AI runner can submit a seat's orders through the same OrderWriter-to-`roybot-galaxies-orders` pipeline a human client uses (see §7.3). No other change.
- **galaxies-turngen (additive)** - the authoritative turn engine and deadline scheduler already owns the `gen-{gameId}-{turnYear}` Cloud Tasks deadline task, the one-minute Cloud Scheduler sweep backstop, and the Firestore `turnYear`+lock exactly-once transaction. It gains only the signal that an AI seat is due (the events galaxies-ai subscribes to) and the abandoned-seat controller flip (see §8.3).

### 2.2 Dispatch / turn flow (main path, one AI seat, one turn)

1. A turn generates: turngen runs `TurnGenerator.Generate()` once for the game, advancing one year, writing each empire its private fog-of-war intel via `ScanStep` + `IntelWriter` to `roybot-galaxies-intel`, and publishing `turn-generated`.
2. galaxies-ai consumes `turn-generated`. For each seat controlled by an AI in that game (from `games/{gameId}/ai_seats/*`), it enqueues one Cloud Task `ai-{gameId}-{turnYear}-{empireId}` onto the same Cloud Tasks queue that holds the `gen-{gameId}-{turnYear}` deadline task, scheduled early relative to the human deadline so a slow AI never eats the human window.
3. The task fires: Cloud Tasks calls `POST /v1/dispatch` on galaxies-ai with an OIDC identity token (audience = the galaxies-ai URL). The body is `{game_id, turn_year, empire_id}` only; nothing trusts the network.
4. galaxies-ai takes a Firestore run lock on `ai_runs/{gameId}_{turnYear}_{empireId}` (idempotency; a duplicate task delivery is a no-op).
5. It loads that seat's own intel from `roybot-galaxies-intel` (the exact `<race>.intel` `IntelWriter` produced for `empire_id`, already fog-projected) and transcodes it to the `empire_view` JSON (§4.2). It never loads another seat's intel.
6. It looks up the seat's pinned participant (`participant_id`, `version`, `difficulty`) and computes `game.seed = hash(MasterSeed, turnYear, empireId)` (§10.3).
7. It calls the participant's `POST /v1/act` (OIDC, audience = the participant URL) with a hard wall-clock timeout from the manifest (`resources.timeout_s`, default 60).
8. The participant returns `orders[]`. galaxies-ai maps each order to the OrderWriter wire shape and submits them through the galaxies-api internal orders route (§7.3), which writes them to `roybot-galaxies-orders` and marks the seat `TurnSubmitted`. Authoritative validation is turngen's `OrderReader` / `CommandRegistry` running each command's `IsValid` (§4.4).
9. galaxies-ai writes an `ai_runs` record (status, timing, token counts, and a durable copy of the request and response under `roybot-galaxies-state/ai-runs/...` for the harness).
10. When every seat (human and AI) has submitted, or the deadline task fires, turngen generates the next turn. Held orders (§8.2) guarantee the turn generates on schedule whether or not every AI answered.

### 2.3 GCP Topology

- **Project / region:** `roybot` / `us-central1`. Image registry `us-central1-docker.pkg.dev/roybot/roybot-galaxies` (runner image `.../galaxies-ai`, participant images `.../ai-nova-default`, `.../ai-claude-strategist`, `.../ai-<community>`).
- **galaxies-ai runner:** Cloud Run, `--ingress=internal`, `--allow-unauthenticated=false` (invocation gated by IAM `run.invoker`), on the roybot VPC, `--min-instances=0 --max-instances` sized to expected concurrent seat-turns, `--cpu-boost` for cold-start latency, `--concurrency` moderate (the worker is I/O-bound on participant calls). Invoked by Cloud Tasks and Pub/Sub push, both carrying OIDC identity tokens minted for their own service accounts and accepted only for the galaxies-ai audience.
- **Participant services:** each Cloud Run, `--ingress=internal`, invokable only by `galaxies-ai-sa`. `participants/nova-default` scales to zero and needs no egress (`network: none`). `participants/claude-strategist` (M6) is kept warm enough to benefit from a live process, reaches Vertex AI in `roybot` only, and runs under its own egress-restricted service account.
- **Internal auth:** Galaxies uses GCP-native OIDC everywhere internal. galaxies-ai to galaxies-api, galaxies-ai to participant, Cloud Tasks to galaxies-ai, and Pub/Sub push to galaxies-ai all carry Cloud Run OIDC identity tokens with the callee URL as audience. There is no shared HMAC secret and no `X-Aries-Internal-Secret` header anywhere in Galaxies; do not add one. The public API is the only Google-ID-token verifier and the only minter of the first-party JWT (see `GALAXIES-CLOUD-DESIGN.md` §D).
- **Egress:** each participant runs under a per-participant service account with VPC egress default-deny. `network: none` gets no egress; `network: vertex-only` reaches `aiplatform.googleapis.com` in `roybot` and nothing else; `network: listed` reaches only `allowed_hosts`. See §10.2.
- **Scheduling:** the existing Cloud Tasks queue (holder of `gen-{gameId}-{turnYear}`) also carries `ai-{gameId}-{turnYear}-{empireId}` per-seat tasks. The one-minute Cloud Scheduler sweep that backstops turngen is unchanged; it triggers the deadline path, and the deadline path guarantees held-orders generation independent of AI liveness.
- **Pub/Sub:** galaxies-ai subscribes (push, OIDC-authenticated) to `game-created` (record AI seat assignments and pin versions), `turn-generated` (enqueue the new year's AI seat tasks), and `deadline-approaching` (backstop: dispatch any AI seat still unsubmitted). No new topic is introduced; per-seat fan-out is Cloud Tasks, not a new Pub/Sub topic.

### 2.4 Repository Layout

One folder per microservice, each with its own `cloudbuild.yaml` and `Dockerfile`. Cloud Build triggers are configured per subdirectory in the Console wizard; there is no monolithic Dockerfile and no shared build context.

```
galaxies-ai/                          # runner + registry (port 8082), ASP.NET Core / .NET 10
├── src/
│   ├── Program.cs                    # host, health, OIDC auth, feature-flag gate
│   ├── Dispatch/
│   │   ├── DispatchController.cs      # POST /v1/dispatch (one seat-turn)
│   │   ├── EventHandlers.cs          # game-created / turn-generated / deadline-approaching push
│   │   ├── TaskEnqueuer.cs           # ai-{gameId}-{turnYear}-{empireId} Cloud Tasks
│   │   └── RunLock.cs                # Firestore idempotency lock
│   ├── Projection/
│   │   └── EmpireViewTranscoder.cs   # <race>.intel (Intel + EmpireData) -> empire_view JSON
│   ├── Contract/
│   │   ├── ActRequest.cs / ActResponse.cs
│   │   └── OrderMapper.cs            # orders[] -> OrderWriter wire shape (one arm per ICommand)
│   ├── ApiClient/
│   │   └── OrderSubmitClient.cs      # POST to galaxies-api internal orders route (OIDC)
│   ├── Registry/                     # (M6) Firestore manifest registry
│   │   ├── RegistryController.cs     # /v1/participants CRUD + lifecycle
│   │   └── ManifestValidator.cs
│   ├── Budget/                       # (M6) LLM token budget + degrade-to-Nova
│   ├── Harness/                      # replay / golden / ladder
│   ├── Config.cs                     # env + feature flags
│   └── Firestore/                    # typed accessors: ai_participants, ai_seats, ai_runs, ai_budget, ai_memory
├── Dockerfile
├── cloudbuild.yaml
├── spec.md                          # this file
├── ACTIVATE_galaxies_ai.md          # the ships-dark rollout runbook (§14)
└── questions.md                     # forward-looking dev-team forks (§16)

participants/
├── nova-default/                     # (M3) built-in Nova AI as a /v1/act worker, .NET 10
│   ├── src/                          # wraps DefaultAi : AbstractAI; EmpireData(XmlNode)/ToXml shim
│   ├── Dockerfile
│   └── cloudbuild.yaml
├── claude-strategist/                # (M6) LLM adapter, Claude on Vertex
│   ├── src/
│   ├── Dockerfile
│   └── cloudbuild.yaml
└── sample-community/                 # (M6) template community participant (docs/reference)
    ├── Dockerfile
    └── cloudbuild.yaml
```

---

## 3. Configuration & Feature Flags

Every feature ships dark behind the `_GALAXIES_AI_ENABLED` substitution and its per-feature sub-flags. While a flag is off, reads on the affected route return `{"disabled":true}` and mutations return `403`. The dispatch worker with `AI_DISPATCH_ENABLED=false` never calls a participant and never submits orders, so every AI seat runs on held orders (empty on turn one); this is the safe default and the kill switch.

### 3.1 Switches (all ship OFF)

| Where (trigger) | Switch | Off state | On state |
|---|---|---|---|
| galaxies-ai | `_GALAXIES_AI_ENABLED` | all routes read `{"disabled":true}`, mutations 403; dispatch is a no-op | service live |
| galaxies-ai | `_AI_DISPATCH_ENABLED` | `/v1/dispatch` records `held` and submits nothing | worker projects, calls the participant, submits orders |
| galaxies-ai | `_AI_TAKEOVER_ENABLED` | abandoned human seats are not auto-filled | abandoned/timed-out seats run the default AI (§8.3) |
| galaxies-ai | `_AI_REGISTRY_ENABLED` (M6) | `/v1/participants*` read `{"disabled":true}`, publish 403; lobby lists only the built-in AI | open registry live |
| galaxies-ai | `_AI_COMMUNITY_ENABLED` (M6) | dispatch to any non first-party image 403s; games pinned to one degrade to held orders | community images dispatchable |
| galaxies-ai | `_AI_LLM_ENABLED` (M6) | LLM seats degrade to the built-in Nova AI | LLM participants dispatchable within budget (§10.4) |
| galaxies-ai | `_AI_LADDER_ENABLED` (M6) | `/v1/ladder*` read `{"disabled":true}` | ladder harness runnable |
| galaxies-ai | `_AI_LLM_MODEL` (M6) | empty → manifest default per tier | pin a cheaper model per call |
| galaxies-api | `_API_AI_ORDERS_ENABLED` | internal AI orders route 403 (no AI seat can submit) | route live (see §7.3) |

### 3.2 Environment variables

| Env var | Example / default | Purpose |
|---|---|---|
| `GOOGLE_CLOUD_PROJECT` | `roybot` | project |
| `GALAXIES_REGION` | `us-central1` | region for Cloud Tasks, Vertex, buckets |
| `GALAXIES_API_URL` | `https://galaxies-api-<hash>-uc.a.run.app` | order-submit target; also the OIDC audience |
| `GALAXIES_TURNGEN_URL` | `https://galaxies-turngen-<hash>-uc.a.run.app` | seat-state reads on backstop |
| `INTEL_BUCKET` | `roybot-galaxies-intel` | source of the seat's `empire_view` |
| `ORDERS_BUCKET` | `roybot-galaxies-orders` | where submitted AI orders land (via the API) |
| `STATE_BUCKET` | `roybot-galaxies-state` | durable `ai-runs/` artifacts for the harness |
| `TASKS_QUEUE` | the turngen deadline queue | carries `ai-{gameId}-{turnYear}-{empireId}` |
| `CONTRACT_VERSION` | `1.0` | contract version this runner speaks |
| `DEFAULT_TIMEOUT_S` | `60` | wall-clock cap when a manifest omits one |
| `DISPATCH_RETRY` | `1` | retries before a run is marked failed → held orders |
| `MAX_ORDERS_PER_TURN` | `2000` | anti-spam cap; excess dropped (§10.5) |
| `LLM_GAME_TOKEN_BUDGET` (M6) | per-game cap; exhaustion → degrade-to-Nova (§10.4) |
| `LLM_DAILY_TOKEN_BUDGET` (M6) | service-wide daily cap |
| `AI_LLM_MODEL` (M6) | empty | per-call model pin, overrides manifest tier default |

---

## 4. The Open Participant Contract

The full request/response schema is the standalone contract in `AI-PARTICIPANTS.md` §F.1; this section states what galaxies-ai implements against it. Every participant of every kind implements exactly one route.

### 4.1 Transport

```
POST {participant_endpoint}/v1/act
Content-Type: application/json
Authorization: Bearer <OIDC id token, audience = participant URL>
```

One `act` request per seat per turn. The participant touches no storage, no locks, and no object graph; it receives its view in the body and returns orders in the body.

### 4.2 Request (host to participant), `empire_view`

The request is a JSON transcoding of the seat's own `<race>.intel` (an `Intel` wrapping one `EmpireData`) plus a projection of `GameSettings`, restricted to this empire's view. Because `IntelWriter` already produced a fog-of-war intel per empire (`OwnedStars` vs `StarReports`, `OwnedFleets` vs `FleetReports`, `EmpireReports`, `Designs`, `ResearchLevels`), galaxies-ai transcodes rather than re-projects; it cannot widen the view. Shape (abridged; full schema in `AI-PARTICIPANTS.md` §F.1.2):

```jsonc
{
  "contract_version": "1.0",
  "request_id": "b1e5...uuid",
  "issued_unix_ms": 1752883200000,
  "deadline_unix_ms": 1752883260000,          // when held orders are used instead
  "game": {
    "game_id": "roybot:game:8f21",
    "turn_year": 2118,                          // ServerData.TurnYear
    "seed": "9a3f00c1",                         // per-seat seed = hash(MasterSeed, turnYear, empireId)
    "settings": { "map": {...}, "victory": {...} }
  },
  "seat": { "empire_id": 7, "race_name": "Gestalti", "difficulty": "hard" },
  "empire_view": {
    "research": { "budget": 15, "levels": {...}, "topics": {...}, "resources": {...} },
    "available_components": [ ... ],
    "designs": [ ... ],
    "owned_stars": [ ... ],  "star_reports": [ ... ],   // owned vs last-seen scan
    "owned_fleets": [ ... ], "fleet_reports": [ ... ],
    "other_empires": [ ... ], "minefields": [ ... ],
    "messages": [ ... ], "scores": [ ... ], "battle_reports": [ ... ]
  }
}
```

64-bit fleet and design keys are sent as decimal strings because `EmpireData.GetNextFleetKey()` packs `empireId` into the high bits and the values exceed safe JSON integer range. Research fields mirror `ResearchBudget` / `ResearchLevels` / `ResearchTopics` / `ResearchResources`; `messages` / `scores` / `minefields` come from the `Intel` wrapper.

### 4.3 Response (participant to host), `orders[]`

Orders map 1:1 to the five existing `ICommand` implementations; the `type` token is the same one `OrderReader.ReadPlayerTurn()` switches on today.

```jsonc
{
  "contract_version": "1.0",
  "request_id": "b1e5...uuid",
  "empire_id": 7,
  "turn_year": 2118,                        // must equal request.game.turn_year
  "orders": [
    { "type": "Research", "budget": 15, "topics": { ... } },
    { "type": "Waypoint", "mode": "Add", "fleet_key": "700000001", "index": 1,
      "waypoint": { "x": 250, "y": 140, "warp": 6, "task": "Scout" } },
    { "type": "Production", "mode": "Add", "star_key": "Alpha", "index": 0,
      "order": { "unit": "Colony Ship", "quantity": 1 } },
    { "type": "Design", "mode": "Add", "design": { "name": "Colonizer", "hull": "Colony Ship", "modules": [ ... ] } },
    { "type": "RenameFleet", "fleet_key": "700000001", "new_name": "Trailblazer" }
  ],
  "diagnostics": { "notes": "expanding to Beta", "tokens_used": 5120, "seed_used": "9a3f00c1" }
}
```

`mode` is the `CommandMode` enum (`Add` / `Edit` / `Delete`). `diagnostics` never affects the turn; it feeds logs and the harness.

### 4.4 Trust boundary and validation

galaxies-ai does not trust the participant. It stamps `empire_id` from the dispatch record, not from the response body; the response `empire_id` is only checked for equality, mirroring `OrderReader`'s empire-Id guard. It maps each order to the OrderWriter wire shape and submits through the API. Authoritative validation happens exactly where it happens for a human: turngen's `OrderReader` / `CommandRegistry` constructs the matching `ICommand` and runs `IsValid(EmpireData)` before `ApplyToState(EmpireData)`.

| Contract `type` | C# type | Existing validation |
|---|---|---|
| `Research` | `ResearchCommand` | rejects `budget < 0 or > 100`; no-op if unchanged |
| `Waypoint` | `WaypointCommand` | rejects a fleet the empire does not own |
| `Design` | `DesignCommand` | rejects Add of an existing key, Edit/Delete of an absent key |
| `Production` | `ProductionCommand` | validates star ownership and queue index/cost |
| `RenameFleet` | `RenameFleetCommand` | rejects an unowned fleet or an empty name |

galaxies-ai additionally runs a local `IsValid` dry-run against the reconstructed `EmpireData` to decide fallback: invalid orders are dropped and logged; a response that is entirely invalid degrades to held orders (§8.2). A participant can never act on a seat it was not handed, and can never forge an order for an object it does not own, because keys are checked against the seat's own `OwnedFleets` / `OwnedStars` / `Designs`. Adding a sixth order type still requires one hardcoded arm in turngen's `CommandRegistry` plus one arm in `OrderMapper`; the contract localizes the change to two switch points but does not remove it.

---

## 5. Participant Kinds

All three implement `POST /v1/act`; they differ only in packaging and where code runs. See `AI-PARTICIPANTS.md` §F.2 for the full table.

| Kind | What it is | Milestone | Determinism | Notes |
|---|---|---|---|---|
| (a) Built-in Nova AI | `DefaultAi : AbstractAI` wrapped as a `/v1/act` container | **M3** | `seeded` once the seat seed threads into its `new Random()` | first-party image `ai-nova-default`; also the default takeover controller (§8.3) |
| (b) Community container | any image in any language answering `/v1/act` | **M6** | manifest-declared | untrusted: per-participant SA, egress deny + allowlist, review gate (§10.2) |
| (c) LLM-driven | container that digests `empire_view`, prompts Claude on Vertex, emits one `submit_orders` tool call | **M6** | `best-effort` | first-party image `ai-claude-strategist`; see §11 |

The container-implements-HTTP-contract model is the common denominator: kinds (a) and (c) are special cases of a container answering `/v1/act`, so dispatch, sandboxing, and budgeting have exactly one shape. Community and LLM AIs are always containers, never in-process. This is what removes the single-AI-at-a-time file-lock limitation: there is no `.lock` file and no shared folder, only isolated invocations.

---

## 6. Data Model (Firestore + GCS)

Firestore native mode, one store (see `GALAXIES-CLOUD-DESIGN.md` §D). No Cloud SQL. Collections galaxies-ai owns:

- **`ai_participants/{participantId}`** (M6) - the registry head for a stable participant id (for example `galaxies.default-ai`, `galaxies.claude-strategist`). Fields: `name`, `author`, `description`, `latest_version`, `visibility` (`public` | `unlisted` | `private`), `difficulty` (declared tiers). The M3 seed doc for `galaxies.default-ai` exists from first deploy even with the registry flag off; it is the only participant the lobby can select in M3.
  - **`ai_participants/{participantId}/versions/{version}`** - one immutable manifest per semver (§9.1). Fields mirror the manifest in §9.1: `kind`, `image`, `endpoint`, `contract_versions`, `resources` (`cpu`, `memory`, `timeout_s`, `max_concurrency`), `determinism` (`seeded` | `best-effort` | `nondeterministic`), `network` (`none` | `vertex-only` | `listed`), `allowed_hosts`, `cost_class` (`free` | `metered`), `service_account`, `lifecycle` (`active` | `deprecated` | `yanked`), and, for LLM kinds, an `llm` block (`provider`, `model`, `max_input_tokens_per_turn`, `max_output_tokens_per_turn`).
- **`games/{gameId}/ai_seats/{empireId}`** - the per-seat pin. Fields: `participant_id`, `version` (pinned at game creation), `difficulty`, `controller` (`ai` | `human` | `ai_takeover`), `pinned_at`. Written from the `game-created` event using the lobby selection, where `PlayerSettings.AiProgram` (once `"Human"` or an exe path) becomes a participant id plus difficulty. A running game never changes a pinned version (§9.2). `EmpireData.Id` is still assigned at game creation exactly as today.
- **`ai_runs/{gameId}_{turnYear}_{empireId}`** - one record per seat-turn dispatch and the idempotency lock. Fields: `status` (`running` | `submitted` | `held` | `failed`), `attempts`, `participant_id`, `version`, `started_at`, `finished_at`, `orders_count`, `orders_dropped`, `tokens_input`, `tokens_output` (M6), `request_uri` and `response_uri` (GCS paths under `roybot-galaxies-state/ai-runs/{gameId}/{turnYear}/{empireId}.{req,resp}.json`), `error`. The GCS copies make every dispatch replayable (§12.1) at no extra capture cost.
- **`ai_budget/{gameId}`** (M6) - per-game LLM accounting: `input_tokens_used`, `output_tokens_used`, `budget_input`, `budget_output`, `degraded_to_nova` (bool, set when the game exhausts its budget). Plus a service-wide `ai_budget/_daily/{yyyymmdd}` doc for the daily cap.
- **`ai_memory/{gameId}/seats/{empireId}`** (M6) - the LLM participant's small per-seat strategy-notes blob (a few hundred tokens: current plan, colonization targets claimed, who it considers hostile), keyed by `(game_id, empire_id)`. Never holds secrets; treated as untrusted on read-back (still validated downstream). See §11.4.

GCS artifacts: the `empire_view` source is the seat's own object in `roybot-galaxies-intel`; submitted orders land in `roybot-galaxies-orders` via the API (identical to human orders); durable run copies for the harness live under `roybot-galaxies-state/ai-runs/`.

---

## 7. Endpoint Catalog

All galaxies-ai routes are ingress=internal and OIDC-gated. Callers: Cloud Tasks and Pub/Sub (dispatch and events), galaxies-api (registry reads for the lobby), and operators/CI (harness, from a VPC host).

### 7.1 Dispatch and events

| Method + path | Purpose | Caller |
|---|---|---|
| `POST /v1/dispatch` | run one seat-turn `{game_id, turn_year, empire_id}`; idempotent via run lock | Cloud Tasks `ai-{gameId}-{turnYear}-{empireId}` |
| `POST /events/game-created` | record `ai_seats`, pin versions | Pub/Sub push (`game-created`) |
| `POST /events/turn-generated` | enqueue this year's AI seat tasks | Pub/Sub push (`turn-generated`) |
| `POST /events/deadline-approaching` | backstop: dispatch any AI seat still unsubmitted | Pub/Sub push (`deadline-approaching`) |
| `GET /healthz` / `GET /readyz` | liveness / Firestore-reachable readiness | infra |

### 7.2 Registry, harness (M6 except replay)

| Method + path | Purpose | Flag |
|---|---|---|
| `GET /v1/participants` | list registry manifests for the lobby | `_AI_REGISTRY_ENABLED` |
| `GET /v1/participants/{id}` | one participant + its versions | `_AI_REGISTRY_ENABLED` |
| `POST /v1/participants/{id}/versions` | publish a new immutable manifest version | `_AI_REGISTRY_ENABLED` (admin) |
| `POST /v1/participants/{id}/versions/{v}:deprecate` | hide from lobby, keep runnable for pinned games | `_AI_REGISTRY_ENABLED` (admin) |
| `POST /v1/participants/{id}/versions/{v}:yank` | disallow for new games; pinned games fall to held orders if image gone | `_AI_REGISTRY_ENABLED` (admin) |
| `POST /v1/participants/{id}/versions/{v}:validate` | self-test the image against a golden `empire_view` before it goes public | `_AI_REGISTRY_ENABLED` (admin) |
| `POST /v1/replay` | post a saved `empire_view` to a participant, print orders + `IsValid` results | always (harness) |
| `POST /v1/ladder/run` / `GET /v1/ladder/{runId}` | round-robin games; win-rate matrix | `_AI_LADDER_ENABLED` |

### 7.3 galaxies-api additive: internal orders route

`POST /internal/v1/games/{gameId}/seats/{empireId}/orders` (OIDC, invokable only by `galaxies-ai-sa`, gated by `_API_AI_ORDERS_ENABLED`). Body is the OrderWriter wire shape (`ROOT/Turn`, `ROOT/Id`, `ROOT/Orders/Command[]`). It writes to `roybot-galaxies-orders` exactly as a human submission does and marks `TurnSubmitted`, so turngen's `OrderReader` / `CommandRegistry` validates AI and human orders through the identical pipeline; the engine cannot tell them apart. The only difference from the human path is authentication (service OIDC + explicit `empire_id`, versus a human's first-party JWT).

---

## 8. Dispatch, Held Orders, and Abandoned-Seat Takeover

### 8.1 Concurrency and failure

| Situation | Behavior |
|---|---|
| Many AI seats in one game, or many games | each seat is an independent Cloud Run invocation; no locks, no shared files; bounded only by per-participant `max_concurrency` and account quota |
| Participant slow | wall-clock timeout from the manifest (`resources.timeout_s`, default 60); the invocation is cancelled |
| Participant errors, crashes, or times out | the seat falls back to held orders (§8.2); the game is never blocked by one bad AI |
| Participant returns invalid orders | invalid orders are dropped (§4.4); valid ones are kept; an all-invalid response degrades to held orders |
| Repeated failures | after `DISPATCH_RETRY` (default 1) the run is marked `failed`, the seat uses held orders, and the participant version is flagged for the registry (M6) |
| Deadline pressure | AI seats are dispatched early (as soon as the human seats they do not depend on have submitted) so a slow AI does not eat the human deadline window |

### 8.2 Held orders

Held orders make the async, play-by-email cadence safe. On timeout, failure, or an all-invalid response, galaxies-ai does not overwrite the seat's submission: the seat retains its last submitted `orders[]` for the current year if any, otherwise an empty order list (the engine already tolerates an empire that submitted nothing; it simply does not change that empire's plans). galaxies-ai marks the seat submitted-with-`held` so turngen does not wait on it, and records `status: held` or `failed`. A turn always generates on schedule whether or not every AI answered.

### 8.3 Abandoned-seat takeover

When turngen marks a human seat abandoned or timed-out and AI-controlled, it flips `games/{gameId}/ai_seats/{empireId}.controller` to `ai_takeover` and assigns the default participant (`galaxies.default-ai` at a default difficulty). From that turn on, the seat is dispatched exactly like any AI seat. In M3 the takeover controller is always the built-in Nova AI; choosing a different takeover participant per game is an M6 lobby option. Gated by `_AI_TAKEOVER_ENABLED`.

---

## 9. Registry, Manifest, Lobby, and Versioning (M6)

### 9.1 The manifest

A manifest is one immutable Firestore document describing one participant version. Adapted to pinned infra (registry `us-central1-docker.pkg.dev/roybot/roybot-galaxies`, network `vertex-only`):

```jsonc
{
  "manifest_version": "1.0",
  "id": "galaxies.default-ai",
  "version": "1.4.2",
  "name": "Nova Default AI",
  "author": "Galaxies core team",
  "description": "The classic Stars! Nova AI: expands, scouts, colonizes.",
  "kind": "container",                 // container | builtin-csharp | llm
  "image": "us-central1-docker.pkg.dev/roybot/roybot-galaxies/ai-nova-default:1.4.2",
  "endpoint": "/v1/act",
  "contract_versions": ["1.0"],
  "difficulty": ["easy", "normal", "hard"],
  "resources": { "cpu": "1", "memory": "512Mi", "timeout_s": 60, "max_concurrency": 20 },
  "determinism": "seeded",
  "network": "none",                   // none | vertex-only | listed
  "allowed_hosts": [],
  "cost_class": "free",                // free | metered
  "service_account": "ai-nova-default-sa@roybot.iam.gserviceaccount.com",
  "visibility": "public"
}
```

The LLM manifest differs only in a few fields (`kind: "llm"`, `network: "vertex-only"`, `cost_class: "metered"`, larger `resources`, and an `llm` block):

```jsonc
{
  "id": "galaxies.claude-strategist",
  "kind": "llm",
  "image": "us-central1-docker.pkg.dev/roybot/roybot-galaxies/ai-claude-strategist:0.3.0",
  "difficulty": ["normal", "hard", "brutal"],
  "determinism": "best-effort",
  "network": "vertex-only",
  "cost_class": "metered",
  "llm": { "provider": "anthropic-vertex", "model": "claude-opus-4-8",
           "max_input_tokens_per_turn": 12000, "max_output_tokens_per_turn": 1500 },
  "resources": { "cpu": "1", "memory": "1Gi", "timeout_s": 90, "max_concurrency": 8 },
  "service_account": "ai-claude-strategist-sa@roybot.iam.gserviceaccount.com"
}
```

### 9.2 Registry, lobby, versioning

- The **registry** is the set of `visibility: public` manifests whose `contract_versions` overlap the server's current contract. The lobby lists each by `name`, `author`, `description`, and declared `difficulty` tiers.
- A **game creator** picks an AI opponent the same way they pick a race: a participant from the registry plus a difficulty from its declared tiers. This slots into the existing new-game flow (`NewGameWizard` to `GameInitialiser`).
- **Versioning:** a manifest is immutable per `version`; publishing a change mints a new version doc. A running game pins the version its seats were created with, so an AI update never changes an in-flight game. New games get `latest_version` by default. A version can be `deprecated` (hidden from the lobby, still runnable for games that pinned it) or `yanked` (disallowed for new games; pinned games fall to held orders if the image is gone).
- In M3 the registry contains exactly one seeded participant (`galaxies.default-ai`) and the lobby offers only it; `_AI_REGISTRY_ENABLED` stays off.

---

## 10. Safety & Fairness

### 10.1 The fog-of-war boundary is server-enforced

The most important guarantee: a participant only ever receives its own empire's view. The `empire_view` is a transcoding of one `EmpireData` that already contains only that empire's owned data plus what it has scanned (`StarReports`, `FleetReports`, `EmpireReports`). galaxies-ai loads only the intel object whose `empire_id` matches the dispatch record; there is no code path by which a participant reads another empire's owned stars, fleets, designs, or research. This is the same isolation `IntelWriter` gives human players when it writes one `<race>.intel` per empire.

### 10.2 Sandboxing and resource limits

| Control | Mechanism |
|---|---|
| Isolation | community and LLM AIs run as containers on Cloud Run, never in the engine process; a crash or hang cannot corrupt `ServerData` or `TurnGenerator` |
| Egress | per-participant service account, VPC egress default-deny. `none` gets no egress; `vertex-only` reaches `aiplatform.googleapis.com` in `roybot` and nothing else; `listed` gets `allowed_hosts` only |
| CPU / memory / time | from the manifest `resources` block, enforced by Cloud Run; timeout falls back to held orders |
| Concurrency | `max_concurrency` per participant caps blast radius and cost |
| Input hardening | every returned order is hostile until `IsValid` passes; keys are checked against the seat's own `OwnedFleets` / `OwnedStars` / `Designs` |
| Image trust | only images under `us-central1-docker.pkg.dev/roybot/roybot-galaxies` are dispatchable; community submissions are reviewed and scanned before their manifest is set `public` |
| Secrets | Secret Manager, mounted only into LLM participant service accounts; community containers never see them |

### 10.3 Determinism and seeding

`ServerData.MasterSeed` is persisted per game. The per-turn seed is `hash(MasterSeed, turnYear)`; the per-seat seed the dispatch carries as `game.seed` is `hash(MasterSeed, turnYear, empireId)`. A participant declaring `determinism: seeded` must use only this seed for randomness, so replaying the same `empire_view` yields the same `orders[]`, which is what makes the golden harness (§12.2) meaningful. LLM AIs declare `best-effort`: sampling makes them non-reproducible even with a seed, and the manifest says so. Engine RNG seeding itself is a turngen prerequisite, not a galaxies-ai deliverable (see §16).

### 10.4 Cost controls for LLM AIs (M6)

- Per-turn hard caps `max_input_tokens_per_turn` and `max_output_tokens_per_turn` in the manifest; the worker refuses to exceed them and falls back to held orders rather than overspending.
- A per-game (`ai_budget/{gameId}`) and per-day (`ai_budget/_daily/{yyyymmdd}`) token budget in Firestore; when a game exhausts its LLM budget, its LLM seats degrade to the built-in Nova AI for the remainder, keeping the free, ad-supported service solvent. Degrade is a controller swap on the seat, not a game interruption.
- Cheaper models for cheaper tiers (a Haiku-class model for `normal`, `claude-opus-4-8` for `brutal`), declared per manifest and overridable by `_AI_LLM_MODEL`.

### 10.5 Abuse prevention

- Prompt injection: in-game `messages[]` (chat, event text) are untrusted data. The LLM adapter presents them as quoted data, never as instructions, and the server validates every order regardless of what any message said. An AI talked into a bad move can still only emit orders that pass `IsValid` for its own seat.
- Order spam: a response is capped at `MAX_ORDERS_PER_TURN`; excess is dropped.
- Griefing by a slow or crashing community AI cannot stall a game (held orders) and cannot exceed its own cost or quota.

---

## 11. LLM Adapter (Claude on Vertex) (M6)

The `participants/claude-strategist` worker is a first-class participant kind, not an afterthought. See `AI-PARTICIPANTS.md` §F.7 for the full treatment.

### 11.1 Where it runs and which model

A Cloud Run service in `roybot` calling Claude through Vertex AI (`AnthropicVertex(project_id="roybot", region="global")`) so the key path stays inside GCP. Default model `claude-opus-4-8` for the flagship `brutal` tier; a cheaper Haiku-class model for `normal`. Adaptive thinking (`thinking: {type: "adaptive"}`) with `output_config.effort` tuned per tier (`low` to `medium` for cheap tiers, `high` for the flagship). Structured outputs and tool use on Vertex are all this participant needs.

### 11.2 State digest

The worker does not hand the model the raw `empire_view`; it builds a compact digest: a one-paragraph situation summary (year, owned planet count, total population, fleet count, research levels, current research target, known threats within scan range); a short table of owned planets (name, population, factories/mines, mineral surplus, queue head); a short table of owned fleets (name, position, fuel, role, current waypoint); the handful of `messages[]` that changed since last turn, quoted as data; and nearby unowned `star_reports` worth colonizing. This keeps per-turn token count in the low thousands.

### 11.3 Tool-style order emission

The model gets exactly one tool, `submit_orders`, whose input schema is the `orders[]` schema from §4.3 with `strict: true`, so it cannot emit a malformed shape. The turn ends when the model calls it. The adapter then still runs every order through the local `IsValid` dry-run, and turngen validates again at read time, so output is doubly guarded: the tool schema constrains the shape and the engine constrains the legality.

### 11.4 Memory across turns

A small per-seat strategy-notes blob (`ai_memory/{gameId}/seats/{empireId}`, a few hundred tokens) is fed into the next turn's prompt and rewritten by the model as part of its turn, giving continuity across the day-scale cadence without resending history. It never holds secrets and is untrusted on read-back.

### 11.5 The honest note on prompt caching, quality, and cost

- **Prompt caching, honestly.** The fixed rules-of-the-game and race-traits prefix is a prompt-cache candidate, but Vertex prompt-cache TTL is on the order of minutes while a seat's turns are day-scale, so the cache almost never survives from one turn to the next for a single seat. Caching pays off where many concurrent games or a ladder batch hit the same prefix inside the TTL window, not turn-to-turn for one seat. Do not budget as if every repeat turn reads a cached prefix.
- **Strategic quality.** An LLM plays a plausible, human-legible game and narrates its plan, but it will not out-optimize a well-tuned procedural AI at the micro level (production ordering, exact warp economics). It is best sold as a characterful opponent, not the hardest one.
- **Latency.** A turn is one or more model calls plus validation, seconds to tens of seconds, invisible in an async deadline-based cadence and intolerable in real time, which is precisely why Galaxies is async-only.
- **Cost.** Cost scales as (games) x (LLM seats per game) x (turns per game) x (tokens per turn). A summarized turn is a few thousand input tokens plus about a thousand output tokens: a few cents per turn on an Opus-class model, a fraction of a cent on a Haiku-class model. A 50-year game with two flagship LLM seats is dollars, not cents. For a free, ad-supported service this is real money, which is why the per-game budget (§10.4) and degrade-to-Nova are not optional. Measure exact counts with the token-counting endpoint before enabling LLM seats broadly, and default new public games to the procedural AI with LLM opponents as an opt-in.

---

## 12. Test & Evaluation Harness

A participant is a pure function from `empire_view` to `orders[]`, so it is testable without a running game.

### 12.1 Replay a saved state

Capture is free: every dispatch writes its `empire_view` and `orders[]` to `roybot-galaxies-state/ai-runs/`. `POST /v1/replay` (or the CLI `replay <participant> <empire_view.json>`) posts a saved `empire_view` to a participant's `/v1/act`, prints the orders, and optionally runs each through `IsValid` against a reconstructed `EmpireData` and reports which were accepted or dropped. This is the cloud form of the old `Nova --ai -r <race> -t <turn> -i <intel>`, with no files and no lock.

### 12.2 Golden-game regression

A golden game is a fixed seed plus a scripted sequence of `empire_view`s for one seat with a recorded expected `orders[]`. For a `seeded` participant, replay must reproduce the recorded orders byte-for-byte; a diff is a regression. This runs in the existing NUnit `Tests` project alongside `SimpleTurnGenerator` (which already overrides the `ReadOrders` / `WriteIntel` / `BackupTurn` / `CleanupOrders` seams).

### 12.3 Difficulty ladder (M6)

`POST /v1/ladder/run` drives an automated round-robin: each participant-and-difficulty plays a batch of full headless games (built-in vs built-in, community vs built-in, LLM vs built-in) via `TurnGenerator.Generate()`, scored by `VictoryCheck.cs` and `Scores.cs`. It outputs a win-rate matrix used to sanity-check that a `hard` tier beats a `normal` tier and to rank community submissions. Because seats are isolated and concurrent, a ladder run parallelizes across Cloud Run.

### 12.4 What to assert for non-deterministic AIs

For LLM (`best-effort`) participants, assert properties rather than exact orders: every returned order passes `IsValid`; the participant colonizes at least one reachable habitable world within N turns of one becoming visible; research budget stays in 0 to 100; no order references an unowned key. These catch illegal or self-defeating output without demanding reproducibility the model cannot give.

---

## 13. Build Phases

Ordered, testable steps. Each is small enough to ship. Steps 1 to 10 are **M3**; 11 to 17 are **M6**.

**M3 - built-in AI dispatch**

1. Scaffold `galaxies-ai` (port 8082, ingress=internal, OIDC, `/healthz` `/readyz`), the feature-flag gate, and typed Firestore accessors. Ships dark: everything returns `{"disabled":true}` / `403`.
2. Build `participants/nova-default`: wrap `DefaultAi` behind `POST /v1/act`; add the `EmpireData` to/from JSON shim; thread the per-seat seed into the AI RNG.
3. Build the `empire_view` transcoder: the seat's own `<race>.intel` to `empire_view` JSON. Unit test against captured intel fixtures.
4. Build `OrderMapper` (`orders[]` to OrderWriter wire shape, one arm per `ICommand`) and the local `IsValid` dry-run.
5. Add the galaxies-api internal orders route (§7.3), OIDC-gated to `galaxies-ai-sa`, behind `_API_AI_ORDERS_ENABLED`.
6. Build the `/v1/dispatch` worker: run lock, load intel, project, call participant, submit, write `ai_runs`. Behind `_AI_DISPATCH_ENABLED`.
7. Wire the scheduler glue: subscribe `game-created` (record `ai_seats`), `turn-generated` (enqueue `ai-{gameId}-{turnYear}-{empireId}` tasks), `deadline-approaching` (backstop); dispatch AI seats early.
8. Implement held orders and retry (default 1), and mark seats submitted-with-`held` so turngen never waits.
9. Implement abandoned-seat takeover (turngen controller flip to `galaxies.default-ai`; galaxies-ai dispatches it). Behind `_AI_TAKEOVER_ENABLED`.
10. Seed the single `galaxies.default-ai` manifest and lobby entry; build the replay endpoint and the golden-game NUnit harness for the built-in AI.

**M6 - open registry + LLM**

11. Build the Firestore participant registry: immutable version docs, publish / deprecate / yank / validate endpoints. Behind `_AI_REGISTRY_ENABLED`.
12. Lobby selection of arbitrary registry participants and per-game version pinning at `game-created`.
13. Community container support: per-participant service accounts, VPC egress default-deny + per-manifest allowlist, resource/timeout/concurrency enforcement, image-review gate. Behind `_AI_COMMUNITY_ENABLED`.
14. Build `participants/claude-strategist`: Vertex/Claude, state digest, prompt-cached prefix, strict `submit_orders` tool, `ai_memory` blob, per-tier model + effort.
15. Implement the per-game and per-day LLM token budget, per-turn token caps to held orders, and degrade-to-Nova on exhaustion. Behind `_AI_LLM_ENABLED`.
16. Build the difficulty ladder and win-rate matrix. Behind `_AI_LADDER_ENABLED`.
17. Add property-assertion tests for `best-effort` participants (§12.4).

---

## 14. Rollout (ships dark)

Everything ships dark; features arm behind their substitutions. galaxies-ai and the participant services are ingress=internal, so smoke runs from a VPC host (a small `smoke-runner` GCE VM on the roybot VPC) with an OIDC identity token whose audience is the target service. Values are pinned to `roybot` / `us-central1`.

### §0 - Set these once per shell (on `smoke-runner`)

```bash
gcloud config set project roybot
export REGION=us-central1
export REG=us-central1-docker.pkg.dev/roybot/roybot-galaxies
export AI=$(gcloud run services describe galaxies-ai   --region=$REGION --format='value(status.url)')
export API=$(gcloud run services describe galaxies-api  --region=$REGION --format='value(status.url)')
export TG=$(gcloud run services describe galaxies-turngen --region=$REGION --format='value(status.url)')
tok() { gcloud auth print-identity-token --audiences="$1"; }   # OIDC per callee audience
```

### §1 - One-time IAM (paste as-is)

```bash
# runtime SA for the runner
gcloud iam service-accounts create galaxies-ai-sa --display-name="galaxies-ai runtime"
AI_SA=galaxies-ai-sa@roybot.iam.gserviceaccount.com

# runner may invoke the API (internal orders) and the participant services
gcloud run services add-iam-policy-binding galaxies-api --region=$REGION \
  --member="serviceAccount:${AI_SA}" --role="roles/run.invoker"
gcloud run services add-iam-policy-binding galaxies-ai --region=$REGION \
  --member="serviceAccount:service-$(gcloud projects describe roybot --format='value(projectNumber)')@gcp-sa-cloudtasks.iam.gserviceaccount.com" \
  --role="roles/run.invoker"   # Cloud Tasks -> /v1/dispatch (OIDC)

# runner data-plane roles: Firestore, read intel, write ai-runs artifacts, enqueue tasks
gcloud projects add-iam-policy-binding roybot --member="serviceAccount:${AI_SA}" --role="roles/datastore.user"
gcloud projects add-iam-policy-binding roybot --member="serviceAccount:${AI_SA}" --role="roles/cloudtasks.enqueuer"
gsutil iam ch serviceAccount:${AI_SA}:roles/storage.objectViewer  gs://roybot-galaxies-intel
gsutil iam ch serviceAccount:${AI_SA}:roles/storage.objectAdmin   gs://roybot-galaxies-state

# built-in participant SA (no egress), invokable only by the runner
gcloud iam service-accounts create ai-nova-default-sa --display-name="Nova default AI worker"
gcloud run services add-iam-policy-binding ai-nova-default --region=$REGION \
  --member="serviceAccount:${AI_SA}" --role="roles/run.invoker"
```

### §2 - First deploy (ordered), each dark

Deploy order: `participants/nova-default` → `galaxies-ai` → redeploy `galaxies-api` (adds the internal orders route, flag off) → confirm `galaxies-turngen` publishes `turn-generated` / `deadline-approaching` (already deployed). Push each branch so its per-directory Console trigger builds, or `gcloud builds submit . --config=<dir>/cloudbuild.yaml`. Leave every `_*_ENABLED` at `false`.

Verify the runner is up and gated:

```bash
curl -s -H "Authorization: Bearer $(tok "$AI")" "$AI/readyz" | jq .
# -> {"status":"ready","firestore":true}
curl -s -H "Authorization: Bearer $(tok "$AI")" "$AI/v1/participants" | jq .
# flags off -> {"disabled":true}
```

Confirm the flags actually landed on the revision (a substitution without the matching `--set-env-vars` entry is a silent no-op):

```bash
gcloud run services describe galaxies-ai --region=$REGION \
  --format='value(spec.template.spec.containers[0].env)' | tr ',' '\n' | grep -E 'ENABLED|BUDGET|MODEL'
```

### §3 - Staged flips (each with a smoke)

Flip a trigger substitution and redeploy for a durable change, or `gcloud run services update ... --update-env-vars` for an immediate change the next deploy wipes.

**Flip 1 (M3) - built-in AI fills AI seats.** Set `_API_AI_ORDERS_ENABLED=true` on `galaxies-api`, then `_GALAXIES_AI_ENABLED=true` and `_AI_DISPATCH_ENABLED=true` on `galaxies-ai`; redeploy both.

```bash
# use a seeded solo-vs-AI test game with a known AI seat, e.g. game roybot:game:ZZTEST, empire 7, year 2101
GID=roybot:game:ZZTEST; YEAR=2101; EMP=7
# drive one seat-turn directly (idempotent):
curl -s -X POST -H "Authorization: Bearer $(tok "$AI")" -H "Content-Type: application/json" \
  "$AI/v1/dispatch" -d "{\"game_id\":\"$GID\",\"turn_year\":$YEAR,\"empire_id\":$EMP}" | jq .
# -> {"status":"submitted","orders_count":>0,...}

# confirm the run record and the durable artifacts:
gcloud firestore documents get "ai_runs/${GID//:/_}_${YEAR}_${EMP}" 2>/dev/null | grep -E 'status|orders_count'
gsutil ls gs://roybot-galaxies-state/ai-runs/$GID/$YEAR/          # -> {EMP}.req.json, {EMP}.resp.json

# confirm orders reached the SAME bucket humans use:
gsutil ls gs://roybot-galaxies-orders/$GID/$YEAR/                 # -> the seat's orders object

# replay the captured view against nova-default and show IsValid results:
gsutil cp gs://roybot-galaxies-state/ai-runs/$GID/$YEAR/$EMP.req.json /tmp/ev.json
curl -s -X POST -H "Authorization: Bearer $(tok "$AI")" -H "Content-Type: application/json" \
  "$AI/v1/replay" -d "{\"participant_id\":\"galaxies.default-ai\",\"empire_view\":$(jq .empire_view /tmp/ev.json)}" \
  | jq '{orders: (.orders|length), accepted, dropped}'
```

Then let a real solo-vs-AI game reach its deadline and confirm the AI seat auto-submits (the `ai-{gameId}-{turnYear}-{empireId}` task fires ahead of the human deadline; turngen generates on schedule). Timeout / crash a participant deliberately and confirm the seat records `held` and the turn still generates.

**Flip 2 (M3) - abandoned-seat takeover.** Set `_AI_TAKEOVER_ENABLED=true`. Abandon a human seat past its timeout; confirm turngen flips `ai_seats/{empireId}.controller` to `ai_takeover` with `galaxies.default-ai` and the seat is dispatched thereafter.

**Flip 3 (M6) - open registry + lobby.** Set `_AI_REGISTRY_ENABLED=true`. Publish and validate a manifest, then confirm the lobby lists it:

```bash
curl -s -X POST -H "Authorization: Bearer $(tok "$AI")" -H "Content-Type: application/json" \
  "$AI/v1/participants/galaxies.default-ai/versions" -d @participants/nova-default/manifest.json | jq '.version'
curl -s -X POST -H "Authorization: Bearer $(tok "$AI")" \
  "$AI/v1/participants/galaxies.default-ai/versions/1.4.2:validate" | jq '{ok, orders, dropped}'
curl -s -H "Authorization: Bearer $(tok "$AI")" "$AI/v1/participants" | jq '[.[].name]'
```

**Flip 4 (M6) - community images.** Set `_AI_COMMUNITY_ENABLED=true` only after the image is reviewed, scanned, pushed under `$REG`, and its per-participant SA + egress allowlist exist. Pin a test game to it and confirm dispatch works and egress is denied outside the allowlist (a `network: none` community image reaching the internet must fail).

**Flip 5 (M6) - LLM seats.** Set `_AI_LLM_ENABLED=true` (optionally `_AI_LLM_MODEL=<cheaper model>`), with `LLM_GAME_TOKEN_BUDGET` / `LLM_DAILY_TOKEN_BUDGET` set. Dispatch an LLM seat, confirm `ai_runs.tokens_*` populate and `ai_budget/{gameId}` increments; drive the game budget to zero and confirm the seat degrades to `galaxies.default-ai` (`ai_budget.degraded_to_nova=true`).

**Flip 6 (M6) - ladder.** Set `_AI_LADDER_ENABLED=true`; run a small round-robin and confirm the win-rate matrix returns and `hard` beats `normal`.

### §4 - Kill switch / rollback

Flip any switch back to `false` and redeploy, or for an immediate stop:

```bash
gcloud run services update galaxies-ai --region=$REGION --update-env-vars=AI_DISPATCH_ENABLED=false
```

Every AI seat then runs on held orders, turns still generate on schedule, and no data migration is involved (Firestore additions are additive). Delete the `ZZTEST` game artifacts from `roybot-galaxies-orders` and `roybot-galaxies-state/ai-runs/` after smoking.

---

## 15. Testing (CI + acceptance gates)

- **Unit (NUnit `Tests` project):** the `empire_view` transcoder against captured intel fixtures; `OrderMapper` round-trips for all five `ICommand` types; the local `IsValid` dry-run drops exactly the orders turngen would drop.
- **Golden regression (CI, blocks merge):** `galaxies.default-ai` replays each golden game byte-for-byte (§12.2). A diff fails CI. Gates build phase 10.
- **Fault injection (CI):** a participant that times out, crashes, or returns all-invalid orders yields `status: held` and a generated turn; a duplicate `/v1/dispatch` delivery is a no-op (run lock). Gates phases 6 to 8.
- **Isolation assertion (CI, security):** a dispatch for `empire_id = X` can load only intel object X; any attempt to widen the view fails the test. Gates §10.1.
- **Property assertions (M6, CI):** for `best-effort` participants, the four invariants in §12.4. Gates phase 17.
- **Ladder acceptance (M6, manual/nightly):** a `hard` tier must beat its `normal` tier across a batch; community submissions ranked before they go `public`. Gates §12.3.
- **Smoke gates (§14):** each staged flip has a copy-paste smoke that must pass on `smoke-runner` before the flip is left on.

---

## 16. Open Questions

Forward-looking forks for the dev team.

1. **Engine RNG seeding is a hard prerequisite for `seeded`.** Golden-game reproducibility (§12.2) is meaningless until turngen seeds `TurnGenerator` from `ServerData.MasterSeed`. Do we block phase 10's golden gate on that turngen work, or ship replay-only golden (compare orders, not full-game outcomes) until seeding lands?
2. **In-process assembly participant (kind b, assembly form).** Deferred entirely in v1. Do we ever want the `AssemblyLoadContext` in-process path for trusted first-party AIs to avoid a container per call, or is one container shape forever simpler? The container path is the recommendation; revisit only if built-in dispatch latency or cost becomes a measured problem.
3. **Community image trust pipeline.** Manual review plus scan is the v1 gate. Do we want Binary Authorization with attestations, and a signed-manifest requirement, before opening community submissions widely?
4. **Pull vs push of `empire_view`.** This spec has galaxies-ai pull the seat's intel from `roybot-galaxies-intel` (the AI-is-a-client model). Should turngen instead push the projected `empire_view` in the dispatch to avoid a second read, at the cost of larger task payloads and a second projection path to keep in sync?
5. **Per-seat difficulty auto-scaling.** Should a losing human seat's takeover AI, or a runaway leader, have its difficulty tier adjusted mid-game for balance, or is a fixed pinned difficulty the honest contract?
6. **The sixth-order-type edit.** Adding an order type still touches turngen's `CommandRegistry` and `OrderMapper`. Is a shared code-generated schema (one source of truth for both the JSON arm and the C# arm) worth building, or is the two-switch edit rare enough to leave manual?
7. **Metered LLM seats in a free service.** §10.4 degrades to Nova on budget exhaustion. Do we ever expose a paid or ad-gated tier that raises the per-game LLM budget, and if so where does billing live given there is no Cloud SQL and Firestore is the only store?
8. **Ladder compute cost.** Full headless games across many participant pairings is real Cloud Run spend. Do we cap ladder runs, schedule them off-peak, or sample rather than exhaustively round-robin?
9. **Prompt-cache economics at day-scale cadence.** §11.5 notes the cache rarely survives turn-to-turn for one seat. Is it worth engineering cache-warming across concurrent games sharing the fixed prefix, or do we simply budget without caching for single-game play and rely on caching only inside ladder batches?