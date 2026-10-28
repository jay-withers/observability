# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo does

Grafana for the jay-withers Container Apps estate: a scale-to-zero container
app on the shared environment, reading the shared Log Analytics workspace.
Phase 1 of the observability plan ships the Deployments dashboard over
`Deployments_CL`. `README.md` covers the why and the first-time setup; this
file covers the traps.

A tenant of [azure-container-apps](https://github.com/jay-withers/azure-container-apps),
shaped like gym-log: its own resource group and identity, the environment,
workspace and Postgres server resolved by name or passed in, never by remote
state.

## Decisions not to re-litigate

- **Dashboards and the data source ship inside the image** (`grafana/`), not
  through the Grafana Terraform provider. That provider must reach a running
  Grafana at plan time, and this one is created by the same apply and scales
  to zero. Provisioned dashboards are read-only in the UI on purpose.
- **`__WORKSPACE_RESOURCE_ID__` in dashboard JSON** is filled in by
  `grafana/entrypoint.sh` at start-up. Grafana substitutes environment
  variables in provisioning YAML but not in dashboard JSON. Never commit a
  real workspace ID into a dashboard.
- **Two passwords, both container app secrets from `random_password`**, never
  Key Vault secrets: a Terraform-managed vault secret needs data-plane access
  just to plan, which the pipeline identity lacks (the estate's documented
  trap). No Key Vault exists here at all.
- **Grafana's database login is a password** because Grafana cannot use an
  Entra token for its own database. It is the only password-holding role on
  the shared server. `sql/grafana-role.sql` creates it; `make db-role` runs
  it as the Entra administrator.
- **One local admin, no Entra SSO** (no app registration). Sign-up and
  anonymous access are off.
- **Created on dev-containers' `placeholder:latest`, never a release tag.**
  Terraform needs an image before any release exists; a guessed tag (v0.0.1
  vs v0.1.0, observability#4) passes plan and apply and fails only on the
  pull. The placeholder answers 200 on every path on `$PORT`, which is why
  `PORT` is set; Grafana ignores it. The first deploy puts Grafana on.
- **Scale to zero.** Grafana is opened a few times a week. Alerting lives in
  Azure Monitor, so nothing depends on it being awake.
- **Its own smoke test** (`scripts/smoke.sh`), not the app repos' shared one,
  which needs a `/healthz` reporting the release tag. This one checks the
  configured image, that the latest revision is the ready one, and that
  `/api/health` reports `"database": "ok"`.

## Traps

- **The first apply is local.** It creates the resource group and the
  `Log Analytics Reader` role assignment, which cd-terraform's identity
  cannot. The deploy identity only gets Contributor on `rg-observability-dev`
  once github-repos adds it, after this group exists.
- **`postgres_fqdn` is copied, not derived.** The shared server's name has a
  random suffix (it is a global DNS label). Take it from azure-container-apps'
  `make outputs`.
- **`make db-role` needs a firewall hole.** The server admits Azure IPs only
  (`AllowAllAzureIps`); the target opens a rule for your public IP and removes
  it on exit. Its SQL assumes the Entra administrator can act for the
  database's owner, which held on a local Postgres 17 where the owner was a
  role it belonged to. If `ALTER DATABASE` is refused on Azure, that
  assumption is what broke.
- **New GHCR packages start private.** Flip `observability/grafana` to public
  once after the first push; the app has no pull secret.
- **`GF_SERVER_ROOT_URL` is computed**: the custom domain when
  `custom_domain_name` is set, otherwise the app's name under the
  environment's default domain, because the FQDN is not known until the app
  exists and Grafana needs it for every link and redirect.
- **The custom domain is a local apply plus `make bind-domain`.** The managed
  certificate is a child of the shared environment in `rg-platform-dev`,
  where cd-terraform's identity cannot write, and the provider cannot finish
  the bind (#27362). `certificate_binding_type` is under `ignore_changes`, or
  every plan would propose unbinding it. Cloudflare records must be DNS only.
- **Only the image is under `ignore_changes`.** Env is managed by Terraform:
  nothing in it changes per release.
- **`pre-commit run --all-files` skips untracked files.** `git add` new files
  before `make lint`, or terraform-docs, tflint and checkov never see them.

## Terraform layout

`terraform/` is a deployable root configuration with a partial `azurerm`
backend (`backends/dev.hcl`). File layout is enforced by
`scripts/check-tf-standards.sh` (shared verbatim with the other Terraform
repos): `locals`/`variable`/`output`/`data` blocks in matching files or
topic-scoped variants. Required inputs live in `variables.required.tf`.
`.terraform.lock.hcl` carries hashes for linux_amd64, linux_arm64 and
darwin_arm64. Only dev exists.

## CI

Every workflow is a thin caller of
[`jay-withers/workflows`](https://github.com/jay-withers/workflows), pinned by
SHA with the tag as a comment, the same set gym-log uses:

- `ci-pre-commit` (`pre-commit / Pre-commit`), `ci-codeql`.
- `ci-terraform`: validate (`terraform / Terraform`), and a dev plan behind the
  always-reporting `terraform-plan` gate. The plan fails until the identity has
  Reader, i.e. until github-repos gives this repo `deploy_resource_groups`.
- `ci-container-build`: builds `grafana/` behind the `container-build` gate.
- `cd-tag` → `cd-publish` (when `grafana/` changed) → `container-apps-deploy`.
- `cd-terraform`: plans dev on merge; applies when run by hand.

Required checks are set in github-repos. Only `pre-commit / Pre-commit` and
`terraform / Terraform` are required until the rest report reliably.

## Commit messages

Conventional Commits, enforced by commitlint. `no-commit-to-branch` blocks
direct commits to `main`.
