# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo does

Grafana for the jay-withers Container Apps estate: a scale-to-zero container
app on the shared environment, reading the shared Log Analytics workspace.
It ships the Deployments dashboard over `Deployments_CL` (phase 1 of the
observability plan), Jobs and ops over `OpsEvents_CL` (phase 2) and Costs over
`Costs_CL`. It also runs the checks that feed them: `schedule-expiry`,
`schedule-heartbeats`, `schedule-session-resources` and `schedule-costs`.
`README.md` covers the why and the first-time setup; this file covers the
traps.

A tenant of [azure-container-apps](https://github.com/jay-withers/azure-container-apps),
shaped like gym-log: its own resource group and identity, the environment
and workspace resolved by name, never by remote state.

## Decisions not to re-litigate

- **Dashboards and the data source ship inside the image** (`grafana/`), not
  through the Grafana Terraform provider. That provider must reach a running
  Grafana at plan time, and this one is created by the same apply and scales
  to zero. Provisioned dashboards are read-only in the UI on purpose.
- **`__WORKSPACE_RESOURCE_ID__` in dashboard JSON** is filled in by
  `grafana/entrypoint.sh` at start-up. Grafana substitutes environment
  variables in provisioning YAML but not in dashboard JSON. Never commit a
  real workspace ID into a dashboard.
- **The admin password is a container app secret from `random_password`**, never
  a Key Vault secret: a Terraform-managed vault secret needs data-plane access
  just to plan, which the pipeline identity lacks (the estate's documented
  trap). No Key Vault exists here at all.
- **Grafana's state is its default SQLite file, on the replica's disk**, not
  a database server: a Postgres B1ms bills every hour to keep a single admin
  login. A new replica starts empty, which loses only UI preferences: the
  dashboards and data source are provisioned, and `GF_SECURITY_ADMIN_PASSWORD`
  sets the admin on every start. Don't move it to an Azure Files mount:
  SQLite's locking over SMB is the documented way to corrupt it.
- **One local admin, no Entra SSO** (no app registration). Sign-up and
  anonymous access are off.
- **Moving to Grafana Cloud** (README, "Grafana Cloud") to remove the cold
  start. Log Analytics stays the data source and Azure Monitor keeps
  alerting. The stack is configured by `scripts/sync-grafana-cloud.sh` through
  its API, not by the Grafana Terraform provider: this root plans in CI as
  the Azure identity, and the provider would add a Grafana token to every
  plan for no benefit. The stack reads the workspace as an app registration
  with a client secret, created by hand (README), because Grafana Cloud
  cannot use a managed identity and the pipeline identity cannot create app
  registrations. This is a deliberate exception to "no app registration".
  The container app above stays until the stack is verified, and is retired
  in a separate change.
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

- **The resource group is github-repos'.** It creates `rg-observability-dev`
  (`create_resource_groups`) and grants the pipeline identity Contributor and
  conditioned RBAC Administrator on it, so role assignments *inside* the group
  apply in CD. This config reads it by data source; a `removed` block dropped
  the original from state. The `Log Analytics Reader` assignment on the
  platform's workspace is outside the group, so changing it is still a local
  apply.
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
  every plan would propose unbinding it. The DNS records are in
  jay-withers/cloudflare's `app_domains`, and must stay DNS only.
- **Only the image is under `ignore_changes`.** Env is managed by Terraform:
  nothing in it changes per release.
- **The scheduled checks run as the pipeline identity, not Grafana's.**
  Its subscription Reader is what reads Resource Graph and Cost Management.
  Grafana's own identity has `Log Analytics Reader` on the workspace and
  nothing else, and needs nothing more: costs reach it as `Costs_CL` rows.
- **Every `session` category gets a row on every run**, `ok` when nothing is
  up. The alert takes the latest row per Kind and Name, so dropping the `ok`
  rows would leave a deleted firewall's alert lit. A new category goes in the
  script's list *and* its KQL `case`.
- **The Costs dashboard's areas are fixed groups of resource groups**, so
  each keeps its colour whatever the month's ranking. A new resource group
  falls into "landing zone and other" until it is added to the `case` in
  every panel that groups by area.
- **The check scripts are tested in pre-commit, not a workflow.**
  `tests/run.sh` (`make test`) runs each `tests/*.test.sh` against stub `az`,
  `curl`, `openssl` and `sleep` written onto PATH, and the `script-tests` hook
  runs it, so `pre-commit / Pre-commit` fails a PR that breaks a check. A
  change to a check's jq or its retry, status or session rules needs a test
  there. The KQL itself is not exercised; check it against the workspace.
  `grafana/entrypoint.sh` is untested (fixed image paths, then `exec`).
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
- `cd-grafana-cloud`: syncs `grafana/dashboards/` and the data source to the
  Grafana Cloud stack on merge and on demand. Skips with a notice while
  `GRAFANA_URL` is unset on `dev`. It is tested in `tests/sync-grafana-cloud.test.sh`.

Required checks are set in github-repos. Only `pre-commit / Pre-commit` and
`terraform / Terraform` are required until the rest report reliably.

## Commit messages

Conventional Commits, enforced by commitlint. `no-commit-to-branch` blocks
direct commits to `main`.
