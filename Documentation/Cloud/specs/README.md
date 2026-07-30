# Galaxies engineering specs - program overview

Galaxies is a cloud port of the open-source game Stars! Nova (GPL v2): an async, turn-based, play-by-email 4X space strategy game that is free, ad-supported, and Google/Gmail sign-in only. The platform is a small set of Cloud Run services plus a static web surface, all in GCP project `roybot`, region `us-central1`. The turn engine is the ported Nova code (`TurnGenerator.Generate()` advances one game-year with real per-empire fog of war); everything else exists to put that engine behind account-backed identity, cloud storage, a turn clock, AI seats, and notifications, replacing Nova's shared-folder file exchange without rewriting the roughly 24k-line domain model. This document is the index an engineering lead reads first: it maps the five per-service specs to milestones, states the conventions every spec shares, and lists the cross-spec collisions to fix before handoff.

## Service map

| Service | Folder | Build phase | Ingress | One-line purpose |
|---|---|---|---|---|
| galaxies-turngen | `galaxies-turngen/` (code scaffolded today under `ServerHost/`) | M0, M1 | private (internal) | Headless worker: load a game's state plus orders, run `TurnGenerator.Generate()` one year, write new state plus per-empire intel back. |
| galaxies-api | `galaxies-api/` | M1, M2 | public | The only public service: verifies Google ID tokens, mints a first-party JWT, boundary authorization, lobby and game creation, orders/intel I/O, turn clock, dispatch to turngen. |
| galaxies-ai | `galaxies-ai/` (participants under `participants/`) | M3, M6 | private (internal) | Runs AI participants and abandoned-seat takeovers behind one HTTP contract; owns the participant registry (M6). |
| galaxies-notifier | `galaxies-notifier/` | M5 | private (internal) | Turns Pub/Sub events into player email (Postmark) and web push (FCM); owns the delivery ledger, suppression, and preferences read path. |
| galaxies-web | `galaxies-web/` (M1 desktop changes land in `Nova.Client/` and `Nova.Common/Commands/`) | M1, M4, M7 | public (static Firebase Hosting; no Cloud Run) | Player surface: desktop client adaptation (M1), marketing site (M4), browser client (M7). |

Local dev ports do not collide: galaxies-api 8080, galaxies-turngen 8081, galaxies-ai 8082, galaxies-notifier 8083, galaxies-web has none (static; `firebase emulators:start` defaults to 5000).

## Build-phase order (M0 to M7)

| Milestone | Delivered by | What ships |
|---|---|---|
| M0 | galaxies-turngen | Prove the pipe: storage in, one turn, per-empire storage out, in a Linux container, no UI. |
| M1 | galaxies-api, galaxies-web, galaxies-turngen | Playable core begins (public API); desktop client adaptation onto `ITurnTransport`; turngen stream refactor off the scratch-directory shim. |
| M2 | galaxies-api, galaxies-turngen | Turn clock, deadlines, Cloud Tasks `gen-{gameId}-{turnYear}` plus the one-minute Scheduler sweep; the Firestore `turnYear` plus lock exactly-once transaction wired to the live trigger. |
| M3 | galaxies-ai | Built-in Nova AI participant plus the dispatch worker and held-orders fallback. |
| M4 | galaxies-web | Marketing site (Hearthlight / Vigil), desktop installer distribution, auto-update feed. |
| M5 | galaxies-notifier | Email plus (dark) web-push notifications from the three Pub/Sub topics. |
| M6 | galaxies-ai | Open participant registry, community container plug-ins, and the LLM participant (`claude-strategist`). |
| M7 | galaxies-web | Browser client: a third `ITurnTransport` in JavaScript against `/v1` with native JSON DTOs and FCM web push. |

Every milestone M0 to M7 has an owner, and no milestone is orphaned.

## Shared house conventions

- **GCP:** one project, `roybot`; one region, `us-central1`; image registry `us-central1-docker.pkg.dev/roybot/roybot-galaxies`.
- **Repo shape:** one folder per microservice, each with its own `cloudbuild.yaml` and `Dockerfile` and service account. Participant workers under `galaxies-ai/participants/*` follow the same one-folder-one-image rule.
- **Ships dark:** every feature ships behind a `_<SERVICE>_ENABLED` Cloud Build substitution (for example `_GALAXIES_NOTIFIER_ENABLED`). Off state: reads return `{"disabled":true}`, mutations return 403, until the flag is flipped.
- **Control plane:** Firestore (native mode) is the single store for everything, including accounts (`users/{google_sub}`), game control docs, seats, AI credentials, refresh tokens, and audit events. Cloud SQL is not used; Postgres is a resolved rejection and must not be reintroduced.
- **State buckets:** three private GCS buckets, uniform access with public-access-prevention enforced: `roybot-galaxies-state` (the `ServerData` XML and per-year backups, written only by turngen), `roybot-galaxies-orders`, `roybot-galaxies-intel`. Intel and orders are never public; they leave only through the API under per-empire authorization.
- **Auth split:** the public API verifies Google ID tokens (Firebase Auth brokers Google) and mints a short-lived first-party JWT plus a rotating refresh token. Private services (turngen, ai, notifier) are `ingress=internal` and are invoked with Cloud Run OIDC identity tokens. Galaxies uses GCP-native OIDC for service-to-service auth, not the Aries `X-Aries-Internal-Secret` HMAC; the HMAC pattern is explicitly not copied.
- **Eventing:** Pub/Sub topics `turn-generated`, `game-created`, `deadline-approaching`; api and turngen publish, ai and notifier subscribe.
- **Determinism:** `ServerData.MasterSeed` is persisted per game; per-turn seed is `hash(MasterSeed, turnYear)`, per-seat seed is `hash(MasterSeed, turnYear, empireId)`.
- **Style guardrail #1:** zero em dashes and zero en dashes anywhere. Use the comma, parenthesis, semicolon, and ellipsis; write ranges as "10 to 120"; hyphens in compounds are fine. No emoji, no decorative glyphs. Tables and code blocks over prose.

## How to use these specs

- Each `spec.md` is a standalone engineering handoff. When a service is scaffolded, its spec moves into that service's folder (for example `galaxies-notifier/spec.md`), per the Aries pattern; until then the five specs live together as design docs.
- The ACTIVATE-style "Rollout (ships dark)" section in each spec is the deploy instruction of record: the ordered deploy first, then staged flips, each with a copy-paste smoke test using pinned `roybot` values. Follow that section rather than improvising a rollout.
- Build phases inside each spec are ordered and testable; ship them in order, each step small enough to land on its own.

## Pointers (authoritative shared docs)

- `Documentation/Cloud/GALAXIES-CLOUD-DESIGN.md` - the master design. Section map: A engine modernization and headless extraction (A.3 serialization, A.4 determinism, A.5 storage seams); B GCP architecture (B.3 `ServerData` storage modeling, B.4 eventing and notifications plumbing); C authentication, identity, authorization (C.2 identity data model, C.3 API-boundary authorization); D turn scheduling, deadlines, game lifecycle (D.3 scheduler mechanism, D.5 notifications, D.6 full game-creation options); E API/protocol and desktop client adaptation (E.3 DTO mapping); G product, brand site, ads, licensing (G.4 licensing and credit analysis). Note there is no Section F here on purpose; Section F is the standalone AI doc below.
- `Documentation/Cloud/AI-PARTICIPANTS.md` - Section F, the Open Participant Contract (F.1 the contract, F.3 runtime, F.5 safety and fairness, F.7 the Claude-powered participant). All `§F.*` references in galaxies-ai point here, not into the design doc.
- `Documentation/Cloud/M0.md` - the M0 "prove the pipe" milestone: the headless port subset, the pre-seeded two-player fixture, local run and `roybot` deploy steps, and the exit criteria that galaxies-turngen builds against.

## Reconciliation items (lock before build)

The five specs were authored in parallel and agree on ports, folders, ingress, the OIDC-not-HMAC decision, the three buckets, and the no-Postgres rule. A few shared names and contracts drifted, which is expected when specs are written independently. Each is a lead decision, not a redesign; none moves a service boundary.

Items 1 to 5 were open questions when this list was written. The M3 build closed all five, in code and in infrastructure, so they are now settled facts rather than pending decisions. They are kept here with their resolutions because the specs themselves still carry the older wording in places, and a reader who hits that wording needs to know which text won.

1. **Canonical seat model. RESOLVED.** One model: `games/{gameId}/members/{empireId}`, a Firestore subcollection, as galaxies-api defines it (design §C.2, §D.6). galaxies-notifier's roster resolver and galaxies-ai's dispatch both read what the API writes, so they read this subcollection. galaxies-ai's `ai_seats` is additive metadata keyed by the same `(gameId, empireId)`, never a parallel roster. Built this way in M3: the dispatch worker resolves seats from `members`, and `ai_seats` holds only the participant pin (`participant_id`, `version`, `difficulty`, `controller`).

2. **Account document id. RESOLVED.** `users/{google_sub}` is canonical (design §C.2). Where a spec writes `users/{uid}`, `uid` is the Google subject; treat them as the same value. New code uses `google_sub`.

3. **Per-game seed. RESOLVED, and the field is renamed.** `ServerData.MasterSeed` is the one persisted field. The value galaxies-ai puts in the participant contract is derived, not a second seed. The contract field is now `seat_seed` (`GameContext.SeatSeed` in `Galaxies.AiContract`), computed by `SeatSeed.For(masterSeed, turnYear, empireId)`. The old `game.seed` name is gone from the contract, so it can no longer be mistaken for a master value.

4. **`turn-generated` payload. RESOLVED.** The year field is `turnYear`, never `newTurnYear`. Publisher (galaxies-turngen) and both subscribers (galaxies-ai, galaxies-notifier) use `{ GameId, TurnYear, EmpireIds, AiEmpireIds, GameEnded, Handoffs }`.

5. **Feature-flag names, and how the static web surface ships dark. RESOLVED.** Cloud Run services gate on `_GALAXIES_API_ENABLED`, `_GALAXIES_TURNGEN_ENABLED`, `_GALAXIES_AI_ENABLED`, `_GALAXIES_NOTIFIER_ENABLED` (Cloud Build substitutions, default false). One correction the M3 infrastructure forced into the open: the substitution and the container environment variable are different names, `_GALAXIES_AI_ENABLED` versus `GALAXIES_AI_ENABLED`, and a substitution with no matching env var on the revision is a silent no-op. Both are therefore declared in terraform (`infra/terraform/m3_ai.tf`), so a flip is a variable change and an apply rather than a hand-typed deploy argument. galaxies-web is static Firebase Hosting with no request-time substitution, so it ships dark differently: the marketing site stays on a Hosting preview channel until launch, and the browser client gates at the API (the same first-party JWT and per-empire authorization), not a container flag. Each web sub-surface names its own gate.

6. **Design-doc cross-references. STILL OPEN.** Fix three stale citations before the specs move into their folders: galaxies-ai's "control-plane store = §D" should read §C.2 plus "Resolved key decisions"; galaxies-turngen's "control plane = §B.3" should read "Resolved key decisions" (its §A.3 serialization citation is correct and stays); confirm galaxies-notifier's "email is pre-verified because sign-in is Google-only" cites §C, not §D.5. The authoritative section map is in the Pointers block above.

## Decisions the M3 build made (read before touching galaxies-ai or galaxies-turngen)

Two decisions were taken during the M3 build that are not derivable from the specs as written, and that a reader will otherwise reverse by accident. Both are locked.

**A. galaxies-api owns every control-plane write; galaxies-turngen is a stateless worker.** Claim, commit, and lifecycle transitions all happen in galaxies-api. turngen loads state, runs `TurnGenerator.Generate()`, writes state and per-empire intel to GCS, and returns; it takes no Firestore lock and owns no lifecycle. This is a deliberate fork from the older galaxies-turngen spec text, which had the worker holding the generation lock, and the newer arrangement wins. One writer means one place where the exactly-once transaction lives, and it makes the worker safe to retry, kill, or run twice. Consequences to know: `GameMeta` has two independent axes, `Lifecycle` (Draft, Lobby, Active, Paused, Finished, Cancelled, Archived) and `Generation` (Idle, Generating), plus one `GenerationLock { Token, LeaseUntil }`; and where a spec says turngen writes the control plane, read galaxies-api.

**B. The act request carries both the language-neutral `empire_view` and, for first-party C# participants, the engine-native intel XML.** The request holds a projected `empire_view` (what any participant in any language can consume, and the whole point of an open contract) and an `intel_native` payload carrying the engine's own intel XML, gzipped and base64 encoded inside the same JSON envelope.

This looks like redundancy and is not. The alternative was a hand-written JSON projection of a roughly 24k-line domain model as the only input. That projection would be lossy on the day it was written and would drift further with every engine change, and the two things that would break are the two things that matter most. The built-in Nova AI (`DefaultAi` and its sub-AIs) reads real `EmpireData`, so feeding it a lossy projection would quietly weaken it: it would still emit orders, they would still validate, and it would simply play worse, with no test failing. Golden replay would become meaningless for the same reason, because a replay that reconstructs a different `EmpireData` than the original run is not a replay of anything.

So first-party participants deserialize `intel_native` through `Envelope.ReadIntel` and get a real `Intel` back, losslessly. Third-party participants read `empire_view` and never see the native payload. The fog-of-war boundary is identical either way: both are transcodings of the one per-seat intel object `IntelWriter` already produced for that empire, so neither path can widen the view. The cost is one extra representation to keep in sync; the alternative was a silently degraded AI and a replay harness that proved nothing.
