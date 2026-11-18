# obsview's passcode. It lives only in this state and in the container app's
# secrets, never in a Key Vault: a Terraform-managed vault secret needs
# data-plane access just to plan, which the pipeline identity does not have.
#
# Read it with `make passcode`. Rotating it is a `terraform taint` and an
# apply; the session cookie's key is derived from it, so rotating it also
# signs everyone out.
resource "random_password" "passcode" {
  length  = 24
  special = false
}

# Grafana's admin password, kept only until the container app's
# `admin-password` secret can go (see main.container-apps.tf).
resource "random_password" "admin" {
  length  = 32
  special = false
}
