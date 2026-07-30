# Galaxies product analytics and KPIs

Status: **nothing here is built.** There is no analytics subscriber, no
BigQuery dataset, no dashboard, no rollup job, and of the five events this
document depends on only one has a publisher in code, with nothing deployed to
publish it to. The main design document names
"analytics" as a Pub/Sub consumer and then never builds it, and lists the
analytics and KPI pipeline as an open gap. This document is the specification
for closing it, written before the code so the metric definitions are argued
about once rather than discovered later in a query.

House rules apply: plain, direct, no em dashes, honest about limits.

## 1. Why this matters, stated without flattery

Galaxies is free and ad-supported. The owner has approved standard ads; that is
settled and is not relitigated here. What follows from it is arithmetic: ad
revenue is impressions times rate, impressions are people times sessions, and
the only levers on that are **how many people arrive, how many of them start a
game, and how long they keep coming back.**

That is the entire commercial model. It means three unglamorous numbers decide
whether the servers stay on: signups, the share of signups who actually start a
game, and retention. Everything else on this page is either an input to those
or a check that the game itself is healthy.

It also means the ad-free zones are a constraint we are choosing, not an
oversight to be optimized away later. The active game view (map, orders,
combat) carries no ads and no ad measurement, permanently. Ads live on the
marketing site, the lobby and game browser, profile pages, and the game-over
summary. Any analytics design that starts eyeing the game view for inventory
has misunderstood the product.

## 2. DAU is the wrong headline metric, and pretending otherwise will mislead you

The standard free-to-play dashboard leads with daily active users. For Galaxies
that number will look bad and the badness will mean nothing.

Galaxies is asynchronous. A game runs on a cadence measured in days (the
per-game "maximum time between turns"), across in-game decades and real-life
weeks. A perfectly healthy, deeply engaged player opens the game once a day,
spends ten minutes, submits orders, and leaves. A player in a three-day-cadence
game might open it twice a week and be having a wonderful time.

So:

- **The primary activity unit is the submitted turn, not the session.** A
  session with no submitted turn is a person who looked at the map and did not
  play.
- **The primary activity window is the week, not the day.** Report weekly
  active players (WAP) as the headline, with daily as a secondary series that
  is expected to be a small fraction of it and is not a problem.
- **"Active" means submitted at least one turn in the window.** Not signed in,
  not loaded the client. Submitted.

Track DAU anyway, because ad impressions are a daily phenomenon and the ad
business genuinely runs on daily numbers. Just do not put it at the top of the
page and do not manage against it, or you will end up designing nudges to make
an asynchronous game behave like a synchronous one, which is the one change
that would break the thing people came for.

## 3. The KPIs that actually matter

Seven metrics. The list is deliberately short; a dashboard with forty numbers
gets read as decoration.

### 3.1 Signups

**Definition:** distinct new accounts created per day and per week.

**Why:** the top of everything. Sign-in is Google-only, so a signup is
unambiguous; there are no anonymous or partial accounts to reconcile.

**Source:** a new `account-created` event. **This does not exist.** See section
5.

**Watch for:** signups are the metric most vulnerable to a good week of
referral traffic making everything look healthy while the front door is still
broken. Always read it next to 3.2, never alone.

### 3.2 First-game-started rate

**Definition:** of accounts created in a period, the share that join or create
a game and submit at least one turn within 24 hours; reported again at 7 days.

**Why:** this is the front-door metric and the most important number in this
document. The known problem is that the site says "Sign in with Google to play"
and then drops a new player into a 1990s-complexity 4X with no tutorial. If
this number is low, nothing downstream can be fixed, because there is no
downstream. It is the metric the solo-versus-AI quick start exists to move (see
ONBOARDING-SOLO-VS-AI.md).

**Source:** `account-created` joined to `game-created` (or a join event) and an
`orders-submitted` event, cohorted by signup day. None of those three has a
publisher: two are not defined anywhere, and `game-created` is a declared topic
nothing writes to.

**Note the two-stage split.** Report "started a game" and "submitted a first
turn" as separate numbers. They fail for different reasons: the gap between
signup and starting is a lobby and choice-paralysis problem, and the gap
between starting and submitting is a "I do not understand what to do with this
star map" problem. Collapsing them into one number hides which one you have.

### 3.3 Turn submission rate against deadline

**Definition:** of human seats that were open for orders when a turn generated,
the share that had submitted before the deadline. Reported per turn, rolled up
per game and service-wide.

**Why:** this is the health metric for an asynchronous game, and it is the one
Galaxies has that a normal game does not. A game where seats stop submitting is
dying from the inside while still looking active on a lobby list. It is also
the leading indicator for the missed-turn ladder escalating seats to AI, which
is a real product harm (a human's empire quietly becomes a bot).

**Source:** `deadline-approaching` carries `gameId`, `turnYear`, `hoursRemaining`,
and the unsubmitted `empireIds`; `turn-generated` carries `empireIds`,
`aiEmpireIds`, and `handoffs`. Counting unsubmitted seats at the last
`deadline-approaching` fire against total human seats gives a usable
approximation, and `handoffs` gives the escalations directly. Neither is
available yet: `deadline-approaching` is a declared topic with no publisher.

**Honest limit:** the approximation is coarse. Submission timing (do people
submit early or in the last hour?) needs an `orders-submitted` event with a
timestamp, and that is genuinely useful for setting sensible default cadences.
Ship the approximation first; it answers the important question, which is
whether the number is falling.

**Companion metric:** the AI-handoff rate, meaning seats per week converted to
AI by the missed-turn ladder. A rising handoff rate is submission collapse that
has already become irreversible for those players.

### 3.4 Games reaching a victory condition

**Definition:** of games that reached `Active`, the share that reach `Finished`
via a victory condition, versus `Cancelled` or abandoned. Report as a cohort by
game start month, because a game takes weeks and an in-period ratio will lie.

**Why:** a finished game is the product working. It is also, bluntly, where the
game-over summary ad impression lives, and it is the strongest signal that a
player will start another game. Abandonment is the dominant failure mode of
play-by-email 4X games and always has been; measuring it is how you find out
whether Galaxies has solved anything or has just moved the old problem to new
infrastructure.

**Source:** `game-created` and `turn-generated` with `gameEnded` true.
`turn-generated` exists in code; `game-created` is a declared topic with no
publisher. **No KPI on this page is computable today**, because nothing is
deployed and half the events have no publisher. This is the KPI that needs the
least new work: it becomes computable as soon as `game-created` has a publisher
and the pipeline exists, with no new event types invented.

**Report alongside:** median turns per game, and median real-world days from
start to finish. A high completion rate on games that finish in four turns is
not the good news it looks like.

### 3.5 Seven and thirty day retention

**Definition:** of accounts created in a week, the share that submit at least
one turn in the seven-day window starting on day 7, and again in the window
starting on day 30. Activity means a submitted turn.

**Why:** ad revenue is retention times time. A player who stays for three games
is worth many times one who plays half of one, and no amount of acquisition
fixes a leaky retention curve.

**Source:** this one has a genuine tension with the privacy floor, and it is
worth being explicit about how it is resolved rather than quietly doing the
convenient thing.

Retention is inherently a per-person measurement, and section 6 forbids
per-person identifiers in analytics. The resolution is **to compute retention
where identity legitimately already lives, and export only the answer.** A
daily aggregation job runs inside the control-plane boundary, reads Firestore
(`users/{google_sub}` and the member documents, which lawfully hold identity
because the product cannot function otherwise), computes the cohort counts, and
writes **only counts** to the analytics store: cohort week, day offset, number
retained, number in cohort. The analytics store never receives an account
identifier, a pseudonymous player key, or a row that describes one person.

This is slightly more work than piping user ids into BigQuery and it is the
right call. It means the analytics dataset holds nothing that needs deleting
when an account is deleted, which is a real operational simplification and not
just a principle (see section 6).

### 3.6 AI seat usage

**Definition:** three numbers, not one.

- Share of active games that contain at least one AI seat.
- Share of all seats service-wide that are AI.
- Count of solo games, meaning one human and all other seats AI.

**Why:** AI seats are load-bearing for the product in three separate ways, and
this metric tells you which one is actually happening. They fill empty lobby
slots so a game can start without waiting for strangers; they take over
abandoned seats so one quitter does not kill a game for five other people; and
they are the entire solo-versus-AI onboarding path. They also cost real compute
that produces no ad impressions, so the number has a cost side too.

**Source:** `turn-generated` carries `empireIds`, `aiEmpireIds`, and `handoffs`,
so the seat ratios and handoff counts need no new event shape, only a deployed
pipeline to read them. Distinguishing
lobby-filled AI from onboarding solo games needs `game-created` to carry a
creation-origin field (quick start, lobby, invite), which is a small addition
worth making before the quick start ships.

**Watch for:** if AI seats grow as a share of all seats while human WAP is flat
or falling, the service is quietly becoming bots playing bots. That is a
striking failure to detect early and an embarrassing one to detect late.

### 3.7 Ad revenue per active player

**Definition:** total ad revenue for a period, divided by weekly active players
for the same period. Report weekly. Also report the daily version for the ad
business, and revenue per completed game.

**Why:** the sustainability number. It answers "does another thousand players
pay for another thousand players' compute", which is the only question that
decides whether this stays free.

**Source:** ad network reporting for the numerator, the WAP rollup for the
denominator. **These two are joined at the aggregate level only, by time
period.** There is no per-person join, no user-level revenue attribution, and
no ad identifier flowing into the product analytics store. That is a deliberate
limit; it costs the ability to segment revenue by player type and it buys a
dataset that cannot be turned into a profile.

**Honest limit:** this metric is coarse and will stay coarse. It cannot tell
you which surface earned the money without separate per-placement reporting
from the ad network, and it cannot distinguish a high-value returning player
from a drive-by. That is acceptable, because the decision it informs (are we
above or below cost) does not need precision.

**Pair it with cost per active player.** Cloud Run generation, GCS, Firestore,
and egress, divided by the same denominator. Revenue per player is only
meaningful next to what a player costs, and the AI seats put real compute on
the cost side that the revenue side never sees.

## 4. What "good" looks like, with the honesty label attached

Numbers in this table are **priors, not targets.** There is no data behind
them; they are drawn from general expectations for niche strategy games and are
written down only so the first real measurement has something to be surprising
against. Replace every one of them with a measurement as soon as you have one,
and do not let anyone quote this table as a goal in the meantime.

| Metric | Rough prior | Would worry me below |
|---|---|---|
| First game started within 24h of signup | 40 percent | 20 percent |
| First turn submitted within 24h of signup | 30 percent | 15 percent |
| Turn submission rate against deadline | 85 percent | 70 percent |
| Games reaching a victory condition | 25 percent | 10 percent |
| Day 7 retention | 25 percent | 12 percent |
| Day 30 retention | 12 percent | 5 percent |
| AI share of all seats | 30 percent | above 60 percent |

The most likely outcome is that the first real numbers come in well below the
priors and that first-game-started is the worst of them. That is the expected
shape of the problem, and it is exactly why the onboarding work exists.

## 5. Where the data comes from, and what is missing

### The events that are declared

The terraform declares three Pub/Sub topics (`infra/terraform/m2_clock.tf`),
none of them deployed. Only `turn-generated` has a publisher in code
(`ControlPlane/Eventing/TurnEventPublisher.cs`); `game-created` and
`deadline-approaching` are declared topics with no publisher yet.

| Topic | Publisher | Intended payload | Analytics use |
|---|---|---|---|
| `game-created` | None yet; the topic is declared and nothing writes to it | `gameId`, players, settings summary | Games started, cohort denominators, cadence distribution |
| `turn-generated` | `ControlPlane/Eventing/TurnEventPublisher.cs`, on the generation commit path | `gameId`, `turnYear`, `empireIds`, `aiEmpireIds`, `gameEnded`, `handoffs` | Turn volume, completion, AI seat share, handoff rate |
| `deadline-approaching` | None yet; the topic is declared and nothing writes to it | `gameId`, `turnYear`, `hoursRemaining`, unsubmitted `empireIds` | Submission rate approximation, at-risk seats |

Note the locked field shape on `turn-generated`: the year field is `turnYear`,
not `newTurnYear`. The design document's older text says otherwise; the code in
`ControlPlane/Eventing/TurnEventPublisher.cs` is correct and the analytics
schema follows the code.

### The events that do not exist and must be added

Being blunt about this, because it is the practical finding of this document:
**none of the seven KPIs is computable today.** "Games reaching a victory
condition" is the closest, and it needs only a publisher for `game-created`
plus the pipeline. The rest need two genuinely new events.

1. **`account-created`.** Published by `galaxies-api` on first successful
   Google sign-in that creates a `users/{google_sub}` document. Payload:
   timestamp, and a coarse acquisition source if one is known (organic, direct,
   referral domain class). **No `google_sub`, no email.** Without this there is
   no signup metric and no cohort to measure first-game-started or retention
   against.

2. **`orders-submitted`.** Published on a successful order submission. Payload:
   `gameId`, `turnYear`, seat kind (human or AI), seconds remaining before the
   deadline, and whether this replaced a previous submission for the same turn.
   **No `empireId`, no order content.** This gives an exact submission rate
   instead of an approximation, gives submission timing (which informs default
   cadence), and completes the first-turn half of the front-door metric.

Two additions to existing events are also worth making:

- `game-created` should carry a **creation origin** (`quickstart`, `lobby`,
  `invite`) so solo onboarding games are distinguishable from real multiplayer
  games. Without it, the quick start will pollute every game-level metric it
  touches and you will not be able to tell.
- `game-created` should carry the **cadence** and **seat composition**
  (human count, AI count) as a settings summary, so completion and submission
  rates can be segmented by how fast the game runs. The hypothesis that faster
  cadence means better completion is worth being able to test.

### The pipeline

Keep it small. This is a low-volume system; a few events per game per turn is
not a data engineering problem and should not be built like one.

- A **Pub/Sub BigQuery subscription** per topic, writing raw events into a
  `galaxies_analytics` dataset. No collector service, no code to maintain, no
  service to page anyone about at 3am.
- A **daily scheduled query** producing the rollup tables the dashboard reads:
  daily and weekly actives, cohort retention (fed by the aggregation job in
  3.5), submission rates, game funnels.
- **90-day retention on raw event tables, indefinite on rollups.** Rollups are
  tiny and aggregate; raw events are the only place with any granularity worth
  aging out.
- One dashboard, seven numbers, plus a small set of segment breakdowns. If it
  needs a second page it has stopped being a KPI dashboard.
- Every topic keeps a dead-letter subscription. **Analytics must never be able
  to block or slow a turn generation.** If the analytics consumer is broken,
  turns still generate and events pile up, and that trade is not negotiable.

## 6. The privacy floor

These are constraints on the analytics system, not aspirations, and they hold
regardless of what a future dashboard request would find convenient.

**No game content, ever.** No fleet positions, no ship or race designs, no
production queues, no battle details, no diplomacy or message text, no star or
empire names, no map data, no order contents. An analytics event carries counts
and identifiers of games and turns, never anything about what happened inside
the galaxy. This is partly privacy and partly integrity: a fog-of-war game
whose telemetry describes the board has built a leak, and the per-empire intel
authorization work in the API is undone by one careless event payload.

**No per-empire data.** Events may carry a count of seats or an array of
empire ids where the system needs it for dispatch, but the **analytics** store
holds no per-empire rows. Aggregate at or before the boundary. An empire id
plus a game id plus a timestamp is a behavioral record about one identifiable
player, and it is not needed to compute any metric on this page.

**Aggregate only.** The analytics dataset holds counts, rates, and cohort
totals. It holds no `google_sub`, no email address, no display name, no IP
address, and no pseudonymous player key. Per-person computations that genuinely
require identity (retention) happen inside the control plane and export only
their results, as described in 3.5.

**A clean consequence worth naming.** Because the analytics store holds no
identifiers, deleting an account requires no analytics deletion. The DSAR and
account-deletion path (`DELETE /v1/account`) touches Firestore and the object
stores, and the analytics dataset is simply out of scope, permanently and by
construction. That is a large operational simplification obtained by declining
to collect something we did not need, which is usually how those work.

**The game view stays clean.** No ad tags and no analytics beacons on the map,
orders, or combat views. Product events for those surfaces are published
server-side from the API and the generation path, not from a client-side
tracker in the game UI. The ad-free zone is ad-free for measurement too;
otherwise it is a technicality rather than a promise.

**Ads and product analytics are separate systems.** Ad personalization is
governed by the consent platform on the ad-carrying surfaces. Product analytics
as specified here is aggregate and non-identifying and is a different thing
with a different lawful basis. Do not blur them in the privacy policy, and do
not let the ad stack become a back door into the product dataset. Where the two
meet is section 3.7, and they meet as two aggregate totals divided by each
other, nothing more.

**No autoplay audio, no interstitials on the game flow, no ads on error pages
or the account-deletion flow.** Listed here because these are the places where
an analytics-driven optimization would eventually suggest putting them, and the
answer is settled.

## 7. What to build, in order

1. `account-created` and `orders-submitted` events. Nothing about the front
   door is measurable without them, and the front door is the problem.
2. The BigQuery subscriptions and the `galaxies_analytics` dataset. One
   afternoon of Terraform.
3. The creation-origin and settings-summary fields on `game-created`, before
   the quick start ships rather than after.
4. The daily rollup query and the seven-number dashboard.
5. The in-control-plane retention aggregation job.
6. Cost per active player, from billing export, next to revenue per active
   player.

Until step 1 is done, the honest statement about Galaxies' product performance
is that we do not know it. Not that it is unproven, not that it is early: we do
not know. That is the situation this document is written to end.
