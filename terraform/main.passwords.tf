# Grafana's one local admin password. It lives only in this state and in the
# container app's secrets, never in a Key Vault: a Terraform-managed vault
# secret needs data-plane access just to plan, which the pipeline identity does
# not have.
#
# Read it with `make admin-password`. Rotating it is a `terraform taint` and an
# apply.
resource "random_password" "admin" {
  length  = 32
  special = false
}
