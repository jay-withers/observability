output "resource_group_name" {
  description = "This project's resource group."
  value       = azurerm_resource_group.this.name
}

output "container_app_name" {
  description = "Name of the Grafana container app, which `make deploy` passes to `az containerapp update`."
  value       = azurerm_container_app.this.name
}

output "grafana_url" {
  description = "Grafana's stable HTTPS URL: the custom domain when var.custom_domain_name is set."
  value       = local.public_url
}

output "default_url" {
  description = "The app's default `*.azurecontainerapps.io` URL, which the custom domain's CNAME points at and the smoke test checks."
  value       = local.default_url
}

output "custom_domain_name" {
  description = "The custom domain bound to Grafana, if var.custom_domain_name is set. Empty otherwise."
  value       = var.custom_domain_name
}

# The value of the asuid.<label> TXT record. Azure checks it during binding to
# prove the domain is ours. It is the shared environment's, so every app on it
# publishes the same value.
output "custom_domain_verification_id" {
  description = "Domain verification ID, published as the `asuid.<label>` TXT record before apply."
  value       = azurerm_container_app.this.custom_domain_verification_id
  sensitive   = true
}

output "admin_password" {
  description = "Password for Grafana's `admin` user. `make admin-password` prints it."
  value       = random_password.admin.result
  sensitive   = true
}

output "database_password" {
  description = "Password of the `grafana` role on the shared Postgres server, which `make db-role` sets."
  value       = random_password.database.result
  sensitive   = true
}

output "postgres_fqdn" {
  description = "The shared Postgres server, for `make db-role`."
  value       = var.postgres_fqdn
}
