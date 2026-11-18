# obsview (app/), on the shared platform environment but in this project's
# resource group, like every tenant. Grafana ran here until the cutover.
resource "azurerm_container_app" "this" {
  name                         = module.naming.container_app.name
  container_app_environment_id = data.azurerm_container_app_environment.platform.id
  resource_group_name          = data.azurerm_resource_group.this.name
  revision_mode                = "Single"

  # The shared environment is Consumption-only; stated so a plan doesn't
  # propose clearing what the API reports back.
  workload_profile_name = "Consumption"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.this.id]
  }

  # The one secret obsview reads, as APP_PASSCODE (`make deploy` maps it).
  secret {
    name  = "passcode"
    value = random_password.passcode.result
  }

  ingress {
    # Public, behind obsview's passcode.
    external_enabled = true
    target_port      = local.target_port
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    # **Scale to zero.** Opened a few times a week; the price is a cold start
    # of a few seconds on the first load. Alerting lives in Azure Monitor, so
    # nothing depends on this being awake.
    min_replicas               = 0
    max_replicas               = 1
    cooldown_period_in_seconds = 900

    container {
      name   = "obsview"
      image  = local.image
      cpu    = local.container_cpu
      memory = local.container_memory

      # Read by the first revision's placeholder image only, so the probes
      # find it on the port they check. Everything obsview reads is set by
      # `make deploy` (see ignore_changes below).
      env {
        name  = "PORT"
        value = tostring(local.target_port)
      }

      # Both probes use /healthz, which answers from memory. /readyz reaches
      # Log Analytics; the smoke test checks it once per deploy, but as the
      # readiness probe a slow workspace would take the app out of rotation
      # and hide the error page that explains it.
      liveness_probe {
        transport               = "HTTP"
        port                    = local.target_port
        path                    = "/healthz"
        initial_delay           = 5
        interval_seconds        = 30
        timeout                 = 5
        failure_count_threshold = 3
      }

      readiness_probe {
        transport               = "HTTP"
        port                    = local.target_port
        path                    = "/healthz"
        interval_seconds        = 10
        timeout                 = 5
        failure_count_threshold = 3
        success_count_threshold = 1
      }
    }
  }

  tags = local.tags

  # **`make deploy` owns the image and the env**, as gym-log's does, but with
  # `--replace-env-vars` rather than its `--set-env-vars`: it sets IMAGE_TAG
  # (what /healthz reports and the smoke test checks) alongside every other
  # value obsview reads, taken
  # from this config's outputs. Terraform managing the env as well would show
  # IMAGE_TAG as drift on every plan. So a change to the env is a change to
  # the Makefile's `deploy`, and lands on the next deploy.
  lifecycle {
    ignore_changes = [
      template[0].container[0].image,
      template[0].container[0].env,
    ]
  }

  # So the first page load after the first apply can already query the
  # workspace, rather than erroring until the role assignment lands.
  depends_on = [azurerm_role_assignment.workspace_reader]
}

# --- the custom domain --------------------------------------------------------
#
# The same shape as finances, gym-log and career, which hit and confirmed the
# provider's gaps first. Both resources are guarded by the same count: a
# hostname binds to exactly one container app globally.
#
# **The certificate lands in the *platform's* resource group**, as a child of
# the environment, so cd-terraform's identity (Contributor on this project's
# group only) cannot create it: an apply that adds or replaces it is local.
#
# **Both DNS records must resolve before apply.** Azure validates them during
# issuance, not after. `make dns` prints them.
resource "azurerm_container_app_environment_managed_certificate" "this" {
  count = var.custom_domain_name != "" ? 1 : 0

  name                         = replace(var.custom_domain_name, ".", "-")
  container_app_environment_id = data.azurerm_container_app_environment.platform.id
  subject_name                 = var.custom_domain_name
  domain_control_validation    = "CNAME"

  # Azure requires the hostname to already be registered on an app in the
  # environment before it will issue the managed certificate.
  depends_on = [azurerm_container_app_custom_domain.this]
}

resource "azurerm_container_app_custom_domain" "this" {
  count = var.custom_domain_name != "" ? 1 : 0

  name                     = var.custom_domain_name
  container_app_id         = azurerm_container_app.this.id
  certificate_binding_type = "Disabled"

  # container_app_environment_certificate_id is deliberately unset: passing a
  # *managed* certificate's id 400s the plan
  # (hashicorp/terraform-provider-azurerm#25788).
  #
  # **The apply cannot finish the bind, and says nothing about it.** ARM's bind
  # needs the certificate id and the provider has no field for it
  # (hashicorp/terraform-provider-azurerm#27362, open), so `make bind-domain`
  # is the manual step after any apply that recreates either resource. The bind
  # leaves the domain at `SniEnabled`; ignoring both arguments stops every plan
  # from proposing to replace it back to `Disabled`, which would unbind it.
  lifecycle {
    ignore_changes = [certificate_binding_type, container_app_environment_certificate_id]
  }
}
