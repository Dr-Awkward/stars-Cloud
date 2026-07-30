# Galaxies Terraform providers and backend.

terraform {
  required_version = ">= 1.6.0"

  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 6.0"
    }
  }

  # Shared remote state. Create this bucket once, by hand, before the first init,
  # then run: terraform init -backend-config="bucket=YOUR-TFSTATE-BUCKET".
  backend "gcs" {
    prefix = "galaxies"
    # bucket = "roybot-galaxies-tfstate"   # set via -backend-config
  }
}

provider "google" {
  project = var.project_id
  region  = var.region
}
