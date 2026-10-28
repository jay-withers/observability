locals {
  image = "${var.image_registry}/grafana:${var.image_tag}"

  # Grafana's default port; the Dockerfile EXPOSEs it and both probes check it.
  target_port = 3000

  # Grafana's documented minimum is 512 MiB; it idles well under that, and at
  # scale to zero the size only bills while someone is looking at it.
  container_cpu    = 0.5
  container_memory = "1Gi"

  # The role Grafana logs in to the shared server as. The Postgres role itself
  # is created once by `make db-role`, with the password from this state.
  database_user = "grafana"
  database_name = "grafana"

  # Known before the app exists: an app's ingress FQDN is its name under the
  # environment's default domain. Grafana builds every link and redirect from
  # its root URL, so it has to be the real one.
  public_url = "https://${module.naming.container_app.name}.${data.azurerm_container_app_environment.platform.default_domain}"

  # Everything Grafana needs that is not a secret. Its provisioning files
  # (grafana/provisioning) read the AZURE_* and WORKSPACE_* values, so the
  # data source points at this subscription and workspace without either
  # being committed.
  grafana_env = {
    GF_SERVER_ROOT_URL = local.public_url

    GF_DATABASE_TYPE     = "postgres"
    GF_DATABASE_HOST     = "${var.postgres_fqdn}:5432"
    GF_DATABASE_NAME     = local.database_name
    GF_DATABASE_USER     = local.database_user
    GF_DATABASE_SSL_MODE = "require"

    # One local admin, by the decision recorded in the README: no Entra app
    # registration. Sign-up and anonymous access stay off.
    GF_SECURITY_ADMIN_USER         = "admin"
    GF_USERS_ALLOW_SIGN_UP         = "false"
    GF_AUTH_ANONYMOUS_ENABLED      = "false"
    GF_SECURITY_COOKIE_SECURE      = "true"
    GF_ANALYTICS_REPORTING_ENABLED = "false"

    # The Azure Monitor data source authenticates as this app's identity.
    GF_AZURE_MANAGED_IDENTITY_ENABLED   = "true"
    GF_AZURE_MANAGED_IDENTITY_CLIENT_ID = azurerm_user_assigned_identity.this.client_id
    AZURE_SUBSCRIPTION_ID               = data.azurerm_subscription.current.subscription_id
    WORKSPACE_RESOURCE_ID               = data.azurerm_log_analytics_workspace.platform.id
  }
}
