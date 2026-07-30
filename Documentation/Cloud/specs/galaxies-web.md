# galaxies-web - Service Specification

**Service Name:** galaxies-web (desktop client adaptation, marketing site, browser client)
**Port:** none (static Firebase Hosting; no Cloud Run, no inbound port; local dev via `firebase emulators:start`, default 5000)
**Repository Path:** `galaxies-web/` (marketing site + browser client). The M1 desktop client changes land in the existing engine repo under `Nova.Client/` and `Nova.Common/Commands/`, not under `galaxies-web/`.
**Build Phase:** M1 (desktop client adaptation), M4 (marketing site), M7 (browser client)
**Status:** Planned; docs only, no code yet
**Owner:** Farehard / Galaxies
**Classification:** Galaxies; Internal

---

## 1. Purpose & Scope

galaxies-web is the player-facing surface of Galaxies: everything a human touches that is not the turn engine. It is a cloud port of the open-source game Stars! Nova (GPL v2), an async, turn-based, play-by-email 4X space strategy game that is free, ad-supported, and Google/Gmail sign-in only. This surface has three parts, deliberately separated by build phase because they are three different codebases with three different lifecycles:

1. **Desktop client adaptation (M1).** The smallest set of changes to the existing WinForms client that lets it talk to the cloud without disturbing the roughly 35,000-line GUI above `ClientData`. The whole change is one seam, `ITurnTransport`, with two implementations (local hotseat, cloud), two surgical file splits (`IntelReader`, `OrderWriter`), a desktop OAuth flow, and a slower poll. The guiding principle from GALAXIES-CLOUD-DESIGN.md §E is that the intel/orders file boundary is already a per-player wire protocol; we are lifting it onto HTTPS, not inventing a protocol.
2. **Marketing site (M4).** A static Firebase Hosting site in the Hearthlight/Vigil design language: the hero, the how-a-turn-works sequence, the depth callouts, the open-AI section, the built-on-Stars!-Nova credit, ads plus the reworded donations block, and the fixed dedication once in the footer. It also hosts desktop client distribution and the auto-update feed.
3. **Browser client (M7).** A third `ITurnTransport` written in JavaScript against the same `/v1` API, using native JSON DTOs behind content negotiation and FCM web push. It is an honest XL greenfield: it re-implements the star-map renderer, the ship designer, and the battle viewer, none of which port from WinForms.

**Out of scope for v1:**

- Any change to the turn engine, `ServerState`, `TurnGenerator`, fog-of-war computation, or `EmpireData` owned-vs-report split. Those belong to galaxies-turngen and galaxies-api. See the galaxies-turngen spec and GALAXIES-CLOUD-DESIGN.md §E.
- Native JSON DTOs for the whole domain model in M1. The desktop client keeps sending and receiving the existing XML in a JSON envelope (see §4.3, GALAXIES-CLOUD-DESIGN.md §E.3). Native JSON is an M7 concern, selected per request by `Accept: application/json`.
- The full browser star-map, designer, and battle-viewer renderers before M7. The first useful browser slice is read-mostly (read intel, read messages, submit simple orders, submit the turn); the renderers come after.
- Live/real-time transport. Cadence is hours to days; there is no WebSocket game channel. Turn-ready delivery is poll plus out-of-band nudges (email now, FCM web push at M7).
- Any Aries-style `X-Aries-Internal-Secret` HMAC. galaxies-web only ever talks to the public API with a Bearer first-party JWT; Galaxies uses GCP-native OIDC for service-to-service auth on the private tier, which this surface never touches. Do not copy the HMAC pattern.
- On-site payment capture and subscriptions. Donations are outbound links only (see §5.5).
- Legal sign-off on the GPL boundary and the Stars! trademark. GALAXIES-CLOUD-DESIGN.md §G.4 is the engineering brief for a lawyer, not a ruling; the site copy in §5.3 is written to that brief but must be confirmed before launch.

---

## 2. High-Level Architecture

### 2.1 Components

- **Marketing site (M4)** - Static HTML/CSS/JS built from `galaxies-web/marketing/`, deployed to Firebase Hosting on project `roybot`. No server. Sign-in hands off to the app (Firebase Auth brokers Google). Hosts the desktop installer links and the auto-update appcast.
- **Browser client (M7)** - Static SPA built from `galaxies-web/browser-client/`, deployed to Firebase Hosting (same project, `/play` path or an `app.` subdomain, see §12). Talks only to galaxies-api `/v1` over TLS with `Accept: application/json`. Registers a service worker for FCM web push and an offline app shell.
- **Desktop client (M1)** - The existing WinForms Windows app, adapted in place in `Nova.Client/`. It is a pure client: no inbound ports, talks to galaxies-api `/v1` over TLS/443 in cloud mode, or to a local shared folder in hotseat mode. Distributed as a signed Windows installer linked from the marketing site.
- **Shared wire contract** - The intel/orders envelope (GALAXIES-CLOUD-DESIGN.md §E.3) and the `CommandRegistry` (§4.7) are consumed by all three clients. The envelope's `contentType` and `protocolVersion` fields let XML-in-field (desktop) and native JSON (browser) coexist per request; this is what keeps the three transports on one API.
- **Upstream services (owned elsewhere, consumed here)** - galaxies-api (public `/v1`, Google ID-token verification, first-party JWT + rotating refresh token minting, per-empire authorization for intel/orders); galaxies-notifier (email and FCM web push nudges); galaxies-turngen (turn generation). galaxies-web owns none of these; it is their front door.

### 2.2 Request / turn flow (main path, cloud desktop client)

1. Player launches the client. It reads the DPAPI-protected token store (§4.4). If a valid session/refresh token exists, it silently refreshes; otherwise it runs the loopback + PKCE OAuth flow and `POST /auth/google`.
2. Client calls `GET /version`, compares its build to `minClientVersion`. On mismatch (`426`) it prompts an upgrade and does not send orders (see §4.4, §5.6, GALAXIES-CLOUD-DESIGN.md §E.5).
3. Client calls `GET /games?scope=mine`, then `GET /games/{gameId}/status` for each active game.
4. The 60-second status poll (plus on-launch, on-focus, on-manual-refresh, optional `?wait=` long-poll) watches for `status.turnYear > clientState.EmpireState.TurnYear` (§4.5).
5. On a new turn, the client calls `GET /games/{gameId}/intel`, unwraps the envelope, hands the decoded XML to `new Intel(xmldoc)`, and runs the existing `ProcessIntel` / `LinkIntelReferences` / `ProcessMessages` pipeline. The roughly 35,000-line GUI renders from the in-memory `ClientData` exactly as it does today.
6. Player composes a turn offline against the last-fetched `Intel`, persisting the `Commands` stack to the local `<race>.state` file as they go.
7. Player finishes: the client builds the orders XML via `command.ToXml`, `PUT /games/{gameId}/orders` (idempotent draft write), then `POST /games/{gameId}/orders/submit` (the one intentional state transition).
8. galaxies-api authorizes the empire, validates turn year and each `ICommand.IsValid`, and may trip early generation when the last empire submits. Out-of-band, galaxies-notifier emails "your turn is ready" or "deadline approaching"; these never carry game data, they only tell the client to poll.

### 2.3 GCP Topology

galaxies-web is static. It runs on Firebase Hosting, not Cloud Run, so the standard Galaxies Cloud Run settings (scale to zero, concurrency, ingress) do not apply to this surface. The relevant topology:

| Concern | Setting (project `roybot`, region `us-central1` where regional) |
|---|---|
| Marketing site host | Firebase Hosting, global CDN, custom domain `galaxies.<domain>`. Public. Preview channels for staging; promote to the live channel on flip (see §10). |
| Browser client host (M7) | Firebase Hosting, `/play` path on the same site or `app.galaxies.<domain>` (see §12). Public. Static SPA. |
| Desktop installers + appcast | Served from Firebase Hosting under `/downloads/` (M4). Large binaries may move to a dedicated public downloads bucket later; this is NOT one of the three private state buckets. See §12. |
| API the clients call | galaxies-api on Cloud Run, public ingress, `/v1`, TLS/443. Owned by the galaxies-api spec. |
| Internal services | galaxies-turngen, galaxies-ai, galaxies-notifier are `ingress=internal`, invoked with Cloud Run OIDC identity tokens. galaxies-web never reaches these directly. |
| Buckets | `roybot-galaxies-state`, `roybot-galaxies-orders`, `roybot-galaxies-intel` are private, uniform access, public-access-prevention enforced. galaxies-web never reads them; intel and orders are served only through galaxies-api with per-empire authorization. |
| Image registry | `us-central1-docker.pkg.dev/roybot/roybot-galaxies` is used by the container services. galaxies-web has no container image (see §2.4). |
| CDN / caching | Firebase Hosting default CDN. Static assets fingerprinted and long-cached; `index.html`, `runtime-config.json`, and `appcast.json` served `no-cache` so flips and updates land immediately. |

### 2.4 Repository Layout

galaxies-web is the one Galaxies service that ships a `cloudbuild.yaml` but **no `Dockerfile`**, because it is static (Firebase Hosting, no port). The build produces static assets and runs `firebase deploy`; there is no Cloud Run container. Every other Galaxies service (galaxies-api, galaxies-turngen, galaxies-ai, galaxies-notifier) keeps its own `cloudbuild.yaml` + `Dockerfile`. The M1 desktop changes live in the engine repo, shown at the bottom for reference.

```
galaxies-web/
├── marketing/                       # M4 static marketing site (Hearthlight/Vigil)
│   ├── index.html                   # single scroll, anchored nav
│   ├── support.html                 # "support the lamp" page (donations)
│   ├── privacy.html                 # privacy note + account-deletion link
│   ├── status.html                  # honest status line, or a link out to an uptime service
│   ├── styles/
│   │   ├── tokens.css               # Vigil semantic tokens (shared with browser client)
│   │   └── site.css
│   ├── assets/
│   │   ├── fonts/                    # Fraunces, IBM Plex Sans, IBM Plex Mono (self-hosted)
│   │   ├── lamplight.svg             # the brand's closing mark
│   │   └── og-card.png
│   ├── downloads/
│   │   └── appcast.json              # desktop auto-update feed (M4, see §5.6)
│   ├── ads.txt                       # AdSense ownership record
│   └── runtime-config.json.tmpl      # stamped at build from _WEB_* substitutions (§3)
├── browser-client/                  # M7 SPA (greenfield)
│   ├── index.html
│   ├── service-worker.js             # FCM web push + offline app shell
│   ├── src/
│   │   ├── transport/httpTurnTransport.js   # the third ITurnTransport, in JS (§6.1)
│   │   ├── dto/                      # native JSON DTOs: Intel, ICommand set (§6.2)
│   │   ├── auth/webOAuth.js          # standard web redirect (Firebase Auth JS)
│   │   ├── push/fcm.js               # FCM registration, token POST to /v1 (§6.3)
│   │   ├── render/starmap/           # greenfield star-map renderer (XL, §6.4)
│   │   ├── render/designer/          # greenfield ship designer (XL, §6.4)
│   │   └── render/battle/            # greenfield battle viewer (XL, §6.4)
│   └── styles/                       # reuses ../marketing/styles/tokens.css
├── firebase.json                    # Hosting config: headers, rewrites, cache rules
├── .firebaserc                      # default project: roybot
├── cloudbuild.yaml                  # build static assets + `firebase deploy` (NO Dockerfile)
├── spec.md
└── questions.md                     # forward-looking dev-team forks (see §12)

# M1 desktop client adaptation - lands in the existing engine repo, not galaxies-web:
Nova.Client/
├── ITurnTransport.cs                # new seam (§4.1)
├── FileTurnTransport.cs             # local hotseat: wraps IntelReader/OrderWriter (§4.2)
├── HttpTurnTransport.cs             # cloud: /v1 envelope over HttpClient (§4.2)
├── Auth/LoopbackPkceFlow.cs         # Google OAuth loopback + PKCE (§4.4)
├── Auth/DpapiTokenStore.cs          # DPAPI-protected session/refresh store (§4.4)
├── IntelReader.cs                   # split: add ReadIntel(XmlDocument) overload (§4.3)
└── OrderWriter.cs                   # split: BuildOrdersXml() + sink (§4.3)
Nova.Common/Commands/
├── CommandRegistry.cs               # replaces OrderReader's hardcoded switch (§4.7)
└── ICommandFactory.cs
```

---

## 3. Configuration & Feature Flags

galaxies-web is static, so its switches gate the **presence and visibility** of UI, not server responses. Everything data-bearing flows to galaxies-api and inherits that service's `_API_ENABLED` gate: reads return `{"disabled":true}`, mutations return `403`, until galaxies-api is flipped on. The galaxies-web switches are Cloud Build substitutions on the galaxies-web Hosting trigger; `cloudbuild.yaml` stamps them into `runtime-config.json`, which the site and browser client read at load (the Aries frontend runtime-config pattern, ported to Hosting). Every feature ships dark.

### Switches (all ship OFF)

| Where (trigger) | Switch | Off state | On state |
|---|---|---|---|
| galaxies-web | `_WEB_SITE_ENABLED` | live channel serves a plain holding page; the built site sits on a preview channel | marketing site promoted to the primary domain |
| galaxies-web | `_WEB_SIGNIN_ENABLED` | "Sign in with Google" is disabled and links to a waitlist note | sign-in runs the Firebase Auth web flow and hands off to the app |
| galaxies-web | `_WEB_ADS_ENABLED` | no AdSense script loads; ad slots render empty | AdSense units load behind the CMP consent gate (§5.4) |
| galaxies-web | `_WEB_DOWNLOAD_ENABLED` | downloads section shows "coming soon"; no installer or appcast linked | installers linked, `appcast.json` live (§5.6) |
| galaxies-web | `_WEB_BROWSER_CLIENT_ENABLED` | `/play` serves an honest panel: "the browser client is not ready yet; download the desktop client" | `/play` serves the browser client SPA (M7) |
| galaxies-web | `_WEB_PUSH_ENABLED` | browser client does not register the FCM service worker; no push prompt | FCM web push registration offered after first sign-in (M7, §6.3) |
| desktop client | cloud mode (config, not a substitution) | `HttpTurnTransport` reads return `{"disabled":true}` from galaxies-api while `_API_ENABLED` is off; the client shows "cloud play is not available yet" and `FileTurnTransport` hotseat keeps working | cloud play live against `/v1` |

The desktop client's local hotseat path (`FileTurnTransport`) is never gated: GPL desktop play with no server must always work. The cloud path lights up when galaxies-api flips. This is the ships-dark story for M1.

### Environment / build config

| Key | Where | Example value | Purpose |
|---|---|---|---|
| `_WEB_API_BASE_URL` | galaxies-web build | `https://api.galaxies.<domain>/v1` | the `/v1` base the browser client and sign-in handoff target |
| `_WEB_FIREBASE_PROJECT` | galaxies-web build | `roybot` | Firebase Hosting + Auth project |
| `_WEB_ADSENSE_CLIENT` | galaxies-web build | `ca-pub-<id>` | AdSense publisher id (only used when `_WEB_ADS_ENABLED`) |
| `_WEB_CMP_ID` | galaxies-web build | `<Google-certified CMP id>` | consent management platform for EU/UK (§5.4) |
| `_WEB_FCM_SENDER_ID` / `_WEB_FCM_VAPID_KEY` | galaxies-web build | `<sender id>` / `<VAPID public key>` | FCM web push registration (M7) |
| `_WEB_UPDATE_FEED_URL` | galaxies-web build | `https://galaxies.<domain>/downloads/appcast.json` | desktop auto-update feed (§5.6) |
| `GALAXIES_API_BASE_URL` | desktop client config | `https://api.galaxies.<domain>/v1` | configurable so a GPL self-hoster points the same binary at their own deployment (§4.4) |
| `GALAXIES_OAUTH_CLIENT_ID` | desktop client config | `<installed-app OAuth client id>` | Google OAuth client for loopback + PKCE (§4.4) |
| `GALAXIES_POLL_SECONDS` | desktop client config | `60` | status-poll interval; replaces the 2.5s timer (§4.5) |

---

## 4. Part One - Desktop Client Adaptation (M1)

The whole point of M1 is that the GUI operates on an in-memory `ClientData` (its `EmpireState`, `Commands` stack, `InputTurn`, `Messages`); it does not care where those bytes came from. We insert one seam and provide two implementations. See GALAXIES-CLOUD-DESIGN.md §E.4 for the full rationale.

### 4.1 The `ITurnTransport` seam

Introduce a transport interface in `Nova.Client`:

```csharp
public interface ITurnTransport
{
    Intel        FetchIntel(int? turnYear = null);   // null = current
    GameStatus   GetStatus();
    void         SubmitOrders(int turnYear, ushort empireId,
                              IEnumerable<ICommand> commands, bool final);
}
```

`ClientData`'s source generalizes from a filesystem path (`GameFolder` / `StatePathName`) to an injected `ITurnTransport` plus a `gameId`. `Restore` / `Save` are unchanged. `Nova.Ai` (AbstractAI, DefaultAi, planners) is architecturally just another client; it gets the same `ITurnTransport`, which incidentally kills the "only one AI at a time" file-contention limit because the server-side AI worker no longer uses the `<race>.lock` + shared-file scheme.

### 4.2 Two transports

| Implementation | Backing store | Used for |
|---|---|---|
| `FileTurnTransport` | the existing shared folder, wrapping today's `IntelReader.ReadIntel` and `OrderWriter.WriteOrders` (the 8-second lock-retry loops stay) | local hotseat, single-box, offline; keeps GPL desktop play working with no server |
| `HttpTurnTransport` | `HttpClient` against `/v1`; unwraps the envelope, hands decoded XML to `new Intel(xmldoc)`, builds orders XML via `command.ToXml`, gzips and base64s into the envelope, and `PUT`s | cloud play against galaxies-api on GCP |

`HttpTurnTransport` uses `System.Net.Http.HttpClient`, already present in .NET Framework 4.8 (and .NET 10 if the client is retargeted), so there are zero new runtime dependencies. The API subset it calls is in §5.1.

### 4.3 Surgical splits: `IntelReader` and `OrderWriter`

Two small, clean splits, each separating file I/O from the parse/build so both transports share one code path:

1. **`IntelReader.ReadIntel(string turnFileName)`** - extract a `ReadIntel(XmlDocument)` (or `ReadIntel(Stream)`) overload that runs the existing `new Intel(xmldoc)` then `ProcessIntel()` / `LinkIntelReferences()` / `ProcessMessages()`. `FileTurnTransport` opens the file and calls the overload; `HttpTurnTransport` decodes the envelope `body` and calls the same overload. The turn-year gate (`newIntel.EmpireState.TurnYear >= clientState.EmpireState.TurnYear`) stays and is what decides "is this actually a new turn."
2. **`OrderWriter.WriteOrders()`** - split into `BuildOrdersXml()` (pure: everything from `Global.InitializeXmlDocument` through the `foreach (ICommand ... ToXml)` loop, including writing `Turn` and `Id`) and the sink. `FileTurnTransport` writes the doc to `<race>.orders`; `HttpTurnTransport` gzips/base64s it into the envelope and `PUT`s. The "final" semantics move from the write to the `submit` call.

The wire format is untouched: every `ICommand.ToXml` / `XmlNode`-constructor and `Intel.ToXml` / `Intel(XmlDocument)` stays byte-for-byte, so there is zero risk of semantic drift in the roughly 24,000-line domain model on day one. The XML travels inside the JSON envelope (`contentType: application/vnd.nova.intel+xml` / `...orders+xml`, `encoding: gzip+base64`); see GALAXIES-CLOUD-DESIGN.md §E.3. This also sidesteps the `using System.Drawing;` coupling inside `Intel.cs`, which a naive JSON serializer would trip over; native JSON is deferred to M7 (§6.2).

The server-side identity checks stay server-side and are load-bearing: wrong turn year is `409`, empire mismatch is `403` (GALAXIES-CLOUD-DESIGN.md §E.5). They do not move to the client.

### 4.4 Desktop auth: Google OAuth loopback + PKCE, DPAPI token storage

The client is a Windows desktop app talking to Cloud Run over TLS/443, with no inbound ports. Auth uses the standard installed-app flow:

1. The client launches the system browser to Google consent (Firebase Auth brokers Google; Google/Gmail sign-in only).
2. It catches the authorization code on a `http://localhost:<ephemeral-port>` loopback redirect, using PKCE (no client secret is embedded in the distributed binary, which is correct for a GPL public binary).
3. It exchanges the code for a Google ID token, then `POST /auth/google` to galaxies-api, which verifies the Google ID token and mints a short-lived first-party JWT plus a rotating refresh token.
4. The session JWT and refresh token are stored in the user profile protected with **DPAPI** (`CryptProtectData`, per-user scope), in `DpapiTokenStore`. On refresh-token rotation the store is rewritten.
5. On startup and on any `426` / version-mismatch, the client compares its build to `GET /version`'s `minClientVersion` and prompts an upgrade (§5.6) rather than sending orders the server can no longer parse.

`GALAXIES_API_BASE_URL` is configurable, so a self-hoster can point the same binary at their own deployment (GPL-friendly). `GALAXIES_OAUTH_CLIENT_ID` is likewise configurable for self-host.

### 4.5 Status polling: 60 seconds replaces the 2.5s timer

Replace NovaConsole's 2.5-second WinForms `Timer` (tuned for a local disk, not a WAN) with a `GetStatus()` poll:

- Default interval 60 seconds (`GALAXIES_POLL_SECONDS`), plus on launch, on window focus, and on manual Refresh.
- Optional `GET /games/{gameId}/status?wait=<sec>` long-poll holds the request open on the server (Cloud Run supports long request timeouts) and returns early when the turn generates. One flag, no new transport.
- When `status.turnYear > clientState.EmpireState.TurnYear`, call `FetchIntel()` and run the existing `ProcessIntel` pipeline. A poll that returns the same turn year is a no-op. Async play makes a stale poll harmless.

Submission is queue-and-retry-with-backoff: a dropped connection means "try again in a minute," not a lost turn, as long as the retry lands before the deadline. This is the one place the legacy 8-second file-lock-retry mindset translates cleanly: same patience, better transport.

### 4.6 What stays completely untouched

- The entire WinForms GUI shell, the star-map renderer, the ship/hull designer, the research and production panels, the battle/message viewer, the tech browser. They read and mutate `ClientData` in memory; they never see HTTP. This is the roughly 35,000-line surface we are explicitly not rewriting in M1.
- `Nova.Ai` (AbstractAI, DefaultAi, planners). An AI gets the same `ITurnTransport`.
- `AllComponents` and other static definition loads. Component definitions ship with the client and load locally, exactly as `ClientData.Initialize` does today.
- Every `ICommand.ToXml` / `XmlNode`-constructor and `Intel.ToXml` / `Intel(XmlDocument)`. They are the wire format.
- The local `<race>.state` file (`ClientStateExtension`) that caches the `Commands` stack and history between sessions. It stays on the player's Windows machine and is exactly what makes offline order composition safe.

### 4.7 The command registry (shared seam)

The worst extensibility blocker on the boundary is the hardcoded `switch (subnode.Attributes["Type"].Value...)` in `OrderReader.ReadPlayerTurn`, duplicated in weaker form inside `ClientData`'s XML constructor. Replace it with a registry so new command types drop in without editing a switch. This seam is shared by all three clients and the server:

- Add `ICommandFactory` and a static `CommandRegistry` in `Nova.Common.Commands`: `Dictionary<string, Func<XmlNode, ICommand>>` keyed by the lowercased `Type` string (`"waypoint"`, `"research"`, `"design"`, `"production"`, `"renamefleet"`). A parallel `Func<JToken, ICommand>` map is added when native JSON lands (M7, §6.2).
- Each command self-registers via a `[Command("waypoint")]` attribute discovered by a one-time reflection scan at startup, which is what makes a community/plug-in AI assembly pluggable (drop the DLL, it registers its own command types). Explicit static registration is the fallback.
- `OrderReader.ReadPlayerTurn`, `OrderWriter` round-trip validation, and the `ClientData` XML constructor all call `CommandRegistry.Create(type, node)` instead of switching. The `<remarks>` warning on `ICommand.cs` ("OrderReader must be modified when new commands are added") becomes obsolete and is deleted.
- The registry gives the server a clean rejection path: an unknown `Type` becomes a structured `400` per-command error rather than the current silent `Report.Error` + skip.

Determinism note: the client never seeds the RNG. `ServerData.MasterSeed` and the derived per-turn and per-seat seeds (`hash(MasterSeed, turnYear)`, `hash(MasterSeed, turnYear, empireId)`) are server-side only; the client submits orders and reads the resulting per-empire intel. See the galaxies-turngen spec.

---

## 5. Part Two - Marketing Site (M4)

Ship first, static. The single most characteristic honest thing about this game is its simultaneous, secret, slow cadence: everyone plans in private, the whole galaxy resolves at once, then you wait for the next deadline. The site leads with that, not with "4X" or "space." See GALAXIES-CLOUD-DESIGN.md §G.2.

### 5.1 API subset the clients consume

The desktop `HttpTurnTransport` (M1) and the browser client (M7) call the same `/v1` surface; the full catalog is GALAXIES-CLOUD-DESIGN.md §E.2. The subset each transport needs:

| Method + path | Desktop | Browser | Purpose |
|---|---|---|---|
| `POST /auth/google` | yes | yes | exchange a Google ID token for a session JWT + refresh token |
| `POST /auth/refresh` / `POST /auth/logout` | yes | yes | rotate / revoke the session |
| `GET /me` | yes | yes | profile, owned/joined games |
| `GET /games?scope=mine\|open\|public` | yes | yes | lobby / "my games" |
| `POST /games`, `POST /games/{id}/join`, `POST /games/{id}/start` | yes | later | lobby actions (host + join) |
| `GET /games/{id}` / `GET /games/{id}/status` | yes | yes | summary, deadline, generation state, `?wait=` long-poll |
| `GET /games/{id}/intel` (+ `/{turnYear}`) | yes | yes | the caller's fog-of-war-correct intel |
| `PUT /games/{id}/orders` / `GET /games/{id}/orders` | yes | yes | draft write / read-back |
| `POST /games/{id}/orders/submit` / `DELETE /games/{id}/orders` | yes | yes | finalize / unsubmit |
| `GET /version` | yes | yes | protocol version + `minClientVersion` (upgrade gate) |

Content negotiation: the desktop client sends and accepts `application/vnd.nova.*+xml`; the browser client sends `Accept: application/json`. Same endpoints, same auth, same lobby, same deadlines, same command registry.

### 5.2 Design language (Hearthlight / Vigil)

Both the marketing site and the browser-client skin use the shared `tokens.css` semantic tokens, Fraunces for display, IBM Plex Sans for body, IBM Plex Mono for the `//` eyebrow labels, the Vigil warm-flame-on-cool-dark/snow palette, and the lamplight signature. WCAG 2.2 AA is the floor: contrast, `focus-visible`, target size, no keyboard traps. The Vigil flame accent is reserved for the single primary action per screen (on the site, "Sign in with Google to play"). The fixed dedication appears exactly once per property, in the footer, and must not be duplicated elsewhere.

### 5.3 Page outline (single scroll, anchored nav)

| Section | Eyebrow (`//`) | Content |
|---|---|---|
| Hero | `// a turn-based galaxy that moves once a day` | Headline (Fraunces): "Everyone plans in secret. The galaxy moves all at once. Then you wait." Subhead: "Galaxies is a free, play-by-email space strategy game. Design a species, settle the stars, and submit your orders before the deadline. Turns resolve when everyone is in, or when the clock runs out." Primary: "Sign in with Google to play." Secondary: "See how a turn works." Honest-limits line: "Slow on purpose. A game runs for in-game decades across real-life weeks. It is ad-supported and free, and it will stay that way." |
| How a turn works | `// plan, submit, wait` | Three steps: plan your orders, submit before the deadline, read what the galaxy did. States the async/deadline model plainly. |
| Depth callouts | `// more than it looks` | Design a species (racial traits); real fog of war (you see only what you scan); simultaneous combat resolution; victory on your terms (planets, tech, score). Grounded in real engine features, no hype. |
| Play with anyone | `// public or private` | Public games in the lobby, or invite friends by Gmail address; async, so time zones do not matter. |
| Bring your own brain | `// open AI contract` | Built-in C# AIs, community plug-ins, and LLM-driven players all plug into the same seat via the open AI-participant contract. A genuine differentiator, stated once. |
| Open and credited | `// built on Stars! Nova` | "Galaxies is built on the Stars! Nova engine, an open-source (GPL v2) reimagining of the classic Stars!. The engine stays open; the client source is public." Links to the repos. Credit the Stars! team by name and Stars! Nova prominently; keep every GPL v2 notice intact. See GALAXIES-CLOUD-DESIGN.md §G.4 (legal confirmation pending). |
| Support the lamp | `// keep the lights on` | The ads-plus-donations block (§5.5). |
| Footer | (none) | Nav, repo/source links, status-page link, privacy and account-deletion links, the lamplight signature, and the fixed dedication (once, here). |

### 5.4 Ad integration (AdSense placement rules)

Start with Google AdSense for the marketing site and low-traffic web surfaces (simplest approval and fill). Move to Google Ad Manager (GAM) only if and when volume justifies direct placement control, house ads, and frequency capping. AdSense first, GAM later. Ads load only when `_WEB_ADS_ENABLED` is on.

Placement rules (the UX floor, non-negotiable):

- Ads live on the marketing site, the lobby/browser, profile pages, and the game-over summary.
- The active game view (map, orders, combat) is an ad-free zone. Never overlay the star map, never sit between a player and the Submit button, never interstitial a turn submission.
- One well-placed unit beats three. Prefer a single in-flow unit on list pages and a footer/summary unit on the game-over screen.
- No autoplay audio, no full-screen interstitials on the game flow, no ads on error or account-deletion pages.
- Consent: serve a Google-certified Consent Management Platform (`_WEB_CMP_ID`) so EU/UK visitors get the legally required consent choice before personalized ads load. No AdSense script executes before the CMP resolves.

`ads.txt` in the site root records AdSense ownership. See GALAXIES-CLOUD-DESIGN.md §G.3.

### 5.5 Donations (reworded block)

Two channels per the brand: GitHub Sponsors and Cash App, presented as a quiet "support the lamp" block, never a modal, never gated content. The brand's old donate headline was built around "No ads." Since Galaxies now carries ads, that headline is false and is replaced:

- Headline: "The ads keep the servers on. Donations let me care less about the ads."
- Subline: "Galaxies is free and always will be. If it earns a place in your week, you can chip in. If not, play anyway."

No subscriptions, no on-site payment capture. `/support` links out to GitHub Sponsors and the Cash App cashtag. `.github/FUNDING.yml` is added by the implementer, not by this design pass.

### 5.6 Desktop client distribution and auto-update

The marketing site is the distribution point for the M1 desktop client (gated on `_WEB_DOWNLOAD_ENABLED`).

- **Installer.** A signed Windows installer, linked from the downloads section. Because the client is GPL, the modified client source is published publicly and linked alongside the binary (we owe the client source anyway; doing it openly removes ambiguity). See GALAXIES-CLOUD-DESIGN.md §G.4.
- **Auto-update feed.** An `appcast.json` served from `/downloads/appcast.json` (`_WEB_UPDATE_FEED_URL`), listing the current version, download URL, and release notes. On launch the client fetches the appcast, compares its build, and offers an in-app upgrade prompt.
- **Hard gate.** The soft appcast prompt is backed by the API's `minClientVersion`: a `426` on any call forces the upgrade before orders can be sent (§4.4, GALAXIES-CLOUD-DESIGN.md §E.5). This is what prevents a stale client from `PUT`ing orders the server can no longer parse.
- The specific installer/updater technology (MSI, Squirrel, ClickOnce, Velopack, ...) is a dev-team fork (§12).

---

## 6. Part Three - Browser Client (M7)

The browser client is, conceptually, a third `ITurnTransport` living in JavaScript: same `/v1` REST surface, same Google Identity auth, same envelope. It is an honest XL greenfield, because the roughly 35,000-line WinForms GUI does not port. Everything in §4 and §5.1 except the loopback OAuth flow and DPAPI storage is reused; the browser substitutes the standard web OAuth redirect and browser storage. Phase it: a read-mostly companion slice ships first, the renderers come after.

### 6.1 The third `ITurnTransport` in JS

`httpTurnTransport.js` implements the same three-call shape as the C# seam (`FetchIntel`, `GetStatus`, `SubmitOrders`) against `/v1`, sharing endpoints, auth, lobby, deadlines, history, and the command registry with the desktop client. It requests `Accept: application/json` to get native JSON DTOs (see §6.2); the desktop client keeps XML-in-field, so both clients coexist per request with no flag day.

### 6.2 Native JSON DTOs via content negotiation

Full native-JSON DTOs for the whole domain model are the large, error-prone surface deferred out of M1. They arrive here, selected by `Accept: application/json`:

- The server serializes `Intel` (`EmpireState`, `Messages`, `AllScores`, `AllMinefields`) and the `ICommand` set (Waypoint, Research, Design, Production, RenameFleet) to native JSON. The `System.Drawing` coupling inside `Intel.cs` that blocks a naive JSON serializer is resolved as part of this work (the decoupling task); the browser DTOs mirror the fields, not the `System.Drawing` types.
- The `CommandRegistry` gains its parallel `Func<JToken, ICommand>` map (§4.7), so a JSON order round-trips to a real `ICommand` and runs `IsValid` / `ApplyToState` server-side exactly as the XML path does. The JSON is a transport encoding, not an opaque blob the server trusts; turn year and empire id are cross-checked against the parsed body and the session, as `OrderReader` cross-checks today.
- The browser client re-implements the DTO shapes in `src/dto/`. This is a real, sizeable body of work; scope and ordering are a dev-team fork (§12).

### 6.3 FCM web push

Out-of-band nudges for the browser client (email stays the guaranteed channel for all Gmail-only users):

- The service worker (`service-worker.js`) registers for FCM using `_WEB_FCM_SENDER_ID` and the VAPID key, after first sign-in and only when `_WEB_PUSH_ENABLED` is on.
- The FCM token is sent to galaxies-api and stored against the user (a `POST /me/push-subscriptions`-style endpoint owned by the galaxies-api / galaxies-notifier boundary; confirm the exact path in the galaxies-notifier spec).
- galaxies-notifier fans out "your turn is ready" and "deadline approaching" from the `turn-generated` and `deadline-approaching` Pub/Sub topics. These push messages never carry game data; they only tell the client to poll `GET /status`. See GALAXIES-CLOUD-DESIGN.md §E.1.

### 6.4 The greenfield renderers (honest note)

The browser client re-implements, from scratch, three surfaces that exist today only as WinForms code and do not port:

- **Star-map renderer** (`render/starmap/`) - the galaxy view: stars, fleets, scan ranges, ownership, minefields, waypoints. Canvas/WebGL. XL.
- **Ship designer** (`render/designer/`) - hull slots, component fitting (including the mining-robot hull-slot fix already landed in the engine), design validation. XL.
- **Battle viewer** (`render/battle/`) - the turn's simultaneous-combat replay. XL.

Accessibility carries the AA floor into the app: visible focus rings on every interactive map control, a keyboard path to submit a turn, and non-color cues for ownership and alliance (the map cannot rely on hue alone). Data views (score graphs, standings) use the `dataviz` palette method so charts read as one system across light and dark and never encode meaning in color alone. The Vigil flame accent is reserved for the single primary action per screen, usually "Submit turn."

Phasing within M7: ship the read-mostly companion slice first (read your intel, read messages, submit simple orders such as research and production tweaks, and above all submit the turn), then build the renderers. Full map editing stays on the desktop client until the browser client matures. See GALAXIES-CLOUD-DESIGN.md §G.1 (companion web view) and §G.2 (web-app UI direction).

### 6.5 Browser auth and offline state

- **Auth.** `webOAuth.js` runs the standard Firebase Auth web redirect (Google/Gmail only), gets the Google ID token, `POST /auth/google` for the first-party JWT + rotating refresh token. Tokens live in browser session storage; the refresh token rotates as on desktop. No DPAPI, no loopback.
- **Offline order composition.** The desktop client persists the `Commands` stack to the local `<race>.state` file; the browser has no such file. The equivalent is IndexedDB (persist the in-progress `Commands` and last-fetched `Intel` locally so a dropped connection is "retry in a minute," not a lost turn). This is a dev-team fork (§12).

---

## 7. Data Model & Persistence

galaxies-web is read-mostly and owns almost no authoritative state. Authoritative game state lives in GCS (`roybot-galaxies-*` buckets, private) and the control plane is Firestore (native mode), one store for everything including accounts (`users/{google_sub}`); both are owned by galaxies-api and galaxies-turngen. Cloud SQL is not used. This surface persists only client-local state and speaks the shared wire envelope.

### 7.1 Firestore touchpoints (owned by galaxies-api, read via `/v1`)

galaxies-web never opens a Firestore connection. It reads the projections galaxies-api exposes over `/v1`:

- `users/{google_sub}` - profile (Google sub, display name, email, owned/joined games). Surfaced via `GET /me`. Account deletion is a galaxies-api concern (GALAXIES-CLOUD-DESIGN.md §G.5); the site links to it from the footer and privacy page.
- `games/*` - lobby state (forming/active/finished, seat list, cadence, visibility, deadline). Surfaced via `GET /games` and `GET /games/{id}`.
- Per-turn generation state and locks (`turnYear` + lock for exactly-once generation) are internal to galaxies-turngen; galaxies-web sees only `GET /status`.

### 7.2 The wire envelope DTO (shared contract)

The one contract this surface speaks. Reused verbatim across desktop (M1) and browser (M7); see GALAXIES-CLOUD-DESIGN.md §E.3.

```
GET /games/{id}/intel  ->  200
{
  "protocolVersion": "1",
  "gameId": "...",
  "turnYear": 2101,
  "empireId": 1,
  "contentType": "application/vnd.nova.intel+xml",   // or application/json (M7)
  "encoding": "gzip+base64",                          // "identity" for native JSON
  "body": "<base64 of gzipped <Intel>...</Intel>>"
}
```

`contentType` and `protocolVersion` are what let XML-in-field (desktop) and native JSON (browser) coexist per request. `GET /intel` is naturally cacheable per turn (immutable once resolved), so `ETag` / `If-None-Match` cut bandwidth on repeated polls. `PUT /orders` is keyed by `(gameId, empireId, turnYear)`, so retries are safe; an `ETag` + `If-Match` guards against two devices clobbering each other's draft.

### 7.3 Client-local persisted state

| Store | Client | What it holds |
|---|---|---|
| DPAPI token store | desktop | session JWT + rotating refresh token, per-user scope (§4.4) |
| `<race>.state` (`ClientStateExtension`) | desktop | the `Commands` stack and history between sessions; the durable offline draft |
| `runtime-config.json` | site + browser | build-stamped flags and endpoints (§3) |
| browser session storage | browser | session JWT + refresh token (§6.5) |
| IndexedDB | browser | in-progress `Commands` + last-fetched `Intel` for offline composition (fork, §12) |
| FCM subscription token | browser | registered with galaxies-api for push (§6.3) |

---

## 8. Endpoint & Route Catalog

galaxies-web serves no API of its own. Its "endpoints" are the `/v1` subset the clients consume (§5.1, and the full catalog in GALAXIES-CLOUD-DESIGN.md §E.2) plus the static routes it hosts.

### 8.1 Marketing site routes (M4)

| Path / anchor | Purpose |
|---|---|
| `/` (`#hero`, `#how-a-turn-works`, `#depth`, `#play-with-anyone`, `#open-ai`, `#built-on-stars-nova`, `#support`) | the single-scroll landing page (§5.3) |
| `/support` | the "support the lamp" donations page (§5.5) |
| `/privacy` | privacy note + account-deletion link (GALAXIES-CLOUD-DESIGN.md §G.5) |
| `/status` | honest status line or a link out to an uptime service |
| `/downloads/` + `/downloads/appcast.json` | desktop installer + auto-update feed (§5.6) |
| `/ads.txt` | AdSense ownership record |

### 8.2 Browser client routes (M7)

| Path | Purpose |
|---|---|
| `/play` | My Games (resume list, "waiting on you", deadline countdowns). Serves the honest "not ready" panel while `_WEB_BROWSER_CLIENT_ENABLED` is off. |
| `/play/g/{gameId}` | game view (map, reports, messages) |
| `/play/g/{gameId}/submit` | submit the turn (the single flame-accent primary action) |
| `/play/g/{gameId}/history` | fog-of-war-safe per-empire history scrubber |
| `/play/auth/callback` | web OAuth redirect landing (§6.5) |

---

## 9. Build Phases

Each step is small enough to ship and test on its own, mapped to its milestone.

**M1 - Desktop client adaptation** (engine repo):
1. Add `ICommandFactory` + `CommandRegistry`; convert `OrderReader.ReadPlayerTurn`, `OrderWriter` round-trip, and the `ClientData` XML constructor to `CommandRegistry.Create`; delete the obsolete `ICommand.cs` remark (§4.7).
2. Split `IntelReader.ReadIntel` into a `ReadIntel(XmlDocument)` overload + a file opener (§4.3).
3. Split `OrderWriter.WriteOrders` into `BuildOrdersXml()` + sink (§4.3).
4. Introduce `ITurnTransport`; implement `FileTurnTransport` over today's shared folder; prove hotseat is byte-for-byte unchanged (§4.1, §4.2).
5. Generalize `ClientData` from a filesystem path to an injected `ITurnTransport` + `gameId` (§4.1).
6. Implement `HttpTurnTransport` against the `/v1` envelope (§4.2, §7.2).
7. Loopback + PKCE OAuth flow + `DpapiTokenStore` (§4.4).
8. Replace the 2.5s timer with the 60-second `GetStatus()` poll (plus focus/manual/long-poll) (§4.5).
9. `GET /version` / `minClientVersion` gate + upgrade prompt; `GALAXIES_API_BASE_URL` self-host config (§4.4, §5.6).

**M4 - Marketing site** (`galaxies-web/marketing/`):
1. `tokens.css`, self-hosted Fraunces / IBM Plex Sans / IBM Plex Mono, the page shell, AA baseline (§5.2).
2. The single-scroll page: hero, how-a-turn-works, depth, play-with-anyone, open-AI, built-on-Stars!-Nova credit, support, footer with the lamplight signature and the fixed dedication once (§5.3).
3. AdSense integration behind the CMP consent gate + `ads.txt` (§5.4).
4. Downloads section + `appcast.json` auto-update feed (§5.6).
5. Sign-in handoff (Firebase Auth web flow), gated on `_WEB_SIGNIN_ENABLED` (§3).
6. `/support`, `/privacy`, `/status` pages (§8.1).
7. WCAG 2.2 AA audit (axe + manual keyboard pass).

**M7 - Browser client** (`galaxies-web/browser-client/`):
1. JS `httpTurnTransport.js` on `/v1` with `Accept: application/json` (§6.1).
2. Native JSON DTOs for `Intel` + the `ICommand` set behind content negotiation; resolve the `System.Drawing` coupling; add the `CommandRegistry` JSON map (§6.2, §4.7).
3. Web OAuth redirect + session storage + IndexedDB offline draft (§6.5).
4. FCM web push + service worker (§6.3).
5. The read-mostly companion slice (read intel/messages, submit simple orders, submit turn) (§6.4).
6. Greenfield star-map renderer (§6.4).
7. Greenfield ship designer (§6.4).
8. Greenfield battle viewer (§6.4).
9. AA pass + `dataviz` score graphs and standings (§6.4).

---

## 10. Rollout (ships dark)

The marketing site and browser client are Firebase Hosting; the desktop client is a distributed binary whose cloud path is gated by galaxies-api. Everything ships dark. Staged flips, each with a copy-paste smoke using pinned `roybot` values.

### §0 - Set these once per shell

```bash
gcloud config set project roybot
firebase use roybot
export REGION=us-central1
export WEB=https://galaxies.<domain>            # marketing site (Firebase Hosting)
export APP=https://galaxies.<domain>/play       # browser client (M7)
export API=https://api.galaxies.<domain>/v1     # galaxies-api (Cloud Run)
```

### §1 - First deploy (ordered), all flags OFF

Deploy order: build the static site to a preview channel, verify, then hold at the holding page. The desktop client build ships to the downloads area but stays behind `_WEB_DOWNLOAD_ENABLED`.

```bash
# Build + deploy the static site to a preview channel (does NOT touch the live domain):
gcloud builds submit galaxies-web --config=galaxies-web/cloudbuild.yaml \
  --substitutions=_WEB_SITE_ENABLED=false,_WEB_SIGNIN_ENABLED=false,_WEB_ADS_ENABLED=false,_WEB_DOWNLOAD_ENABLED=false,_WEB_BROWSER_CLIENT_ENABLED=false,_WEB_PUSH_ENABLED=false
# The cloudbuild deploys to a preview channel and prints a preview URL. Smoke the preview:
curl -sS -o /dev/null -w "%{http_code}\n" "<preview-url>"          # expect 200
# Confirm runtime-config was stamped with every flag OFF:
curl -sS "<preview-url>/runtime-config.json" | jq '{site,signin,ads,download,browser,push}'
# expect all false
```

Verify the live domain still serves only the holding page while `_WEB_SITE_ENABLED=false`.

### §2 - Staged flips (each a substitution + redeploy)

Set a substitution on the galaxies-web trigger and redeploy for a durable change. Flip in this order.

**Flip 1 - site live.** `_WEB_SITE_ENABLED=true` → promote the built site to the live channel.

```bash
curl -sS -o /dev/null -w "%{http_code}\n" "$WEB"                    # expect 200
curl -sS "$WEB" | grep -o "Everyone plans in secret" | head -1      # hero headline present
curl -sS "$WEB" | grep -oc "the fixed dedication line" || true      # dedication appears exactly once (in the footer)
```

**Flip 2 - ads.** `_WEB_ADS_ENABLED=true`. The AdSense script must load only after the CMP resolves, and never on the game flow.

```bash
curl -sS "$WEB/ads.txt"                                             # ownership record present
curl -sS "$WEB" | grep -o "adsbygoogle" | head -1                  # ad unit markup present on the marketing page
# Manual: EU/UK locale shows the CMP before any personalized ad loads; the active game view stays ad-free.
```

**Flip 3 - downloads + auto-update.** `_WEB_DOWNLOAD_ENABLED=true`.

```bash
curl -sS -o /dev/null -w "%{http_code}\n" "$WEB/downloads/appcast.json"   # expect 200
curl -sS "$WEB/downloads/appcast.json" | jq '{version, url, minClientVersion}'
# Manual: the installer link resolves; the linked client source repo is reachable (GPL obligation, §5.6).
```

**Flip 4 - sign-in.** `_WEB_SIGNIN_ENABLED=true` (requires galaxies-api `_API_ENABLED=true` to be useful).

```bash
# Confirm the API the button targets is live (not dark):
curl -sS "$API/version" | jq '{protocolVersion, minClientVersion}'   # not {"disabled":true}
# Manual: "Sign in with Google to play" runs the Firebase Auth web flow and hands off to /play (or the download path pre-M7).
```

**Flip 5 - browser client (M7).** `_WEB_BROWSER_CLIENT_ENABLED=true`, then `_WEB_PUSH_ENABLED=true`.

```bash
curl -sS -o /dev/null -w "%{http_code}\n" "$APP"                    # expect 200 (SPA shell)
# Manual: sign in, land on My Games; a game with a new turn fetches intel via Accept: application/json;
#   submit a simple order + submit turn; grant push and confirm a "turn ready" web push arrives on the next generation.
```

### §3 - Desktop client cloud-mode gate (M1)

The desktop client ships independently of the flags above. Its hotseat path is always live; its cloud path is gated by galaxies-api.

```bash
# Hotseat smoke (no server): launch the client on a local shared folder, run a hotseat game,
#   confirm FileTurnTransport advances a turn locally (2100 -> 2101) with no network.

# Cloud smoke while galaxies-api is dark: point the client at $API and open a game.
#   HttpTurnTransport GET /status returns {"disabled":true}; the client shows
#   "cloud play is not available yet" and hotseat still works.

# Cloud smoke once galaxies-api is live: GET /version passes the minClientVersion gate;
#   a new turn fetches intel, and PUT /orders + POST /orders/submit round-trips.
```

### §4 - Kill switches / rollback

Flip any `_WEB_*` substitution back to `false` and redeploy: the marketing page falls back to the holding page (site), ads stop loading, downloads and appcast delink, sign-in greys to the waitlist, and `/play` serves the honest "not ready" panel. Firebase Hosting keeps prior releases, so `firebase hosting:rollback` restores the last good deploy instantly. The desktop client's cloud path pauses automatically the moment galaxies-api goes dark; hotseat is unaffected.

---

## 11. Testing

Three layers, mirrored across the three parts.

**Unit**
- `CommandRegistry` create/round-trip for every `Type` (`waypoint`, `research`, `design`, `production`, `renamefleet`); unknown `Type` yields a structured error (§4.7).
- Envelope encode/decode: gzip+base64 XML round-trips byte-for-byte; `contentType` / `protocolVersion` honored (§7.2).
- `IntelReader.ReadIntel(XmlDocument)` and `OrderWriter.BuildOrdersXml()` produce identical output to the pre-split file paths (§4.3).
- `DpapiTokenStore` protect/unprotect round-trip, per-user scope, refresh-token rotation rewrite (§4.4).
- 60-second poll loop: `status.turnYear` advance triggers `FetchIntel`; same turn year is a no-op; long-poll early return (§4.5).

**Integration**
- `HttpTurnTransport` against a mock `/v1`: `FetchIntel` / `GetStatus` / `SubmitOrders`; `409` on wrong turn year, `403` on empire mismatch, `426` on version mismatch (§4.3, §4.4).
- Parity: `FileTurnTransport` and `HttpTurnTransport` produce the same in-memory `ClientData` from the same turn (proves the split is clean).
- Loopback + PKCE flow against a mock Google/Firebase Auth: code capture on the ephemeral port, token exchange, `POST /auth/google` (§4.4).
- Content negotiation: the same order round-trips via `application/vnd.nova.orders+xml` (desktop) and `application/json` (browser) to the same server-side `ICommand` with `IsValid` passing (§6.2).

**Site + browser + e2e**
- Static build validity: `runtime-config.json` reflects the flags; `ads.txt` present when ads on; `appcast.json` schema valid; every internal link resolves.
- WCAG 2.2 AA: axe on every marketing route and browser route; a keyboard-only path to submit a turn; non-color ownership/alliance cues; contrast in light and dark.
- The dedication appears exactly once (footer) across the whole property; the CMP gates AdSense in an EU/UK locale; the active game view carries no ad markup.
- FCM: service-worker registration, token POST to `/v1`, a "turn ready" push on a staged generation (never carrying game data).
- Hotseat e2e: a local `FileTurnTransport` game advances a turn. Cloud e2e against a staging game: sign in, fetch intel, `PUT` a draft, `submit`, poll until the next turn generates.

---

## 12. Open Questions

Tracked in `galaxies-web/questions.md`; resolve before the first code pass.

- **Installer/updater technology.** MSI, Squirrel, ClickOnce, or Velopack for the Windows client? Choice drives the `appcast.json` schema and signing. Whichever wins must not break the GPL source-offer obligation (§5.6, GALAXIES-CLOUD-DESIGN.md §G.4).
- **Downloads hosting.** Serve installers directly from Firebase Hosting, or a dedicated public downloads bucket (never one of the three private `roybot-galaxies-*` state buckets)? Decision hinges on binary size and Hosting quota.
- **One Firebase site or two.** Marketing site and browser client on one site (`/play` path) or split to `app.galaxies.<domain>`? A split simplifies cache and CSP rules for the SPA but adds a subdomain and an auth redirect origin.
- **Native JSON DTO scope and order (M7).** Which entities move to native JSON first, and does the `System.Drawing` decoupling in `Intel.cs` land as one task or incrementally? This is the highest-risk M7 fork (§6.2).
- **Browser offline draft.** IndexedDB shape for the in-progress `Commands` stack + last-fetched `Intel`, and the conflict rule against the server `ETag` when two devices edit one draft (§6.5, §7.2).
- **Push endpoint ownership.** Confirm whether the FCM token registration path is `POST /me/push-subscriptions` on galaxies-api or a galaxies-notifier route (§6.3).
- **CMP vendor and AdSense-to-GAM timing.** Which Google-certified CMP, and the traffic threshold that justifies moving from AdSense to Ad Manager (§5.4).
- **Desktop poll default.** Ship the 60-second poll, or default to `?wait=` long-poll where the network allows and fall back to polling? (§4.5)
- **PWA installability.** Does the browser client ship as an installable PWA (adds an offline story and a home-screen entry) or a plain SPA first? (§6)
- **Ratings placement.** The Glicko-2/Elo "for fun" rating (GALAXIES-CLOUD-DESIGN.md §G.1) is computed server-side on game end; confirm whether profile pages render it in M4 or M7.
- **Localization.** The marketing copy and the client strings are English-only in v1; decide whether the token/typography stack reserves a localization path now.

---

*Galaxies - Internal*