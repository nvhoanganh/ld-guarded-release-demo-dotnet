terraform {
  required_version = ">= 1.5"

  required_providers {
    launchdarkly = {
      source  = "launchdarkly/launchdarkly"
      version = "~> 3.0"
    }
  }
}

provider "launchdarkly" {
  access_token = var.ld_api_key
}

variable "ld_api_key" {
  type        = string
  sensitive   = true
  description = "LaunchDarkly personal API access token (Admin). Read from the AVTA .env by apply.sh."
}

variable "ld_project_key" {
  type        = string
  default     = "default"
  description = "LaunchDarkly project key. The guarded-release demo shares the account's default project (AVTA Tour)."
}

variable "ld_environment_key" {
  type        = string
  default     = "production"
  description = "LaunchDarkly environment key."
}
