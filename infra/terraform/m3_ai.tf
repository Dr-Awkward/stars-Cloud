# Galaxies infrastructure, M3 slice: AI seats.
# Adds the galaxies-ai runner (dispatch worker, empire_view transcoder, run
# lock), the built-in participant ai-nova-default, their two service accounts
# and IAM, and the three Pub/Sub push subscriptions that drive dispatch
# (galaxies-ai spec Sections 2.3, 3.1, 6, 7.1, 8). M0's main.tf and M2's
# m2_clock.tf are unchanged apart from one flag env var on galaxies-api.
#
# Everything here ships dark. Every flag defaults to false in variables.tf, so
# the first apply provisions live services that do nothing: dispatch records
# held orders and submits nothing, and every AI seat runs on held orders. That
# is the safe default and the kill switch, not a temporary state.

data "google_project" "this" {
  project_id = var.project_id
}

locals {
  # Google-managed service agents. Cloud Tasks and Pub/Sub mint the OIDC tokens
  # that reach galaxies-ai, so both agents need invoker on it.
  tasks_agent  = "serviceAccount:service-${data.google_project.this.number}@gcp-sa-cloudtasks.iam.gserviceaccount.com"
  pubsub_agent = "serviceAccount:service-${data.google_project.this.number}@gcp-sa-pubsub.iam.gserviceaccount.com"
}

# ---- Service accounts -------------------------------------------------------
# These two break the sa-* naming of M0 and M2 on purpose: the spec's Section 14
# IAM block, the runbook, and every participant manifest name them galaxies-ai-sa
# and ai-nova-default-sa. Renaming them here would silently invalidate the
# copy-paste rollout, so the spec name wins.
resource "google_service_account" "ai" {
  project      = var.project_id
  account_id   = "galaxies-ai-sa"
  display_name = "Galaxies AI dispatch runner"
}

# The built-in participant. It holds no data-plane roles at all: it receives its
# view in the request body and returns orders in the response body, so it needs
# no bucket, no Firestore, and no token. An empty role set is the whole point.
resource "google_service_account" "ai_nova_default" {
  project      = var.project_id
  account_id   = "ai-nova-default-sa"
  display_name = "Nova default AI participant worker"
}

# ---- Runner IAM (least privilege) -------------------------------------------
resource "google_project_iam_member" "ai_firestore" {
  project = var.project_id
  role    = "roles/datastore.user"
  member  = "serviceAccount:${google_service_account.ai.email}"
}

# Enqueues the per-seat ai-{gameId}-{turnYear}-{empireId} tasks.
resource "google_project_iam_member" "ai_tasks_enqueuer" {
  project = var.project_id
  role    = "roles/cloudtasks.enqueuer"
  member  = "serviceAccount:${google_service_account.ai.email}"
}

# Read only on intel: the runner loads exactly one object per dispatch, the seat's
# own already fog-projected intel. It has no writer role here, so a bug cannot
# widen or corrupt another empire's view. The per-empire object path check is
# enforced in code (spec Section 10.1); this role is the second lock.
resource "google_storage_bucket_iam_member" "ai_intel" {
  bucket = google_storage_bucket.intel.name
  role   = "roles/storage.objectViewer"
  member = "serviceAccount:${google_service_account.ai.email}"
}

# Writes the durable ai-runs/ request and response copies that make every
# dispatch replayable by the harness.
resource "google_storage_bucket_iam_member" "ai_state" {
  bucket = google_storage_bucket.state.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${google_service_account.ai.email}"
}

# The runner never writes orders directly. It posts them to the galaxies-api
# internal orders route, which writes the same bucket a human client writes, so
# turngen's OrderReader validates AI and human orders through one pipeline.
# Deliberately no objectAdmin on the orders bucket.
resource "google_cloud_run_v2_service_iam_member" "ai_calls_api" {
  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.api.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.ai.email}"
}

# The runner is the only caller the participant will ever accept.
resource "google_cloud_run_v2_service_iam_member" "ai_calls_nova_default" {
  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.ai_nova_default.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.ai.email}"
}

# Cloud Tasks mints the dispatch OIDC token as sa-invoker (the same identity M2
# uses for the deadline task), so the runner must be allowed to act as it when
# it enqueues.
resource "google_service_account_iam_member" "ai_acts_as_invoker" {
  service_account_id = google_service_account.invoker.name
  role               = "roles/iam.serviceAccountUser"
  member             = "serviceAccount:${google_service_account.ai.email}"
}

# ---- Inbound invoker IAM on galaxies-ai -------------------------------------
# The runner is never public. Three identities may call it, and nothing else.
resource "google_cloud_run_v2_service_iam_member" "invoker_calls_ai" {
  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.ai.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.invoker.email}"
}

resource "google_cloud_run_v2_service_iam_member" "tasks_agent_calls_ai" {
  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.ai.name
  role     = "roles/run.invoker"
  member   = local.tasks_agent
}

resource "google_cloud_run_v2_service_iam_member" "pubsub_agent_calls_ai" {
  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.ai.name
  role     = "roles/run.invoker"
  member   = local.pubsub_agent
}

# Pub/Sub push signs its own OIDC token as sa-invoker, which requires the Pub/Sub
# service agent to be a token creator on that account. Without this the
# subscriptions deliver 403s that look like an application bug.
resource "google_service_account_iam_member" "pubsub_mints_invoker_tokens" {
  service_account_id = google_service_account.invoker.name
  role               = "roles/iam.serviceAccountTokenCreator"
  member             = local.pubsub_agent
}

# ---- galaxies-ai Cloud Run service (private) --------------------------------
resource "google_cloud_run_v2_service" "ai" {
  project             = var.project_id
  name                = "galaxies-ai"
  location            = var.region
  deletion_protection = false

  # INTERNAL_ONLY, not INTERNAL_LOAD_BALANCER as turngen uses. The distinction
  # matters: internal-load-balancer ingress rejects Pub/Sub push and Cloud Tasks
  # deliveries, and both are how this service is driven. Narrowing this further
  # would silently stop dispatch. There is no allUsers binding anywhere in this
  # file, so allow-unauthenticated is false and every call needs run.invoker.
  ingress = "INGRESS_TRAFFIC_INTERNAL_ONLY"

  template {
    service_account = google_service_account.ai.email

    # The runner is I/O bound: it waits on the participant, on GCS, and on the
    # API. Moderate concurrency is fine here, unlike turngen and the participant.
    max_instance_request_concurrency = var.ai_concurrency

    # Long enough to cover a slow participant plus submission, and still shorter
    # than the human deadline window the dispatch is scheduled ahead of.
    timeout = "${var.ai_request_timeout_s}s"

    scaling {
      min_instance_count = 0
      max_instance_count = var.ai_max_instances
    }

    containers {
      image = var.ai_image != "" ? var.ai_image : var.turngen_image

      resources {
        limits = {
          cpu    = "1"
          memory = "1Gi"
        }
        # Cold start is on the seat-turn critical path and dispatch is bursty
        # (one invocation per AI seat when a turn generates).
        startup_cpu_boost = true
      }

      # ---- Feature flags (spec Section 3.1). All default false in variables.tf.
      # The Cloud Build substitution is _GALAXIES_AI_ENABLED and the container
      # env var is GALAXIES_AI_ENABLED, no leading underscore. A substitution
      # without a matching env var is a silent no-op, which is why they are
      # declared here rather than left to the deploy command.
      env {
        name  = "GALAXIES_AI_ENABLED"
        value = tostring(var.galaxies_ai_enabled)
      }
      env {
        name  = "AI_DISPATCH_ENABLED"
        value = tostring(var.ai_dispatch_enabled)
      }
      env {
        name  = "AI_TAKEOVER_ENABLED"
        value = tostring(var.ai_takeover_enabled)
      }
      env {
        name  = "AI_REGISTRY_ENABLED"
        value = tostring(var.ai_registry_enabled)
      }
      env {
        name  = "AI_COMMUNITY_ENABLED"
        value = tostring(var.ai_community_enabled)
      }
      env {
        name  = "AI_LLM_ENABLED"
        value = tostring(var.ai_llm_enabled)
      }
      env {
        name  = "AI_LADDER_ENABLED"
        value = tostring(var.ai_ladder_enabled)
      }
      env {
        name  = "AI_LLM_MODEL"
        value = var.ai_llm_model
      }

      # ---- Wiring (spec Section 3.2) ----------------------------------------
      env {
        name  = "GOOGLE_CLOUD_PROJECT"
        value = var.project_id
      }
      env {
        name  = "GALAXIES_REGION"
        value = var.region
      }
      env {
        name  = "GALAXIES_API_URL"
        value = google_cloud_run_v2_service.api.uri
      }
      env {
        name  = "GALAXIES_TURNGEN_URL"
        value = google_cloud_run_v2_service.turngen.uri
      }
      env {
        name  = "INTEL_BUCKET"
        value = google_storage_bucket.intel.name
      }
      env {
        name  = "ORDERS_BUCKET"
        value = google_storage_bucket.orders.name
      }
      env {
        name  = "STATE_BUCKET"
        value = google_storage_bucket.state.name
      }
      env {
        name  = "TASKS_QUEUE"
        value = google_cloud_tasks_queue.deadlines.name
      }
      env {
        name  = "TASKS_INVOKER_SA"
        value = google_service_account.invoker.email
      }
      env {
        name  = "PARTICIPANT_NOVA_DEFAULT_URL"
        value = google_cloud_run_v2_service.ai_nova_default.uri
      }
      env {
        name  = "CONTRACT_VERSION"
        value = var.ai_contract_version
      }
      env {
        name  = "DEFAULT_TIMEOUT_S"
        value = tostring(var.ai_default_timeout_s)
      }
      env {
        name  = "DISPATCH_RETRY"
        value = tostring(var.ai_dispatch_retry)
      }
      env {
        name  = "MAX_ORDERS_PER_TURN"
        value = tostring(var.ai_max_orders_per_turn)
      }
      env {
        name  = "LLM_GAME_TOKEN_BUDGET"
        value = tostring(var.ai_llm_game_token_budget)
      }
      env {
        name  = "LLM_DAILY_TOKEN_BUDGET"
        value = tostring(var.ai_llm_daily_token_budget)
      }
    }
  }

  depends_on = [google_project_service.enabled]
}

# ---- ai-nova-default Cloud Run service (private participant) -----------------
resource "google_cloud_run_v2_service" "ai_nova_default" {
  project             = var.project_id
  name                = "ai-nova-default"
  location            = var.region
  deletion_protection = false

  # Reached only from galaxies-ai, in-project, service to service. No Pub/Sub or
  # Cloud Tasks delivery ever targets it, so the tighter ingress is correct here.
  ingress = "INGRESS_TRAFFIC_INTERNAL_ONLY"

  template {
    service_account = google_service_account.ai_nova_default.email

    # Concurrency 1 is mandatory, not a tuning choice. DefaultAi and its sub-AIs
    # run on the ported Nova domain model, which carries process-wide mutable
    # statics (the component and race tables, AllComponents and friends) and a
    # large mutable object graph per call. Two seat-turns in one process would
    # interleave on that shared state and silently produce wrong orders for both,
    # with no exception to notice. Do not raise this to "improve utilization";
    # scale out with instances instead.
    max_instance_request_concurrency = 1

    # A hard wall-clock cap slightly above the participant timeout the runner
    # enforces, so the platform is the backstop and the runner is the primary.
    timeout = "${var.ai_nova_default_timeout_s}s"

    scaling {
      min_instance_count = 0
      max_instance_count = var.ai_nova_default_max_instances
    }

    containers {
      image = var.ai_nova_default_image != "" ? var.ai_nova_default_image : var.turngen_image

      resources {
        limits = {
          cpu    = "1"
          memory = "1Gi"
        }
        startup_cpu_boost = true
      }

      env {
        name  = "CONTRACT_VERSION"
        value = var.ai_contract_version
      }
      env {
        name  = "MAX_ORDERS_PER_TURN"
        value = tostring(var.ai_max_orders_per_turn)
      }
    }
  }

  # Egress, stated honestly. The manifest declares network: none for this
  # participant, and the process genuinely needs no outbound call: it reads its
  # view from the request body and writes orders to the response body. Cloud Run
  # has no "deny all egress" switch, though. Real default-deny needs Direct VPC
  # egress into a subnet whose firewall denies all egress, and M3 provisions no
  # VPC. So what is enforced today is: no credentials in the container, no
  # data-plane IAM on its service account (see above), and no inbound path except
  # galaxies-ai. That is containment by capability, not by network. The network
  # half lands with the community-image work in M6 (spec Section 10.2), which is
  # when an untrusted image first runs here and the gap actually starts to matter.

  depends_on = [google_project_service.enabled]
}

# ---- Cloud Tasks: no new queue ----------------------------------------------
# The per-seat ai-{gameId}-{turnYear}-{empireId} tasks go onto the EXISTING
# galaxies-deadlines queue from m2_clock.tf, deliberately. One queue means one
# place where the whole clock's rate limits and retry policy live, so an AI
# dispatch storm is throttled by the same max_concurrent_dispatches that bounds
# turn generation and cannot starve the gen-{gameId}-{turnYear} deadline tasks it
# shares a budget with. A second queue would let AI load and deadline load each
# think it had the full budget, which is exactly the failure the held-orders
# design exists to prevent. Task names stay unique across both uses because of
# the ai- and gen- prefixes, and Cloud Tasks name-based dedup still gives
# per-seat idempotency on top of the Firestore run lock.

# ---- Pub/Sub push subscriptions (spec Section 7.1) --------------------------
# No new topic. The three M2 topics gain one subscriber each. Per-seat fan-out is
# Cloud Tasks, not Pub/Sub.
resource "google_pubsub_subscription" "ai_turn_generated" {
  project = var.project_id
  name    = "ai-turn-generated"
  topic   = google_pubsub_topic.turn_generated.id

  ack_deadline_seconds       = var.ai_push_ack_deadline_s
  message_retention_duration = "86400s"

  push_config {
    push_endpoint = "${google_cloud_run_v2_service.ai.uri}/events/turn-generated"
    oidc_token {
      service_account_email = google_service_account.invoker.email
      audience              = google_cloud_run_v2_service.ai.uri
    }
  }

  # A handler that keeps failing must not retry forever against a live game.
  dead_letter_policy {
    dead_letter_topic     = google_pubsub_topic.dead_letter.id
    max_delivery_attempts = var.ai_dead_letter_max_attempts
  }

  retry_policy {
    minimum_backoff = "10s"
    maximum_backoff = "600s"
  }

  depends_on = [google_project_service.enabled]
}

resource "google_pubsub_subscription" "ai_game_created" {
  project = var.project_id
  name    = "ai-game-created"
  topic   = google_pubsub_topic.game_created.id

  ack_deadline_seconds       = var.ai_push_ack_deadline_s
  message_retention_duration = "86400s"

  push_config {
    push_endpoint = "${google_cloud_run_v2_service.ai.uri}/events/game-created"
    oidc_token {
      service_account_email = google_service_account.invoker.email
      audience              = google_cloud_run_v2_service.ai.uri
    }
  }

  dead_letter_policy {
    dead_letter_topic     = google_pubsub_topic.dead_letter.id
    max_delivery_attempts = var.ai_dead_letter_max_attempts
  }

  retry_policy {
    minimum_backoff = "10s"
    maximum_backoff = "600s"
  }

  depends_on = [google_project_service.enabled]
}

resource "google_pubsub_subscription" "ai_deadline_approaching" {
  project = var.project_id
  name    = "ai-deadline-approaching"
  topic   = google_pubsub_topic.deadline_approaching.id

  ack_deadline_seconds       = var.ai_push_ack_deadline_s
  message_retention_duration = "86400s"

  push_config {
    push_endpoint = "${google_cloud_run_v2_service.ai.uri}/events/deadline-approaching"
    oidc_token {
      service_account_email = google_service_account.invoker.email
      audience              = google_cloud_run_v2_service.ai.uri
    }
  }

  dead_letter_policy {
    dead_letter_topic     = google_pubsub_topic.dead_letter.id
    max_delivery_attempts = var.ai_dead_letter_max_attempts
  }

  retry_policy {
    minimum_backoff = "10s"
    maximum_backoff = "600s"
  }

  depends_on = [google_project_service.enabled]
}

# Dead lettering is not automatic. Pub/Sub forwards a poisoned message as its own
# service agent, so that agent needs publish on the dead-letter topic and
# subscribe on each subscription. Without both, delivery simply retries forever
# and the dead_letter_policy above is decorative.
resource "google_pubsub_topic_iam_member" "pubsub_agent_publishes_dead_letter" {
  project = var.project_id
  topic   = google_pubsub_topic.dead_letter.name
  role    = "roles/pubsub.publisher"
  member  = local.pubsub_agent
}

resource "google_pubsub_subscription_iam_member" "pubsub_agent_subscribes_turn_generated" {
  project      = var.project_id
  subscription = google_pubsub_subscription.ai_turn_generated.name
  role         = "roles/pubsub.subscriber"
  member       = local.pubsub_agent
}

resource "google_pubsub_subscription_iam_member" "pubsub_agent_subscribes_game_created" {
  project      = var.project_id
  subscription = google_pubsub_subscription.ai_game_created.name
  role         = "roles/pubsub.subscriber"
  member       = local.pubsub_agent
}

resource "google_pubsub_subscription_iam_member" "pubsub_agent_subscribes_deadline_approaching" {
  project      = var.project_id
  subscription = google_pubsub_subscription.ai_deadline_approaching.name
  role         = "roles/pubsub.subscriber"
  member       = local.pubsub_agent
}
