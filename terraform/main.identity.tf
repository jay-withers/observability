# The identity Grafana queries Azure Monitor as.
resource "azurerm_user_assigned_identity" "this" {
  name                = module.naming.user_assigned_identity.name
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  tags                = local.tags
}

# Read-only on the shared workspace, which is everything Grafana shows: KQL
# queries against Deployments_CL, OpsEvents_CL and the platform's own logs.
#
# A role assignment, so cd-terraform's identity cannot create it: the first
# apply, which creates it, runs locally (see the README).
resource "azurerm_role_assignment" "workspace_reader" {
  scope                = data.azurerm_log_analytics_workspace.platform.id
  role_definition_name = "Log Analytics Reader"
  principal_id         = azurerm_user_assigned_identity.this.principal_id
  principal_type       = "ServicePrincipal"
}
