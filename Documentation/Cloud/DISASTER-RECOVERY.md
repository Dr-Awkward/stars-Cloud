# Galaxies disaster recovery and backup

Status: this document is a specification and a runbook. Parts of it describe
infrastructure that exists (`infra/terraform/main.tf` provisions the three
buckets and Firestore), and parts describe protection that does not exist yet
(there is no Firestore export schedule, no off-project copy of the buckets, and
no `Errored` lifecycle state). Every gap is marked. Nothing in here has been
rehearsed. See "The rehearsal has not been run" near the end, and read it before
you trust any number above it.

The house rules apply: plain, direct, no em dashes, honest about limits.

## 1. What is actually at risk

Galaxies keeps a game in two places, and the whole of disaster recovery is the
problem of keeping those two places agreeing with each other.

**The game itself lives in three private GCS buckets.** All three are
uniform-access with `public_access_prevention = "enforced"`, so there is no
object ACL surface and no way to accidentally publish one.

| Bucket | Holds | Object path |
|---|---|---|
| `roybot-galaxies-state` | The authoritative `ServerData` XML, one immutable snapshot per turn | `games/{gameId}/state/{turnYear}.sstate` |
| `roybot-galaxies-orders` | Per-empire submitted orders | `games/{gameId}/orders/{turnYear}/{empireId}.orders` |
| `roybot-galaxies-intel` | Per-empire fog-of-war views | `games/{gameId}/intel/{turnYear}/{empireId}.intel` |

**The control plane lives in Firestore, native mode.** Three collections
matter: `users/{google_sub}`, `games/{gameId}`, and
`games/{gameId}/members/{empireId}`. Cloud SQL is not used, and Postgres is a
resolved rejection; do not reintroduce a relational store as part of a recovery
plan, because a second transactional store is a second thing to keep in sync
and this document is already about the cost of that.

The load-bearing habit is that **the engine writes a full snapshot every single
turn, and never overwrites one.** `games/{gameId}/state/{turnYear}.sstate` is
the authoritative `ServerData` as of the completion of turn `turnYear`, which is
also the input from which turn `turnYear + 1` is generated. It is written once
and then left alone forever.

That is unusual and it is a gift. Most systems have to buy point-in-time
recovery with continuous log shipping and a replay mechanism. Galaxies gets it
for free, because the game is already a chain of immutable states and the
natural recovery unit is "the last completed turn". Almost everything below is
an attempt to not squander that.

## 2. RPO and RTO

These are targets, not measurements. Nothing has been tested. The
justification for each is stated so you can argue with the number instead of
inheriting it.

### Recovery point objective

| What | RPO target | Why that number |
|---|---|---|
| Committed game state | **Zero completed turns** | Every turn is a distinct immutable object. Losing a committed turn requires losing objects, not losing recent writes. There is no write-ahead window to lose. |
| Orders for the open turn | **One turn**, bounded by the game's `MaxTimeBetweenTurnsSeconds` | Orders accumulate in the open turn's prefix until generation consumes them. Losing that prefix costs at most the orders submitted since the last generation. Players can resubmit; the game does not diverge. |
| Intel | **Zero, because intel is derived** | Intel is a projection of state. If intel objects are lost, regenerate them from the state snapshot rather than restoring them. Treat intel as a cache with a long life, not as a backup target. |
| Control plane (Firestore) | **24 hours** from the daily scheduled export, and effectively **zero for `TurnYear`** | The export cadence sets the raw number. But the fields that matter most for coherence are re-derivable from GCS (see section 4), so the real exposure is membership and account changes made since the last export, not the game's position in time. |

The honest headline: **the natural RPO for a Galaxies game is "the last
completed turn", and for most incidents that is also the achieved RPO.** The
cases where it is not are bucket-level loss and Firestore-ahead-of-GCS drift,
and those are the two cases the rest of this document is organized around.

### Recovery time objective

| Scenario | RTO target | Composition of that number |
|---|---|---|
| One game rolled back one turn | **30 minutes** | About 5 minutes of mechanical work. The rest is deciding to do it, writing the player-facing message, and verifying. The mechanics are not the slow part; the judgment is. |
| One poisoned game moved to a visible errored state | **10 minutes**, automatic | This should not need a human to start. A human is needed to fix it, not to stop it. |
| Full project restore, first game generating again | **8 hours** | Terraform apply, image pushes, Firestore import, and a per-game reconciliation pass. Eight hours assumes one person and no surprises, which is optimistic and is why it is written down rather than assumed. |
| Full project restore, all games current and unpaused | **24 hours** | Reconciliation is per-game and partly manual today. It gets faster when the reconciliation script in section 6 actually exists. |

A note on what RTO means for this product. Galaxies is an asynchronous game on
a cadence measured in days. A four-hour outage during which no turn was due is
close to invisible; a four-hour outage that eats a deadline is not, because
players planned around that deadline. So the operationally correct move during
any incident is **pause the affected games first**, which stops the clock and
converts an outage into a delay. Pausing is cheap, reversible, and honest. Do
it early.

## 3. What protects what, today and as specified

### GCS Object Versioning

`roybot-galaxies-state` has `versioning { enabled = true }`. Overwriting or
deleting an object keeps the prior bytes as a noncurrent version, which
protects against the two most likely human errors: a bad `gsutil rm`, and a
process that writes to the wrong path.

Three things you should know about this, in order of how much they matter:

1. **`roybot-galaxies-orders` and `roybot-galaxies-intel` do not have
   versioning enabled.** Check `infra/terraform/main.tf`: only the state bucket
   sets it. Intel is derived, so that is defensible. Orders are not derived, and
   a deletion there costs a real turn of player work. **Action: enable
   versioning on the orders bucket.** This is a one-line Terraform change and it
   is the cheapest risk reduction in this document.

2. **There is no noncurrent-version lifecycle rule anywhere.** The only
   lifecycle rule that exists moves objects to COLDLINE at 30 days. That means
   noncurrent versions accumulate forever, which is a slow cost leak rather than
   a risk, but it also means the retention of your safety net is undefined. An
   undefined retention is not a policy. **Specified rule: keep noncurrent
   versions for 90 days, then delete.** Ninety days comfortably outlives any
   incident that anyone will notice, and it bounds the bill.

3. **The COLDLINE transition has two edges worth knowing.** COLDLINE carries a
   90-day minimum storage duration, so deleting a game's data 45 days after
   creation incurs an early-deletion charge; and reading a coldline object costs
   a retrieval fee. Neither is a problem at Galaxies' scale, but a rollback that
   reads a six-month-old snapshot is a paid read, and someone should not be
   surprised by that on the invoice.

Also: confirm the bucket soft-delete policy explicitly in Terraform rather than
inheriting whatever the default is. A protection you did not choose is a
protection you cannot reason about, and defaults change.

### Firestore scheduled exports

**This does not exist yet.** There is no export schedule, no backup bucket, and
no import rehearsal. As of today, a Firestore mistake is unrecoverable, which
is a worse position than the buckets are in.

Specified configuration:

- A dedicated bucket, `roybot-galaxies-backup`, in a **different region** from
  the primary buckets, uniform access, public access prevention enforced,
  versioning on, and a 90-day object lifecycle.
- A daily scheduled export of the whole database to
  `gs://roybot-galaxies-backup/firestore/{yyyy-mm-dd}/`, run under its own
  service account with `datastore.importExportAdmin` and write access to that
  bucket and nothing else.
- Firestore's built-in point-in-time recovery enabled (7-day window). This is
  strictly better than the daily export for anything inside a week, and the
  daily export is the long-tail and off-region copy. Use both; they fail
  differently.
- The export bucket is **not** writable by `sa-api` or `sa-turngen`. If the
  service that made the mistake can also erase the evidence of the mistake, you
  do not have a backup.

### The gap nobody should be comfortable with

There is currently **no copy of the three game buckets outside the `roybot`
project**. Object Versioning protects an object inside its bucket. It does not
protect against bucket deletion, project deletion, a billing lapse, a
compromised credential with `storage.objectAdmin` (which `sa-turngen` holds on
the state bucket), or a regional outage, because all three buckets are single
region.

**Specified mitigation:** a daily Storage Transfer Service job copying all three
buckets into a separate GCP project with separate ownership, retaining 30 days.
This is the difference between "we keep versions" and "we have a backup". Until
it exists, say so out loud rather than describing the current setup as backed
up.

## 4. What must be restored together, and the one dangerous case

A Galaxies game is coherent only when three things agree:

1. **The state snapshot**, `games/{gameId}/state/{N}.sstate`.
2. **The control-plane game document**, `games/{gameId}`, specifically
   `TurnYear`, `CurrentStatePath`, `Lifecycle`, `Generation`, `Lock`,
   `DeadlineAt`, `SubmittedCount`, and `ActivePlayerCount`.
3. **The member documents**, `games/{gameId}/members/{empireId}`, specifically
   `TurnSubmitted`, `LastSubmittedTurn`, `ConsecutiveMisses`, `AccountId`,
   `Kind`, and `Resigned`.

Restore any one of those without the others and you get a game that is wrong in
a way the system will happily keep running.

### Firestore behind GCS: recoverable, usually boring

The game document says turn 45; the newest state object is turn 47. This
happens when Firestore is restored from an export taken before two turns
generated.

This case is safe because **GCS is the authority on how far the game got.** The
snapshots for 46 and 47 exist, are complete, and were derived from committed
orders. The fix is to roll the document forward: set `TurnYear` to 47 and
`CurrentStatePath` to the turn 47 object, then rebuild the member submission
flags from the orders bucket. Players lose nothing. You may need to reconstruct
membership changes made in the lost window (a player who joined at turn 46),
which is why the member docs are in the coherence set.

### Firestore ahead of GCS: the dangerous case

The game document says turn 47; the newest state object is turn 45. **This is
the failure mode to design against, and it is worse than downtime, because it
is silent.**

Why it is dangerous, concretely:

- If `CurrentStatePath` points at an object that no longer exists, generation
  fails loudly on the next deadline. That is the good version of this bug. It
  becomes a poisoned game (section 7), a human is alerted, and nothing incorrect
  is shown to players.
- If `CurrentStatePath` points at an object that **does** exist but disagrees
  with `TurnYear`, the failure is quiet and much worse. The engine loads the
  turn 45 state, the API reads orders from the `turnYear = 47` prefix, and the
  game generates a turn that never should have existed. Players already
  downloaded intel for turns 46 and 47. They made plans on a timeline the
  server has now discarded. The galaxy they see and the galaxy the server
  believes in have forked, and nothing in the system notices.

There is no clean automated repair for a fork once players have acted on the
discarded branch. So the rule is preventive, and it is absolute:

> **The state bucket is the authority on `turnYear`. Firestore is never allowed
> to claim a turn that has no corresponding state object.** Any restore,
> reconciliation, or manual edit must set `TurnYear` and `CurrentStatePath` from
> the highest `{turnYear}.sstate` object that actually exists for that game, and
> must verify the object is readable before writing the document.

Enforce it in code, not in discipline: the reconciliation step in section 6
should refuse to write a game document whose `CurrentStatePath` it could not
successfully read, and generation should assert on load that the state blob's
own turn year matches the document's `TurnYear` and fail closed if it does not.

## 5. Runbook A: roll one game back exactly one turn

Use when turn `N` generated but is wrong: a bad turn, a corrupted state, a bug
whose blast radius is one game, or the operator's chosen remedy for a poisoned
game. Assume the game document currently reads `TurnYear = N`.

Prerequisites: `gcloud` authenticated with an operator account, the `gameId`,
and a decision already made about what you will tell the players. Write the
player message before you touch anything, because writing it forces you to be
clear about what you are doing.

**Step 1. Stop the clock.**

```
POST /v1/games/{gameId}/pause
```

This cancels the deadline task and prevents any worker from claiming the lock
mid-repair. Do this first, every time, even if you think the repair will take
two minutes.

**Step 2. Record the current facts before you change them.**

Capture the game document (`TurnYear`, `CurrentStatePath`, `Lifecycle`,
`Generation`, `Lock`, `DeadlineAt`, `SubmittedCount`) and every member document
(`EmpireId`, `AccountId`, `Kind`, `TurnSubmitted`, `LastSubmittedTurn`,
`ConsecutiveMisses`, `Resigned`). Save it to a file. If the repair goes wrong,
this is how you get back to a known bad state instead of an unknown one.

**Step 3. Quarantine the evidence.**

```
gsutil cp gs://roybot-galaxies-state/games/{gameId}/state/{N}.sstate \
          gs://roybot-galaxies-backup/quarantine/{gameId}/{N}.sstate
gsutil -m cp -r gs://roybot-galaxies-orders/games/{gameId}/orders/{N-1}/ \
          gs://roybot-galaxies-backup/quarantine/{gameId}/orders-{N-1}/
```

The turn `N` state plus the orders that produced it is the complete
reproduction case for whatever went wrong. Copy it before you disturb anything.

**Step 4. Verify the rollback target exists and is readable.**

```
gsutil stat gs://roybot-galaxies-state/games/{gameId}/state/{N-1}.sstate
gsutil cat  gs://roybot-galaxies-state/games/{gameId}/state/{N-1}.sstate | head -c 512
```

Confirm it is non-empty and parses as the expected XML with a matching turn
year. **If this object does not exist or does not read, stop.** You cannot roll
back, and the correct action is escalation, not improvisation.

**Step 5. Do not delete the turn `N` state object.**

Leave `state/{N}.sstate` in place. It costs almost nothing, it is evidence, and
deleting it removes your ability to compare the two branches later. The
rollback is accomplished by moving the pointer, not by destroying the future.

**Step 6. Decide what happens to the orders for turn `N-1`.**

Two options. Pick deliberately and record which you picked.

- **Preserve them** (default). The orders at `orders/{N-1}/{empireId}.orders`
  survive, so members can be restored as already-submitted and the turn will
  regenerate from the same inputs. Correct when you are re-driving after fixing
  an engine bug, because it reproduces the player's actual intent.
- **Clear them.** Move them to quarantine and let players resubmit. Correct
  when the orders themselves are suspect, or when enough real time has passed
  that players would rather replan than have an old plan silently re-executed.

**Step 7. Write the control plane, game document first, in one transaction.**

Set, on `games/{gameId}`:

- `TurnYear` = `N-1`
- `CurrentStatePath` = `games/{gameId}/state/{N-1}.sstate` (the object you
  verified in step 4, not a constructed string you assume is right)
- `Generation` = `Idle`
- `Lock` = null (clear the token and lease outright)
- `SubmittedCount` = the count you will set in step 8
- `DeadlineAt` = leave null while paused; it is recomputed on resume from
  `LastGenerationAt` and `MaxTimeBetweenTurnsSeconds`
- `Lifecycle` = stays `Paused` until step 10

**Step 8. Write the member documents to match.**

For each `games/{gameId}/members/{empireId}`:

- If you preserved orders and `orders/{N-1}/{empireId}.orders` exists:
  `TurnSubmitted = true`, `LastSubmittedTurn = N-1`.
- If you cleared orders, or no order object exists for that empire:
  `TurnSubmitted = false`, `LastSubmittedTurn` = the highest turn below `N-1`
  for which an order object exists, or `-1` if none.
- **Decrement `ConsecutiveMisses` by one for any empire that was marked missed
  during turn `N`**, and reverse any AI handoff that turn `N` triggered
  (`Kind` back to `Human`, `AccountId` restored from your step 2 capture). A
  rollback that leaves a player's seat in AI hands because of a turn that no
  longer exists is a real harm, and it is the single most commonly forgotten
  step here.

Derive submission flags from the orders bucket, never from memory. The bucket
is the truth about what was submitted.

**Step 9. Tell the players, before you resume.**

Post the message you wrote at the start. Say the turn was rolled back, say why
in one plain sentence, and say explicitly that any turn `N` results they already
saw no longer happened. Do not paper over this. A player who read their turn
`N` intel has seen a future that was withdrawn, and finding that out from a
notice is annoying while finding it out from confusion is corrosive.

**Step 10. Resume and verify.**

```
POST /v1/games/{gameId}/resume
```

Then verify, in this order: the game document reads `TurnYear = N-1` with a
readable `CurrentStatePath`; `Generation` is `Idle` with no lock; `DeadlineAt`
is in the future and matches the cadence; the member submission counts sum to
`SubmittedCount`; and the game's status endpoint returns what a player should
see. Watch the next generation complete before you close the incident.

## 6. Runbook B: full project restore

Use when the `roybot` project, the Firestore database, or the buckets are lost
or comprehensively corrupted. This is the runbook that most depends on backups
that do not exist yet, so read section 3 first and be honest with yourself
about which steps you can currently perform.

**Step 1. Stop the world.**

Pause the Cloud Tasks deadline queue and disable the one-minute backstop sweep
scheduler job. Nothing must attempt a generation while the control plane is in
an unknown state. If the project is entirely gone this is moot; if it is partly
alive, this is the most important step on the page.

**Step 2. Rebuild infrastructure.**

```
terraform init && terraform apply
```

against `infra/terraform/`. This recreates the buckets, Firestore, the service
accounts, the queue, the topics, and the Cloud Run services. Then push the
`galaxies-api` and `galaxies-turngen` images to Artifact Registry and deploy
the revisions matching the engine version the games were last generated with.
**Engine version matters:** a game generated by one engine build and resumed by
another can diverge, so pin the restore to the build the games were on, not to
`latest`.

**Step 3. Restore object data.**

- **Objects deleted, buckets alive:** restore noncurrent versions with
  `gsutil cp` from the versioned generation, oldest game first.
- **Buckets or project lost:** restore from the off-project Storage Transfer
  copy described in section 3. **If that copy does not exist, the game data is
  gone and no runbook step recovers it.** Stating that plainly is the point of
  writing this down.
- Restore the state bucket first, then orders. Intel is derived; do not spend
  restore time on it, and regenerate it in step 6.

**Step 4. Restore the control plane.**

```
gcloud firestore import gs://roybot-galaxies-backup/firestore/{yyyy-mm-dd}/
```

Use the most recent export that predates the corruption, not simply the most
recent export. If the incident was a bad write rather than a loss, prefer
Firestore point-in-time recovery to a timestamp just before the bad write,
which is finer grained than the daily export.

**Step 5. Reconcile every game against GCS. This is the step that matters.**

For each `games/{gameId}` document, do not trust the document. Do this:

1. List `gs://roybot-galaxies-state/games/{gameId}/state/` and take the highest
   `{turnYear}.sstate` that reads successfully. Call it `S`.
2. Compare with the document's `TurnYear`, call it `F`.
3. If `F == S`: the game is coherent. Clear `Lock`, set `Generation = Idle`,
   and move on.
4. If `F < S` (Firestore behind): roll the document forward. Set `TurnYear = S`
   and `CurrentStatePath` to that object, then rebuild member submission flags
   from `orders/{S}/`. Check for membership changes lost in the export window.
5. If `F > S` (Firestore ahead, the dangerous case): roll the document **back**
   to `S`. Never leave a document claiming a turn that has no state object. Then
   treat it as a rollback under Runbook A from step 6 onward, including telling
   those players, because they may have seen turns that no longer exist.
6. Refuse to write any document whose `CurrentStatePath` you could not read.

This loop should be a script. Written as a script it is an hour of work and
runs in seconds across every game; performed by hand it is the reason the full
restore RTO is 8 hours instead of 2. Writing that script is the highest-value
unbuilt item in this document.

**Step 6. Regenerate derived data and re-arm the clock.**

Regenerate current-turn intel from each restored state snapshot rather than
restoring intel objects. Then recompute `DeadlineAt` for every active game from
`LastGenerationAt` and `MaxTimeBetweenTurnsSeconds`, and re-arm one Cloud Tasks
entry per game (`gen-{gameId}-{turnYear}`; the naming makes a duplicate enqueue
a no-op, so a careful double-run is safe here).

**Step 7. Give the time back.**

Every active game lost real deadline time during the outage. Extend deadlines
by at least the outage duration before unpausing, so nobody is auto-missed for
an outage they did not cause. This is a correctness step, not a courtesy: the
missed-turn ladder escalates to AI takeover after three consecutive misses, and
an outage must never be allowed to feed that counter.

**Step 8. Unpause, then verify one game end to end.**

Resume the queue and the sweep. Then watch one low-stakes game generate a full
turn before declaring the incident closed. A restore is not finished when the
data is back; it is finished when a turn has generated correctly on top of it.

**Step 9. Tell everyone what happened.**

One honest note: what broke, what was lost, what was restored, and what you
changed so it does not recur. Name the data loss if there was any. A player
finding out later that a turn silently vanished costs more trust than the
outage did.

## 7. Runbook C: the poisoned game

A poisoned game is one whose turn generation fails reliably: a corrupted state
blob, an order that trips an engine bug, an unhandled condition in combat or
movement resolution. Left alone, it retries on every deadline and every sweep,
burns compute, and, worst of all, **looks to the players like a game that is
merely slow.**

### What exists today

Honestly: not this. `GameLifecycle` has `Draft`, `Lobby`, `Active`, `Paused`,
`Finished`, `Cancelled`, and `Archived`. There is no `Errored`. The main design
document lists the errored-game state, the poison-turn circuit breaker, and the
operator runbook as Milestone 5 gaps. So the following is a specification for
work that has not been done.

### The specified behavior

**Detection.** Add to the game document a `GenerationFailures` counter and a
`LastGenerationError` (message, timestamp, and the turn year it applied to).
Increment on any generation attempt that fails after the lock was claimed.
Reset to zero on any successful commit.

**The circuit breaker.** On the **third consecutive failure for the same
`(gameId, turnYear)`**, stop trying:

- Set `Lifecycle = Errored`.
- Release the generation lock and set `Generation = Idle`.
- Cancel the deadline task and do not re-arm it.
- Skip the game in the backstop sweep.

Three is chosen because it is enough to ride out a transient (a cold start
timeout, a momentary GCS failure) and few enough that the game stops within one
cadence rather than grinding for days.

**Player visibility is not optional.** The game must show its state in plain
words wherever a player can see the game, and the message must say what
happened, what it means for them, and that a person knows. Something like:
"This game is stopped. Turn 47 could not be generated. Nobody has been
eliminated and no orders have been lost. A person has been alerted and will
look at it." No glyph, no spinner pretending to be progress, and no silent
stall. A player refreshing a dead game is the failure this state exists to
prevent.

**Alerting a human.** A log-based alert on the transition into `Errored`, going
to a channel a person actually reads, carrying the `gameId`, the `turnYear`,
the error, and a link to the logs. This is the one alert in the system that
must never be routed to a dashboard nobody opens.

**Automatic quarantine.** On entering `Errored`, copy the input state blob and
the full orders prefix for the failing turn to
`gs://roybot-galaxies-backup/quarantine/{gameId}/{turnYear}/`. Do it
automatically, because by the time a human arrives the temptation to start
poking is high and the reproduction case is fragile.

### The operator's three options

Once a human has looked at the quarantined inputs and understands the failure,
exactly three moves are legitimate:

1. **Re-drive the same turn.** For a transient or an infrastructure fault, or
   after deploying an engine fix. Clear `GenerationFailures`, set `Lifecycle`
   back to `Active`, extend the deadline to give players back the lost time, and
   re-arm the deadline task for the same `(gameId, turnYear)`. Nothing is lost;
   the turn simply happens later than planned.

2. **Roll back exactly one turn.** For a corrupted current state, where the
   previous snapshot is clean. Follow Runbook A. **Exactly one turn, never
   two.** Multi-turn rollbacks discard player decisions wholesale and are not an
   operator's call to make alone; if one turn is not enough, escalate and talk
   to the players before touching anything.

3. **Cancel the game, honestly.** When the state is unrecoverable and no
   snapshot is clean. Set `Lifecycle = Cancelled`, explain plainly what happened
   and that their game cannot continue, and do not dress it up. Keep the data;
   do not archive it away until the bug is understood.

There is no fourth option. In particular, "leave it in `Errored` and hope"
is not an option, because an errored game with no operator decision is just a
stall with better labelling.

### The rules that bound all three

- A poisoned game never re-arms its own deadline. Recovery is always a
  deliberate human act.
- Never roll back more than one turn without talking to the players first.
- Never repair a game without quarantining its inputs first. The bug will
  recur, and the reproduction is worth more than the ten seconds it costs.
- The circuit breaker is per game. One poisoned game must never pause the
  service; a bug that poisons many games at once is a different incident and
  belongs to Runbook B's "stop the world" step.

## 8. Object Versioning is not a backup

Stated plainly, because this is the belief most likely to be quietly held and
most likely to be wrong:

**Object Versioning is not a backup. It is an undo button inside one bucket, in
one project, in one region, under one set of credentials.**

It does not protect against a deleted bucket, a deleted or suspended project, a
billing lapse, a regional outage, a lifecycle rule configured wrong, or a
compromised credential (and `sa-turngen` holds `storage.objectAdmin` on the
state bucket, which means the service that writes your game data can also erase
its own history). Every one of those is a real way to lose a game, and
versioning stops none of them.

And the deeper point, which applies to the Firestore exports just as much:

**A backup that has never been restored is not a backup. It is a hypothesis.**

Until someone has actually taken a snapshot from storage, put it back, and
watched a turn generate on top of it, the correct claim is "we retain data",
not "we can recover". Those are different sentences and only one of them is
currently true for Galaxies.

## 9. The restore rehearsal

The rehearsal is the thing that converts the numbers in section 2 from
aspiration into measurement. It should be run against a purpose-made throwaway
game, not a real one, and it should be timed.

### Rehearsal checklist

| Step | What you must actually do | Pass condition |
|---|---|---|
| 1 | Create a rehearsal game and generate at least 5 turns | Five distinct `{turnYear}.sstate` objects exist |
| 2 | Verify each snapshot is independently readable and parses | All five read; turn year inside each matches its filename |
| 3 | Restore a noncurrent version of a deliberately overwritten state object | Bytes match the pre-overwrite object exactly |
| 4 | Take a Firestore export, then import it into a scratch database | Import completes; game and member docs present and correct |
| 5 | Run Runbook A end to end on the rehearsal game | Game rolls back one turn and generates the next turn correctly |
| 6 | Force a Firestore-ahead mismatch, then reconcile it | Reconciliation detects it and rolls the document back to the real snapshot |
| 7 | Force three generation failures | Game enters `Errored`, alert fires, inputs land in quarantine |
| 8 | Restore the whole rehearsal game into a clean project | A turn generates on top of the restored data |
| 9 | Time every step | Measured times recorded against the RTO targets in section 2 |
| 10 | Update this document with what was actually measured | Section 2 cites measurements, not guesses |

### Rehearsal log

| Rehearsal | Date last actually run | Run by | Measured RTO | Notes |
|---|---|---|---|---|
| Single game, one turn rollback (Runbook A) | **NOT YET RUN** | | | |
| Firestore export and import | **NOT YET RUN** | | | |
| Firestore-ahead reconciliation | **NOT YET RUN** | | | |
| Poisoned game circuit breaker (Runbook C) | **NOT YET RUN** | | | |
| Full project restore (Runbook B) | **NOT YET RUN** | | | |

**As of 2026-07-20, no restore rehearsal has been run.** Not one row above has
a date in it. Nothing in this document has been proven in practice, and the RPO
and RTO figures in section 2 are reasoned targets rather than observed results.

Milestone 4, the public launch gate, lists "a tested disaster-recovery restore
with a defined RPO and RTO" as a requirement. This document supplies the
definitions. **It does not supply the test, and the launch gate is not met
until at least rows 1, 2, and 5 of the rehearsal log have real dates in them.**

## 10. Known gaps, collected

Everything this document specified but that does not exist, in rough priority
order:

1. No off-project or cross-region copy of the three game buckets. A project
   loss is currently a total loss.
2. No Firestore scheduled export and no point-in-time recovery enabled. A
   Firestore mistake is currently unrecoverable.
3. No restore rehearsal has ever been run, so none of this is proven.
4. No `Errored` lifecycle state, no failure counter, and no circuit breaker, so
   a poisoned game currently retries forever and looks merely slow to players.
5. Versioning is not enabled on `roybot-galaxies-orders`.
6. No noncurrent-version lifecycle rule, so safety-net retention is undefined.
7. No reconciliation script, which is why the full-restore RTO is 8 hours.
8. No assertion at generation time that the state blob's own turn year matches
   the control plane's `TurnYear`, which is the cheap guard against a silent
   fork.
9. Buckets are single region, and the dual-region cost trade has not been
   decided.

Items 1, 2, and 3 are the ones that make the difference between "we would
recover" and "we would find out". Do them in that order.
