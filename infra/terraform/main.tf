# Galaxies infrastructure, M0 slice, on GCP project roybot.
# This provisions only what "prove the pipe" needs: an image repository, the
# three game-state buckets, a least-privilege service account, a private Cloud
# Run turn-generation service, and Firestore (native) for the control plane that
# M2 will use. Auth, the scheduler, AI workers, and the notifier come in later
# milestones and are intentionally absent here.
#
# State is kept in a GCS backend so the team shares one source of truth. Fill the
# backend bucket in versions.tf (or via -backend-config) before init.

locals {
  services = [
    "run.googleapis.com",
    "artifactregistry.googleapis.com",
    "firestore.googleapis.com",
    "storage.googleapis.com",
    "cloudtasks.googleapis.com",
    "cloudscheduler.googleapis.com",
    "pubsub.googleapis.com",
    "secretmanager.googleapis.com",
    "iam.googleapis.com",
  ]
}

resource "google_project_service" "enabled" {
  for_each                   = toset(local.services)
  project                    = var.project_id
  service                    = each.value
  disable_on_destroy         = false
  disable_dependent_services = false
}

# ---- Image repository -------------------------------------------------------
resource "google_artifact_registry_repository" "images" {
  project       = var.project_id
  location      = var.region
  repository_id = "roybot-galaxies"
  description   = "Galaxies service and AI participant images."
  format        = "DOCKER"
  depends_on    = [google_project_service.enabled]
}

# ---- Game-state buckets (design Section B.3) --------------------------------
# Private, versioned, uniform access. Intel and orders are never public; the API
# is their only reader and it authorizes per empire.
resource "google_storage_bucket" "state" {
  project                     = var.project_id
  name                        = "${var.project_id}-galaxies-state"
  location                    = var.region
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  versioning { enabled = true }

  lifecycle_rule {
    condition { age = 30 }
    action {
      type          = "SetStorageClass"
      storage_class = "COLDLINE"
    }
  }
}

resource "google_storage_bucket" "orders" {
  project                     = var.project_id
  name                        = "${var.project_id}-galaxies-orders"
  location                    = var.region
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
}

resource "google_storage_bucket" "intel" {
  project                     = var.project_id
  name                        = "${var.project_id}-galaxies-intel"
  location                    = var.region
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
}

# ---- Firestore control plane (used from M2) ---------------------------------
resource "google_firestore_database" "control_plane" {
  project     = var.project_id
  name        = "(default)"
  location_id = var.region
  type        = "FIRESTORE_NATIVE"
  depends_on  = [google_project_service.enabled]
}

# ---- Turn-generation service account (least privilege) ----------------------
resource "google_service_account" "turngen" {
  project      = var.project_id
  account_id   = "sa-turngen"
  display_name = "Galaxies turn generation worker"
}

# It reads and writes state and intel, and reads orders.
resource "google_storage_bucket_iam_member" "turngen_state" {
  bucket = google_storage_bucket.state.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${google_service_account.turngen.email}"
}

resource "google_storage_bucket_iam_member" "turngen_intel" {
  bucket = google_storage_bucket.intel.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${google_service_account.turngen.email}"
}

resource "google_storage_bucket_iam_member" "turngen_orders" {
  bucket = google_storage_bucket.orders.name
  role   = "roles/storage.objectViewer"
  member = "serviceAccount:${google_service_account.turngen.email}"
}

# ---- Turn-generation Cloud Run service --------------------------------------
# Private (no unauthenticated access), scales to zero, one game per instance
# (container concurrency 1) because the engine holds a large mutable graph and
# process-wide singletons (design Section B.1).
resource "google_cloud_run_v2_service" "turngen" {
  project             = var.project_id
  name                = "galaxies-turngen"
  location            = var.region
  deletion_protection = false
  ingress             = "INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER"

  template {
    service_account                  = google_service_account.turngen.email
    max_instance_request_concurrency = 1
    timeout                          = "600s"

    scaling {
      min_instance_count = 0
      max_instance_count = var.turngen_max_instances
    }

    containers {
      image = var.turngen_image

      resources {
        limits = {
          cpu    = "1"
          memory = "1Gi"
        }
      }

      env {
        name  = "GALAXIES_STATE_BUCKET"
        value = google_storage_bucket.state.name
      }
      env {
        name  = "GALAXIES_ORDERS_BUCKET"
        value = google_storage_bucket.orders.name
      }
      env {
        name  = "GALAXIES_INTEL_BUCKET"
        value = google_storage_bucket.intel.name
      }
    }
  }

  depends_on = [google_project_service.enabled]
}
