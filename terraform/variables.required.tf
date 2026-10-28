variable "environment" {
  description = "Deployment environment. Drives resource naming, and selects which shared platform environment Grafana runs on."
  type        = string

  validation {
    condition     = contains(["dev", "stg", "prd"], var.environment)
    error_message = "environment must be one of: dev, stg, prd."
  }
}

variable "postgres_fqdn" {
  description = "Host of the platform's shared Postgres server, from azure-container-apps' `make outputs` (`postgres_fqdn`). It carries a random suffix there, so it is passed in rather than derived."
  type        = string
}
