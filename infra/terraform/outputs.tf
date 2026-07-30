# Galaxies infrastructure outputs.

output "turngen_url" {
  description = "Private URL of the turn-generation service."
  value       = google_cloud_run_v2_service.turngen.uri
}

output "state_bucket" {
  value = google_storage_bucket.state.name
}

output "orders_bucket" {
  value = google_storage_bucket.orders.name
}

output "intel_bucket" {
  value = google_storage_bucket.intel.name
}

output "image_repository" {
  description = "Artifact Registry repo for service images."
  value       = "${var.region}-docker.pkg.dev/${var.project_id}/${google_artifact_registry_repository.images.repository_id}"
}

# ---- M2 (the clock) ----------------------------------------------------------

output "api_url" {
  description = "Public URL of the galaxies-api gateway."
  value       = google_cloud_run_v2_service.api.uri
}

output "deadline_queue" {
  description = "Cloud Tasks queue that fires per-game deadlines."
  value       = google_cloud_tasks_queue.deadlines.name
}

output "turn_generated_topic" {
  description = "Pub/Sub topic carrying generated-turn events."
  value       = google_pubsub_topic.turn_generated.name
}

# ---- M3 (AI seats) -----------------------------------------------------------

output "ai_url" {
  description = "Private URL of the galaxies-ai dispatch runner. Also the OIDC audience its callers must use."
  value       = google_cloud_run_v2_service.ai.uri
}

output "ai_nova_default_url" {
  description = "Private URL of the built-in participant. Only galaxies-ai may invoke it."
  value       = google_cloud_run_v2_service.ai_nova_default.uri
}

output "ai_service_account" {
  description = "Runtime service account of galaxies-ai."
  value       = google_service_account.ai.email
}

output "ai_nova_default_service_account" {
  description = "Runtime service account of the built-in participant. It holds no data-plane roles by design."
  value       = google_service_account.ai_nova_default.email
}
