output "resource_group_name" {
  description = "This project's resource group."
  value       = azurerm_resource_group.this.name
}

output "container_app_name" {
  description = "Name of the Grafana container app, which `make deploy` passes to `az containerapp update`."
  value       = azurerm_container_app.this.name
}

output "grafana_url" {
  description = "Grafana's stable HTTPS URL."
  value       = local.public_url
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
