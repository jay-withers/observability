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

# --- the custom domain --------------------------------------------------------

variable "custom_domain_name" {
  description = "Hostname to bind to Grafana with a free Azure-managed certificate, e.g. `observability.jaywithers.uk`. Empty creates neither the certificate nor the binding, and Grafana's root URL stays the default `*.azurecontainerapps.io` one. The CNAME (to that default FQDN) and the `asuid.<label>` TXT record (holding `custom_domain_verification_id`) must already resolve before apply, because Azure validates both during issuance — `make dns` prints them."
  type        = string
  default     = ""

  validation {
    # An apex domain cannot be a CNAME, and CNAME is the only validation method
    # these resources use. Caught here rather than as an opaque DNS failure
    # midway through certificate issuance.
    condition     = var.custom_domain_name == "" || length(split(".", var.custom_domain_name)) > 2
    error_message = "custom_domain_name must be a subdomain, not an apex domain: validation is by CNAME, which an apex record cannot hold."
  }
}
