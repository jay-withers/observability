terraform {
  required_version = ">= 1.6"

  # Partial: per-environment values live in backends/<env>.hcl.
  #   terraform init -backend-config=backends/dev.hcl
  # Anything not touching state must init with -backend=false, or this prompts.
  backend "azurerm" {}

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.0"
    }
    # Grafana's two passwords, and a transitive dependency of module.naming.
    random = {
      source  = "hashicorp/random"
      version = ">= 3.3.2"
    }
  }
}

provider "azurerm" {
  features {
    resource_group {
      prevent_deletion_if_contains_resources = false
    }
  }

  use_oidc = true
}
