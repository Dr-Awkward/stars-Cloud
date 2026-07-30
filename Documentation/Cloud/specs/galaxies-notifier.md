# galaxies-notifier - Service Specification

**Service Name:** galaxies-notifier
**Port:** 8083 (local dev)
**Repository Path:** `galaxies-notifier/`
**Build Phase:** M5
**Status:** Planned; docs only, no code yet
**Owner:** Farehard / Galaxies
**Classification:** Galaxies; Internal

---

## 1. Purpose & Scope

galaxies-notifier is the private Cloud Run service that turns platform events into player-facing notifications. It subscribes to the three Galaxies Pub/Sub topics (`turn-generated`, `game-created`, `deadline-approaching`, see GALAXIES-CLOUD-DESIGN.md §B.4), resolves each event to the set of affected human players, applies each player's stored preferences (per-event toggles, quiet hours, digest mode, per-game mute, suppression), renders a plain-text-first template in the Hearthlight house style, and sends over two channels: transactional email through Postmark on the Galaxies domain, and web push through Firebase Cloud Messaging (FCM) for the future browser client. It owns the delivery ledger, the suppression list, bounce handling, and reminder volume control. Because auth is Google/Gmail only, every player's email is already verified; there is no separate email-verification step (see GALAXIES-CLOUD-DESIGN.md §D.5).

The service holds no gameplay logic. It never loads `ServerData`, never touches the intel or orders buckets, and never advances a turn. It is a downstream consumer of results, exactly as `galaxies-ai` is a downstream consumer of the same `turn-generated` topic.

**Out of scope for v1 (M5):**

- SMS / push-to-phone. FCM covers browser and any later mobile; no Twilio, no APNs direct.
- In-app notification rendering. The ported client surfaces turn state itself (the successor to Nova's `SetPlayerList` per-empire status and a live `deadlineAt` countdown); the notifier does not drive an in-app inbox in M5. It only emits email and push.
- Web push delivery at go-live. The M5 web client is not shipping yet, so the push channel ships built but dark (see §5.2, §14). The near-term desktop (WinForms-derived) client learns "your turn is ready" by polling `galaxies-api` on its existing timer cadence and raises a local OS toast; email is its authoritative backstop.
- Marketing / newsletter / digest-of-new-games mail. Every message this service sends is transactional (triggered by the recipient's own participation in a game).
- Sending through the Gmail API or any Workspace mailbox. This is a deliberate rejection, not a deferral (see §5.4).
- Hosting the preference UI and the public unsubscribe / webhook endpoints. Those are public surfaces and live on `galaxies-api`; this service consumes the Firestore state they write (see §11).

---

## 2. High-Level Architecture

### 2.1 Components

- **Push handler layer** (`Notifier/PubSub/`) - three ASP.NET Core minimal-API endpoints, one per topic, each the target of a Pub/Sub push subscription. Every request carries a Cloud Run OIDC identity token minted by `sa-invoker` with the service URL as audience; the handler verifies the token before doing anything. This is GCP-native OIDC, not an HMAC shared secret.
- **Event resolver** (`Notifier/Resolve/`) - maps one platform event to zero or more `(user, eventKey)` targets. For turn and lifecycle events it reads the game control-plane doc `games/{gameId}` to turn `empireId` values into `(GoogleUserId, Email)` via the `PlayerSettings` roster (see GALAXIES-CLOUD-DESIGN.md §D.6). AI-played empires resolve to no target.
- **Preference resolver** (`Notifier/Prefs/`) - applies `users/{uid}.notify` at send time: global channel toggles, per-event toggles, per-game mute, quiet hours, digest mode, and suppression (§7).
- **Channels** (`Notifier/Channels/`) - `EmailChannel` (Postmark) and `PushChannel` (FCM / Web Push), both behind `IChannel` so a channel can be flag-gated independently and a new channel can be added without touching the resolvers.
- **Renderer** (`Notifier/Rendering/`) - Hearthlight plain-text templates with an optional minimal HTML part (§8). No images, no icon fonts, no decorative glyphs.
- **Notification ledger** (`Notifier/Dedup/`) - the Firestore exactly-once guard. Pub/Sub is at-least-once; the ledger makes each `(eventType, gameId, turnYear, uid, channel)` send happen once even under redelivery (§12).
- **Digest buffer + flush** (`Notifier/Digest/`) - buffers digest-eligible notifications per user and flushes them on a Cloud Scheduler tick (§7).
- **Suppression store** (`Notifier/Prefs/SuppressionStore.cs`) - read path over the `suppressions/*` collection that `galaxies-api` populates from Postmark webhooks and one-click unsubscribes (§9).

### 2.2 Event flow (main path: "your turn is ready")

1. `galaxies-turngen` commits a generation and publishes `turn-generated` with `{ gameId, turnYear, empireIds, aiEmpireIds, gameEnded, handoffs[] }` (see GALAXIES-CLOUD-DESIGN.md §B.4, §D.3 step 7).
2. The `turn-generated` push subscription delivers to `POST /pubsub/turn-generated` with an OIDC token (audience = the notifier service URL). The handler verifies the token, decodes the message, and rejects a bad or missing token with 401.
3. If `_GALAXIES_NOTIFIER_ENABLED` is off, the handler logs `notifier_disabled` and returns 204 (ack-and-drop, so Pub/Sub does not redeliver). Nothing is sent.
4. The resolver reads `games/{gameId}`, maps each id in `empireIds` that is a human seat (not in `aiEmpireIds`) to its `(uid, email)`, and produces two candidate events per human: `yourTurnReady` and `turnGenerated` (summary). Each `handoffs[]` entry produces a `handedToAi` event for the affected human. `gameEnded == true` produces a `gameEnded` event for every human.
5. For each `(uid, eventKey, channel)` candidate, the preference resolver decides send / defer / drop (§7), and the suppression store drops any email to a suppressed address.
6. For each surviving `(uid, eventKey, channel)`, the ledger transactionally claims `notifications/{dedupKey}` create-if-absent. A claim that finds an existing `sent` doc drops (redelivery). A fresh claim proceeds.
7. The renderer builds the message; the channel sends it (Postmark or FCM). The provider message id and status are written back to the ledger doc.
8. The handler returns 200 only after all claims are resolved. A transient channel error on one recipient does not fail the whole batch: that recipient's ledger doc is left `failed` for the reminder/retry sweep, and the ack still fires so a single bad address cannot wedge the turn's fan-out.

Deadline reminders (`POST /pubsub/deadline-approaching`) and lifecycle events (`POST /pubsub/game-created`) follow the same shape with their own resolvers (§4).

### 2.3 GCP Topology

| Setting | Value |
|---|---|
| Cloud Run type | Service |
| Region | us-central1 |
| Image | us-central1-docker.pkg.dev/roybot/roybot-galaxies/galaxies-notifier |
| Ingress | internal (invoked only by Pub/Sub push and Cloud Scheduler, both via OIDC) |
| Authentication | required; no `--allow-unauthenticated`. Callers carry an OIDC identity token from `sa-invoker`, audience = service URL |
| Container concurrency | 20 (I/O bound; matches GALAXIES-CLOUD-DESIGN.md §B.1) |
| min / max instances | 0 / 2 |
| CPU / memory | 1 vCPU, 512 MiB (no engine, lean image) |
| VPC | none required; egress to Postmark and FCM is public internet. Add Cloud NAT with a static egress IP only if Postmark IP allowlisting is later wanted |
| Runtime | .NET 10, ASP.NET Core minimal API (platform consistency; does not link the engine, so the image stays small) |

**Service account `sa-notifier` (least privilege):**

| Grant | For |
|---|---|
| Firestore read/write | notification ledger, digest buffer, suppression read, `users/*` prefs read, `games/*` roster read |
| Secret Manager accessor | `POSTMARK_SERVER_TOKEN`, FCM credentials / VAPID keys, `UNSUBSCRIBE_SIGNING_KEY` |
| (no Pub/Sub subscriber role needed) | push subscriptions authenticate as `sa-invoker` with `run.invoker` on this service; the notifier only verifies the token |

Pub/Sub push subscriptions, their dead-letter topics, and the Cloud Scheduler digest job are provisioned in Terraform alongside the topics (see GALAXIES-CLOUD-DESIGN.md §B.5). Each subscription has a max-delivery-attempts policy and a `*-dead-letter` topic so a poisoned message surfaces in Error Reporting rather than looping.

### 2.4 Repository Layout

One folder per microservice, each with its own `cloudbuild.yaml` and `Dockerfile`, per the Galaxies convention.

```
galaxies-notifier/
├── Notifier/                         # ASP.NET Core minimal API (.NET 10)
│   ├── Program.cs                    # app, OIDC push-token verification, health
│   ├── Config.cs                     # env + Secret Manager binding, feature flags
│   ├── PubSub/
│   │   ├── TurnGeneratedHandler.cs
│   │   ├── GameLifecycleHandler.cs   # game-created, discriminated by `type`
│   │   └── DeadlineApproachingHandler.cs
│   ├── Resolve/
│   │   ├── EmpireUserResolver.cs     # reads games/{gameId} roster
│   │   └── EventFanout.cs
│   ├── Prefs/
│   │   ├── PreferenceResolver.cs     # per-event, quiet hours, digest, mute
│   │   └── SuppressionStore.cs
│   ├── Dedup/
│   │   └── NotificationLedger.cs     # Firestore claim per (event,turn,uid,channel)
│   ├── Channels/
│   │   ├── IChannel.cs
│   │   ├── EmailChannel.cs           # Postmark client + List-Unsubscribe headers
│   │   └── PushChannel.cs            # FCM / Web Push (ships dark in M5)
│   ├── Rendering/
│   │   ├── TemplateRenderer.cs       # deadline/time localization, deep links
│   │   └── Templates/                # Hearthlight plain-text + minimal HTML
│   │       ├── your-turn-ready.txt / .html
│   │       ├── deadline-approaching.txt / .html
│   │       ├── game-started.txt / .html
│   │       ├── game-ended.txt / .html
│   │       ├── handed-to-ai.txt / .html
│   │       ├── game-paused.txt / .html
│   │       ├── game-resumed.txt / .html
│   │       └── invited.txt / .html
│   └── Digest/
│       └── DigestFlush.cs            # Cloud Scheduler target
├── tests/                            # NUnit
├── Dockerfile
├── cloudbuild.yaml
├── spec.md
└── questions.md                      # forward-looking dev-team forks
```

---

## 3. Configuration & Feature Flags

Every capability ships dark behind a substitution set on the Cloud Build trigger. Because the notifier is a Pub/Sub push consumer, "dark" for the push handlers means ack-and-drop (return 204, send nothing, log the reason) rather than 403, so a disabled service does not force Pub/Sub into redelivery and dead-lettering. The public preference surface lives on `galaxies-api` and honors the same master flag: reads return `{"disabled":true}` and mutations return 403 while the notifier is dark (see §11).

### 3.1 Switches (all ship OFF)

| Where (trigger) | Switch | Off state | On state |
|---|---|---|---|
| galaxies-notifier | `_GALAXIES_NOTIFIER_ENABLED` | all push handlers ack-and-drop, send nothing; `galaxies-api` prefs reads return `{"disabled":true}`, prefs mutations 403 | service live |
| galaxies-notifier | `_NOTIFIER_EMAIL_ENABLED` | email channel no-ops (logged `channel_disabled:email`); other channels unaffected | Postmark email send live |
| galaxies-notifier | `_NOTIFIER_PUSH_ENABLED` (reserved, M6) | push channel no-ops; subscriptions still register | FCM / Web Push send live |
| galaxies-notifier | `_NOTIFIER_REMINDERS_ENABLED` | `deadline-approaching` handler ack-and-drops | reminder email/push live |
| galaxies-notifier | `_NOTIFIER_DIGEST_ENABLED` | digest mode collapses to immediate (nothing buffered) | daily digest flush live |

### 3.2 Send mode - `NOTIFIER_MODE` (live / testing redirect)

Orthogonal to `_GALAXIES_NOTIFIER_ENABLED` (which must still be on to send at all), modeled on the Aries `MAILMATCH_MODE` pattern.

| Env var | Default | Effect |
|---|---|---|
| `NOTIFIER_MODE` | `live` | `live` mails the real recipient. `testing` reroutes every send to `NOTIFIER_TEST_REDIRECT_TO` |
| `NOTIFIER_TEST_REDIRECT_TO` | `coop@farehard.com` | address that receives all sends while in `testing` mode |

In `testing` mode the single send chokepoint reroutes delivery only: `To` becomes the redirect address, the subject is prefixed `[SMOKE -> <intended>]`, and a banner naming the intended recipient is prepended to the body. The ledger still records the intended `uid` and address so smoke data stays realistic. Fail-safe: `testing` with an empty `NOTIFIER_TEST_REDIRECT_TO` refuses the send rather than falling through to a real player.

### 3.3 Environment variables & secrets

| Env / substitution | Default | Purpose |
|---|---|---|
| `POSTMARK_SERVER_TOKEN` | Secret Manager ref | Postmark Server API token |
| `POSTMARK_MESSAGE_STREAM` | `outbound` | Postmark transactional stream |
| `NOTIFIER_FROM_EMAIL` | `notifications@${GALAXIES_DOMAIN}` | envelope + header From |
| `NOTIFIER_FROM_NAME` | `Galaxies` | From display name |
| `NOTIFIER_BOUNCE_DOMAIN` | `pm-bounces.${GALAXIES_DOMAIN}` | custom Return-Path for DKIM/SPF alignment (§9) |
| `GALAXIES_WEB_BASE_URL` | `https://${GALAXIES_DOMAIN}` | deep-link + unsubscribe base |
| `FCM_PROJECT_ID` | `roybot` | FCM project |
| `FCM_CREDENTIALS` | Secret Manager / ADC | Firebase Admin credentials for send |
| `VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY` | Secret Manager | Web Push API path (if used alongside FCM) |
| `UNSUBSCRIBE_SIGNING_KEY` | Secret Manager | HMAC key for unsubscribe / one-click tokens |
| `REMINDER_MAX_PER_TURN` | `3` | hard cap on reminders per (uid, game, turnYear) (§10) |
| `REMINDER_COALESCE_WINDOW` | `15m` | cross-game reminder coalescing window (§10) |
| `DIGEST_FLUSH_CRON` | hourly | Cloud Scheduler cadence that drives `/tasks/digest-flush` |
| `LEDGER_TTL_DAYS` | `60` | Firestore TTL on `notifications/*` |

`${GALAXIES_DOMAIN}` is the sending domain chosen at DNS setup and pinned in `source_of_truth.md`; it is not hard-coded here. `GALAXIES_DOMAIN` is the one substitution that must be set before go-live, because it anchors DKIM, the From address, and every deep link.

---

## 4. Event Catalog

The three pinned topics carry every notification. `game-created` is used as the game-lifecycle topic and carries a `type` discriminator; the naming friction (a topic called `game-created` also carrying paused / resumed / ended) is called out in §16. Events map to channels per GALAXIES-CLOUD-DESIGN.md §D.5.

### 4.1 Events consumed (topics in)

| Topic | Payload (relevant fields) | Published by |
|---|---|---|
| `turn-generated` | `gameId, turnYear, empireIds, aiEmpireIds, gameEnded, handoffs[]` | galaxies-turngen after a committed generation (§B.4, §D.3) |
| `deadline-approaching` | `gameId, turnYear, leadTime, hoursRemaining, empireIds` (unsubmitted) | scheduling layer, one message per `ReminderLeadTimes` entry (§B.4, §D.1) |
| `game-created` | `gameId, type, players[], settingsSummary, inviteEmails[]` where `type ∈ {created, started, paused, resumed, ended, cancelled}` | galaxies-api on each lifecycle transition (§D.4) |

`handoffs[]` (an addition requested of the turngen payload) carries `{empireId, kind: temporary|permanent}` so the notifier does not have to diff `aiEmpireIds` against prior state to detect a takeover. If `handoffs` is absent on an early build, the resolver falls back to diffing `aiEmpireIds` against `games/{gameId}.aiEmpireIds` before the write; this fallback is flagged in §16.

### 4.2 Notifications produced (player-facing)

| Player event | Derived from | Default channels | Urgency |
|---|---|---|---|
| Game started | `game-created` `type=started` | email, push | deferrable |
| You were invited | `game-created` `type=created` with `inviteEmails[]` | email | deferrable |
| Game paused / resumed | `game-created` `type=paused` / `resumed` | email, push | deferrable |
| Game ended | `turn-generated` `gameEnded=true`, or `game-created` `type=ended` | email, push | deferrable |
| Your turn is ready | `turn-generated`, per human empire in `empireIds` | email, push | deferrable |
| Turn generated (summary) | `turn-generated`, per human empire | push (default), email off by default | deferrable |
| Your empire was handed to AI | `turn-generated` `handoffs[]` | email, push | deferrable |
| Deadline approaching | `deadline-approaching`, per unsubmitted human empire | push; email only on the final lead time | urgent on final lead time |

"Urgent" events bypass digest batching and are exempt from quiet-hours deferral when they are the final reminder before a deadline, so a player is never silently timed out (§7, §10). Cancelled games (`type=cancelled`) send no notification by default; a lobby that never started is noise, not news.

---

## 5. Channels

### 5.1 Email (Postmark)

Transactional email is sent through Postmark on the Galaxies domain, over the `outbound` transactional message stream. Postmark is chosen over SendGrid and SES for the same reason the design source recommends a dedicated transactional provider: fast setup of SPF/DKIM/DMARC, first-class bounce and complaint webhooks, per-message activity you can inspect during a smoke, and shared transactional IP pools with managed reputation that suit a low-and-bursty, near-zero-budget sender. The provider choice is a substitution boundary (`EmailChannel` behind `IChannel`), so a later swap to SES is a channel implementation, not a rewrite.

Every email:

- Comes from `NOTIFIER_FROM_EMAIL` on the Galaxies domain, never from a player's mailbox.
- Uses a custom Return-Path on `NOTIFIER_BOUNCE_DOMAIN` so SPF and DKIM align and DMARC passes (§9).
- Carries a deep link into the game (`GALAXIES_WEB_BASE_URL/games/{gameId}` for the browser client; an optional `galaxies://game/{gameId}?turn={turnYear}` protocol link for the desktop client).
- Carries `List-Unsubscribe` and `List-Unsubscribe-Post` headers (RFC 8058 one-click) plus a footer unsubscribe link, both signed with `UNSUBSCRIBE_SIGNING_KEY` (§7, §9).
- Renders in the Hearthlight plain style (§8).

### 5.2 Web push (FCM / Web Push)

Web push targets the future browser client and any later mobile, using FCM (native to the `roybot` project). The standard Web Push API with VAPID keys is supported behind the same `PushChannel` for browsers that a caller registers directly; FCM is the recommended path. Push subscriptions are stored per device under `users/{uid}/pushSubscriptions/{subId}` and registered through `galaxies-api` (§11). The channel is built in M5 but ships dark (`_NOTIFIER_PUSH_ENABLED` reserved) because the web client is not live yet; when a send fires with no registered subscription it is a clean no-op recorded as `status=skipped_no_subscription`, never an error. A `404`/`410` from FCM marks the subscription `valid=false` so a dead token is not retried.

### 5.3 In-app / desktop polling (near-term)

The near-term WinForms-derived client cannot receive web push. It learns "your turn is ready" by polling `galaxies-api` on its existing timer cadence (the cloud successor to Nova's 2.5s `consoleTimer`, throttled to a game-appropriate interval) and raises a local OS toast. The notifier does nothing special for this client; the polled turn state and the authoritative email path together cover it. This is why email is never optional as a floor even once push lands.

### 5.4 Why sending is NOT done through the Gmail API

Google/Gmail sign-in gives us a verified address for every player, which removes the email-verification step. It does not make Gmail a sending channel, and we deliberately do not use it:

- **Per-mailbox send caps.** A consumer or Workspace mailbox is capped at roughly 500 to 2000 recipients per day. A game-wide "your turn is ready" fan-out across many concurrent games blows past that; a transactional ESP is built for the volume.
- **Wrong From identity and no domain alignment.** Sending via Gmail would originate from a mailbox, not from the Galaxies domain, so we would get no DKIM/DMARC alignment on our own domain and no coherent sending reputation to manage.
- **No bounce or complaint webhooks.** Gmail API sending gives weak, non-real-time bounce signal and no complaint feedback loop, so we could not maintain a suppression list or protect deliverability (§9).
- **Spam-folder risk and reputation coupling.** Bulk transactional blasts from a consumer mailbox land in spam and tie the game's deliverability to an unrelated personal or Workspace reputation.

The players' addresses happening to be Gmail addresses is irrelevant to this decision. We send from the Galaxies domain through Postmark, full stop.

---

## 6. Data Model (Firestore, native mode)

Firestore is the single control plane for the whole platform (see GALAXIES-CLOUD-DESIGN.md §B.3). Cloud SQL is not used. The notifier reads the account and roster docs owned elsewhere and owns four collections of its own.

**`users/{google_sub}`** (account doc, shared platform-wide; the notifier reads and writes only the notify subtree)

| Field | Type | Notes |
|---|---|---|
| `email` | string | verified Gmail from the Google ID token; the send address |
| `displayName` | string | greeting |
| `timezone` | IANA tz string | deadline rendering and quiet-hours evaluation; falls back to the game's `GameTimezone` |
| `notify` | map | preference block, see §7 |
| `unsubscribeToken` | string | opaque HMAC-signed token; appears in every email footer and `List-Unsubscribe` |
| `updatedAt` | timestamp | last prefs change |

**`users/{google_sub}/pushSubscriptions/{subId}`**

| Field | Type | Notes |
|---|---|---|
| `fcmToken` | string | FCM registration token (or Web Push `endpoint` + `keys.p256dh` + `keys.auth`) |
| `ua` | string | user agent hint, for the manage-devices UI |
| `valid` | bool | flipped false on FCM `404`/`410` |
| `createdAt` / `lastSeenAt` | timestamp | |

**`notifications/{dedupKey}`** (delivery ledger and idempotency guard; §12)

| Field | Type | Notes |
|---|---|---|
| `dedupKey` (doc id) | string | `hash(eventType, gameId, turnYear, uid, channel[, leadTime])` |
| `uid`, `gameId`, `turnYear`, `eventType`, `channel` | mixed | denormalized for querying failed sends |
| `status` | enum | `queued` \| `sent` \| `suppressed` \| `skipped_no_subscription` \| `deferred` \| `failed` |
| `provider` | enum | `postmark` \| `fcm` |
| `providerMessageId` | string | Postmark MessageID or FCM message name, for tracing bounces back to a send |
| `suppressReason` | string? | when `status=suppressed` |
| `createdAt` / `sentAt` | timestamp | |

A Firestore TTL policy on `createdAt` expires ledger docs after `LEDGER_TTL_DAYS`.

**`suppressions/{emailKey}`** (written by `galaxies-api` from Postmark webhooks and one-click unsubscribes; read by the notifier before every email)

| Field | Type | Notes |
|---|---|---|
| `emailKey` (doc id) | string | sha256 of the lowercased address (id keeps the raw address out of the key) |
| `email` | string | |
| `reason` | enum | `hard_bounce` \| `spam_complaint` \| `manual_unsubscribe` \| `repeated_soft` |
| `source` | enum | `postmark_webhook` \| `user_action` |
| `active` | bool | reactivation flips this false-to-true only on an explicit re-opt-in |
| `createdAt` | timestamp | |

**`users/{google_sub}/digestItems/{itemId}`** and **`users/{google_sub}/digestState`** (daily-digest buffer; §7)

| Field | Type | Notes |
|---|---|---|
| digestItems: `eventType`, `gameId`, `turnYear`, `renderContext` (map), `createdAt` | mixed | one buffered notification |
| digestState: `nextFlushAt` | timestamp | when the buffer is due to be sent as one email |

Empire-to-user resolution reads the game control-plane doc `games/{gameId}` (its `players[]` carry `GoogleUserId`, `Email`, and `PlayerNumber` mapped to `EmpireData.Id`, see GALAXIES-CLOUD-DESIGN.md §D.6). The notifier never writes that doc.

---

## 7. Notification Preferences

Stored in `users/{uid}.notify` and applied at send time (see GALAXIES-CLOUD-DESIGN.md §D.5).

| Preference | Type | Default |
|---|---|---|
| `emailEnabled` | bool | true |
| `pushEnabled` | bool | true (once a push subscription exists) |
| `perEvent` | map<eventKey, bool> | all true except `turnGenerated` push (false) |
| `reminderLeadTimes` | list<Duration> | inherits the game's `ReminderLeadTimes`; a user may subset it, never widen it |
| `quietHours` | `{start, end, tz}` | off; when set, deferrable notices wait until the window's end |
| `digestMode` | enum {immediate, daily} | immediate |
| `perGameMute` | map<gameId, bool> | empty |
| `unsubscribeToken` | string | required in every email footer and `List-Unsubscribe` |

**Resolution order per `(uid, eventKey, channel)`:**

1. Master flag off (`_GALAXIES_NOTIFIER_ENABLED`) -> drop.
2. `perGameMute[gameId]` true -> drop.
3. Channel disabled (`emailEnabled` / `pushEnabled` false, or the channel's feature flag off) -> drop that channel.
4. `perEvent[eventKey]` false -> drop.
5. Email only: address in an active `suppressions/*` row -> drop email (push may still go).
6. `digestMode == daily` and the event is deferrable -> buffer into `digestItems` and set `nextFlushAt`; do not send now.
7. `quietHours` set and the event is deferrable and now is inside the window -> `status=deferred`; a lightweight re-check at the window's end sends it. Exception: a `deadlineApproaching` event on its final lead time is urgent and sends immediately regardless of quiet hours, so a player is not timed out in silence.
8. Otherwise send now.

**Quiet-hours and digest mechanics.** Quiet hours and daily digests both need a wall-clock trigger to flush deferred and buffered items. A dedicated Cloud Scheduler job hits `POST /tasks/digest-flush` (OIDC, audience = service URL) on `DIGEST_FLUSH_CRON`; the handler scans `digestState` docs whose `nextFlushAt <= now` and `notifications` docs `status=deferred` whose quiet window has ended, renders one combined digest email per user (or releases the single deferred notice), and marks them sent. Reminders are never buffered into a digest (they are time-critical, §10).

The public preference and device endpoints are hosted on `galaxies-api` (the only public service) and honor the master flag (§11). The notifier only reads the resulting docs.

---

## 8. Templates (Hearthlight style)

Every message is text-first: a plain-text part is always present, and an optional minimal HTML part uses only system fonts and inline layout. No images, no icon fonts, no emoji, no decorative glyphs. This is the Hearthlight rule and it also helps deliverability (§9).

**Structure (all templates):**

1. One line stating what happened.
2. The load-bearing facts: game name, turn year, and, where relevant, the deadline rendered in the user's timezone with the UTC value in parentheses.
3. One clear call to action: the deep link into the game.
4. A short footer: the per-game mute link, the one-click unsubscribe link, and a plain "why you received this" line.

**Subjects** are plain and informative, kept under about 60 characters, and use a colon or comma rather than a dash, for example:

- `Your turn is ready: Feel the Nova, year 2103`
- `Deadline in 1 hour: Feel the Nova, year 2103`
- `Your empire is now played by AI: Feel the Nova`
- `You are invited: Feel the Nova`

**Time localization.** Deadlines render in `users/{uid}.timezone` (falling back to the game's `GameTimezone`, then UTC), always with the UTC value shown too, so a player in any zone reads an unambiguous time. The `deadlineAt` value comes from the game control-plane doc, not recomputed here.

The template set lives in `Notifier/Rendering/Templates/` as `.txt` plus optional `.html` per event, and is versioned in the repo so copy edits ride a normal Cloud Build deploy.

---

## 9. Deliverability, Suppression, and Bounce Handling

### 9.1 Domain authentication

Set up once on `${GALAXIES_DOMAIN}` before go-live:

- **SPF** includes Postmark's sending hosts.
- **DKIM** uses the Postmark DKIM CNAME records for the domain.
- **DMARC** starts at `p=quarantine` with an aggregate `rua` mailbox for monitoring, then moves to `p=reject` once reports are clean.
- **Custom Return-Path** on `NOTIFIER_BOUNCE_DOMAIN` (CNAME to Postmark's bounce host) so SPF and DKIM both align to the Galaxies domain and DMARC passes.

At this volume, Postmark's shared transactional IP pool (managed reputation) is the right default; a dedicated IP is revisited only at high sustained volume, since a dedicated IP needs consistent warmup traffic to earn reputation.

### 9.2 Suppression list

`suppressions/{emailKey}` is the authoritative do-not-send list. The notifier checks it before every email send (step 5 in §7). A hard bounce or a spam complaint writes a permanent (`active=true`) suppression; a repeatedly soft-bouncing address is suppressed after a bounded number of transient failures. Suppression stops email only; push may still reach the player. Reactivation happens only on an explicit user re-opt-in, never automatically.

### 9.3 Bounce and complaint webhooks

Postmark posts Bounce, SpamComplaint, and SubscriptionChange (one-click unsubscribe) events to a public endpoint. Because the notifier is ingress=internal, that endpoint lives on `galaxies-api` at `POST /webhooks/postmark`, authenticated by HTTP Basic auth plus a secret path segment (both from Secret Manager). `galaxies-api` verifies the request, writes or updates `suppressions/{emailKey}`, and for a complaint also sets the offending user's `notify.emailEnabled=false` with a recorded reason. Postmark itself already deactivates hard-bounced recipients on its side; mirroring into `suppressions/*` means the notifier does not even attempt the send, which saves an API call and protects reputation. Failed hard-bounce and complaint deliveries into the webhook surface in Error Reporting via the standard dead-letter path.

---

## 10. Reminder Rate & Volume Control

Deadline reminders are the highest-volume, most annoyance-prone traffic, so they carry explicit controls beyond the per-user preferences.

- **One per lead time, unsubmitted only.** `deadline-approaching` fires once per `ReminderLeadTimes` entry (default 24h, 6h, 1h) and targets only empires still absent from `submittedEmpireIds` (see GALAXIES-CLOUD-DESIGN.md §D.1). A user's `reminderLeadTimes` may subset the game's list, never widen it.
- **Re-check submission at send time.** The `empireIds` (unsubmitted) list in the payload can be seconds stale. Before sending, the notifier re-reads `games/{gameId}.submittedEmpireIds`; a player who has since submitted gets no reminder.
- **Hard cap per turn.** No more than `REMINDER_MAX_PER_TURN` (default 3) reminders per `(uid, gameId, turnYear)` regardless of configuration, enforced through the notification ledger (each reminder's `dedupKey` includes `leadTime`, and a count guard blocks the fourth).
- **Channel policy.** Reminders default to push for the earlier lead times and add email only on the final lead time, to keep inbox volume down while still guaranteeing a last email before a player is timed out.
- **Quiet-hours exemption for the final reminder only.** Earlier reminders defer under quiet hours; the final lead-time reminder is urgent and sends anyway (§7).
- **No digest batching.** Reminders are time-critical and never buffered into a daily digest.
- **Cross-game coalescing.** A player active in many games could receive many reminders at once. Within `REMINDER_COALESCE_WINDOW` (default 15m), multiple reminder emails to the same user are coalesced into one "several of your games have deadlines soon" email listing each game and its deadline, rather than N separate messages. Push notifications are similarly collapsed.

---

## 11. Endpoint / Trigger Catalog

### 11.1 Notifier-internal (ingress=internal, OIDC only)

| Method + path | Caller | Purpose |
|---|---|---|
| `POST /pubsub/turn-generated` | Pub/Sub push (`sa-invoker` OIDC) | your-turn-ready, turn-generated summary, handed-to-AI, game-ended |
| `POST /pubsub/game-created` | Pub/Sub push | lifecycle: started, invited, paused, resumed, ended |
| `POST /pubsub/deadline-approaching` | Pub/Sub push | reminders (§10) |
| `POST /tasks/digest-flush` | Cloud Scheduler (OIDC) | flush due daily digests and released quiet-hours deferrals (§7) |
| `GET /healthz` | Cloud Run | liveness |
| `GET /readyz` | Cloud Run | readiness: Firestore reachable, secrets loaded, Postmark token present |

### 11.2 Public surfaces hosted on `galaxies-api` (contract the notifier depends on)

These are not hosted by the notifier; they read and write the Firestore state the notifier consumes, and they honor `_GALAXIES_NOTIFIER_ENABLED`.

| Method + path | Auth | Purpose |
|---|---|---|
| `GET /me/notifications/prefs` | first-party JWT | read `users/{uid}.notify` (returns `{"disabled":true}` while the notifier is dark) |
| `PUT /me/notifications/prefs` | first-party JWT | update prefs (403 while dark) |
| `POST /me/push-subscriptions` | first-party JWT | register an FCM / Web Push subscription |
| `DELETE /me/push-subscriptions/{id}` | first-party JWT | unregister a device |
| `GET /u/{token}` | signed token | one-click unsubscribe landing; also serves `List-Unsubscribe-Post` |
| `POST /webhooks/postmark` | Postmark basic auth + secret path | bounce / complaint / unsubscribe ingestion -> `suppressions/*` (§9.3) |

---

## 12. Exactly-once & Idempotency

Pub/Sub delivery is at-least-once, so the notifier must not double-send on redelivery. This reuses the discipline the turn engine uses for generation (see GALAXIES-CLOUD-DESIGN.md §B.2, §D.3): a Firestore transaction as the single-writer guard.

- The `dedupKey` for a send is `hash(eventType, gameId, turnYear, uid, channel[, leadTime])`. It is fully determined by the event, so any redelivery computes the same key.
- Before sending, the notifier transactionally creates `notifications/{dedupKey}` create-if-absent. If the doc already exists with `status ∈ {sent, suppressed, deferred}`, the send is dropped (this is a redelivery or a legitimate skip). Only the transaction winner proceeds to `queued` and then the actual send.
- After the provider call, the winner writes `status=sent` with the provider message id. A crash between `queued` and `sent` leaves a `queued` doc that the reminder/retry sweep can safely re-drive, because the same `dedupKey` still guards it.
- The handler acks the Pub/Sub message (200) only after all per-recipient claims resolve; a single failed recipient is left `failed` and does not block the ack, so one bad address never wedges a turn's fan-out or forces the whole message to redeliver.

---

## 13. Build Phases (M5)

Ordered, each step small enough to ship and test. M5 assumes the M1 headless port and the eventing plumbing (the `turn-generated`, `game-created`, `deadline-approaching` topics and the scheduler that publishes them) are already in place from the platform, turngen, and scheduling milestones.

| Step | Deliverable | Testable outcome |
|---|---|---|
| M5.1 | Service skeleton: minimal API, OIDC push-token verification, `/healthz`, `/readyz`, config binding, the master flag as ack-and-drop | a synthetic push with a valid OIDC token returns 204 while dark; an unsigned push returns 401 |
| M5.2 | Notification ledger + `EmpireUserResolver` reading `games/{gameId}` | a `turn-generated` message resolves the right `(uid, email)` set; a second delivery of the same message writes no second ledger claim |
| M5.3 | `PreferenceResolver` + `SuppressionStore` (read path) | prefs, per-game mute, per-event toggle, and suppression each drop the right candidate; unit-tested |
| M5.4 | Hearthlight renderer + templates for all eight events, with time localization and signed unsubscribe links | golden-file render tests; deep link and List-Unsubscribe present; zero decorative glyphs |
| M5.5 | `EmailChannel` (Postmark) behind `_NOTIFIER_EMAIL_ENABLED` and `NOTIFIER_MODE` | in `testing` mode a send reroutes to `NOTIFIER_TEST_REDIRECT_TO`; the ledger records the intended recipient |
| M5.6 | `DeadlineApproachingHandler` + reminder caps and cross-game coalescing behind `_NOTIFIER_REMINDERS_ENABLED` | the fourth reminder in a turn is blocked; an already-submitted player gets none |
| M5.7 | `GameLifecycleHandler` for `game-created` `type` discriminator (started, invited, paused, resumed, ended) | each `type` produces the right event to the right recipients; `cancelled` produces none |
| M5.8 | Digest buffer + quiet-hours deferral + `/tasks/digest-flush` behind `_NOTIFIER_DIGEST_ENABLED` | daily mode buffers and flushes one combined email; quiet hours defer and release; final reminder is exempt |
| M5.9 | `galaxies-api` contract: prefs endpoints, push-subscription endpoints, `/u/{token}`, `/webhooks/postmark` -> suppression writes | a Postmark bounce webhook writes an active suppression; the next send to that address drops |
| M5.10 | `PushChannel` (FCM) built behind `_NOTIFIER_PUSH_ENABLED` (reserved) | a send with no subscription is a clean `skipped_no_subscription`; a `410` marks the subscription invalid |

---

## 14. Rollout (ships dark)

ACTIVATE-style staged rollout with copy-paste smokes, pinned to `roybot` / `us-central1`. The notifier is ingress=internal, so the natural smoke is to publish a synthetic message onto the real topic (the exact path Pub/Sub uses) and then inspect the ledger, Postmark activity, and logs. `NOTIFIER_MODE=testing` keeps every smoke send in `coop@farehard.com`.

### §0 - Set these once per shell

```bash
gcloud config set project roybot
export REGION=us-central1
export PROJECT_NUMBER="$(gcloud projects describe roybot --format='value(projectNumber)')"
export NOT="$(gcloud run services describe galaxies-notifier --region="$REGION" --format='value(status.url)')"
export POSTMARK_TOKEN="$(gcloud secrets versions access latest --secret=POSTMARK_SERVER_TOKEN)"
# pinned smoke values:
export SMOKE_GAME="smoke-0001"
export SMOKE_YEAR=2103
export REDIRECT="coop@farehard.com"
```

### §1 - First deploy (ordered, all flags OFF)

Deploy order: `galaxies-api` (carries the prefs / webhook / unsubscribe contract) then `galaxies-notifier`. The Cloud Run "Continuously deploy from a source repository" wizard points at `galaxies-notifier/cloudbuild.yaml`; the deploy step pins `--port=8083`, `--ingress=internal`, `--no-allow-unauthenticated`, `--min-instances=0 --max-instances=2`, `--concurrency=20`, and mounts the secrets. Leave every `_*_ENABLED` at `false` and `NOTIFIER_MODE=live`. Verify the flags landed on the revision (a substitution without the matching `--set-env-vars` entry is a silent no-op):

```bash
gcloud run services describe galaxies-notifier --region="$REGION" \
  --format='value(spec.template.spec.containers[0].env)' | tr ',' '\n' | grep -E 'ENABLED|NOTIFIER_MODE'
```

Confirm the push handlers ack-and-drop while dark. From a VPC host (or by publishing to the topic, which routes through Pub/Sub), a `turn-generated` message must produce a `notifier_disabled` log line and no send:

```bash
gcloud pubsub topics publish turn-generated \
  --message="{\"gameId\":\"$SMOKE_GAME\",\"turnYear\":$SMOKE_YEAR,\"empireIds\":[1,2],\"aiEmpireIds\":[],\"gameEnded\":false}"
gcloud logging read 'resource.labels.service_name="galaxies-notifier" AND textPayload:"notifier_disabled"' --limit=5
```

### §2 - Flip 1: enable the service in testing mode

Set `_GALAXIES_NOTIFIER_ENABLED=true` and `NOTIFIER_MODE=testing` (and confirm `NOTIFIER_TEST_REDIRECT_TO=coop@farehard.com`) on the trigger, then redeploy (or `gcloud run services update galaxies-notifier --region="$REGION" --update-env-vars=GALAXIES_NOTIFIER_ENABLED=true,NOTIFIER_MODE=testing,NOTIFIER_TEST_REDIRECT_TO=$REDIRECT` for an immediate flip the next deploy wipes). Email is still off, so this proves resolution and the ledger without sending:

```bash
gcloud pubsub topics publish turn-generated \
  --message="{\"gameId\":\"$SMOKE_GAME\",\"turnYear\":$SMOKE_YEAR,\"empireIds\":[1,2],\"aiEmpireIds\":[],\"gameEnded\":false}"
# expect ledger claims written (one per human empire x channel) and channel_disabled:email logged:
gcloud logging read 'resource.labels.service_name="galaxies-notifier" AND textPayload:"channel_disabled:email"' --limit=10
```

Inspect the `notifications` collection in the Firestore console: one doc per `(uid, channel)` for game `smoke-0001`, year `2103`, `status` `queued` or `skipped`.

### §3 - Flip 2: email live, still testing redirect

Set `_NOTIFIER_EMAIL_ENABLED=true`, keep `NOTIFIER_MODE=testing`. Republish the turn event, then confirm the mail landed in the redirect inbox with a subject prefixed `[SMOKE -> ...]`:

```bash
gcloud pubsub topics publish turn-generated \
  --message="{\"gameId\":\"$SMOKE_GAME\",\"turnYear\":$SMOKE_YEAR,\"empireIds\":[1,2],\"aiEmpireIds\":[],\"gameEnded\":false}"
# confirm Postmark accepted the send and check auth results:
curl -s "https://api.postmarkapp.com/messages/outbound?count=5" \
  -H "Accept: application/json" -H "X-Postmark-Server-Token: $POSTMARK_TOKEN" \
  | jq '.Messages[] | {To, Subject, MessageID, Status}'
```

In the received message, verify: From is on `${GALAXIES_DOMAIN}`; SPF, DKIM, and DMARC all pass in the raw headers; a `List-Unsubscribe` header and a footer unsubscribe link are present; the deadline renders in a local timezone with UTC in parentheses; the body has no decorative glyphs. Click the unsubscribe link and confirm `GET /u/{token}` on `galaxies-api` writes an active `suppressions/*` row and that a re-published send to that address then logs `suppressed`.

Smoke the lifecycle and reminder handlers the same way:

```bash
# invited (game-created type=created with inviteEmails):
gcloud pubsub topics publish game-created \
  --message="{\"gameId\":\"$SMOKE_GAME\",\"type\":\"created\",\"inviteEmails\":[\"$REDIRECT\"]}"
# game started:
gcloud pubsub topics publish game-created \
  --message="{\"gameId\":\"$SMOKE_GAME\",\"type\":\"started\",\"players\":[{\"empireId\":1},{\"empireId\":2}]}"
# final-lead-time reminder to an unsubmitted empire (needs _NOTIFIER_REMINDERS_ENABLED=true):
gcloud pubsub topics publish deadline-approaching \
  --message="{\"gameId\":\"$SMOKE_GAME\",\"turnYear\":$SMOKE_YEAR,\"leadTime\":\"1h\",\"hoursRemaining\":1,\"empireIds\":[2]}"
```

Confirm the fourth reminder in a turn is blocked (`REMINDER_MAX_PER_TURN`) and that a reminder to an empire already in `games/smoke-0001.submittedEmpireIds` is dropped.

### §4 - Flip 3: reminders and digest on

Set `_NOTIFIER_REMINDERS_ENABLED=true` and `_NOTIFIER_DIGEST_ENABLED=true`. Verify the cross-game reminder coalescing (two `deadline-approaching` messages for two games to the same user within `REMINDER_COALESCE_WINDOW` produce one email listing both) and that a `daily` digestMode user's turn-generated summary is buffered and released by `/tasks/digest-flush`:

```bash
gcloud scheduler jobs run notifier-digest-flush --location="$REGION"
gcloud logging read 'resource.labels.service_name="galaxies-notifier" AND textPayload:"digest_flushed"' --limit=5
```

### §5 - Flip 4: go live

Set `NOTIFIER_MODE=live`. Real players now receive mail. This is the go-live flip; do it only after §3 and §4 pass and the domain shows DMARC `p=quarantine` or stronger:

```bash
gcloud run services update galaxies-notifier --region="$REGION" --update-env-vars=NOTIFIER_MODE=live
```

### Reserved - do NOT flip in M5

`_NOTIFIER_PUSH_ENABLED` stays `false`: the FCM channel is built but the web client is not live, so pushes have no subscriptions to reach. Registration through `galaxies-api` still works and stores subscriptions for when M6 flips push on.

### Kill switch / rollback

Flip `_GALAXIES_NOTIFIER_ENABLED=false` on the trigger and redeploy, or for an immediate stop:

```bash
gcloud run services update galaxies-notifier --region="$REGION" --update-env-vars=GALAXIES_NOTIFIER_ENABLED=false
```

Push handlers immediately ack-and-drop and nothing sends. The prefs surface on `galaxies-api` returns `{"disabled":true}` on read and 403 on write. No data migration is involved; the ledger and suppression collections are additive and self-expiring.

---

## 15. Testing

**Unit (`tests/unit/`)**
- `dedupKey` determinism: the same event computes the same key across redeliveries; different lead times differ.
- Preference resolution: master flag, per-game mute, channel toggle, per-event toggle, suppression, digest, quiet hours, each dropping or deferring the right candidate; multi-rule precedence (mute beats everything, final-reminder exemption beats quiet hours).
- Reminder caps: `REMINDER_MAX_PER_TURN` blocks the fourth; already-submitted re-check drops; cross-game coalescing collapses N into one.
- Renderer golden files: every template renders with a deep link, a signed unsubscribe link, localized time, and zero decorative glyphs; subjects stay under 60 characters.
- Empire-to-user resolution: AI empires resolve to no target; `handoffs[]` present and the diff fallback both yield the same handed-to-AI target.

**Integration (`tests/integration/`)**
- Pub/Sub push contract: a signed OIDC push is accepted; an unsigned or wrong-audience token is 401; a disabled service acks with 204.
- Postmark send path: mock the Postmark API; verify send-then-ledger-write, `testing`-mode redirect, and List-Unsubscribe headers.
- Idempotency: two deliveries of one `turn-generated` message produce exactly one `sent` ledger doc per `(uid, channel)`.
- Suppression round-trip: a mocked Postmark bounce webhook to `galaxies-api` writes an active suppression; the next notifier send to that address drops.
- Digest and quiet-hours flush: buffered items release exactly once on `/tasks/digest-flush`; a deferred urgent final reminder is not held.
- FCM path: `410` marks a subscription invalid; a send with no subscription is `skipped_no_subscription`, not a failure.

**Smoke (`tests/test_smoke.py` equivalent)**
- `/healthz` returns 200; `/readyz` returns 200 only when Firestore is reachable and the Postmark token loads.
- An unsigned push to any `/pubsub/*` route returns 401.
- Publishing a synthetic `turn-generated` in `testing` mode lands one email in `NOTIFIER_TEST_REDIRECT_TO` and writes the ledger claims.

**End-to-end (`tests/e2e/`)**
- Seed a game control-plane doc with two human seats, publish `game-created type=started`, then `turn-generated`, then a final `deadline-approaching` for the unsubmitted seat; assert the exact set of emails (started, your-turn-ready, final reminder) with correct recipients, and that a repeated `turn-generated` sends nothing further.

---

## 16. Open Questions

- **Topic naming for lifecycle events.** The pinned topic set is `turn-generated`, `game-created`, `deadline-approaching`. M5 overloads `game-created` with a `type` discriminator to carry started / paused / resumed / ended / cancelled. Do we split a dedicated `game-lifecycle` topic in a later milestone, or keep the overload? The overload works but reads oddly in the Pub/Sub console.
- **Handoff signal source.** Does `galaxies-turngen` add `handoffs[]` to the `turn-generated` payload (preferred), or does the notifier diff `aiEmpireIds` against the prior `games/{gameId}.aiEmpireIds` snapshot? The diff is stateful and races a concurrent roster write; the explicit field is cleaner but needs a turngen change.
- **Is "your turn is ready" urgent or deferrable?** M5 treats it as deferrable (defers under quiet hours, batches under daily digest). For fast-clock games (12h turns) that risks a player missing a whole turn while their digest waits. Do we make turn-ready urgency a function of the game's `MaxTimeBetweenTurns`, sending immediately when the clock is short?
- **Where does the game-started signal originate?** `game-created` fires at creation for invites, but "game started" is the Lobby-to-Active transition. Confirm `galaxies-api` publishes a `type=started` message on that transition rather than relying on the first `turn-generated`.
- **Push before the web client.** `_NOTIFIER_PUSH_ENABLED` is reserved for M6. Should the desktop client register a Web Push subscription in the interim, or is polling plus email sufficient until the browser client ships?
- **Digest and quiet-hours flush cadence.** `DIGEST_FLUSH_CRON` default is hourly. Is an hourly resolution acceptable for quiet-hours release, or do short-clock games need a tighter tick (which raises Cloud Scheduler and cold-start cost)?
- **Cross-game reminder coalescing scope.** Coalescing collapses reminders per user within a window. Should the same window coalesce non-reminder events (for example a player whose three games all generate turns at once), or is that over-batching?
- **Postmark stream split.** All traffic rides one transactional stream today. If reminder volume ever threatens transactional reputation, do we split reminders onto a separate stream, accepting that they are still transactional (game-participation-triggered), not marketing?
- **DMARC hardening timeline.** Start at `p=quarantine` and move to `p=reject` once aggregate reports are clean. Who owns the `rua` mailbox and the go/no-go on `p=reject`?

---

*Galaxies - Internal*