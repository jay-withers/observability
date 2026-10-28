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

# The resource group is github-repos' (`create_resource_groups`), which
# imported the one this config's first apply made. Dropped from this state
# without touching Azure; data.tf looks it up by the same name.
removed {
  from = azurerm_resource_group.this

  lifecycle {
    destroy = false
  }
}
