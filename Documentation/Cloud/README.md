# Galaxies cloud design

Design and build for taking Stars! Nova to the cloud as Galaxies: a free, ad-supported, Google-sign-in, play-by-email space strategy service on GCP project `roybot`.

## Documents

- [GALAXIES-CLOUD-DESIGN.md](GALAXIES-CLOUD-DESIGN.md) is the full modernization spec. It covers the headless engine port, the GCP topology, Google auth, the turn clock (the "maximum time between turns"), the API and desktop-client adaptation, the product surface and Hearthlight public site, ads and donations, licensing and credit, and a phased roadmap (M0 to M7).
- [AI-PARTICIPANTS.md](AI-PARTICIPANTS.md) is the open AI-participant contract: how the built-in Nova AI, community plug-ins, and LLM-driven agents all plug into the same seat as cloud workers. It stands on its own because it is a contract other people will implement against.
- [M0.md](M0.md) is the first milestone, "prove the pipe": the acceptance criterion, the exact headless port subset it needs, the determinism seeding plan, and how to run and deploy it.
- [STATUS-END-OF-M2.md](STATUS-END-OF-M2.md) is the build handoff at the end of Milestone 2: what was built through M0 to M2, the load-bearing decisions, what to watch for, what is proven versus compile-only, and the concrete next steps into M3 and M4.
- [STATUS-END-OF-M4.md](STATUS-END-OF-M4.md) is the current handoff, at the end of Milestone 4: the headless AI assembly, the open participant contract and its dispatch runner, the launch-gate API surface, the marketing site, the legal and operability artifacts, the two engine bugs this work uncovered, and the honest list of what is still only compile-verified. Read the M2 handoff first; everything it says still holds.
- [DISASTER-RECOVERY.md](DISASTER-RECOVERY.md), [ANALYTICS-AND-KPIS.md](ANALYTICS-AND-KPIS.md), and [ONBOARDING-SOLO-VS-AI.md](ONBOARDING-SOLO-VS-AI.md) are the M4 operability set: the backup and restore position with stated RPO and RTO, the KPI specification, and the first-run solo-versus-AI path.
- `../Legal/` holds the terms of service, the privacy policy, and the credits and licensing brief. All three are drafts pending legal review; the licensing document is the brief for counsel, not a ruling.

## What is built so far

M0 through M2 are built and compile on net10.0 on Linux (`Galaxies.slnx`); the
test suites are green.

M0, prove the pipe (done and tested):

- `Common` and `ServerState` are ported to headless, SDK-style net10.0 with no WinForms or `System.Drawing` on the turn path (`Report` becomes an `IReporter` sink, `FileSearcher`/`Config`/`GameSettings` lose their dialogs, `AllComponents` loads without a progress dialog, `ShipIcon`/`RaceIcon`/`Component` drop their live bitmaps, `ServerData` loses its `SaveFileDialog`).
- Determinism: `ServerData.MasterSeed` is persisted and round-trips through the state XML; `TurnGenerator`, `BattleEngine`, and `CheckForMinefields` are seeded from it (FNV-1a, in `Common/Determinism`); `IterateAllFleets` and command application iterate in deterministic order. A determinism test asserts a turn generated twice from identical inputs is identical.
- `ServerHost/` is the headless turn host (`TurnService`, `IGameStore` with local and GCS stores, `Dockerfile`, entry point). `Tests/` runs on net10.0 (NUnit 3) and passes, including in-memory turn generation.

M1, the desktop client talks to the cloud (built, compiles, runs):

- `Api/` is `galaxies-api`: Google sign-in verification, first-party session JWTs with rotating refresh, Firestore identity and membership, the boundary authorization rules, games CRUD plus lobby, orders `PUT`/submit, per-empire intel with fog-of-war authorization, and status. `/healthz`, `/version`, and a 401 on unauthenticated `/v1/me` are verified running.
- `Common/Commands/CommandRegistry.cs` retires `OrderReader`'s hardcoded switch; the XML order path and the API's order ingestion resolve through it.
- `Client/` is the `ITurnTransport`/`HttpTurnTransport` seam that replaces the desktop game's shared-folder file exchange.

M2, the clock (built, compiles, unit-tested):

- `ControlPlane/` is the shared control plane: the Firestore `GameMeta`/`Member`/`UserAccount` model, per-game cadence (the maximum time between turns), the lifecycle state machine, the missed-turn HoldOrders ladder, the Cloud Tasks deadline scheduler (`gen-{gameId}-{turnYear}`), and the Pub/Sub `turn-generated` publisher.
- The exactly-once generation guard (claim then commit on the Firestore `turnYear`/lock transaction) lives here and is covered by a concurrency test: twelve simultaneous triggers for one turn produce exactly one winner, and a duplicate trigger for a generated turn drops.
- `infra/terraform/m2_clock.tf` provisions the public API service, the deadline queue, the one-minute backstop sweep, the fan-out topics, and the API/invoker service accounts and secrets (validated with `terraform validate`).

Known limits in this environment: a real two-player `.sstate` fixture and goldens captured on .NET Framework 4.8 need a Windows build, so the file/GCS pipe and cross-runtime goldens are verified by compilation plus the in-memory turn and determinism tests rather than a live fixture run; server-side new-game map generation (its RNG seeding) stays deferred with M0's fixture approach; and AI seats are M3.

## Start here

Read the "Resolved key decisions" table and the roadmap in the main design document, then M0.md. Everything flagged as needing legal review (the GPL boundary and the Stars! name) is an engineering brief for counsel, not a ruling.

These documents follow the Hearthlight house style: plain, direct, no em dashes, honest about limits.
