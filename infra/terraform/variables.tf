# Galaxies infrastructure variables.

variable "project_id" {
  description = "GCP project id."
  type        = string
  default     = "roybot"
}

variable "region" {
  description = "Primary region for Cloud Run, buckets, and Firestore."
  type        = string
  default     = "us-central1"
}

variable "turngen_image" {
  description = "Full image ref for galaxies-turngen, for example us-central1-docker.pkg.dev/roybot/roybot-galaxies/galaxies-turngen:TAG. Push the image before the first apply."
  type        = string
}

variable "turngen_max_instances" {
  description = "Cap on concurrent turn-generation instances, to bound the worst-case bill."
  type        = number
  default     = 10
}

# ---- M2 (the clock) ----------------------------------------------------------

variable "api_image" {
  description = "Full image ref for galaxies-api. Push the image before the first apply that includes M2."
  type        = string
  default     = ""
}

variable "api_max_instances" {
  description = "Cap on galaxies-api instances."
  type        = number
  default     = 4
}

variable "sweep_schedule" {
  description = "Cloud Scheduler cron for the deadline backstop sweep."
  type        = string
  default     = "* * * * *" # every minute
}

# ---- M3 (AI seats) -----------------------------------------------------------
# Images. Both default to empty and fall back to the turngen image so a first
# apply can stand the topology up before the real images exist.

variable "ai_image" {
  description = "Full image ref for galaxies-ai, for example us-central1-docker.pkg.dev/roybot/roybot-galaxies/galaxies-ai:TAG."
  type        = string
  default     = ""
}

variable "ai_nova_default_image" {
  description = "Full image ref for the built-in participant ai-nova-default."
  type        = string
  default     = ""
}

variable "ai_max_instances" {
  description = "Cap on galaxies-ai runner instances. Size to expected concurrent seat-turns."
  type        = number
  default     = 20
}

variable "ai_nova_default_max_instances" {
  description = "Cap on ai-nova-default instances. Each serves one seat-turn at a time, so this is the real AI concurrency ceiling."
  type        = number
  default     = 20
}

variable "ai_concurrency" {
  description = "Requests per galaxies-ai instance. The runner is I/O bound on the participant, GCS, and the API."
  type        = number
  default     = 8
}

variable "ai_request_timeout_s" {
  description = "Cloud Run request timeout for galaxies-ai, in seconds. Must exceed the participant timeout plus submission."
  type        = number
  default     = 120
}

variable "ai_nova_default_timeout_s" {
  description = "Cloud Run request timeout for ai-nova-default, in seconds. The platform backstop behind the runner's own cap."
  type        = number
  default     = 90
}

# Feature flags. All ship OFF (spec Section 3.1). Flipping one is a variable
# change plus an apply, which is the durable form of a flip; the runbook's
# gcloud update is the immediate form that the next apply reverts.

variable "galaxies_ai_enabled" {
  description = "Master gate for galaxies-ai. Off: reads return {\"disabled\":true}, mutations 403, dispatch is a no-op."
  type        = bool
  default     = false
}

variable "ai_dispatch_enabled" {
  description = "Off: /v1/dispatch records held and submits nothing. This is the kill switch; every AI seat then runs on held orders and turns still generate."
  type        = bool
  default     = false
}

variable "ai_takeover_enabled" {
  description = "Off: abandoned or timed-out human seats are not auto-filled by the built-in AI."
  type        = bool
  default     = false
}

variable "ai_registry_enabled" {
  description = "M6. Off: /v1/participants reads return {\"disabled\":true}, publish 403s, lobby lists only the built-in AI."
  type        = bool
  default     = false
}

variable "ai_community_enabled" {
  description = "M6. Off: dispatch to any non first-party image 403s and pinned games degrade to held orders."
  type        = bool
  default     = false
}

variable "ai_llm_enabled" {
  description = "M6. Off: LLM seats degrade to the built-in Nova AI."
  type        = bool
  default     = false
}

variable "ai_ladder_enabled" {
  description = "M6. Off: /v1/ladder routes return {\"disabled\":true}."
  type        = bool
  default     = false
}

variable "ai_llm_model" {
  description = "M6. Empty means the manifest tier default; set to pin a cheaper model per call."
  type        = string
  default     = ""
}

variable "api_ai_orders_enabled" {
  description = "Gate on galaxies-api's internal AI orders route. Off: the route 403s, so no AI seat can submit at all."
  type        = bool
  default     = false
}

# Dispatch behaviour.

variable "ai_contract_version" {
  description = "Participant contract version this runner speaks."
  type        = string
  default     = "1.0"
}

variable "ai_default_timeout_s" {
  description = "Wall-clock cap on a participant call when its manifest omits one."
  type        = number
  default     = 60
}

variable "ai_dispatch_retry" {
  description = "Retries before a run is marked failed and the seat falls to held orders."
  type        = number
  default     = 1
}

variable "ai_max_orders_per_turn" {
  description = "Anti-spam cap on orders accepted from one participant response; excess is dropped."
  type        = number
  default     = 2000
}

variable "ai_llm_game_token_budget" {
  description = "M6. Per-game LLM token budget; exhaustion degrades the seat to the built-in Nova AI. Zero means unset."
  type        = number
  default     = 0
}

variable "ai_llm_daily_token_budget" {
  description = "M6. Service-wide daily LLM token cap. Zero means unset."
  type        = number
  default     = 0
}

# Eventing.

variable "ai_push_ack_deadline_s" {
  description = "Pub/Sub push ack deadline for the galaxies-ai event routes. The handlers only enqueue tasks, so they answer fast."
  type        = number
  default     = 60
}

variable "ai_dead_letter_max_attempts" {
  description = "Deliveries before a poisoned event is moved to galaxies-dead-letter. Range 5 to 100 per Pub/Sub."
  type        = number
  default     = 5
}
