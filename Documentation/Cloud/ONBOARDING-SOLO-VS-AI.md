# First-run onboarding: the solo-versus-AI quick start

Status: **specification, not built.** The endpoint does not exist, the preset
does not exist, and the path depends on Milestone 3 AI seats being live. It
also depends on one engine gap that is easy to miss and is called out plainly
in section 7. This document defines what to build and what has to be true first.

House rules apply: plain, direct, no em dashes, honest about limits.

## 1. The problem, stated without softening

Today the front door reads "Sign in with Google to play". A person clicks it,
authorizes an account, and arrives at a lobby. From there, if everything works,
they get to configure or join a game of a 1990s-complexity 4X: a galaxy of
stars, a race with a dozen tunable traits, planets with mineral concentrations
and terraforming curves, a ship designer, production queues, fuel and cargo
mechanics, and a battle system with movement orders and initiative.

There is no tutorial. There is no one-click game. And in the default
multiplayer case, there is a lobby to fill with strangers and then a day of
waiting before the first turn resolves.

So the first session of Galaxies currently looks like this: sign in, face a
wall of complexity with no guidance, and then wait until tomorrow to find out
whether anything you did mattered. **Retention dies at the front door**, and it
dies before the product has shown anyone the thing that is actually good about
it.

The thing that is actually good about it is a specific feeling: everyone plans
in secret, the galaxy resolves all at once, and then you read what happened.
That loop is the product. A new player has to feel it once, quickly, or they
will never come back to feel it slowly.

The quick start exists to deliver that one feeling inside the first session.
It is not a tutorial, and it does not make Galaxies simple. It makes the first
session **finishable**.

## 2. What the quick start is

One button, on the marketing site and in the lobby, that says what happens:

> **Play a solo game against the AI**
> A small galaxy, three AI opponents, and your first turn resolves in about a
> minute. No setup.

Pressing it (after Google sign-in, which stays required) creates and starts a
game and drops the player into their first turn. No wizard, no race designer,
no lobby, no waiting for strangers, no decisions before the game begins.

Every choice below exists to remove a decision from the first five minutes.
Each one is a real thing the player is giving up, and each one is recoverable
later.

### 2.1 The preset

| Setting | Value | Why |
|---|---|---|
| Galaxy size | Small and dense | Contact happens within a handful of turns. A sparse galaxy means the first ten turns are logistics with nothing to react to, which is the worst possible first impression. |
| Seats | 4: the player plus 3 AI | Enough for the galaxy to feel occupied; few enough that generation is fast and the map stays readable. |
| Race | A balanced default preset, pre-picked | The race designer is one of the best things in the game and it is a terrible first screen. It is a fifty-decision character sheet in front of someone who does not yet know what a mineral is. |
| AI participant | The built-in Nova AI (`DefaultAi` through the M3 participant contract) | It exists, it is the reference implementation, and it needs no external service. |
| AI difficulty | Normal | Easy invites a first game that teaches nothing. Brutal invites a first game that ends. |
| Victory conditions | Standard, and reachable | A solo game that cannot be won is a sandbox, and the completion metric depends on real games ending. |
| Master seed | Random per game, recorded | Every game is different, and every game is reproducible for debugging. |

**On the default race, specifically.** Pick one balanced generalist preset and
use it every time. Do not offer three. Do not offer a picker with "recommended"
next to one of them, because that is still a decision. The player who wants to
design a race will find the designer within their second game, and they will
enjoy it more once they know what the traits mean.

### 2.2 The cadence, which is the whole trick

This is the choice that makes the quick start work, and it is worth
understanding why it costs almost nothing to implement.

A normal Galaxies game runs on the per-game "maximum time between turns", which
is measured in hours or days. That cadence is correct for the product; it is
the thing that lets a game live in the corners of a real life. It is also
completely wrong for a first session, because a new player cannot wait a day to
learn whether their first decision was sensible.

The control plane already generates a turn early when everyone has submitted
(`Cadence.EveryoneSubmitted`). In a solo game, every seat except one is an AI,
and the AI seats submit within seconds of being asked. So:

- **The player submits, the AI seats submit, and the turn generates
  immediately.** In the normal case the player waits under a minute, not a day.
- `MaxTimeBetweenTurnsSeconds` is set to a **15 minute backstop**, not a day.
  This is purely a safety net: if an AI seat is slow or fails, the missed-turn
  ladder holds its orders and the turn generates anyway. A new player must
  never be able to get stuck waiting on a broken bot.

**No new clock code is required for this.** The early-generation path and the
missed-turn ladder both already exist. What is required is AI seats that
actually submit, which is Milestone 3, which is section 7.

The solo game therefore feels like a turn-based game with a "next turn" button,
while running on exactly the same asynchronous machinery as a three-day
play-by-email game. That is the correct architecture; do not build a separate
synchronous path for it.

### 2.3 It is a real game, and it stays

The solo game is not a sandbox that gets deleted when onboarding is over. It is
a real game in the real system, and if the player enjoys it they keep playing it
to a victory condition.

This is cheaper (no second game type, no throwaway state, no "graduate to a
real game" migration) and more honest (the player was not playing a demo while
being told it was a game). It also means a first session can produce a
completed game, which is the strongest predictor available that someone will
start a second one.

## 3. The first session, moment by moment

Three moments must land. If any one of them fails, the session fails, and the
order matters because each one earns the next.

### Moment 1: you submit your first orders

**Target: within five minutes of pressing the button.**

The player arrives at their home world with a small fleet and an empty
production queue, looking at a star map they do not understand. What must
happen here is that the game **tells them what to do first**, without a modal
and without a fourteen-step tour.

Ship a short, dismissible objective panel with exactly three tasks:

1. Send your scout to a nearby star.
2. Add a factory to your home world's production queue.
3. Submit your orders.

Three, not seven. Each one clickable in the sense that selecting the task
highlights the relevant thing on the map or in the panel. Each one written in
the game's own vocabulary, so the player is learning the real words for real
things rather than tutorial words that will not appear again.

This is deliberately **not a tutorial.** It does not explain minerals,
terraforming, ship design, or combat. It gets the player to a submitted turn.
Everything else can be learned later, and most of it will be learned by losing
something, which is how this genre has always taught.

**The "Submit your orders" control must be findable without instruction.** If
the player completes tasks 1 and 2 and cannot find task 3, everything upstream
of this was wasted. It is a primary button, in a fixed place, that says what it
does and stays visible.

**Honest failure mode:** a player who dismisses the panel and then does not know
what to do is a player we have lost, and some will. The panel should be
recoverable from somewhere obvious, and it should not reappear uninvited.

### Moment 2: you watch the galaxy resolve

**Target: under 60 seconds of waiting.**

The player submits. The AI seats submit. The turn generates. This is the
moment the product is actually about, and it deserves the honest presentation
rather than a spinner.

What must happen:

- **State clearly that the galaxy is resolving and that everyone's orders
  happen at once.** In words, briefly, once. This is the single mechanic that
  makes Galaxies different from every real-time strategy game the player has
  seen, and the first turn is when it costs the least to explain.
- **Show real progress, or show honest waiting.** No fake progress bar. If the
  turn takes eleven seconds, eleven seconds of "resolving turn 2401" is fine
  and better than a bar that lies.
- **Handle the slow case honestly.** Cloud Run cold starts are real; the
  turngen service scales to zero and runs one game per instance. If generation
  passes 30 seconds, say so plainly: "This is taking longer than usual. It will
  finish; you can leave this page and come back." Do not let a cold start read
  as a broken game.
- **Handle the failed case honestly too.** If generation errors, the player
  sees the errored-game message from the disaster recovery runbook, not a
  silent stall. A first-session failure that is explained is survivable; one
  that looks like the game hanging is not.
- **No ads, no interstitial, nothing between submit and result.** The active
  game view is a permanent ad-free zone and this is the most important sixty
  seconds in the product. An ad here would be the single most expensive
  impression the service ever sold.

### Moment 3: you read what changed

**Target: the player understands three specific things that happened.**

This is the moment most likely to be built badly, because the engine already
produces something that looks like the answer and is not.

Stars! Nova generates a message log: a long, flat list of engine-voiced
messages covering everything from "your scout arrived" to production details to
minutiae nobody reads. Presented raw to a new player it is a wall of text, and
the natural response to a wall of text is to close it. Then the player has
submitted a turn, watched the galaxy resolve, and learned nothing from it,
which means the loop did not close.

What to build instead, a **turn summary** that leads with consequence:

- **Lead with the three things that changed the player's position.** A new
  star scouted, a colony ship arrived, a rival's fleet spotted for the first
  time. Not the first three messages chronologically; the three with the
  largest effect on what the player can do next.
- **Group the rest by category**, collapsed by default: exploration,
  production, combat, contact. The full raw log stays available for the player
  who wants it, one click away and clearly labelled, because veterans of this
  genre genuinely do read every line.
- **Every summary line links to the thing on the map.** "Your scout reached
  Sirius" selects Sirius. Reading becomes navigation, which is how the player
  learns the map without being taught it.
- **Say what to do next, once.** One line at the end of the first turn's
  summary, pointing at the next obvious action. Then stop; this ends after the
  first two or three turns and does not become a permanent nag.

After moment 3, the player has felt the complete loop: plan in secret, resolve
all at once, read what happened. Everything the product is offering is now
something they have experienced rather than something they have been promised.

### The fourth moment, which is not the first session's job

Somewhere around turn five or ten, when the player has a working mental model,
they should be offered the real thing: a game with humans, on a real cadence.
That offer belongs in the lobby and the game-over summary, both of which are
ad-carrying surfaces, and it is not part of the first session. Do not interrupt
a working first session to sell the next one.

## 4. What this does not fix

Being clear about this, because the quick start will be over-credited if it
works at all and blamed for everything if it does not.

- **Galaxies is still a hard, deep 4X.** Nothing here makes ship design, race
  traits, or battle orders simpler. Most people who arrive will still leave. The
  goal is that they leave **after** seeing a turn resolve, rather than at the
  sign-in button, because those two groups differ enormously in how many of them
  come back.
- **The objective panel is not a tutorial.** It is three tasks. There is no
  written tutorial content, no explanation of the economy, and no guidance on
  combat. That work does not exist and is not scheduled. Calling the objective
  panel "onboarding complete" would be a mistake.
- **A first-session player will not understand the race they were given.** They
  are playing a preset chosen for them, and its traits will shape their game in
  ways they cannot see. That is an acceptable trade for removing a fifty-decision
  screen, and it is worth telling them plainly, once, in the summary: this is a
  balanced starter race, and you can design your own next time.
- **Normal-difficulty AI in a small galaxy will beat a first-time player.**
  Probably often. That is not a bug to tune away; losing the first game is
  normal for the genre. It is a reason to make the game-over summary generous
  and specific about what happened, since that surface is where a defeated
  first-timer decides whether to try again.
- **This does not create demand.** It converts arrivals. If nobody arrives at
  the marketing site, a better front door changes nothing.

## 5. Ads, sign-in, and the boundaries

**The quick start flow and the game view carry no ads.** Not the objective
panel, not the resolving screen, not the turn summary, not the map. Ads are
permitted on the marketing site, the lobby and game browser, profile pages, and
the game-over summary. No interstitials anywhere on the game flow, no autoplay
audio, and no ads on error pages.

**Sign-in stays first, and stays Google-only.** A try-before-signing-in demo is
tempting and would probably help the funnel. It is not recommended, because it
means an anonymous game-state path, an account-claim migration, and a second
authorization model, all to serve a session that ends at a sign-in wall anyway.
That is a large amount of surface for an uncertain gain.

What to do instead is cheap: **make the button honest about what comes next.**
"Sign in with Google, then you are in a game in about a minute." Do not promise
a tutorial that does not exist. Do not say "quick" without saying how quick.
The current copy's failure is not that it asks for sign-in, it is that it asks
for sign-in and then does not say what the sign-in buys.

**Cap the abuse.** A solo game with three AI seats costs real generation
compute and produces no ad impressions (the game view is ad-free by design). A
per-account quota on concurrent solo games, three is a reasonable start, keeps
a scripted account from becoming an expensive way to burn the compute budget.
Per-account quotas are already a Milestone 4 item; this is one of the concrete
reasons for them.

## 6. How we will know if it worked

One metric, defined in ANALYTICS-AND-KPIS.md section 3.2: **first-game-started
rate**, reported as its two separate halves.

- Share of new accounts that **start** a game within 24 hours.
- Share of new accounts that **submit a first turn** within 24 hours.

The two halves fail for different reasons and the quick start attacks both. If
starting improves and first-turn submission does not, the button worked and
moment 1 did not, which means the objective panel is wrong. If neither moves,
the problem is upstream of the front door entirely and is a traffic problem
wearing an onboarding costume.

Supporting numbers: time from signup to first submitted turn (the number the
whole design is trying to push under five minutes), median turns played in the
first session, and 7-day retention split by whether the first game was a quick
start or a lobby game. That last comparison is the one that says whether this
was worth building.

Instrumentation required before the quick start ships: the `account-created`
and `orders-submitted` events, and the creation-origin field on `game-created`
so quick-start games are distinguishable from real multiplayer games. All three
are unbuilt and specified in ANALYTICS-AND-KPIS.md section 5. **Shipping the
quick start without them means shipping a fix for the most important metric in
the product while being unable to tell whether it fixed anything.**

## 7. Dependencies, stated plainly

### This path requires Milestone 3 AI seats to be live

The quick start is a game against three AI opponents whose seats must submit
orders within seconds. That is exactly what Milestone 3 delivers: the Nova AI
extracted into a UI-free worker assembly, the participant act contract and host
adapter, and the `galaxies-ai` dispatch subscriber that fans out from
`turn-generated` and submits orders through the same authenticated channel
humans use.

Until that exists there are no AI seats, so there is no solo game, so there is
no quick start. **This is why AI came before the public launch gate.** The
milestone order (M3 AI, then M4 launch) was not sequencing preference; the
launch gate's onboarding requirement is unsatisfiable without M3, and launching
a marketing site that drives traffic into the current front door would spend
attention we will not get a second time.

If M3 slips, the correct response is to hold the launch, not to ship the
marketing site and hope. A good front door in front of a broken first session
converts strangers into people who have already decided the product is not for
them.

### The other dependency, which is easy to miss

**Server-side new-game map generation must be seeded.** The quick start creates
a game on the server, on demand, which means the server generates a galaxy.
Milestone 0 deliberately sidestepped this by working from a fixture, so the
four `new Random()` sites in `StarMapInitialiser`, plus the RNG in
`StarMapGenerator`, `NameGenerator`, `PointUtilities`, and `SpaceAllocator`,
are **not yet seeded from the master seed**.

That is fine while every game starts from a fixture. It is not fine for the
quick start, which cannot use a fixture, because every new player would get
the identical galaxy, and because an unseeded galaxy is not reproducible from
its master seed, which breaks debugging and any rollback that needs to
regenerate.

So the dependency list before the quick start can ship:

1. **M3 AI seats live**, with the built-in Nova participant submitting orders
   through the dispatch path.
2. **New-game map generation seeded** from `ServerData.MasterSeed`, so
   server-created galaxies are varied and reproducible.
3. **A quick-start creation path**, either `POST /v1/games/quickstart` or the
   existing `POST /v1/games` with a named preset, that creates the game, fills
   the AI seats, starts it, and returns the player straight into turn one.
4. **The turn summary view**, which is the largest piece of genuinely new
   client work here and the one most likely to be underestimated.
5. **The three analytics events**, so the result is measurable.
6. **A per-account concurrent solo game quota.**

Items 1 and 2 are engine and platform work with no client surface. Items 3 to 6
are the quick start proper. Item 4 is the one to start early, because "read
what changed" is where a good implementation and a lazy one differ most, and
the lazy one (show the raw message log) will look finished while quietly
failing moment 3.
