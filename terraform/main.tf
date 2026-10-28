# Grafana over the shared Log Analytics workspace. The Container Apps
# environment, the workspace and the Postgres server Grafana keeps its state
# in are **not** here: they belong to jay-withers/azure-container-apps and are
# resolved by name in data.tf. Everything this project owns is in its own
# resource group.
module "naming" {
  # checkov:skip=CKV_TF_1: Terraform Registry module pinned by semver
  # (version below), not a git source — there's no commit hash to pin.
  source  = "Azure/naming/azurerm"
  version = "~> 0.4"
  suffix  = [var.project_name, var.environment]
}

resource "azurerm_resource_group" "this" {
  name = module.naming.resource_group.name
  # Must match the shared environment's region: a container app may sit in a
  # different resource group from its environment, but not a different region.
  location = data.azurerm_container_app_environment.platform.location
  tags     = local.tags
}
