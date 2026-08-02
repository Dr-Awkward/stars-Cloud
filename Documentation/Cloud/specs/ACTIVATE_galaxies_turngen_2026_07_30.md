# ACTIVATE galaxies-turngen on roybot (M0 exit criterion 3, 2026-07-30)

The deploy instruction of record for the M0 slice. Follow this rather than improvising a
rollout.

This puts one service on GCP, `galaxies-turngen`, and uses it to generate exactly one turn
of the committed `fixture-2p` game against real GCS buckets. That is M0 exit criterion 3
and nothing more. There is no API, no clock, no AI, no sign-in, and no player can reach
anything: the service is private, scales to zero, and is invoked by you with an identity
token.

Criterion 3 is the last thing standing between "the code works" and "the code has met the
network". Criteria 1 and 2 are already met, and criterion 4 (the golden turn captured on
.NET Framework 4.8) is a Windows job that this deploy neither helps nor blocks.

All values are pinned to project `roybot`, region `us-central1`. Every command below is
copy-pasteable as written.

**Cost and blast radius.** Three GCS buckets, one Artifact Registry repo, one Firestore
database, one Cloud Run service that scales to zero, and one service account. At rest this
is pennies a month: storage of a 164 KB fixture plus one container image, and no compute
until you invoke it. The `terraform destroy` at the bottom removes all of it. Nothing here
touches DNS, billing alerts, or any existing `roybot` resource.

---

## The switches, upfront

| Control | Where | Ships | Effect / turning it OFF |
|---|---|---|---|
| `turngen_image` | `terraform apply -var` | required, no default | The image the service runs. No safe default exists, which is deliberate. |
| `turngen_max_instances` | terraform variable | `10` | Ceiling on concurrent generations. Each instance takes one game (concurrency 1). |
| `GALAXIES_LOCAL_ROOT` | Cloud Run env | **unset** | Unset means GCS. Setting it switches the service to filesystem storage, which on Cloud Run means an in-memory disk that is destroyed at scale to zero. Do not set it here. |
| `GALAXIES_SCRATCH_ROOT` | Cloud Run env | **unset** | Unset falls back to `/tmp/galaxies`. It must be an absolute path; the service now refuses to start otherwise. |
| Cloud Run IAM | `roles/run.invoker` | granted to you only | Nobody else can call the service. There is no `allUsers` binding anywhere in this slice. |
| `turngen_ingress` | terraform variable | `INGRESS_TRAFFIC_ALL` | Private via IAM, not public. Narrow to `INTERNAL_ONLY` in M2. |
| `m2_clock.tf`, `m3_ai.tf` | moved aside in step 2 | **not applied** | M0 deploys turngen only. Leaving them in place deploys three more services you do not want yet; see step 2. |

**To revert everything:** step 9.

---

## Before you start

You need, on your own machine:

- `gcloud` authenticated as a principal with Owner or equivalent on `roybot`
- `terraform` 1.x with the `hashicorp/google` provider (pinned to 6.50.0 in
  `.terraform.lock.hcl`, which is committed and should not be regenerated)
- `docker`, working. On WSL this means Docker Desktop with integration enabled for the
  distro. Building from `/mnt/c` walks the whole OneDrive tree and takes roughly ten
  minutes cold; from a native Linux checkout it is under two.
- `curl` and `jq`

Set the shell up once:

```bash
export PROJECT=roybot
export REGION=us-central1
export REPO=${REGION}-docker.pkg.dev/${PROJECT}/roybot-galaxies
gcloud config set project "$PROJECT"
gcloud auth login
gcloud auth configure-docker "${REGION}-docker.pkg.dev"
```

Confirm billing is on the project before anything else, because `terraform apply` fails
halfway through service enablement otherwise and leaves a partial state:

```bash
gcloud billing projects describe "$PROJECT" --format="value(billingEnabled)"
# expect: True
```

---

## 0. What you no longer have to fix by hand

An earlier draft of this runbook asked you to hand-edit two things. Both are now fixed in
the committed Terraform, so this section is here to say what changed rather than to give
you work.

**Ingress.** `main.tf` used to hardcode `INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER`, and no
load balancer exists anywhere in `infra/`. That combination made the service reachable from
nowhere after a successful apply: not from your laptop, and not from a VM in the VPC either.
The failure looks like a network timeout and says nothing about the cause.

It is now `var.turngen_ingress`, defaulting to `INGRESS_TRAFFIC_ALL`. `ALL` is not the same
as public: no `allUsers` invoker binding exists in this slice, so Cloud Run rejects every
unauthenticated request at the edge with a 403. `ALL` only means a request may arrive from
the internet before IAM judges it, which is what lets you smoke it with an identity token
from your own terminal. Narrow it with `-var="turngen_ingress=INGRESS_TRAFFIC_INTERNAL_ONLY"`
once galaxies-api calls it from inside the VPC in M2.

**`turngen_image` still has no default,** deliberately. There is no safe default image, so a
bare `terraform plan` errors until you pass one. You pass it on the command line in step 3,
so no tfvars file is needed.

---

## 1. Create the state bucket, once, by hand

The GCS backend in `versions.tf` is partial on purpose, and the bucket it wants is not
itself declared in Terraform. This is the one chicken-and-egg step.

```bash
gsutil mb -p "$PROJECT" -l "$REGION" "gs://${PROJECT}-galaxies-tfstate"
gsutil versioning set on "gs://${PROJECT}-galaxies-tfstate"
```

Versioning matters: it is the only thing standing between a bad apply and a lost state
file.

---

## 2. Reduce the config to the M0 slice

Applying the directory as committed would also create `galaxies-api`, `galaxies-ai`, and
`ai-nova-default`. You do not want that yet, and one of them will actively fail: `api_image`
and the two AI image variables default to empty and fall back to the turngen image, so
Cloud Run would start the turn generator under the name `galaxies-api`, and the real API
now refuses to boot outside Development without its two secrets, which do not exist yet.

Move them aside:

```bash
cd infra/terraform
mkdir -p .not-yet
mv m2_clock.tf m3_ai.tf .not-yet/
```

They come back in M2. `.not-yet/` is inside `infra/`, which `.dockerignore` already
excludes, and `.gitignore` does not ignore it, so do not commit while they are moved.

---

## 3. Terraform init and apply

```bash
terraform init -backend-config="bucket=${PROJECT}-galaxies-tfstate"
terraform plan -var="turngen_image=${REPO}/galaxies-turngen:bootstrap"
```

The plan should show roughly: nine `google_project_service`, one Artifact Registry repo,
three buckets, one Firestore database, one service account, three bucket IAM bindings, and
one Cloud Run service. If it shows a Cloud Tasks queue, a Pub/Sub topic, or a second Cloud
Run service, step 2 did not take.

The image tag above does not exist yet. Apply the infrastructure without the service first,
so the registry exists to push to:

```bash
terraform apply -var="turngen_image=${REPO}/galaxies-turngen:bootstrap" \
  -target=google_project_service.enabled \
  -target=google_artifact_registry_repository.images \
  -target=google_storage_bucket.state \
  -target=google_storage_bucket.orders \
  -target=google_storage_bucket.intel
```

Firestore is not needed for M0 and is created by the full apply in step 5.

---

## 4. Build and push the image

From the repository root, not from `infra/`:

```bash
cd ../..
export TAG=$(git rev-parse --short HEAD)
docker build -f ServerHost/Dockerfile -t "${REPO}/galaxies-turngen:${TAG}" .
docker push "${REPO}/galaxies-turngen:${TAG}"
```

Tag by commit, not `latest`. A turn is only reproducible against a known engine build, and
criterion 4 depends on being able to say which one generated a given state file.

Confirm it landed:

```bash
gcloud artifacts docker images list "${REPO}/galaxies-turngen" --format="table(version,createTime)"
```

---

## 5. Apply the service

```bash
cd infra/terraform
terraform apply -var="turngen_image=${REPO}/galaxies-turngen:${TAG}"
export TURNGEN_URL=$(terraform output -raw turngen_url)
echo "$TURNGEN_URL"
```

If the revision never becomes ready, read the logs before changing anything:

```bash
gcloud run services logs read galaxies-turngen --region="$REGION" --limit=50
```

The two failures worth recognising: `Unable to locate component definition file` means
`components.xml` did not make it into the image, and a `UnauthorizedAccessException` on a
path under `/app` means the scratch root resolved to a relative path. Both are fixed in the
current build and both would indicate you pushed an older commit.

---

## 6. Seed the fixture into GCS

The committed fixture is the same two-empire game the local and container runs used, so a
turn generated here is directly comparable to one generated on your machine.

```bash
cd ../..
gsutil cp Tests/Fixtures/games/fixture-2p/state/current.sstate \
  "gs://${PROJECT}-galaxies-state/games/fixture-2p/state/current.sstate"

gsutil ls -r "gs://${PROJECT}-galaxies-state/games/fixture-2p/"
# expect exactly one object: .../state/current.sstate
```

No orders are seeded. Both empires hold, which is a legitimate turn and keeps this step to
one variable. Submitting orders is step 8.

---

## 7. Generate one turn on roybot

Grant yourself invoker, then call it:

```bash
gcloud run services add-iam-policy-binding galaxies-turngen \
  --region="$REGION" \
  --member="user:$(gcloud config get-value account)" \
  --role="roles/run.invoker"

curl -s -X POST "${TURNGEN_URL}/internal/games/fixture-2p/generate" \
  -H "Authorization: Bearer $(gcloud auth print-identity-token)" | jq .
```

Expected, exactly:

```json
{
  "turnYear": 2101,
  "newStatePath": "games/fixture-2p/state/2101.sstate",
  "empireIds": [1, 2],
  "aiEmpireIds": [],
  "gameEnded": false
}
```

`gameEnded` must be `false`. If it is `true` you are running a build from before the
`GameInProgress` fix, and every game that service touches would be reported finished after
one turn.

---

## 8. Verify, and then verify the part that matters

Health and the negative case first:

```bash
TOKEN=$(gcloud auth print-identity-token)
curl -s -o /dev/null -w "healthz %{http_code}\n" "${TURNGEN_URL}/healthz" -H "Authorization: Bearer $TOKEN"
curl -s -o /dev/null -w "unknown game %{http_code}\n" -X POST \
  "${TURNGEN_URL}/internal/games/does-not-exist/generate" -H "Authorization: Bearer $TOKEN"
# expect: healthz 200, unknown game 404
```

Then the objects. This is the actual criterion:

```bash
gsutil ls -r "gs://${PROJECT}-galaxies-state/games/fixture-2p/"
gsutil ls -r "gs://${PROJECT}-galaxies-intel/games/fixture-2p/"
```

Expected:

```
state:  games/fixture-2p/state/current.sstate
        games/fixture-2p/state/2101.sstate
        games/fixture-2p/backup/2100/fixture-2p.sstate
intel:  games/fixture-2p/intel/2101/1.intel
        games/fixture-2p/intel/2101/2.intel
```

**One intel object per empire, keyed by empire id.** If the intel prefix is empty, the turn
generated and delivered nothing, which is the failure mode that hid for the entire life of
this project until it was run for real.

### The check worth doing while you are here

Compare the turn generated on GCP against the one your machine generates. Turn generation
is byte deterministic on Linux as of this commit, so these should match exactly, and a
mismatch is the first real evidence of an environment-dependent difference.

```bash
gsutil cp "gs://${PROJECT}-galaxies-state/games/fixture-2p/state/2101.sstate" /tmp/roybot-2101.sstate

ROOT=/tmp/local-2101 && rm -rf $ROOT && mkdir -p $ROOT
cp -r Tests/Fixtures/games "$ROOT/"
GALAXIES_LOCAL_ROOT=$ROOT dotnet run --project ServerHost -c Release -- generate fixture-2p

norm() { sed -E 's#<(GameFolder|StatePathName)>[^<]*#<\1>#g' "$1"; }
diff <(norm /tmp/roybot-2101.sstate) <(norm $ROOT/games/fixture-2p/state/2101.sstate) \
  && echo "IDENTICAL once the scratch path is normalised"
```

The two path elements are normalised because they hold the scratch directory, which differs
between a container and your machine by construction. Everything else must match. If it does
not, capture both files before touching anything: that is a genuine finding and it is
exactly what criterion 4 exists to detect.

---

## 9. OFF-ramp

Stop generation immediately without destroying anything:

```bash
gcloud run services remove-iam-policy-binding galaxies-turngen \
  --region="$REGION" \
  --member="user:$(gcloud config get-value account)" \
  --role="roles/run.invoker"
```

Nobody can invoke it after that, and it is already scaled to zero.

Full teardown:

```bash
cd infra/terraform
terraform destroy -var="turngen_image=${REPO}/galaxies-turngen:${TAG}"
mv .not-yet/*.tf . && rmdir .not-yet
```

`terraform destroy` will not remove the tfstate bucket you made by hand in step 1, nor the
pushed images. Remove those separately if you want the project clean:

```bash
gsutil -m rm -r "gs://${PROJECT}-galaxies-tfstate"
gcloud artifacts repositories delete roybot-galaxies --location="$REGION"
```

---

## What is NOT covered

Stated plainly so a green run does not overclaim.

- **No API, no clock, no auth.** `galaxies-api` is not deployed, so there is no sign-in,
  no lobby, no deadline, and no exactly-once generation lock. You are the lock: invoke it
  once.
- **No AI.** M3's services stay in `.not-yet/`.
- **No player has played anything.** This proves the pipe, not the game. Criterion 3 says
  "the same container does the same thing on roybot", and that is all it says.
- **Orders are not exercised against GCS.** Step 6 seeds no orders. The orders path is
  covered by tests and by the local container run, but the GCS listing branch in
  `GcsGameStore.DownloadOrdersAsync` will not have executed against real GCS until an API
  writes an order. If you want to close that here, upload one to
  `gs://roybot-galaxies-orders/games/fixture-2p/orders/2101/1.orders` before the second
  generate and confirm the log line says `Staged 1 of 2`.
- **No load balancer, no custom domain, no monitoring, no alerting, no budget alert.** None
  of it is declared in `infra/`. Before anything is public, that list is the work.
- **Firestore is created but unused.** M0 keeps no control-plane state.

---

## Known defects that will bite you in M2, not here

These are confirmed by reading the committed Terraform against the code that reads it. They
do not affect this runbook, because none of the affected services deploy in the M0 slice.
Fix them before `m2_clock.tf` comes back out of `.not-yet/`.

All five defects this section originally listed are now **fixed** in the committed
Terraform. They are kept here as a record of what to re-check if a future edit reintroduces
one, because every single one failed silently rather than loudly.

| What was wrong | Where | How it failed | Fixed by |
|---|---|---|---|
| `GALAXIES_API_BASE_URL` set by nothing | `m2_clock.tf` | `Api/Program.cs` built every Cloud Tasks deadline against `http://localhost/internal/deadline-fire`. Cloud Tasks accepted each task and delivered it nowhere, so deadline-driven generation never happened and nothing logged a problem | Now set from `var.api_base_url`, and `CloudTasksDeadlineScheduler` refuses a non-http target rather than arming it |
| `galaxies-google-client-id` created but never granted or mounted | `m2_clock.tf` | Every Google ID token validated against the audience `unset.apps.googleusercontent.com`, so nobody could sign in | Grant and mount both added. The API also refuses to start outside Development without it, so this now shows as a failed revision |
| No `ports {}` on any container | all four Cloud Run resources | Cloud Run defaults to 8080; `galaxies-ai` listens on 8082 and `ai-nova-default` on 8084, so neither revision would ever become ready. The cloudbuild path passed `--port` and worked, so the two deploy paths disagreed | `ports { container_port }` added to all four, matching each Dockerfile |
| Four env names disagreed with the code | `m3_ai.tf` | `PARTICIPANT_NOVA_DEFAULT_URL` vs `DEFAULT_PARTICIPANT_URL`, `TASKS_INVOKER_SA` vs `GALAXIES_INVOKER_SA`, `MAX_ORDERS_PER_TURN` vs `PARTICIPANT_MAX_ORDERS`, and `GALAXIES_AI_URL` set by nothing. All fell through to defaults, so the runner had no AI to call and minted OIDC as the wrong identity | Renamed to match `AiService/Config.cs` and `Participants/NovaDefault/Program.cs`. `PARTICIPANT_ENABLED` added so the flip is a variable rather than a `gcloud` edit the next apply would revert |
| Runbook paths did not exist | `ACTIVATE_galaxies_ai.md` | `participants/nova-default/` and `galaxies-ai/` are not real directories, and on Linux the submit fails on case alone | Corrected to `Participants/NovaDefault/` and `AiService/`. The claim about per-directory Cloud Build triggers was also removed: none exist |

**Still true and not fixed:** `galaxies-api` and `galaxies-ai` cannot derive their own URLs
in Terraform, because a service referencing its own `uri` is a dependency cycle. Both are
therefore two-phase: apply once with `api_base_url` and `ai_base_url` empty, read
`terraform output api_url` and `ai_url`, set them, apply again. Forgetting the second phase
is safe rather than silent, which is the point of the two guards above.

---

## What this deploy is actually testing

Worth being clear about, because the value is not in the green checkmarks.

Everything in the Galaxies cloud port was compile-verified and unit-tested against
in-memory doubles for its entire life. The first time a turn was run for real, on 2026-07-30,
it produced four silent defects in an afternoon: orders were never read, intel was never
delivered, no saved game containing a fleet could load headless, and every game reported
itself over after one turn. Building and running the actual containers produced three more,
including an empty JWT signing key. None of them crashed. All of them would have shipped.

This deploy is the same exercise one layer out. The value is in whatever it finds.
