# Galaxies infrastructure, M2 slice: the clock and the public API.
# Adds the galaxies-api Cloud Run service, the per-game deadline queue
# (Cloud Tasks), the one-minute backstop sweep (Cloud Scheduler), the result
# fan-out topics (Pub/Sub), and the API/invoker service accounts and secrets
# (design Sections B.2, B.4, B.5, D.3). M0's main.tf is unchanged.

# ---- Service accounts -------------------------------------------------------
resource "google_service_account" "api" {
  project      = var.project_id
  account_id   = "sa-api"
  display_name = "Galaxies public API gateway"
}

# The identity Cloud Tasks and Cloud Scheduler mint OIDC tokens as, to invoke the
# private internal endpoints on galaxies-api and galaxies-turngen.
resource "google_service_account" "invoker" {
  project      = var.project_id
  account_id   = "sa-invoker"
  display_name = "Galaxies OIDC invoker for tasks and scheduler"
}

# ---- API IAM (least privilege) ----------------------------------------------
resource "google_project_iam_member" "api_firestore" {
  project = var.project_id
  role    = "roles/datastore.user"
  member  = "serviceAccount:${google_service_account.api.email}"
}

resource "google_storage_bucket_iam_member" "api_orders" {
  bucket = google_storage_bucket.orders.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${google_service_account.api.email}"
}

resource "google_storage_bucket_iam_member" "api_intel" {
  bucket = google_storage_bucket.intel.name
  role   = "roles/storage.objectViewer"
  member = "serviceAccount:${google_service_account.api.email}"
}

resource "google_project_iam_member" "api_tasks_enqueuer" {
  project = var.project_id
  role    = "roles/cloudtasks.enqueuer"
  member  = "serviceAccount:${google_service_account.api.email}"
}

resource "google_project_iam_member" "api_pubsub_publisher" {
  project = var.project_id
  role    = "roles/pubsub.publisher"
  member  = "serviceAccount:${google_service_account.api.email}"
}

# The API invokes the private turngen service to run a turn under the lock.
resource "google_cloud_run_v2_service_iam_member" "api_calls_turngen" {
  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.turngen.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.api.email}"
}

# The invoker (tasks/scheduler) may call the API's private internal endpoints.
resource "google_cloud_run_v2_service_iam_member" "invoker_calls_api" {
  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.api.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.invoker.email}"
}

# The API also needs to mint OIDC tokens as sa-invoker when enqueuing tasks.
resource "google_service_account_iam_member" "api_acts_as_invoker" {
  service_account_id = google_service_account.invoker.name
  role               = "roles/iam.serviceAccountUser"
  member             = "serviceAccount:${google_service_account.api.email}"
}

# ---- Secrets (values injected out of band) ----------------------------------
resource "google_secret_manager_secret" "jwt_signing_key" {
  project   = var.project_id
  secret_id = "galaxies-jwt-signing-key"
  replication {
    auto {}
  }
  depends_on = [google_project_service.enabled]
}

resource "google_secret_manager_secret" "google_client_id" {
  project   = var.project_id
  secret_id = "galaxies-google-client-id"
  replication {
    auto {}
  }
  depends_on = [google_project_service.enabled]
}

resource "google_secret_manager_secret_iam_member" "api_reads_jwt_key" {
  secret_id = google_secret_manager_secret.jwt_signing_key.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.api.email}"
}

# The matching grant for the Google client id. Its absence is why the secret was
# declared but useless: a secret nobody may read cannot be mounted, and the mount
# would have failed the revision even once it was added.
resource "google_secret_manager_secret_iam_member" "api_reads_google_client_id" {
  secret_id = google_secret_manager_secret.google_client_id.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.api.email}"
}

# ---- Cloud Tasks: per-game deadline queue (design B.2) ----------------------
# One task per (gameId, turnYear), named so a second enqueue is deduped, firing
# at the deadline into the API's /internal/deadline-fire with an OIDC token.
resource "google_cloud_tasks_queue" "deadlines" {
  project  = var.project_id
  location = var.region
  name     = "galaxies-deadlines"

  rate_limits {
    max_dispatches_per_second = 50
    max_concurrent_dispatches = 20
  }
  retry_config {
    max_attempts  = 5
    min_backoff   = "5s"
    max_backoff   = "300s"
    max_doublings = 4
  }
  depends_on = [google_project_service.enabled]
}

# ---- Cloud Scheduler: one-minute backstop sweep (design B.2) -----------------
# Belt and suspenders: catches any deadline whose Cloud Tasks entry failed to
# fire, by re-arming overdue games through /internal/sweep.
resource "google_cloud_scheduler_job" "sweep" {
  project   = var.project_id
  region    = var.region
  name      = "galaxies-deadline-sweep"
  schedule  = var.sweep_schedule
  time_zone = "Etc/UTC"

  http_target {
    http_method = "POST"
    uri         = "${google_cloud_run_v2_service.api.uri}/internal/sweep"
    oidc_token {
      service_account_email = google_service_account.invoker.email
    }
  }
  depends_on = [google_project_service.enabled]
}

# ---- Pub/Sub: result fan-out (design B.4) -----------------------------------
resource "google_pubsub_topic" "turn_generated" {
  project    = var.project_id
  name       = "turn-generated"
  depends_on = [google_project_service.enabled]
}

resource "google_pubsub_topic" "game_created" {
  project    = var.project_id
  name       = "game-created"
  depends_on = [google_project_service.enabled]
}

resource "google_pubsub_topic" "deadline_approaching" {
  project    = var.project_id
  name       = "deadline-approaching"
  depends_on = [google_project_service.enabled]
}

resource "google_pubsub_topic" "dead_letter" {
  project    = var.project_id
  name       = "galaxies-dead-letter"
  depends_on = [google_project_service.enabled]
}

# ---- galaxies-api Cloud Run service (public, OAuth verified in-app) ----------
resource "google_cloud_run_v2_service" "api" {
  project             = var.project_id
  name                = "galaxies-api"
  location            = var.region
  deletion_protection = false
  ingress             = "INGRESS_TRAFFIC_ALL"

  template {
    service_account                  = google_service_account.api.email
    max_instance_request_concurrency = 80

    scaling {
      min_instance_count = 0
      max_instance_count = var.api_max_instances
    }

    containers {
      image = var.api_image != "" ? var.api_image : var.turngen_image

      # Cloud Run defaults the container port to 8080 when no ports block is
      # given. galaxies-api listens on 8080 (ASPNETCORE_URLS in its Dockerfile), so
      # without this the platform probes the wrong port and the revision never
      # becomes ready. The cloudbuild path passed --port and worked; the
      # Terraform path did not, and the two disagreed silently.
      ports {
        container_port = 8080
      }

      resources {
        limits = {
          cpu    = "1"
          memory = "512Mi"
        }
      }

      env {
        name  = "GALAXIES_PROJECT_ID"
        value = var.project_id
      }
      env {
        name  = "GALAXIES_REGION"
        value = var.region
      }
      env {
        name  = "GALAXIES_ORDERS_BUCKET"
        value = google_storage_bucket.orders.name
      }
      env {
        name  = "GALAXIES_INTEL_BUCKET"
        value = google_storage_bucket.intel.name
      }
      env {
        name  = "GALAXIES_DEADLINE_QUEUE"
        value = google_cloud_tasks_queue.deadlines.name
      }
      env {
        name  = "GALAXIES_TURNGEN_URL"
        value = google_cloud_run_v2_service.turngen.uri
      }
      env {
        name  = "GALAXIES_INVOKER_SA"
        value = google_service_account.invoker.email
      }
      # Every Cloud Tasks deadline target is built from this. Nothing set it, so
      # Api/Program.cs fell back to http://localhost and every deadline task was
      # created pointing at the loopback address of whichever machine happened to
      # run the API. No deadline was ever deliverable, and nothing complained,
      # because a fallback string is a perfectly valid string.
      #
      # Two-phase: see variables.tf. The service cannot reference its own uri.
      env {
        name  = "GALAXIES_API_BASE_URL"
        value = var.api_base_url
      }
      # M3 gate on the internal AI orders route (see m3_ai.tf). Declared here
      # rather than set by hand at flip time, because an env var applied with
      # gcloud run services update is wiped by the next terraform apply, which
      # would turn AI seats off again without anyone noticing.
      env {
        name  = "API_AI_ORDERS_ENABLED"
        value = tostring(var.api_ai_orders_enabled)
      }
      env {
        name = "GALAXIES_JWT_SIGNING_KEY"
        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.jwt_signing_key.secret_id
            version = "latest"
          }
        }
      }
      # The google_client_id secret was created and then never granted and never
      # mounted, so the API validated every Google ID token against the audience
      # "unset.apps.googleusercontent.com" and no player could sign in. The API now
      # refuses to start outside Development without this, which turns a silent
      # authentication failure into a failed revision.
      env {
        name = "GALAXIES_GOOGLE_CLIENT_ID"
        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.google_client_id.secret_id
            version = "latest"
          }
        }
      }
    }
  }

  depends_on = [google_project_service.enabled]
}

# The API is the public front door: allow unauthenticated ingress (it verifies
# Google ID tokens in-app; unauth callers get 401 from the app, not the platform).
resource "google_cloud_run_v2_service_iam_member" "api_public" {
  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.api.name
  role     = "roles/run.invoker"
  member   = "allUsers"
}
