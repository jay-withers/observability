# Grafana's two passwords: its database login on the shared Postgres server,
# and its one local admin. Both live only in this state and in the container
# app's secrets, never in a Key Vault: a Terraform-managed vault secret needs
# data-plane access just to plan, which the pipeline identity does not have.
#
# Read them with `make admin-password` and `make db-role` (which uses the
# database one to create the role). Rotating either is a `terraform taint` and
# an apply, then `make db-role` again for the database.
resource "random_password" "database" {
  length  = 40
  special = false
}

resource "random_password" "admin" {
  length  = 32
  special = false
}
