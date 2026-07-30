# Galaxies: the open AI-participant contract

This is a companion to the Galaxies cloud modernization design (`GALAXIES-CLOUD-DESIGN.md`) and is written to stand on its own. It specifies how anyone (the core team, the community, or a large language model) builds a new AI opponent for Galaxies, and how the cloud runs them. It is Section F of the program; it lives in its own file because it is a contract other people will implement against.

Status: design, pending build. Engine: Stars! Nova (GPL v2). Cloud: GCP project `roybot`.

Style note: Hearthlight house rules apply (no em dashes, plain and direct, honest about limits).

---

## Section F. AI participants: the open participant contract (standalone spec)

*This is a self-contained specification. It can be read without the rest of the Galaxies spec. It defines how anyone (the core team, the community, or a large language model) can build a new AI opponent for Galaxies, and how the cloud runs them.*

---

### F.0 What this document assumes

Galaxies is the cloud port of Stars! Nova. Its turn engine (`ServerState/TurnGenerator.cs`, `Generate()`) is authoritative: it reads each empire's orders, advances the universe one year, and writes each empire a private, fog-of-war view. Two file-shaped boundaries already exist in the codebase and are the foundation of everything below:

| Direction | Producer today | Consumer today | Payload |
|---|---|---|---|
| host to player | `ServerState/Persistence/IntelWriter.cs` writes `<race>.intel` | `Nova/Client/IntelReader.cs` | one `Nova.Common.Intel` per empire (`Common/Files/Intel.cs`): `EmpireState` (an `EmpireData`), `Messages`, `AllScores`, `AllMinefields` |
| player to host | `Nova/Client/OrderWriter.cs` writes `<race>.orders` | `ServerState/Persistence/OrderReader.cs` | `ROOT/Turn`, `ROOT/Id`, and a list of `ROOT/Orders/Command` (each an `ICommand`) |

`EmpireData` (`Common/DataStructures/EmpireData.cs`) is already per-empire and already fog-of-war: it separates `OwnedStars` from `StarReports`, `OwnedFleets` from `FleetReports`, and holds `EmpireReports` (what this empire has seen of others), `Designs`, `ResearchLevels`, and so on. That boundary is a ready-made per-player wire protocol; this spec formalizes it.

---

### F.1 The core insight and the Open Participant Contract

**The insight, stated once:** an AI participant is not special. It is a client. It consumes exactly one empire's intel view and returns a list of orders. The built-in Nova AI already proves this: `Nova/Ai/AbstractAI.cs` defines `Initialize(CommandArguments)`, `DoMove()`, and a `ClientState` getter; `Nova/Ai/DefaultAi.cs` reads `clientState.EmpireState` plus `clientState.InputTurn` (an `Intel`), pushes `ICommand`s onto `clientState.Commands`, and `Nova/Ai/AiRunner.cs` then calls `OrderWriter.WriteOrders()`. A human client does the identical thing through the GUI. There is one contract, and the AI sits on the same side of it as a person.

So we define **one** contract, language-neutral, so a participant need not be C#.

#### F.1.1 Transport

The common denominator is a small HTTP request/response over JSON. The host sends one `act` request per seat per turn; the participant returns orders. This maps cleanly to Cloud Run (see F.3) and does not require the participant to touch storage, locks, or the object graph.

```
POST {participant_endpoint}/v1/act
Content-Type: application/json
```

#### F.1.2 Request schema (host to participant)

The request is a JSON projection of `Intel` + `EmpireData` + `GameSettings`, restricted to this empire's view. It never contains another empire's owned data (F.5).

```jsonc
{
  "contract_version": "1.0",
  "request_id": "b1e5...uuid",
  "issued_unix_ms": 1752883200000,
  "deadline_unix_ms": 1752883260000,      // when held orders will be used instead
  "game": {
    "game_id": "roybot:game:8f21",
    "turn_year": 2118,                     // maps to ServerData.TurnYear
    "seed": "9a3f00c1",                    // per-seat deterministic seed (see F.5.3)
    "settings": {                          // projection of Common/Files/GameSettings.cs
      "accelerated_start": false,
      "map": { "width": 400, "height": 400, "number_of_stars": 50 },
      "victory": {                         // each an EnabledValue (enabled + threshold)
        "planets_owned":  { "enabled": true,  "value": 60 },
        "tech_levels":    { "enabled": true,  "value": 22 },
        "total_score":    { "enabled": true,  "value": 1000 },
        "targets_to_meet": 1,
        "minimum_game_time": 50
      }
    }
  },
  "seat": {
    "empire_id": 7,                        // EmpireData.Id (0..127, 0 reserved)
    "race_name": "Gestalti",
    "difficulty": "hard"                   // one of the manifest's declared tiers
  },
  "empire_view": {                         // fog-of-war projection of EmpireData + Intel
    "turn_year": 2118,
    "research": {
      "budget": 15,                        // EmpireData.ResearchBudget (percent)
      "levels":    { "Energy": 3, "Weapons": 6, "Propulsion": 7, "Construction": 9,
                     "Electronics": 5, "Biotechnology": 4 },
      "topics":    { "Energy": 0, "Weapons": 1, "Propulsion": 0, "Construction": 0,
                     "Electronics": 0, "Biotechnology": 0 },
      "resources": { "Energy": 120, "Weapons": 300 }        // accumulated per field
    },
    "available_components": [ { "name": "Long Hump 6", "type": "Engine", "tech": {...} } ],
    "designs": [ { "key": "700000001", "name": "Scout", "hull": "Scout",
                   "mass": 25, "armor": 20, "modules": [ { "slot": 0, "component": "Long Hump 6" } ] } ],
    "owned_stars": [ { "key": "Alpha", "name": "Alpha", "x": 210, "y": 160,
                       "owner": 7, "population": 25400, "factories": 10, "mines": 12,
                       "minerals": { "ironium": 400, "boranium": 220, "germanium": 130 },
                       "production_queue": [ { "unit": "Factory", "index": 0, "remaining_cost": {...} } ],
                       "starbase_key": "700000002" } ],
    "star_reports": [ { "name": "Beta", "x": 250, "y": 140, "owner": 0,
                        "year": 2115, "habitability": 0.42 } ],   // last-seen scan; year=Unset if never seen
    "owned_fleets": [ { "key": "700000001", "name": "Scout #1", "x": 210, "y": 160, "owner": 7,
                        "fuel": 300, "in_orbit": "Alpha", "can_colonize": false,
                        "waypoints": [ { "x": 210, "y": 160, "warp": 0 } ],
                        "composition": [ { "design_key": "700000001", "quantity": 1 } ] } ],
    "fleet_reports": [ { "key": "300000004", "name": "?", "x": 260, "y": 150, "owner": 3,
                         "year": 2117, "scan_level": "Hull" } ],
    "other_empires": [ { "id": 3, "relation": "Enemy",
                         "designs": [ { "key": "300000001", "name": "Frigate", "hull": "Frigate" } ] } ],
    "minefields": [ { "key": "300000010", "x": 240, "y": 150, "radius": 30, "owner": 3 } ],
    "messages": [ { "type": "TechAdvance", "audience": 7, "text": "Your race has advanced..." } ],
    "scores": [ { "empire_id": 7, "rank": 2, "score": 640 } ],
    "battle_reports": [ { "location": "Beta", "year": 2117, "stacks": [ ... ] } ]
  }
}
```

Field origins are load-bearing: `research` mirrors `EmpireData.ResearchBudget` / `ResearchLevels` / `ResearchTopics` / `ResearchResources` (each a `TechLevel`); `owned_*` vs `*_reports` mirrors the exact split in `EmpireData`; `messages` / `scores` / `minefields` come from the `Intel` wrapper. Keys that are 64-bit fleet/design values are sent as decimal strings because they exceed safe JSON integer range and because `EmpireData.GetNextFleetKey()` packs `empireId` into the high bits.

#### F.1.3 Response schema (participant to host)

Orders map 1:1 to the five existing `ICommand` implementations (`Common/Commands/*.cs`). The `type` string is the same token `OrderReader.ReadPlayerTurn()` switches on today (`Research`, `Waypoint`, `Design`, `Production`, `RenameFleet`), so the host adapter is a thin JSON-to-`ICommand` translator.

```jsonc
{
  "contract_version": "1.0",
  "request_id": "b1e5...uuid",
  "empire_id": 7,
  "turn_year": 2118,                       // must equal request.game.turn_year
  "orders": [
    { "type": "Research", "budget": 15,
      "topics": { "Energy": 0, "Weapons": 1, "Propulsion": 0, "Construction": 0,
                  "Electronics": 0, "Biotechnology": 0 } },
    { "type": "Waypoint", "mode": "Add", "fleet_key": "700000001", "index": 1,
      "waypoint": { "x": 250, "y": 140, "warp": 6, "task": "Scout" } },
    { "type": "Production", "mode": "Add", "star_key": "Alpha", "index": 0,
      "order": { "unit": "Colony Ship", "quantity": 1 } },
    { "type": "Design", "mode": "Add",
      "design": { "name": "Colonizer", "hull": "Colony Ship", "modules": [ ... ] } },
    { "type": "RenameFleet", "fleet_key": "700000001", "new_name": "Trailblazer" }
  ],
  "diagnostics": { "notes": "expanding to Beta", "tokens_used": 5120, "seed_used": "9a3f00c1" }
}
```

`mode` is the existing `CommandMode` enum (`Add` / `Edit` / `Delete`). `diagnostics` is optional and never affects the turn; it is for logs and the test harness.

#### F.1.4 Host-side adapter and the trust boundary

The adapter lives beside the turn engine and does not trust the participant. For each order it constructs the matching `ICommand` and runs the command's own `IsValid(EmpireData)` before `ApplyToState(EmpireData)`, which is exactly the guarantee `OrderReader` already enforces (it validates `ROOT/Turn == turnYear` and `ROOT/Id == empire.Id`, then applies). Concretely:

| Contract `type` | C# type built | Existing validation |
|---|---|---|
| `Research` | `ResearchCommand` | rejects `budget < 0 or > 100`, no-op if unchanged |
| `Waypoint` | `WaypointCommand` | rejects fleet the empire does not own |
| `Design` | `DesignCommand` | rejects Add of an existing key, Edit/Delete of an absent key |
| `Production` | `ProductionCommand` | validates star ownership and queue index/cost |
| `RenameFleet` | `RenameFleetCommand` | rejects unowned fleet or empty name |

Invalid or malformed orders are dropped, logged, and the turn proceeds. A participant can never act on a seat it was not handed, because the adapter stamps `empire_id` from the dispatch record, not from the response body (the response `empire_id` is only checked for equality, mirroring `OrderReader`'s empire-Id guard).

**Adding a sixth order type** still requires the same one hardcoded edit as today (the `switch` in `OrderReader.ReadPlayerTurn()`), plus one arm in the JSON adapter. This is a known cloud blocker; the contract does not remove it, it just localizes it to two switch statements.

---

### F.2 Three participant kinds, one contract

All three implement `POST /v1/act`. The differences are packaging and where the code runs.

| Kind | What it is | How it meets the contract | Determinism | Effort |
|---|---|---|---|---|
| **(a) Built-in Nova AI** | `DefaultAi : AbstractAI` and its sub-AIs (`DefaultAIPlanner`, `DefaultPlanetAI`, `DefaultFleetAI`) | Thin C# HTTP wrapper: deserialize `empire_view` into an `EmpireData` (reuse the `EmpireData(XmlNode)` / `ToXml` pair, or a JSON shim), build a `ClientData`, call `DoMove()`, serialize `clientState.Commands` to `orders[]` | Becomes seeded once the engine RNG is seeded (blocker #5) and the seat seed is threaded into the AI's `new Random()` | Low: a wrapper around existing code |
| **(b) Plug-in AI (team or community)** | Any container image, or a sandboxed C# assembly | Container: implements `/v1/act` in any language. Assembly: implements a small `IParticipant` interface and is loaded in a locked-down `AssemblyLoadContext` behind the same HTTP shim | Declares its own class in the manifest | Medium: author owns the logic, contract is stable |
| **(c) LLM-driven AI** | A process that summarizes `empire_view`, prompts a model (for example Claude), and emits orders as tool calls | Container implementing `/v1/act`; internally calls the model and validates tool output against the order schema before returning | `best-effort` (see F.5.3, F.7) | Medium to high: prompt and cost engineering |

**Recommendation: the container-implements-HTTP-contract model is the common denominator.** Kind (a) and kind (c) are just special cases of a container that answers `/v1/act`. The C# in-process assembly path (kind b, assembly variant) is offered only for trusted first-party AIs where avoiding a container per call is worth the tighter coupling; community and LLM AIs are always containers, never in-process, so a crash or an infinite loop can never touch the turn engine. Everything downstream (dispatch, sandboxing, billing) then has exactly one shape to reason about.

This directly kills the current single-AI-at-a-time limitation. Today `AiRunner.cs` opens `File.OpenWrite(raceName + ".lock")` and relies on a shared folder; only one AI can hold the lock at once. In the container model there is no shared file, no lock, and no folder. Each `act` call is an isolated invocation that receives its intel in the request body and returns orders in the response body. N seats run as N concurrent invocations.

---

### F.3 Runtime: AI participants as cloud workers

#### F.3.1 Where a dispatch comes from

The async turn scheduler (the cloud replacement for `Nova/WinForms/NovaConsole.cs`, whose 2.5s WinForms timer polled `.orders` and called `GenerateTurn()`) decides a seat needs orders when any of these hold:

- the seat is an AI seat (solo-vs-AI, or an AI opponent chosen in the lobby);
- a human seat was abandoned or timed out and the game marks it AI-controlled;
- the per-game "maximum time between turns" deadline is near and a seat has not submitted.

For each such seat the scheduler emits one dispatch.

#### F.3.2 GCP shape (project `roybot`)

| Concern | Choice | Why |
|---|---|---|
| Participant execution | **Cloud Run** (jobs for batch-style AIs, or services for warm LLM adapters that benefit from a live process and cached prompts) | Scales to zero, bills per use (fits a free ad-supported service), one container per participant image |
| Image storage | **Artifact Registry** (`us-docker.pkg.dev/roybot/ai-participants/...`) | Signed, versioned images referenced by manifest |
| Dispatch fan-out | **Pub/Sub** topic `ai-dispatch`, one message per seat-turn; **Cloud Tasks** for deadline-timed dispatch | Decouples the scheduler from worker latency; concurrent by construction |
| Intel/orders blobs | **Cloud Storage** for the `empire_view` payload and the returned `orders[]`, referenced by signed URL when a body is large | Keeps request bodies small, gives the test harness a durable artifact |
| Registry and game metadata | **Firestore** (manifests, per-game AI seat assignments, run records) | Low-latency reads in the lobby and scheduler |
| Secrets (LLM keys) | **Secret Manager**, mounted only into LLM participant service accounts | Community containers never see them |
| Identity and egress | one **service account per participant**, **VPC egress** default-deny with per-manifest allowlist | Sandboxing (F.5) |

#### F.3.3 One dispatch, end to end

1. Scheduler pulls the seat's authoritative `EmpireData` from `ServerData`, projects it to `empire_view` (F.1.2), writes the request (inline or to GCS), and publishes to `ai-dispatch` with the seat's chosen participant image and difficulty.
2. Cloud Run runs that image's `/v1/act` with a hard wall-clock timeout from the manifest.
3. The worker returns `orders[]`.
4. The host adapter validates each order via `ICommand.IsValid` and records it as the seat's submission (equivalent to `OrderReader` setting `TurnSubmitted = true` and populating `AllCommands[empire.Id]`).
5. When all seats have submitted, or the deadline hits, the scheduler runs `TurnGenerator.Generate()` once for the whole game.

#### F.3.4 Concurrency, timeouts, and failure

| Situation | Behavior |
|---|---|
| Many AI seats in one game, or many games | Each seat is an independent Cloud Run invocation. No locks, no shared files. Concurrency is bounded only by a per-participant `max_concurrency` and account quota. |
| Participant slow | Wall-clock timeout from the manifest (`resources.timeout_s`, default 60). On timeout the invocation is cancelled. |
| Participant errors, crashes, or times out | The seat falls back to **held orders**: the seat's last submitted `orders[]` for the current year if any, otherwise an empty order list (the engine already tolerates an empire that submitted nothing; it simply does not change that empire's plans). The game is never blocked by one bad AI. |
| Participant returns invalid orders | Invalid orders are dropped per F.1.4; valid ones are kept. A response that is entirely invalid degrades to held/empty orders. |
| Repeated failures | After a manifest-configurable retry count (default 1 retry), the run record is marked failed, the seat uses held orders, and the participant version is flagged for the registry (F.4). |
| Deadline pressure | The scheduler dispatches AI seats early (as soon as the human seats they do not depend on have submitted) so a slow AI does not eat the human deadline window. |

Held orders make the async, play-by-email cadence safe: a turn always generates on schedule whether or not every AI answered.

---

### F.4 Registration and packaging

#### F.4.1 The AI manifest

A manifest is a small JSON/YAML record in Firestore describing one participant version. It is what the lobby lists and what the scheduler dispatches.

```jsonc
{
  "manifest_version": "1.0",
  "id": "galaxies.default-ai",           // stable participant id
  "version": "1.4.2",                    // semver; new version = new registry row
  "name": "Nova Default AI",
  "author": "Galaxies core team",
  "description": "The classic Stars! Nova AI: expands, scouts, colonizes.",
  "kind": "container",                   // container | builtin-csharp | llm
  "image": "us-docker.pkg.dev/roybot/ai-participants/nova-default:1.4.2",
  "endpoint": "/v1/act",
  "contract_versions": ["1.0"],          // contract versions this image speaks
  "difficulty": ["easy", "normal", "hard"],
  "resources": { "cpu": "1", "memory": "512Mi", "timeout_s": 60, "max_concurrency": 20 },
  "determinism": "seeded",               // seeded | best-effort | nondeterministic
  "network": "none",                     // none | anthropic-only | listed
  "allowed_hosts": [],                   // used only when network = listed
  "cost_class": "free",                  // free | metered
  "visibility": "public"                 // public | unlisted | private
}
```

An LLM participant differs only in a few fields:

```jsonc
{
  "id": "galaxies.claude-strategist",
  "kind": "llm",
  "image": "us-docker.pkg.dev/roybot/ai-participants/claude-strategist:0.3.0",
  "difficulty": ["normal", "hard", "brutal"],
  "determinism": "best-effort",
  "network": "anthropic-only",
  "cost_class": "metered",
  "llm": { "provider": "anthropic-vertex", "model": "claude-opus-4-8",
           "max_input_tokens_per_turn": 12000, "max_output_tokens_per_turn": 1500 },
  "resources": { "cpu": "1", "memory": "1Gi", "timeout_s": 90, "max_concurrency": 8 }
}
```

#### F.4.2 The registry and lobby

- The **registry** is the set of manifests marked `visibility: public` and `contract_versions` that overlap the server's current contract. The lobby lists each by `name`, `author`, `description`, and the difficulty tiers it declares.
- A **game creator** picks AI opponents the same way they pick a race: choose a participant from the registry and a difficulty from its declared tiers. This slots into the existing new-game flow (`Nova/WinForms/NewGameWizard.cs` to `ServerState/NewGame/GameInitialiser.cs`), where `PlayerSettings.AiProgram` (currently `"Human"` or an exe path) becomes a participant id plus difficulty. `EmpireData.Id` is still assigned at game creation exactly as today.
- **Versioning:** a manifest is immutable per `version`; publishing a change mints a new row. A running game pins the participant version its seats were created with, so an AI update never changes an in-flight game. New games get the latest by default. A version can be marked deprecated (hidden from the lobby, still runnable for games that pinned it) or yanked (disallowed for new games; running games fall back to held orders if the image is gone).

---

### F.5 Safety and fairness

#### F.5.1 The fog-of-war boundary is server-enforced

The single most important guarantee: **a participant only ever receives its own empire's view.** The `empire_view` is built by projecting one `EmpireData`, which already contains only that empire's owned data plus what it has scanned (`StarReports`, `FleetReports`, `EmpireReports`). The projection is done host-side by the scheduler; the participant cannot request another seat's data, and the dispatch carries exactly one `empire_id`. This is the same isolation `IntelWriter` gives human players when it writes one `<race>.intel` per empire. There is no code path in the contract by which a participant can read another empire's owned stars, fleets, designs, or research.

#### F.5.2 Sandboxing and resource limits

| Control | Mechanism |
|---|---|
| Isolation | Community and LLM AIs run as containers on Cloud Run, never in the engine process. A crash or hang cannot corrupt `ServerData` or `TurnGenerator`. |
| Egress | Per-participant service account with VPC default-deny. `network: none` gets no egress; `anthropic-only` gets the model endpoint (Vertex AI in project `roybot`, or the Anthropic API) and nothing else; `listed` gets `allowed_hosts` only. |
| CPU/memory/time | From the manifest `resources` block, enforced by Cloud Run. Timeout falls back to held orders. |
| Concurrency | `max_concurrency` per participant caps blast radius and cost. |
| Input hardening | The adapter treats every returned order as hostile until `ICommand.IsValid` passes. Keys are validated against the seat's own `OwnedFleets` / `OwnedStars` / `Designs`, so a participant cannot forge orders for objects it does not own. |
| Image trust | Only images in Artifact Registry under `roybot` are dispatchable; community submissions are reviewed and scanned before their manifest is set `public`. |

#### F.5.3 Determinism and seeding

The engine's RNG is an unseeded `new Random()` per `TurnGenerator` today (cloud blocker #5). Two independent seed concerns:

1. **Engine determinism** (out of scope for this document but a prerequisite): seed the engine RNG from `ServerData` so a turn is reproducible.
2. **Participant determinism:** every dispatch carries `game.seed`, a per-seat, per-turn seed derived from `(game_id, empire_id, turn_year, engine_seed)`. A participant that declares `determinism: seeded` must use only this seed for any randomness, so replaying the same `empire_view` yields the same `orders[]`. This is what makes the golden-game harness (F.6) meaningful. LLM AIs declare `best-effort`: sampling makes them non-reproducible even with a seed, and the manifest says so honestly.

#### F.5.4 Cost controls for LLM AIs

- Per-turn hard caps `max_input_tokens_per_turn` and `max_output_tokens_per_turn` in the manifest; the worker refuses to exceed them and falls back to held orders rather than overspending.
- **Prompt caching**: the fixed rules-of-the-game and race-traits prefix is cached so repeat turns read it at roughly a tenth of input price; only the per-turn `empire_view` digest varies (see F.7).
- A per-game and per-day token budget in Firestore; when a game exhausts its LLM budget, its LLM seats degrade to the built-in Nova AI (kind a) for the remainder, keeping the free service solvent.
- Cheaper models for cheaper tiers (Haiku-class for `normal`, Opus-class for `brutal`), declared per manifest.

#### F.5.5 Abuse prevention

- Prompt-injection: in-game `messages[]` (chat, event text) are untrusted data. LLM adapters must present them as quoted data, never as instructions, and the server still validates every order regardless of what any message said. An AI that is talked into a bad move can still only emit orders that pass `IsValid` for its own seat.
- Order-spam: a response is capped at a sane maximum order count per turn; excess is dropped.
- Griefing by a slow or crashing community AI cannot stall a game (held orders) and cannot exceed its own cost/quota.

---

### F.6 Testing harness

Because a participant is a pure function from `empire_view` to `orders[]`, it is directly testable without a running game.

#### F.6.1 Replay a saved state

Capture is free: every dispatch's `empire_view` and returned `orders[]` are written to GCS as a run record. The harness command `replay <participant> <empire_view.json>` posts a saved `empire_view` to a participant's `/v1/act` and prints the orders, with an option to run each order through `ICommand.IsValid` against a reconstructed `EmpireData` and report which were accepted or dropped. This is the cloud form of what a developer does today by hand-running `Nova --ai -r <race> -t <turn> -i <intel>`, but with no files and no lock.

#### F.6.2 Golden-game regression

A "golden game" is a fixed seed plus a scripted sequence of `empire_view`s for one seat, with a recorded expected `orders[]`. For a `seeded` participant, replaying the golden game must reproduce the recorded orders byte-for-byte; a diff is a regression. This runs in CI (the existing NUnit `Tests` project is the natural home, alongside `SimpleTurnGenerator`, which already subclasses `TurnGenerator` and overrides the `ReadOrders` / `WriteIntel` / `BackupTurn` / `CleanupOrders` seams). For `best-effort` participants (LLMs), the golden check is relaxed to invariants rather than exact match (see F.6.4).

#### F.6.3 Difficulty ladder

A ladder is an automated round-robin: each participant-and-difficulty plays a batch of full games (built-in AI vs built-in AI, community vs built-in, LLM vs built-in) driven by `TurnGenerator.Generate()` headless, scored by the existing `VictoryCheck.cs` and `Scores.cs`. Outputs a win-rate matrix used to (a) sanity-check that a `hard` tier actually beats a `normal` tier, and (b) rank community submissions. Because seats are isolated and concurrent, a ladder run parallelizes across Cloud Run.

#### F.6.4 What to assert for non-deterministic AIs

For LLM participants, assert properties rather than exact orders: every returned order passes `IsValid`; the participant colonizes at least one reachable habitable world within N turns of one being visible; research budget stays in 0 to 100; no order references an unowned key. These catch the failure modes that matter (illegal or self-defeating output) without demanding reproducibility the model cannot give.

---

### F.7 LLM specifics: a Claude-powered participant

This is a modern build, so an LLM participant is a first-class kind, not an afterthought. Here is the concrete shape.

#### F.7.1 Where it runs and which model

A Cloud Run service in project `roybot` calling Claude through Vertex AI (`AnthropicVertex(project_id="roybot", region="global")`) so the key path stays inside GCP, or through the first-party Anthropic API with a key from Secret Manager. Default model `claude-opus-4-8` for the flagship `brutal` tier; a cheaper Haiku-class model for `normal`. Use adaptive thinking (`thinking: {type: "adaptive"}`) with `output_config.effort` tuned per tier (`low` to `medium` for cheap tiers, `high` for the flagship). Note that on Vertex AI, structured outputs and tool use are available, which is all this participant needs.

#### F.7.2 State summarization to fit context

Do not hand the model the raw `empire_view`; it is large and mostly irrelevant each turn. The adapter builds a compact digest:

- a one-paragraph situation summary (year, owned planet count, total population, fleet count, research levels, current research target, known threats within scan range);
- a short table of owned planets (name, population, factories/mines, mineral surplus, queue head);
- a short table of owned fleets (name, position, fuel, role, current waypoint);
- the handful of `messages[]` that changed since last turn (for example `TechAdvance`), quoted as data;
- nearby unowned `star_reports` worth colonizing.

The fixed rules-of-the-game text and the race's traits go in a stable system prefix that is prompt-cached, so only the per-turn digest is fresh input. This is what keeps per-turn token count in the low thousands.

#### F.7.3 Tool-style order emission

Give the model exactly one tool, `submit_orders`, whose input schema is the `orders[]` schema from F.1.3 (with `strict: true` so the model cannot emit a malformed shape). The turn ends when the model calls `submit_orders`. The adapter then still runs every order through `ICommand.IsValid` before applying, so the model's output is doubly guarded: the tool schema constrains the shape, and the engine constrains the legality. Optionally, a `send_to_user` style tool is unnecessary here; the participant never talks to a human, it only returns orders.

#### F.7.4 Memory across turns

Persist a small per-seat "strategy notes" blob (a few hundred tokens: current plan, colonization targets claimed, who it considers hostile) in Firestore or GCS, keyed by `(game_id, empire_id)`. Feed it into the next turn's prompt and let the model rewrite it as part of its turn. This gives continuity across the async, day-scale cadence without resending full history. Never store secrets there, and treat it as untrusted on read-back (it is the model's own scratchpad, still validated downstream).

#### F.7.5 Guardrails, honestly

- The tool schema plus `IsValid` mean an LLM can never make an illegal move, only a weak one.
- In-game text (`messages[]`) is quoted as data; the model is instructed that message text is never an instruction. A prompt-injected message can at most produce orders that still have to pass `IsValid` for the model's own seat.
- Timeouts fall back to held orders; token caps fall back to held orders; an exhausted game budget falls back to the built-in Nova AI.

#### F.7.6 The honest note on limits and cost

- **Strategic quality.** An LLM plays a plausible, human-legible game and narrates its plan, but it will not out-optimize a well-tuned procedural AI at the micro level (production ordering, exact warp economics). It is best sold as a characterful opponent, not the hardest one.
- **Latency.** A turn is one or more model calls plus validation, on the order of seconds to tens of seconds. That is invisible in an async, deadline-based cadence, which is precisely why Galaxies is async-only; it would be intolerable in real-time.
- **Determinism.** Sampling makes it `best-effort`. The seat seed is passed for any local randomness, but two runs on the same state can differ. The test harness treats it with property assertions (F.6.4), not golden diffs.
- **Cost.** Cost scales as (games) x (LLM seats per game) x (turns per game) x (tokens per turn). A summarized turn is roughly a few thousand input tokens plus a cached fixed prefix and about a thousand output tokens. On an Opus-class model that is on the order of a few cents per turn; on a Haiku-class model a fraction of a cent. A 50-year game with two LLM seats is therefore dollars, not cents, at the flagship tier and pennies at the cheap tier. For a free, ad-supported service this is real money, which is why F.5.4's per-game budget and the automatic degrade-to-Nova-AI fallback are not optional; measure exact counts with the token-counting endpoint before enabling LLM seats broadly, and default new public games to the procedural AI with LLM opponents as an opt-in.