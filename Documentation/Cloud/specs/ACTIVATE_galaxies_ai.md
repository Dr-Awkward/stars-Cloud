# ACTIVATE galaxies-ai (M3, ships dark)

The deploy instruction of record for AI seats. Follow this rather than improvising a rollout.

Everything in M3 ships dark. The first deploy provisions live services that deliberately do nothing: `/v1/dispatch` records `held` and submits no orders, so every AI seat runs on held orders and every turn still generates on schedule. That is the safe resting state and it is also the kill switch. Two flips arm it: Flip 1 lets the built-in Nova AI fill AI seats, Flip 2 lets it take over abandoned human seats.

All values are pinned to project `roybot`, region `us-central1`. Every command below is copy-pasteable as written.

**Where you run this.** `galaxies-ai` and `ai-nova-default` are `ingress=internal`, so curl against them must originate inside the roybot VPC. Use a small `smoke-runner` GCE VM in `us-central1`, with `gcloud`, `jq`, and `gsutil` installed, running as a principal that holds `roles/run.invoker` on both services and `roles/iam.serviceAccountTokenCreator` where noted. Running these from a laptop will fail at the network layer, not the auth layer, and the error will not say so clearly.

**Preconditions.**

- M0 and M2 are applied: the three buckets, Firestore native, `galaxies-turngen`, `galaxies-api`, the `galaxies-deadlines` Cloud Tasks queue, the one-minute sweep, and the three Pub/Sub topics all exist.
- `infra/terraform/m3_ai.tf` is applied at least once with every flag variable left at its default `false`.
- Images for `galaxies-ai` and `ai-nova-default` are built and pushed to `us-central1-docker.pkg.dev/roybot/roybot-galaxies`.

---

## Section 0. Set these once per shell (on `smoke-runner`)

```bash
gcloud config set project roybot
export PROJECT=roybot
export REGION=us-central1
export PROJNUM=$(gcloud projects describe $PROJECT --format='value(projectNumber)')
export REG=us-central1-docker.pkg.dev/roybot/roybot-galaxies

export AI=$(gcloud run services describe galaxies-ai       --region=$REGION --format='value(status.url)')
export NOVA=$(gcloud run services describe ai-nova-default --region=$REGION --format='value(status.url)')
export API=$(gcloud run services describe galaxies-api     --region=$REGION --format='value(status.url)')
export TG=$(gcloud run services describe galaxies-turngen  --region=$REGION --format='value(status.url)')

export INTEL=gs://roybot-galaxies-intel
export ORDERS=gs://roybot-galaxies-orders
export STATE=gs://roybot-galaxies-state

# OIDC identity token for one callee audience. Every internal call needs one.
tok() { gcloud auth print-identity-token --audiences="$1"; }

# Read one Firestore document. gcloud has no "firestore documents get" verb,
# so this goes at the REST API directly.
fsdoc() {
  curl -s -H "Authorization: Bearer $(gcloud auth print-access-token)" \
    "https://firestore.googleapis.com/v1/projects/$PROJECT/databases/(default)/documents/$1"
}
```

Object paths follow the locked convention, which is longer than the sketch in the spec's Section 14. Use these:

| What | Path |
|---|---|
| A seat's intel (the `empire_view` source) | `$INTEL/games/{gameId}/intel/{turnYear}/{empireId}.intel` |
| A seat's submitted orders | `$ORDERS/games/{gameId}/orders/{turnYear}/{empireId}.xml` |
| Game state | `$STATE/games/{gameId}/state/{turnYear}.sstate` |
| Durable dispatch artifacts | `$STATE/ai-runs/{gameId}/{turnYear}/{empireId}.req.json` and `.resp.json` |

---

## Section 1. One-time IAM

Terraform owns all of this. Apply it, do not hand-roll it:

```bash
cd infra/terraform
terraform init -backend-config="bucket=roybot-galaxies-tfstate"
terraform apply \
  -var="turngen_image=$REG/galaxies-turngen:latest" \
  -var="api_image=$REG/galaxies-api:latest" \
  -var="ai_image=$REG/galaxies-ai:latest" \
  -var="ai_nova_default_image=$REG/ai-nova-default:latest"
```

That creates `galaxies-ai-sa` and `ai-nova-default-sa` and grants exactly:

| Principal | Grant | Why |
|---|---|---|
| `galaxies-ai-sa` | `roles/datastore.user` on the project | `ai_seats`, `ai_runs`, the run lock |
| `galaxies-ai-sa` | `roles/cloudtasks.enqueuer` on the project | enqueue `ai-{gameId}-{turnYear}-{empireId}` |
| `galaxies-ai-sa` | `roles/storage.objectViewer` on the intel bucket | read the seat's own intel, read only |
| `galaxies-ai-sa` | `roles/storage.objectAdmin` on the state bucket | write `ai-runs/` artifacts |
| `galaxies-ai-sa` | `roles/run.invoker` on `galaxies-api` | submit orders through the internal route |
| `galaxies-ai-sa` | `roles/run.invoker` on `ai-nova-default` | call the participant |
| `galaxies-ai-sa` | `roles/iam.serviceAccountUser` on `sa-invoker` | mint the dispatch task's OIDC token |
| `sa-invoker`, Cloud Tasks agent, Pub/Sub agent | `roles/run.invoker` on `galaxies-ai` | the only three inbound identities |
| Pub/Sub agent | `roles/iam.serviceAccountTokenCreator` on `sa-invoker` | sign push OIDC tokens |
| Pub/Sub agent | publisher on `galaxies-dead-letter`, subscriber on the three `ai-*` subscriptions | make the dead-letter policy real |

Note what is deliberately absent. `galaxies-ai-sa` has no write role on the orders bucket: it submits through `galaxies-api` so AI and human orders travel one pipeline and turngen's `OrderReader` cannot tell them apart. And `ai-nova-default-sa` holds no data-plane role at all, because the participant receives its whole world in the request body.

Verify the two identities and the inbound policy:

```bash
gcloud iam service-accounts list --filter="email~'(galaxies-ai-sa|ai-nova-default-sa)'" \
  --format='table(email,displayName)'

gcloud run services get-iam-policy galaxies-ai --region=$REGION --format=json \
  | jq -r '.bindings[] | select(.role=="roles/run.invoker") | .members[]'
# expect exactly: sa-invoker, the cloudtasks agent, the pubsub agent.
# "allUsers" appearing here is a stop-the-rollout finding.

gcloud run services get-iam-policy ai-nova-default --region=$REGION --format=json \
  | jq -r '.bindings[] | select(.role=="roles/run.invoker") | .members[]'
# expect exactly one member: serviceAccount:galaxies-ai-sa@roybot.iam.gserviceaccount.com
```

---

## Section 2. First deploy, ordered, every flag off

Deploy order matters. The participant must exist before the runner is told its URL, and the runner must exist before Pub/Sub is pointed at it.

1. `participants/nova-default` becomes `ai-nova-default`.
2. `galaxies-ai`.
3. Redeploy `galaxies-api` so it carries the internal orders route with `API_AI_ORDERS_ENABLED=false`.
4. `galaxies-turngen` is already deployed and already publishes `turn-generated` and `deadline-approaching`. Confirm, do not redeploy.

Push each branch so its per-directory Cloud Build trigger fires, or build one by hand:

```bash
gcloud builds submit . --config=participants/nova-default/cloudbuild.yaml
gcloud builds submit . --config=galaxies-ai/cloudbuild.yaml
```

Then `terraform apply` again with the new image tags so terraform state matches what is actually running.

### Verify the runner is up and gated

```bash
curl -s -H "Authorization: Bearer $(tok "$AI")" "$AI/readyz" | jq .
# -> {"status":"ready","firestore":true}

curl -s -H "Authorization: Bearer $(tok "$AI")" "$AI/v1/participants" | jq .
# flags off -> {"disabled":true}

curl -s -o /dev/null -w '%{http_code}\n' "$AI/readyz"
# no token at all -> 403 from the platform, before the app is reached
```

### Verify the flags actually landed on the revision

A Cloud Build substitution with no matching container env var is a silent no-op. The service will look deployed and the flag will do nothing. Check the running revision, not the trigger config:

```bash
gcloud run services describe galaxies-ai --region=$REGION \
  --format='value(spec.template.spec.containers[0].env)' | tr ',' '\n' | grep -E 'ENABLED|BUDGET|MODEL'
# expect all seven *_ENABLED at false

gcloud run services describe galaxies-api --region=$REGION \
  --format='value(spec.template.spec.containers[0].env)' | tr ',' '\n' | grep API_AI_ORDERS_ENABLED
# expect API_AI_ORDERS_ENABLED=false
```

### Verify the event wiring exists but is inert

```bash
gcloud pubsub subscriptions list --format='table(name,pushConfig.pushEndpoint,deadLetterPolicy.maxDeliveryAttempts)' \
  | grep ai-
# three rows: ai-turn-generated, ai-game-created, ai-deadline-approaching,
# each pointing at $AI/events/... with a dead-letter attempt count of 5
```

Let a real game generate a turn now. The subscriptions deliver, the handlers run, and nothing happens downstream because dispatch is off. That is the dark state working correctly.

---

## Section 3. Flip 1. The built-in AI fills AI seats

Order matters here too. Open the orders route on the API first: if the runner starts dispatching before the API will accept submissions, every seat records `failed` and falls to held orders, which is safe but noisy.

Durable flip, through terraform:

```bash
cd infra/terraform
terraform apply \
  -var="turngen_image=$REG/galaxies-turngen:latest" \
  -var="api_image=$REG/galaxies-api:latest" \
  -var="ai_image=$REG/galaxies-ai:latest" \
  -var="ai_nova_default_image=$REG/ai-nova-default:latest" \
  -var="api_ai_orders_enabled=true" \
  -var="galaxies_ai_enabled=true" \
  -var="ai_dispatch_enabled=true"
```

Immediate flip, for a smoke you intend to reverse within the hour:

```bash
gcloud run services update galaxies-api --region=$REGION --update-env-vars=API_AI_ORDERS_ENABLED=true
gcloud run services update galaxies-ai  --region=$REGION \
  --update-env-vars=GALAXIES_AI_ENABLED=true,AI_DISPATCH_ENABLED=true
```

The immediate form is wiped by the next `terraform apply`. If a flip is meant to stay, put it in the variable.

### Smoke 1a. Drive one seat-turn by hand

Use a seeded solo-versus-AI test game with a known AI seat.

```bash
export GID=roybot:game:ZZTEST
export YEAR=2101
export EMP=7
export RUN="${GID//:/_}_${YEAR}_${EMP}"

curl -s -X POST -H "Authorization: Bearer $(tok "$AI")" -H "Content-Type: application/json" \
  "$AI/v1/dispatch" -d "{\"game_id\":\"$GID\",\"turn_year\":$YEAR,\"empire_id\":$EMP}" | jq .
# -> {"status":"submitted","orders_count":<greater than 0>,...}
```

`orders_count` of zero is a failure, not a pass. The built-in Nova AI always emits at least a research order on a live seat.

### Smoke 1b. The run record and the durable artifacts

```bash
fsdoc "ai_runs/$RUN" | jq '{status: .fields.status.stringValue,
                            orders: .fields.orders_count.integerValue,
                            dropped: .fields.orders_dropped.integerValue,
                            participant: .fields.participant_id.stringValue,
                            attempts: .fields.attempts.integerValue}'
# -> status "submitted", participant "galaxies.default-ai", attempts 1

gsutil ls $STATE/ai-runs/$GID/$YEAR/
# -> $EMP.req.json and $EMP.resp.json
```

### Smoke 1c. The orders reached the same bucket humans use

This is the claim that matters most: the engine cannot tell an AI submission from a human one.

```bash
gsutil ls -l "$ORDERS/games/$GID/orders/$YEAR/$EMP."*
# -> exactly one orders object for this seat

fsdoc "games/$GID/members/$EMP" | jq '.fields.turnSubmitted'
# -> the seat is marked submitted, by the same field a human submission sets
```

### Smoke 1d. Replay the captured view

```bash
gsutil cp $STATE/ai-runs/$GID/$YEAR/$EMP.req.json /tmp/ev.json
curl -s -X POST -H "Authorization: Bearer $(tok "$AI")" -H "Content-Type: application/json" \
  "$AI/v1/replay" -d "{\"participant_id\":\"galaxies.default-ai\",\"request\":$(cat /tmp/ev.json)}" \
  | jq '{orders: (.orders|length), accepted, dropped}'
# accepted should match Smoke 1b's orders_count; dropped should be 0
```

### Smoke 1e. A real game, end to end

Let a live solo-versus-AI game reach its deadline without touching it. Confirm three things in order:

```bash
# 1. the per-seat task was enqueued ahead of the human deadline
gcloud tasks list --queue=galaxies-deadlines --location=$REGION --format='table(name,scheduleTime)' \
  | grep -E "ai-|gen-"
# the ai- task's scheduleTime must be EARLIER than the gen- task's for the same game and year.
# If it is not, a slow AI is eating the human window; stop and fix the lead time.

# 2. the seat auto-submitted
fsdoc "ai_runs/${GID//:/_}_${YEAR}_${EMP}" | jq -r '.fields.status.stringValue'

# 3. the turn generated on schedule
fsdoc "games/$GID" | jq '{turnYear: .fields.turnYear.integerValue,
                          generation: .fields.generation.stringValue}'
# turnYear advanced by one, generation back to "Idle"
```

---

## Section 4. The negative tests

These are the tests that decide whether Flip 1 stays on. A rollout that only proves the happy path has proved nothing about a play-by-email game, where the failure that matters is a turn that never generates.

### Negative 1. A participant that times out must not block the turn

Do not wait for a real participant to misbehave. Force it, by shrinking the runner's wall-clock cap below any possible real answer:

```bash
gcloud run services update galaxies-ai --region=$REGION --update-env-vars=DEFAULT_TIMEOUT_S=1

export YEAR2=2102
export RUN2="${GID//:/_}_${YEAR2}_${EMP}"
curl -s -X POST -H "Authorization: Bearer $(tok "$AI")" -H "Content-Type: application/json" \
  "$AI/v1/dispatch" -d "{\"game_id\":\"$GID\",\"turn_year\":$YEAR2,\"empire_id\":$EMP}" | jq .
# -> {"status":"held",...} after the retry budget, NOT a 500 and NOT a hang

fsdoc "ai_runs/$RUN2" | jq '{status: .fields.status.stringValue,
                             attempts: .fields.attempts.integerValue,
                             error: .fields.error.stringValue}'
# -> status "held" (or "failed" after retries), attempts 2 with DISPATCH_RETRY=1,
#    error naming a timeout

# the seat is still marked submitted, so turngen does not wait on it:
fsdoc "games/$GID/members/$EMP" | jq '.fields.turnSubmitted'
```

Now the part that actually matters. Let the game reach its deadline with that seat held:

```bash
fsdoc "games/$GID" | jq -r '.fields.turnYear.integerValue'
# -> advanced by one, ON SCHEDULE, with a dead participant

gsutil ls "$INTEL/games/$GID/intel/$((YEAR2+1))/"
# -> new intel exists for every empire, including the held seat
```

A held seat keeps its last submitted orders for the year, or an empty order list if it never submitted. The engine already tolerates an empire that submitted nothing; it simply does not change that empire's plans. Restore the cap:

```bash
gcloud run services update galaxies-ai --region=$REGION --update-env-vars=DEFAULT_TIMEOUT_S=60
```

### Negative 2. A duplicate dispatch must be a no-op

Cloud Tasks guarantees at-least-once delivery, so a redelivery of an already-completed seat-turn is normal traffic, not an anomaly. It must not submit a second set of orders over the first.

```bash
export YEAR3=2103
export RUN3="${GID//:/_}_${YEAR3}_${EMP}"
BODY="{\"game_id\":\"$GID\",\"turn_year\":$YEAR3,\"empire_id\":$EMP}"

curl -s -X POST -H "Authorization: Bearer $(tok "$AI")" -H "Content-Type: application/json" \
  "$AI/v1/dispatch" -d "$BODY" | jq .
# -> {"status":"submitted",...}

# record the exact object generation the first dispatch wrote
GEN1=$(gsutil stat "$ORDERS/games/$GID/orders/$YEAR3/$EMP."* | awk '/Generation/{print $2}')
FIN1=$(fsdoc "ai_runs/$RUN3" | jq -r '.fields.finished_at.timestampValue')

# deliver the very same dispatch again
curl -s -X POST -H "Authorization: Bearer $(tok "$AI")" -H "Content-Type: application/json" \
  "$AI/v1/dispatch" -d "$BODY" | jq .
# -> a no-op result, for example {"status":"submitted","duplicate":true}
#    It must NOT report a fresh participant call.

GEN2=$(gsutil stat "$ORDERS/games/$GID/orders/$YEAR3/$EMP."* | awk '/Generation/{print $2}')
FIN2=$(fsdoc "ai_runs/$RUN3" | jq -r '.fields.finished_at.timestampValue')

test "$GEN1" = "$GEN2" && echo "PASS: orders object untouched" || echo "FAIL: orders were rewritten"
test "$FIN1" = "$FIN2" && echo "PASS: run record untouched"    || echo "FAIL: the run re-ran"
```

The GCS object generation is the honest check here. A second write produces a new generation number even when the bytes are identical, so this catches a re-run that a byte comparison would miss.

### Negative 3. A dispatch for empire X may read only empire X's intel

This is the fog-of-war boundary, and it is the one boundary on this path that IAM does not enforce for you. `galaxies-ai-sa` holds `objectViewer` on the whole intel bucket, because per-object IAM does not scale to one grant per seat per turn. So the restriction lives in code, in the single path the dispatch worker composes, and this test is the control that proves it. Say that plainly rather than pointing at the role.

```bash
export YEAR4=2104
export OTHER=8   # a different live empire in the same game
export RUN4="${GID//:/_}_${YEAR4}_${EMP}"
START=$(date -u +%Y-%m-%dT%H:%M:%SZ)

curl -s -X POST -H "Authorization: Bearer $(tok "$AI")" -H "Content-Type: application/json" \
  "$AI/v1/dispatch" -d "{\"game_id\":\"$GID\",\"turn_year\":$YEAR4,\"empire_id\":$EMP}" | jq .

# 3a. the captured request is scoped to empire 7 and mentions no other empire's holdings
gsutil cp $STATE/ai-runs/$GID/$YEAR4/$EMP.req.json /tmp/iso.json
jq '.seat.empire_id' /tmp/iso.json                       # -> 7
jq '[.empire_view.owned_stars[].owner] | unique' /tmp/iso.json   # -> [7] and nothing else
jq '[.empire_view.owned_fleets[].owner] | unique' /tmp/iso.json  # -> [7] and nothing else
# other_empires and star_reports may name empire 8, but only as last-seen scan
# data that ScanStep already fog-projected into empire 7's own intel. That is the
# view the human player sees, so it is correct for the AI to see it too.

# 3b. the runner read exactly one object out of the intel bucket
gcloud logging read \
  "resource.type=gcs_bucket AND resource.labels.bucket_name=roybot-galaxies-intel
   AND protoPayload.authenticationInfo.principalEmail=galaxies-ai-sa@roybot.iam.gserviceaccount.com
   AND timestamp>=\"$START\"" \
  --format='value(protoPayload.resourceName)' | sort -u
# -> exactly one line, ending in /$YEAR4/$EMP.intel
# Any line ending in /$OTHER.intel is a stop-the-rollout finding: flip
# AI_DISPATCH_ENABLED back to false immediately (Section 6) and fix the worker.
```

This requires Data Access audit logs for Cloud Storage to be enabled on `roybot`. If `gcloud logging read` returns nothing, the test did not pass, it did not run. Enable `DATA_READ` for `storage.googleapis.com` and repeat.

The CI counterpart is the isolation assertion in the spec's Section 15, which asserts the same property per commit against fixtures. This runbook check proves it against the deployed service and the real bucket. Both, not either.

---

## Section 5. Flip 2. Abandoned-seat takeover

Only flip this once Section 4 passes clean. Takeover changes who controls a human's empire, so a bug here is visible to a player in a way a held AI seat is not.

```bash
cd infra/terraform
terraform apply \
  -var="turngen_image=$REG/galaxies-turngen:latest" \
  -var="api_image=$REG/galaxies-api:latest" \
  -var="ai_image=$REG/galaxies-ai:latest" \
  -var="ai_nova_default_image=$REG/ai-nova-default:latest" \
  -var="api_ai_orders_enabled=true" \
  -var="galaxies_ai_enabled=true" \
  -var="ai_dispatch_enabled=true" \
  -var="ai_takeover_enabled=true"
```

### Smoke 2a. A human seat that goes silent gets picked up

Take a test game with a human seat (`empire 3` below) and let it miss its deadlines past the abandonment threshold. Do not fake the Firestore doc: the point of the test is that turngen makes the decision.

```bash
export HUMAN=3

# before: the seat is human-controlled and has no AI pin
fsdoc "games/$GID/ai_seats/$HUMAN" | jq '.fields.controller.stringValue'
# -> "human", or the document does not exist yet

# ... let the game pass its abandonment threshold ...

# after: turngen flipped the controller and pinned the built-in AI
fsdoc "games/$GID/ai_seats/$HUMAN" | jq '{controller: .fields.controller.stringValue,
                                          participant: .fields.participant_id.stringValue,
                                          pinned: .fields.pinned_at.timestampValue}'
# -> controller "ai_takeover", participant "galaxies.default-ai"
```

### Smoke 2b. The taken-over seat is dispatched like any AI seat from then on

```bash
NEXT=$(fsdoc "games/$GID" | jq -r '.fields.turnYear.integerValue')
fsdoc "ai_runs/${GID//:/_}_${NEXT}_${HUMAN}" | jq '{status: .fields.status.stringValue,
                                                    orders: .fields.orders_count.integerValue}'
# -> status "submitted" with orders, on the turn after the flip

gsutil ls "$ORDERS/games/$GID/orders/$NEXT/$HUMAN."*
```

### Smoke 2c. The takeover is confined to the abandoned seat

```bash
for e in 7 8; do
  echo -n "empire $e controller: "
  fsdoc "games/$GID/ai_seats/$e" | jq -r '.fields.controller.stringValue // "human"'
done
# no seat other than $HUMAN may have moved to "ai_takeover"
```

---

## Section 6. Kill switch and rollback

One switch stops all AI order submission everywhere, immediately, with no data migration and no game left broken:

```bash
gcloud run services update galaxies-ai --region=$REGION --update-env-vars=AI_DISPATCH_ENABLED=false
```

Every AI seat falls to held orders from the next dispatch onward. Turns keep generating on schedule. In-flight dispatches finish or time out into `held`. Nothing needs undoing.

For a full stop including the event handlers:

```bash
gcloud run services update galaxies-ai --region=$REGION --update-env-vars=GALAXIES_AI_ENABLED=false
```

To close the door from the other side as well, so even a stale runner revision cannot submit:

```bash
gcloud run services update galaxies-api --region=$REGION --update-env-vars=API_AI_ORDERS_ENABLED=false
```

Then make it durable, so the next `terraform apply` does not quietly turn AI back on:

```bash
cd infra/terraform
terraform apply -var="ai_dispatch_enabled=false" -var="galaxies_ai_enabled=false" \
                -var="api_ai_orders_enabled=false" ...
```

Rolling a bad revision back is ordinary Cloud Run:

```bash
gcloud run revisions list --service=galaxies-ai --region=$REGION --format='table(name,active,createTime)'
gcloud run services update-traffic galaxies-ai --region=$REGION --to-revisions=<GOOD_REVISION>=100
```

Firestore additions (`ai_seats`, `ai_runs`) are additive and are ignored while the flags are off, so there is nothing to migrate in either direction. The one thing rollback does not undo is a seat already flipped to `ai_takeover`: turning `AI_TAKEOVER_ENABLED` off stops new takeovers but leaves existing ones pinned. Reverting one is a deliberate write to `games/{gameId}/ai_seats/{empireId}.controller`, and it should be, because it hands an empire back to a human mid-game.

---

## Section 7. Clean up after smoking

```bash
gsutil -m rm -r "$STATE/ai-runs/$GID/"     || true
gsutil -m rm -r "$ORDERS/games/$GID/"      || true
gsutil -m rm -r "$INTEL/games/$GID/"       || true
```

Delete the `ai_runs` documents for `ZZTEST` and the test game itself through the API's own lifecycle route rather than by hand, so the control plane stays consistent.

---

## Exit criteria for M3

Flip 1 and Flip 2 stay on when all of the following hold:

1. A real solo-versus-AI game runs unattended for five consecutive turns with the AI seat submitting every turn.
2. Negative 1 passes: a dead participant yields `held` and the turn still generates on schedule.
3. Negative 2 passes: a duplicate dispatch leaves both the orders object generation and the run record untouched.
4. Negative 3 passes: the audit log shows exactly one intel object read per dispatch, and it is the dispatched empire's own.
5. The `ai-` task for a seat is scheduled strictly earlier than the `gen-` deadline task for the same game and year.
6. `galaxies-dead-letter` is empty for the `ai-*` subscriptions across the whole smoke window.
