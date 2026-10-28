# Terraform module

<!-- BEGIN_TF_DOCS -->
## Requirements

| Name | Version |
| ---- | ------- |
| <a name="requirement_terraform"></a> [terraform](#requirement\_terraform) | >= 1.6 |
| <a name="requirement_azurerm"></a> [azurerm](#requirement\_azurerm) | ~> 5.0 |
| <a name="requirement_random"></a> [random](#requirement\_random) | >= 3.3.2 |

## Providers

| Name | Version |
| ---- | ------- |
| <a name="provider_azurerm"></a> [azurerm](#provider\_azurerm) | ~> 5.0 |
| <a name="provider_random"></a> [random](#provider\_random) | >= 3.3.2 |

## Modules

| Name | Source | Version |
| ---- | ------ | ------- |
| <a name="module_naming"></a> [naming](#module\_naming) | Azure/naming/azurerm | ~> 0.4 |

## Resources

| Name | Type |
| ---- | ---- |
| [azurerm_container_app.this](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs/resources/container_app) | resource |
| [azurerm_container_app_custom_domain.this](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs/resources/container_app_custom_domain) | resource |
| [azurerm_container_app_environment_managed_certificate.this](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs/resources/container_app_environment_managed_certificate) | resource |
| [azurerm_role_assignment.workspace_reader](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs/resources/role_assignment) | resource |
| [azurerm_user_assigned_identity.this](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs/resources/user_assigned_identity) | resource |
| [random_password.admin](https://registry.terraform.io/providers/hashicorp/random/latest/docs/resources/password) | resource |
| [random_password.database](https://registry.terraform.io/providers/hashicorp/random/latest/docs/resources/password) | resource |
| [azurerm_container_app_environment.platform](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs/data-sources/container_app_environment) | data source |
| [azurerm_log_analytics_workspace.platform](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs/data-sources/log_analytics_workspace) | data source |
| [azurerm_resource_group.this](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs/data-sources/resource_group) | data source |
| [azurerm_subscription.current](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs/data-sources/subscription) | data source |

## Inputs

| Name | Description | Type | Default | Required |
| ---- | ----------- | ---- | ------- | :------: |
| <a name="input_custom_domain_name"></a> [custom\_domain\_name](#input\_custom\_domain\_name) | Hostname to bind to Grafana with a free Azure-managed certificate, e.g. `observability.jaywithers.uk`. Empty creates neither the certificate nor the binding, and Grafana's root URL stays the default `*.azurecontainerapps.io` one. The CNAME (to that default FQDN) and the `asuid.<label>` TXT record (holding `custom_domain_verification_id`) must already resolve before apply, because Azure validates both during issuance — `make dns` prints them. | `string` | `""` | no |
| <a name="input_environment"></a> [environment](#input\_environment) | Deployment environment. Drives resource naming, and selects which shared platform environment Grafana runs on. | `string` | n/a | yes |
| <a name="input_platform_environment_name"></a> [platform\_environment\_name](#input\_platform\_environment\_name) | Name of the shared Container Apps environment Grafana runs on. | `string` | `"cae-platform-dev"` | no |
| <a name="input_platform_resource_group_name"></a> [platform\_resource\_group\_name](#input\_platform\_resource\_group\_name) | Resource group holding the shared Container Apps environment, workspace and Postgres server (jay-withers/azure-container-apps). | `string` | `"rg-platform-dev"` | no |
| <a name="input_platform_workspace_name"></a> [platform\_workspace\_name](#input\_platform\_workspace\_name) | Name of the shared Log Analytics workspace Grafana reads, which holds Deployments\_CL and OpsEvents\_CL. | `string` | `"log-platform-dev"` | no |
| <a name="input_postgres_fqdn"></a> [postgres\_fqdn](#input\_postgres\_fqdn) | Host of the platform's shared Postgres server, from azure-container-apps' `make outputs` (`postgres_fqdn`). It carries a random suffix there, so it is passed in rather than derived. | `string` | n/a | yes |
| <a name="input_project_name"></a> [project\_name](#input\_project\_name) | Name included in every resource name here. | `string` | `"observability"` | no |
| <a name="input_tags"></a> [tags](#input\_tags) | Tags applied to all resources, merged with (and taking precedence over) the default tags (`environment`, `managed-by`). | `map(string)` | `{}` | no |

## Outputs

| Name | Description |
| ---- | ----------- |
| <a name="output_admin_password"></a> [admin\_password](#output\_admin\_password) | Password for Grafana's `admin` user. `make admin-password` prints it. |
| <a name="output_container_app_name"></a> [container\_app\_name](#output\_container\_app\_name) | Name of the Grafana container app, which `make deploy` passes to `az containerapp update`. |
| <a name="output_custom_domain_name"></a> [custom\_domain\_name](#output\_custom\_domain\_name) | The custom domain bound to Grafana, if var.custom\_domain\_name is set. Empty otherwise. |
| <a name="output_custom_domain_verification_id"></a> [custom\_domain\_verification\_id](#output\_custom\_domain\_verification\_id) | Domain verification ID, published as the `asuid.<label>` TXT record before apply. |
| <a name="output_database_password"></a> [database\_password](#output\_database\_password) | Password of the `grafana` role on the shared Postgres server, which `make db-role` sets. |
| <a name="output_default_url"></a> [default\_url](#output\_default\_url) | The app's default `*.azurecontainerapps.io` URL, which the custom domain's CNAME points at and the smoke test checks. |
| <a name="output_grafana_url"></a> [grafana\_url](#output\_grafana\_url) | Grafana's stable HTTPS URL: the custom domain when var.custom\_domain\_name is set. |
| <a name="output_postgres_fqdn"></a> [postgres\_fqdn](#output\_postgres\_fqdn) | The shared Postgres server, for `make db-role`. |
| <a name="output_resource_group_name"></a> [resource\_group\_name](#output\_resource\_group\_name) | This project's resource group. |
<!-- END_TF_DOCS -->
