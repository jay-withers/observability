locals {
  # **The first revision runs a placeholder, not obsview.** Terraform only
  # needs an image to create the app; the first `make deploy` (cd-publish, on
  # the first release) puts obsview on, and the image is under
  # `ignore_changes`, so Terraform never touches it again. The placeholder
  # answers 200 on every path on $PORT, so the probes pass. This replaced
  # seeding from a release tag, which had to be guessed (v0.1.0 here, not
  # v0.0.1) and failed only when the revision pulled it.
  #
  # `latest` deliberately: dev-containers prunes all but its last ten releases
  # and never `latest`, and a moving tag's usual trap (a re-push deploys
  # nothing) can't apply to an image used only at create.
  image = "ghcr.io/jay-withers/dev-containers/placeholder:latest"

  # obsview's port, which it binds from PORT (`make deploy` sets it, the
  # Dockerfile defaults and EXPOSEs it), and which both probes check.
  target_port = 8000

  # The smallest combination Container Apps accepts. obsview idles at a
  # fraction of it, and at scale to zero it bills only while open.
  container_cpu    = 0.25
  container_memory = "0.5Gi"

  # Known before the app exists: an app's ingress FQDN is its name under the
  # environment's default domain. `make url` prints the custom domain when
  # one is set. The default URL keeps working either way, and the smoke test
  # uses it, so a deploy never depends on the certificate being bound.
  default_url = "https://${module.naming.container_app.name}.${data.azurerm_container_app_environment.platform.default_domain}"
  public_url  = var.custom_domain_name != "" ? "https://${var.custom_domain_name}" : local.default_url
}
