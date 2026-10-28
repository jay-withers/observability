# The shared platform, resolved by name rather than by reading its Terraform
# state. A plan against an environment the platform has not been applied to
# fails here, which is why only dev is planned.
data "azurerm_container_app_environment" "platform" {
  name                = var.platform_environment_name
  resource_group_name = var.platform_resource_group_name
}

# What Grafana reads: Deployments_CL, OpsEvents_CL and everything the platform
# already collects.
data "azurerm_log_analytics_workspace" "platform" {
  name                = var.platform_workspace_name
  resource_group_name = var.platform_resource_group_name
}

# This project's resource group, created by github-repos along with the
# pipeline identity's grants on it. Same name the naming module always gave
# it; github-repos puts it in the shared environment's region.
data "azurerm_resource_group" "this" {
  name = module.naming.resource_group.name
}

# For the subscription the Azure Monitor data source queries.
data "azurerm_subscription" "current" {}
