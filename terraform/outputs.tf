output "resource_group_name" {
  description = "This project's resource group."
  value       = data.azurerm_resource_group.this.name
}

output "container_app_name" {
  description = "Name of the obsview container app, which `make deploy` passes to `az containerapp update`."
  value       = azurerm_container_app.this.name
}

output "url" {
  description = "obsview's stable HTTPS URL: the custom domain when var.custom_domain_name is set."
  value       = local.public_url
}

output "default_url" {
  description = "The app's default `*.azurecontainerapps.io` URL, which the custom domain's CNAME points at and the smoke test checks."
  value       = local.default_url
}

output "custom_domain_name" {
  description = "The custom domain bound to obsview, if var.custom_domain_name is set. Empty otherwise."
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

output "passcode" {
  description = "obsview's passcode. `make passcode` prints it."
  value       = random_password.passcode.result
  sensitive   = true
}

output "workspace_id" {
  description = "The shared Log Analytics workspace's customer ID, which `make deploy` sets as obsview's WORKSPACE_ID."
  value       = data.azurerm_log_analytics_workspace.platform.workspace_id
}

output "identity_client_id" {
  description = "Client ID of the app's managed identity, which `make deploy` sets as AZURE_CLIENT_ID."
  value       = azurerm_user_assigned_identity.this.client_id
}
