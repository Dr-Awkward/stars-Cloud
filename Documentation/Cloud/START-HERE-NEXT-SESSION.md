# Start here next session

This is the pickup file. Read it first. It says where the build is, what the
last session did, and exactly what the next milestone (M5, the notifier) needs.
It follows the house rules: plain, direct, no em dashes, honest about limits.

Last worked: 2026-07-24. Milestones done as code: M0, M1, M2, M3, M4.

## The one thing to run first

```
export PATH="$HOME/.dotnet:$PATH"          # no dotnet on PATH by default
dotnet build Galaxies.slnx -c Release
dotnet test  Galaxies.slnx -c Release
```

Expect a clean build with zero warnings, then 119 passed and 1 skipped. The skip
is `calculateAdvantagePointsForStandardJoat`, a pre-existing upstream bug, not a
regression. If you see `Nova error: Unable to locate Nova! graphics components.`
in the test output, that is correct: the graphics database failing to load
headless is the whole point of the port.

Environment hazard, unchanged from M2: this repo is a Windows OneDrive folder
mounted over WSL. File reads can serve a stale copy, and two `dotnet build` runs
on the same tree race and throw `MSB3030 file not found` for files that plainly
exist. Trust the compiler and git, and do not run two builds at once. A native
Linux checkout would remove this whole class of confusion and is worth doing.

## Where the build is

Nothing is deployed to GCP. Everything below is buildable, tested code plus
infra as code and docs, in the working tree, not committed. This is the same
posture the M2 handoff described: proven by compilation and unit tests, not by a
running system.

Full detail on M3 and M4 is in [STATUS-END-OF-M4.md](STATUS-END-OF-M4.md). The
short version:

- M3 put the Stars! Nova AI into a headless assembly (`Ai/Nova.Ai.csproj`) and
  built the open participant contract (`AiContract/`), the dispatch runner
  (`AiService/`, the galaxies-ai service), and the first-party `/v1/act` worker
  (`Participants/NovaDefault/`). 46 tests in `Tests.Ai/` prove the AI actually
  plays a turn headless and that a hostile participant cannot forge orders or
  read another empire's data. The single-AI-at-a-time file lock is gone.
- M4 added the launch-gate API surface (game browser, invites, game-over
  summary, DSAR export, moderation, bans, per-account quotas), the Vigil
  marketing site (`galaxies-web/marketing/`), and the legal, trust, and ops
  documents (`Documentation/Legal/`, `SECURITY.md`, DR, KPIs, onboarding).

Two latent engine bugs were found and fixed in M3, both in `ShipDesign`: a null
icon crashed intel serialization, and designs could not be loaded back (they
were silently dropped from the intel round trip). See STATUS-END-OF-M4 for the
detail; the point is that the headless port had these landmines and the AI work
is what stepped on them.

## What is NOT done, and blocks a real launch

These are unchanged from the M2 and M4 handoffs and are still the highest-value
work. None of them is M5; they are the bridge from "built" to "running", and M5
sits on top of a running system.

1. **Nothing has met the network.** Every GCP interaction (Firestore
   transactions, Cloud Tasks, Pub/Sub push, GCS, OIDC audiences) is
   compile-verified and unit-tested against in-memory doubles only. The first
   deploy will find things.
2. **No golden turn captured on .NET Framework 4.8.** This is still the single
   highest-value check in the whole program, because it is where a silently
   different game across architecture would first show.
3. **Server-side new-game map generation is still unseeded.** `StarMapInitialiser`,
   `StarMapGenerator`, `NameGenerator`, `PointUtilities`, and `SpaceAllocator`
   still use bare `new Random()`. Needed before the server can create games
   rather than only advance a fixture.
4. **The marketing site owes binary assets.** Self-hosted fonts (Fraunces, IBM
   Plex Sans, IBM Plex Mono), the Open Graph card, and the desktop installer the
   appcast points at are referenced but not committed.
5. **The legal brief needs counsel.** `Documentation/Legal/CREDITS-AND-LICENSING.md`
   ends with the itemized questions a lawyer must answer in writing before
   launch. No code moves this gate.

If the next session is about deploying rather than building M5, do the four
pre-deploy items in the M4 handoff's "What to do first" section and walk
`specs/ACTIVATE_galaxies_ai.md` to light up M3 dark.

## Next milestone: M5, galaxies-notifier

Goal from the roadmap: turn platform events into player-facing notifications, so
a player learns their turn is ready or their deadline is near without sitting on
the poll. Email now (Postmark), web push later (FCM, for the M7 browser client).

The full spec is [specs/galaxies-notifier.md](specs/galaxies-notifier.md). Read
it before writing code. What follows is the orientation, not a replacement.

### What M5 can lean on that already exists

- The three Pub/Sub topics it subscribes to are already published by the API and
  the control plane: `turn-generated`, `game-created`, `deadline-approaching`.
  The `turn-generated` event already carries `AiEmpireIds` and `Handoffs`, which
  the notifier needs to say "your seat was handed to an AI".
- The seat roster it resolves recipients from is the canonical
  `games/{gameId}/members/{empireId}` subcollection the API writes. A member's
  `AccountId` is the `users/{google_sub}` id; the account carries the email.
  Because sign-in is Google-only, every email is already verified, so there is
  no verification step to build.
- The OIDC-only internal-auth pattern is established. Mirror the API's
  `/internal/*` routes and `AiService`'s push handlers: no in-app bearer check,
  Cloud Run IAM enforces the audience at the edge. Do not add an HMAC secret;
  Galaxies has none by decision.
- The Hearthlight design tokens and voice are in the skill zip and in
  `galaxies-web/marketing/styles/tokens.css`. Email templates are plain-text
  first, zero decorative glyphs, no em dashes.

### What M5 has to build (the spec's own phase order)

`galaxies-notifier/` is a new Cloud Run service, ingress internal, port 8083
local, scale to zero, that owns four Firestore collections it does not share:
the delivery ledger (`notifications/*`, the exactly-once guard), the suppression
list (`suppressions/*`), the digest buffer, and it reads `users/*.notify` prefs
and `games/*` rosters.

The spec's build phases M5.1 to M5.10, in order:

| Step | What it delivers |
|---|---|
| M5.1 | Service skeleton: minimal API, OIDC push verification, health, config, master flag as ack-and-drop while dark |
| M5.2 | Notification ledger plus the empire-to-user resolver reading `games/{gameId}`; a redelivered message writes no second claim |
| M5.3 | Preference resolver plus suppression read path (per-event toggle, per-game mute, quiet hours, suppression) |
| M5.4 | Hearthlight renderer plus templates for all eight events, time-localized, with signed one-click unsubscribe links |
| M5.5 | The Postmark email channel behind `_NOTIFIER_EMAIL_ENABLED`, with a testing mode that reroutes real sends to a redirect address |
| M5.6 | The deadline-reminder handler with volume caps and cross-game coalescing |
| M5.7 | The game-lifecycle handler for the `game-created` `type` discriminator (started, invited, paused, resumed, ended; cancelled sends nothing) |
| M5.8 | Digest buffer plus quiet-hours deferral plus the scheduled `/tasks/digest-flush` |
| M5.9 | The galaxies-api additions (below) |
| M5.10 | The FCM push channel, built behind a flag but reserved until the M7 browser client exists |

Everything ships dark behind `_NOTIFIER_*` flags, same as M3.

### The galaxies-api additions M5 needs (step M5.9)

These land in the existing `Api/` project, additive, the same way the M4 routes
did. The notifier is ingress internal, so the player-facing and public webhook
routes live on the public API, not the notifier:

- `GET` and `PATCH /v1/me/notifications/prefs` (the `users/{uid}.notify` block:
  channel toggles, per-event toggles, per-game mute, quiet hours, digest mode).
- `POST /v1/me/push-subscriptions` and `DELETE /v1/me/push-subscriptions/{id}`
  (register and drop an FCM or Web Push subscription; reserved until M7 but the
  endpoints can land now).
- `GET /u/{token}`, the signed one-click unsubscribe landing that also serves
  `List-Unsubscribe-Post`.
- `POST /webhooks/postmark`, the public bounce and complaint webhook, Basic-auth
  plus a secret path segment from Secret Manager, that writes `suppressions/*`
  and flips `notify.emailEnabled=false` on a complaint. This is the one public
  route in M5, and it exists on the API precisely because the notifier is
  internal.

Suggested M5 order, mirroring how M3 went: build the service skeleton and the
ledger first (nothing is safe to send without exactly-once), then the resolver
and preferences, then the renderer with golden-file tests, then wire Postmark
behind the flag in testing mode, then the API contract, then reminders, digest,
and lifecycle. Leave FCM reserved.

### One doc nit to close in M5

The spec's smoke commands and a couple of tables used the old `newTurnYear`
field name; the code and the reconciliation both use `turnYear`, and the stale
copies were corrected in this session. If you touch the notifier spec, the last
holdout is a C# method parameter name at `specs/galaxies-turngen.md:186`
(`int newTurnYear`), which is a local variable, not the event field, so leave it
unless you are renaming the method.

## Map of what is where

| Path | What it is |
|---|---|
| `Ai/Nova.Ai.csproj` | headless Stars! Nova AI (M3) |
| `AiContract/` | the open participant contract, transcoder, order mapper (M3) |
| `AiService/` | galaxies-ai dispatch runner (M3) |
| `Participants/NovaDefault/` | the built-in AI `/v1/act` worker (M3) |
| `Tests.Ai/` | 46 tests: headless AI, contract, order mapper, golden replay |
| `Api/` | the public API; M3 AI routes and M4 launch routes are additive here |
| `ControlPlane/` | Firestore model, cadence, lifecycle, missed-turn ladder, eventing |
| `galaxies-web/marketing/` | the Vigil static marketing site (M4) |
| `Documentation/Legal/` | terms, privacy, credits and licensing (all drafts for counsel) |
| `Documentation/Cloud/STATUS-END-OF-M4.md` | the full M3 and M4 handoff |
| `Documentation/Cloud/specs/galaxies-notifier.md` | the M5 spec, read before building |
| `Documentation/Cloud/specs/ACTIVATE_galaxies_ai.md` | the M3 ships-dark rollout runbook |
| `infra/terraform/m3_ai.tf` | the M3 infra (validates) |

Nothing here is committed. If you want a branch and commit, that is the first
move next session.
