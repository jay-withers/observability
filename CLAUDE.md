# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo does

Dashboards for the jay-withers Container Apps estate: a scale-to-zero
container app on the shared environment, reading the shared Log Analytics
workspace. It shows deployments (`Deployments_CL`), jobs and ops
(`OpsEvents_CL`), costs (`Costs_CL`) and app errors (console logs), as a
page per app and a Platform page for the shared infrastructure. It also runs
the checks that feed them: `schedule-expiry`, `schedule-heartbeats`,
`schedule-session-resources` and `schedule-costs`. `README.md` covers the
why and the first-time setup; this file covers the traps.

**The container app runs obsview** (`app/`, README "obsview"), a FastAPI
app in health's shape that replaced Grafana. Grafana, its image and the
Grafana Cloud sync were removed after the cutover; a new view goes in obsview.

A tenant of [azure-container-apps](https://github.com/jay-withers/azure-container-apps),
shaped like health: its own resource group and identity, the environment
and workspace resolved by name, never by remote state.

## Decisions not to re-litigate

- **The passcode is a container app secret from `random_password`**
  (`make passcode`), never a Key Vault secret: a Terraform-managed vault
  secret needs data-plane access just to plan, which the pipeline identity
  lacks (the estate's documented trap). No Key Vault exists here at all.
- **One shared passcode, no Entra SSO** (no app registration).
- **obsview, not Grafana.** The self-hosted Grafana's views were stat
  tiles and tables, slow to open and poor on a phone; obsview replaced it
  (2026-11-17). A move to Grafana Cloud was planned and never set up, so
  there is no stack or app registration to clean up. The old
  `observability/grafana` GHCR package is no longer built or deployed.
- **Created on dev-containers' `placeholder:latest`, never a release tag.**
  Terraform needs an image before any release exists; a guessed tag (v0.0.1
  vs v0.1.0, observability#4) passes plan and apply and fails only on the
  pull. The placeholder answers 200 on every path on `$PORT`, which is why
  `PORT` is set. The first deploy puts obsview on.
- **Scale to zero**, cold start accepted (decided at the obsview cutover,
  2026-11-17). It is opened a few times a week. Alerting lives in Azure
  Monitor, so nothing depends on it being awake.
- **The app repos' shared smoke test** (jay-withers/workflows'
  `scripts/smoke.sh`, which `make smoke` fetches at the commit cd-publish
  pins; no copy here): `/healthz` must report the tag, then `/readyz`, which queries
  the workspace as the app's identity, must answer 200. With
  `browser-check: true` in cd-publish it then loads `/` (the login page,
  unauthenticated) in Chromium, and any console error rolls the release back.
  Grafana had a smoke test of its own until the cutover.

## Traps

- **The resource group is github-repos'.** It creates `rg-observability-dev`
  (`create_resource_groups`) and grants the pipeline identity Contributor and
  conditioned RBAC Administrator on it, so role assignments *inside* the group
  apply in CD. This config reads it by data source; a `removed` block dropped
  the original from state. The `Log Analytics Reader` assignment on the
  platform's workspace is outside the group, so changing it is still a local
  apply.
- **New GHCR packages start private.** A new image name must be flipped to
  public once after its first push (as `observability/obsview` was); the
  app has no pull secret.
- **The custom domain is a local apply plus `make bind-domain`.** The managed
  certificate is a child of the shared environment in `rg-platform-dev`,
  where cd-terraform's identity cannot write, and the provider cannot finish
  the bind (#27362). `certificate_binding_type` is under `ignore_changes`, or
  every plan would propose unbinding it. The DNS records are in
  jay-withers/cloudflare's `app_domains`, and must stay DNS only.
- **`make deploy` owns the image and the whole env**, and both are under
  `ignore_changes`. Like health, it sets `IMAGE_TAG` on each deploy, because
  Terraform can't own a value that changes per release without showing
  drift on every plan. **Unlike health it uses `--replace-env-vars`**, not
  `--set-env-vars`: every variable the app reads is listed there, and
  anything not listed (a portal edit, Terraform's create-time `PORT`) is
  dropped on the next deploy. A new setting goes in the Makefile's `deploy`
  (values from Terraform outputs), and lands on the next deploy, not the
  next apply. It reads every output before touching the app, so a missing
  one stops the deploy instead of writing an empty value.
- **A container app secret can't be removed while a revision's env
  references it.** Drop the env reference in `make deploy` and deploy it
  before the Terraform change that removes the secret (as Grafana's
  `admin-password` went).
- **The scheduled checks run as the pipeline identity, not the app's.**
  Its subscription Reader is what reads Resource Graph and Cost Management.
  The app's own identity has `Log Analytics Reader` on the workspace and
  nothing else, and needs nothing more: costs reach it as `Costs_CL` rows.
- **Every `session` category gets a row on every run**, `ok` when nothing is
  up. The alert takes the latest row per Kind and Name, so dropping the `ok`
  rows would leave a deleted firewall's alert lit. A new category goes in the
  script's list *and* its KQL `case`.
- **Cost areas are fixed groups of resource groups**, so each keeps its
  colour whatever the month's ranking. A new resource group falls into
  "landing zone and other" until it is added to `AREAS` in
  `app/src/obsview/costs.py`.
- **The check scripts are tested in pre-commit, not a workflow.**
  `tests/run.sh` (`make test`) runs each `tests/*.test.sh` against stub `az`,
  `curl`, `openssl` and `sleep` written onto PATH, and the `script-tests` hook
  runs it, so `pre-commit / Pre-commit` fails a PR that breaks a check. A
  change to a check's jq or its retry, status or session rules needs a test
  there. The KQL itself is not exercised; check it against the workspace.
- **`pre-commit run --all-files` skips untracked files.** `git add` new files
  before `make lint`, or terraform-docs, tflint and checkov never see them.

## obsview (`app/`)

- **A view is one query for raw rows plus a pure function over them**
  (`deployments.kql`/`window`/`build`). Put the arithmetic in Python with a
  test, not in KQL. The window goes in as the query's `timespan`, never
  interpolated; the only interpolated value is `ENVIRONMENT`, which
  `Settings` holds to a slug.
- **ECharts is fetched by the Dockerfile with `--checksum`**, never
  committed (it is over the 500 KB hook) and never loaded from a CDN (the
  CSP is `script-src 'self'`). Its URL and hash live only on the Dockerfile's
  `ADD` line; `make app-vendor` reads them from there. Renovate does not see
  it.
- **Chart colours come from CSS tokens** in `static/app.css`, which
  `static/app.js` reads at draw time; light and dark are each their own
  steps. Outcomes use the status colours, triggers a fixed colour per name
  (never by rank). No inline `<script>` that executes: the chart payload is
  a `type="application/json"` block, and `headers.py` (copied verbatim from
  health, keep it identical) sets the CSP.
- **No `WORKSPACE_ID` means demo data** (`demo.py`), under a banner, so
  `make app-run` and the route tests need no Azure. A new table a view reads
  needs demo rows there too.
- **Jobs and ops reads "now" as each check's latest row in 48 hours**, and
  counts as problems exactly the statuses Grafana did (`ops.PROBLEMS`). A
  status a check invents later reads as neutral (`ops.SEVERITY`), not ok.
  Its status grid is capped at 30 days whatever the range.
- **The Costs forecast divides by days with data, not days elapsed**, as
  Grafana's did: the feed lags a day or two. The daily chart ends at the
  last day with data for the same reason. The budget is `BUDGET`
  (default 150).
- **A day's costs are its latest run's rows, summed** (`costs.KQL`), not
  `arg_max` per key: each run re-posts seven days, a key that fell to zero
  has no new row to replace its old one, and the rows from before
  `ResourceId` was sent (per group and service) would double the newer
  per-resource ones. `ResourceId` is azure-container-apps' column, read with
  `column_ifexists`; the feed groups by it and service (Cost Management
  takes two groupings at most) and reads the resource group from the ID.
- **Costs follow the range; the budget card stays the month.** The total,
  the breakdowns and the treemap cover the selected range, ending on the
  last day with data. The "vs prior" delta is shown only when the data
  reaches back to the start of the prior range, so retention can't pass for
  a rise from nothing. The ranges are `routes.RANGES` (1 to 90 days), 7 by
  default; the
  event tables keep 90 days (azure-container-apps), so the delta never shows
  on 60d or 90d, and console logs keep the workspace's 30, so errors reach
  back 30 days at most.
- **The pages are Apps, SLOs, Platform and Overall**, the estate's rows sliced by
  `apps.APPS`, plus the whole subscription's costs. `/` is a tile per page
  with its headline figures (`routes.home`): it reads the apps index's
  queries (`_index_queries`, so the warm-up covers both), every one
  optional, and counts the platform's problems as `/platform` does, quiet
  always-reporting checks included (`_silent`). `/apps` has a tile per app and every app's deploys together;
  `/apps/<slug>` is one app; `/platform` is the complement, whatever no app
  owns (`apps.platform_*`). All use the same builders over filtered rows, the
  same queries and windows (`routes._app_queries`, so they share the cache),
  and the same templates (`_deployments.html`, `_ops.html`, `_costs.html`,
  `_errors.html`, with `per_app` hiding the ops count tiles and the "By app"
  links, and `data-group` keying each chart's data). An app's or the platform's
  costs are its own rows only, built with `by_area=False`: the daily chart
  stacks its top services and the treemap starts at its resource groups, so
  nothing names another area. The budget card, the area stacking and the
  by-area bars are the whole subscription's, on `/overall` alone
  (`overall_page`, `overall` in `_costs.html`), which reads the same costs
  query and range end as the other pages. A new app is a row in
  `apps.APPS`; its `prefix` is the repo's `project_name`, which names its
  resource group and Container Apps workloads. Until then its rows show on
  Platform. A renamed repo keeps its old name in `former` (health's
  `gym-log`): rows written before the rename carry it, and without it its
  drift rows would land on Platform as unowned. Deploy rows go through
  `apps.parse_deploys`, which reads the old name as the slug so old and new
  deploys make one lane, and `/apps/<former>` and `/slo/<former>` redirect
  (303) to the slug. Only the deploy query is required on the app pages; on Platform
  none is, and each failure renders a "couldn't read" card. The old
  `/deployments`, `/ops` and `/costs` (to `/overall`) redirect (303, not 301: browsers keep
  a permanent redirect for good). The session and cost-feed checks write a
  row every run, so Platform counts "no row in 48 hours" as a problem
  (`routes._ALWAYS_REPORT`) rather than leaving their section out.
- **SLOs are `/slo` (a tile per app) and `/slo/<slug>`** (`slo.py`,
  `apps.slo_report`). Every SLI is good events over valid ones against a
  target in `slo.OBJECTIVES`: availability (no 5xx) and latency (within
  `slo.LATENCY_MS`, a literal in the KQL too) from `ContainerAppHTTPLogs`,
  only for apps with ingress (`App.serves`); job runs from
  `ContainerAppSystemLogs_CL`; deploys from the shared deploy query. The
  window is a fixed rolling 28 days whatever the range (inside the Container
  Apps logs' 30-day retention), so the range picker is hidden there. Status
  reads only the error budget left (`slo.AT_RISK`); burn rates are shown,
  not alerted on. The HTTP query counts by hour and workload in KQL (a row
  per request is too many to fetch), but every ratio and budget is Python.
  The HTTP table is resource-specific (no `_CL`, no `_s`); its latency
  column is read as `DurationMs` with `column_ifexists`, so if the name is
  wrong the latency SLI reads "no data" rather than failing. Neither KQL is
  exercised by the tests; check them against the workspace. The home page
  reads the SLO queries too (`_home_queries`), so the warm-up covers them.
- **App errors are console-log lines** (`errors.py`): `ERROR` or
  `CRITICAL`, or a 5xx access line, grouped in Python by first line with
  UUIDs, hex ids, numbers and quoted values masked. The query caps each
  workload at `errors.PER_WORKLOAD` lines (a literal in the KQL too) and
  returns its true total, so one crash loop can't crowd out other apps. App crashes come from the failed
  runs query, which reads apps as well as jobs. Neither KQL is exercised by
  the tests; check it against the workspace.
- **CSRF is `api/csrf.py`, a byte-for-byte copy of appkit's `csrf.py`**
  (v0.8.0; obsview doesn't depend on appkit). Re-copy rather than diverge:
  `diff` against appkit's tag should come back clean. Its middleware refuses
  cross-site POSTs, and the login form carries its `csrf_token`, which the
  login route checks before the passcode. A browser with no CSRF cookie
  at all is told sign-in needs cookies, not that the form expired. Route tests sign in through
  `login_form()`, which reads the token from `GET /login`.
- **The release shows beside the name** in the header of every signed-in
  page: `IMAGE_TAG`, trimmed, through the `app_version` template global,
  beside (not inside) the home link in `base.html`, and nothing when it is
  unset or blank. The sign-in page has no header, so it never shows. That is
  a choice about the page, not a secret, since `/healthz` reports the tag to
  anyone.
- **No Key Vault**: the passcode is the container app secret `passcode`,
  mapped to `APP_PASSCODE` by `make deploy`.
- **`/readyz` fails without `WORKSPACE_ID`** unless `DEMO=true` is set:
  demo data in Azure is a misconfiguration. It is checked by the smoke test,
  not used as the readiness probe, so a slow workspace shows the app's error
  page rather than taking it out of rotation.

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
SHA with the tag as a comment, the same set health uses:

- `ci-pre-commit` (`pre-commit / Pre-commit`), `ci-codeql`.
- `ci-terraform`: validate (`terraform / Terraform`), and a dev plan behind the
  always-reporting `terraform-plan` gate. The plan fails until the identity has
  Reader, i.e. until github-repos gives this repo `deploy_resource_groups`.
- `ci-python`: obsview's pytest, mypy and coverage from `app/` (not yet
  required).
- `ci-container-build`: builds `app/` (image `obsview`) behind the
  `container-build` gate.
- `cd-tag` → `cd-publish` (when `app/` changed) → `container-apps-deploy`,
  which deploys and smoke-tests `obsview`.
- `cd-terraform`: plans dev on merge; applies when run by hand.

Required checks are set in github-repos. Only `pre-commit / Pre-commit` and
`terraform / Terraform` are required until the rest report reliably.

## Commit messages

Conventional Commits, enforced by commitlint. `no-commit-to-branch` blocks
direct commits to `main`.
