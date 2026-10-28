# Grafana, on the shared platform environment but in this project's resource
# group, like every tenant.
resource "azurerm_container_app" "this" {
  name                         = module.naming.container_app.name
  container_app_environment_id = data.azurerm_container_app_environment.platform.id
  resource_group_name          = azurerm_resource_group.this.name
  revision_mode                = "Single"

  # The shared environment is Consumption-only; stated so a plan doesn't
  # propose clearing what the API reports back.
  workload_profile_name = "Consumption"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.this.id]
  }

  secret {
    name  = "database-password"
    value = random_password.database.result
  }

  secret {
    name  = "admin-password"
    value = random_password.admin.result
  }

  ingress {
    # Public, behind Grafana's own login. Anonymous access and sign-up are off.
    external_enabled = true
    target_port      = local.target_port
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    # **Scale to zero.** Grafana is opened a few times a week; a standing
    # replica would be the platform's second standing cost after Postgres. The
    # price is a cold start of some seconds on the first page load. Alerting
    # lives in Azure Monitor, so nothing depends on Grafana being awake.
    min_replicas               = 0
    max_replicas               = 1
    cooldown_period_in_seconds = 900

    container {
      name   = "grafana"
      image  = local.image
      cpu    = local.container_cpu
      memory = local.container_memory

      dynamic "env" {
        for_each = local.grafana_env
        content {
          name  = env.key
          value = env.value
        }
      }

      env {
        name        = "GF_DATABASE_PASSWORD"
        secret_name = "database-password"
      }

      env {
        name        = "GF_SECURITY_ADMIN_PASSWORD"
        secret_name = "admin-password"
      }

      # /api/health checks Grafana's own database connection, so a broken
      # Postgres login shows as an unhealthy revision rather than a login page
      # that errors.
      liveness_probe {
        transport               = "HTTP"
        port                    = local.target_port
        path                    = "/api/health"
        initial_delay           = 10
        interval_seconds        = 30
        timeout                 = 5
        failure_count_threshold = 3
      }

      readiness_probe {
        transport               = "HTTP"
        port                    = local.target_port
        path                    = "/api/health"
        interval_seconds        = 10
        timeout                 = 5
        failure_count_threshold = 3
        success_count_threshold = 1
      }
    }
  }

  tags = local.tags

  # `make deploy` owns the running image after the first revision, as in every
  # app repo here. Env stays managed by Terraform: nothing in it changes per
  # release, and the dashboards ship inside the image.
  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }

  # So the first page load after the first apply can already query the
  # workspace, rather than erroring until the role assignment lands.
  depends_on = [azurerm_role_assignment.workspace_reader]
}
