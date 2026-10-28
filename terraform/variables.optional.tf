variable "project_name" {
  description = "Name included in every resource name here."
  type        = string
  default     = "observability"

  validation {
    # The naming module's container app name allows 32 characters; this keeps
    # `ca-<project>-<env>` well inside it.
    condition     = can(regex("^[a-z][a-z0-9]{1,15}$", var.project_name))
    error_message = "project_name must be 2-16 lowercase alphanumeric characters, starting with a letter."
  }
}

variable "tags" {
  description = "Tags applied to all resources, merged with (and taking precedence over) the default tags (`environment`, `managed-by`)."
  type        = map(string)
  default     = {}
}

variable "platform_resource_group_name" {
  description = "Resource group holding the shared Container Apps environment, workspace and Postgres server (jay-withers/azure-container-apps)."
  type        = string
  default     = "rg-platform-dev"
}

variable "platform_environment_name" {
  description = "Name of the shared Container Apps environment Grafana runs on."
  type        = string
  default     = "cae-platform-dev"
}

variable "platform_workspace_name" {
  description = "Name of the shared Log Analytics workspace Grafana reads, which holds Deployments_CL and OpsEvents_CL."
  type        = string
  default     = "log-platform-dev"
}


variable "image_registry" {
  description = "Registry and repository prefix the Grafana image is pulled from. A public package on ghcr.io, like every app here: a private one would need a pull secret, and there is no Azure Container Registry."
  type        = string
  default     = "ghcr.io/jay-withers/observability"
}

variable "image_tag" {
  description = "Image tag seeding the app's **first** revision only. Every deploy after that is `make deploy IMAGE_TAG=vX.Y.Z`, because the container's image sits under `ignore_changes`."
  type        = string
  # Must name a tag that exists in the registry, or the first revision fails
  # on the image pull long after apply reports success. cd-tag's first release
  # on a repo with no tags is v0.0.1.
  default = "v0.0.1"

  validation {
    # A moving tag deploys nothing: Container Apps creates a revision only when
    # the template changes.
    condition     = !contains(["latest", "main", "unset"], var.image_tag)
    error_message = "image_tag must be an immutable tag, not latest/main/unset."
  }
}
