terraform {
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
  description = "LaunchDarkly personal API access token (Admin role)"
}

variable "ld_project_key" {
  type        = string
  default     = "default"
  description = "LaunchDarkly project key"
}

variable "ld_environment_key" {
  type        = string
  default     = "production"
  description = "LaunchDarkly environment key"
}
