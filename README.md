# observability

Dashboards over the shared Log Analytics workspace, for every app on the
[shared Container Apps environment](https://github.com/jay-withers/azure-container-apps):
did the last deploy work, is the app healthy, are its jobs running, and what
is it costing.

Three dashboards so far:

- **Deployments** (phase 1 of the observability plan), from the
  `Deployments_CL` table that
  [`container-apps-deploy.yml`](https://github.com/jay-withers/workflows)
  writes on every deploy.
- **Jobs and ops** (phase 2, liveness), from `OpsEvents_CL`: a heartbeat per
  Container Apps job, failed job runs, nightly Terraform drift per root and
  certificate expiry per custom domain.
- **Costs**, from `Costs_CL`: month to date and a month-end forecast against
  the $150 budget, daily spend by area, month to date by resource group and by
  service, and the nightly session-resource check. Figures are before any
  credit, so on the Visual Studio subscription they are what pay-as-you-go
  would charge.

Later phases add app health and delivery.

## obsview: the replacement for Grafana

`app/` is **obsview**, a small FastAPI app that replaced Grafana: the same
tables, with charts designed for them, built for a phone. It was built in
steps:

1. **Deployments**: KPI tiles with sparklines and deltas against the
   previous period, outcomes per day, a per-app deploy timeline, a 13-week
   activity calendar, lead-time distribution, per-app cards and the recent
   deploys.
2. **Jobs and ops** and **Costs**. Jobs and ops: a problems-now summary,
   counts per kind, a 30-day status grid per kind of check (worst status per
   day), certificate expiry as gauges, and failed job runs. Costs: month to
   date against forecast and the $150 budget as one bullet bar, daily spend
   stacked by area, a treemap of the month (area, resource group, service),
   the top resource groups and services, and the session resources.
3. **The cutover**: the container app runs obsview
   (`ghcr.io/jay-withers/observability/obsview`).
4. **Grafana retired**: its dashboards, image build and the never-used
   Grafana Cloud sync were removed.

(Grafana Cloud was never set up, so the views came before the cutover: it
could not leave Jobs and ops or Costs with nowhere to live.)

How it works:

- **One query per view.** It fetches the raw rows for the range (and the one
  before it, for the deltas) and computes every tile and chart in Python
  (`app/src/obsview/deployments.py`), so the arithmetic is unit-tested
  rather than living in panel KQL. Results are cached in memory for five
  minutes, and the default view is fetched at start-up.
- **It reads the workspace as the container app's own identity**
  (`Log Analytics Reader`, already granted), by `WORKSPACE_ID`, the
  workspace's customer ID.
- **Charts are ECharts**, served from the image. The image fetches it at
  build time with a pinned SHA-256 (`app/Dockerfile`), because the file is
  over the repo's 500 KB limit and a CDN would need the CSP opened to it.
  Tiles and sparklines are plain HTML and SVG, drawn server-side.
- **A passcode gate**, the same signed-cookie scheme as health, from
  `APP_PASSCODE`.

Run it locally with `make app-run` (add `PORT=` if 8000 is taken). With no
`WORKSPACE_ID` it serves generated demo data under a banner. Put
`WORKSPACE_ID=<customer id>` in `app/.env` after `az login` to read the real
tables as yourself. The passcode is `local` unless `APP_PASSCODE` says
otherwise. `make test` runs the app's tests and mypy along with the check
scripts' tests.

## How it fits together

- **obsview** runs as a scale-to-zero container app on the shared
  environment, in this repo's own resource group (`rg-observability-dev`).
  The first page load after an idle spell waits on a cold start, which is
  accepted: it is opened a few times a week.
- **It keeps no state.** Every view is computed from the workspace on
  request, and cached in memory for five minutes.
- **What it reads** is the shared workspace, as its own managed identity
  (`Log Analytics Reader` on the workspace and nothing else).
- **The views are code in `app/`** (see "Changing a view" below).
- **Certificate expiry is checked here, nightly.** `schedule-expiry.yml` runs
  `scripts/check-expiry.sh` over `scripts/expiry-hosts.txt` (a TLS handshake
  per host, which doesn't wake a scale-to-zero app) and records one
  `OpsEvents_CL` row per host. `make expiry` prints the same rows locally. Add
  a host to the file when a custom domain is bound.
- **Job heartbeats are computed here, daily.** `schedule-heartbeats.yml` runs
  `scripts/check-heartbeats.sh`, which learns each job's cadence from 30 days
  of `ContainerAppSystemLogs_CL` (no job list to maintain) and records one
  `heartbeat` row per job: `overdue` once its last success is older than 1.5x
  its longest normal gap, or `removed` once Resource Graph no longer lists
  the job, so a deliberately deleted one doesn't alert. A log alert can look back only two days, too short
  for a weekly job, which is why this isn't an Azure Monitor rule. It also
  records a `check:` heartbeat for each daily check that writes `OpsEvents_CL`
  (each root's drift, cost-feed, expiry, session), `overdue` after 36 hours
  without a row, so a run that dies before it can post isn't silent.
  `scripts/post-ops-events.sh` posts the rows for every scheduled check.
- **Session resources are checked here, nightly.** `schedule-session-resources.yml`
  runs `scripts/check-session-resources.sh`, one Resource Graph query for
  things billed by the hour that are meant to exist only for a working
  session: a firewall, a running AKS cluster, a VM that isn't deallocated, a
  non-Developer bastion, a VPN, application or NAT gateway, a Container Apps
  workload profile. It records one `session` row per category, `ok` or
  `running`, and the platform's `ops_event_bad` alert fires on `running`. To
  keep one overnight on purpose, tag it `session-until=YYYY-MM-DD`.
  `make sessions` prints the rows locally.
- **Costs are read here, daily.** `schedule-costs.yml` runs
  `scripts/check-costs.sh`, which asks Cost Management for the last seven days
  per resource and service (Azure revises a day for about three) and
  posts them to `Costs_CL`, with each row's resource group read from its
  resource ID (the query takes two groupings at most). The run also records a `cost-feed` row in
  `OpsEvents_CL`, so a broken feed alerts instead of leaving the dashboard
  stale. Both checks run as this repo's pipeline identity, whose subscription
  Reader is enough for Resource Graph and Cost Management. `make costs` prints
  the rows locally.
- **Alerting is not here.** It stays in Azure Monitor (azure-container-apps),
  so nothing depends on the app being awake.

Sign in with the passcode from `make passcode`. The session lasts 30 days.
There is no Entra single sign-on, by decision: it would need an app
registration.

## First-time setup

In order, once:

1. **github-repos** already lists this repo as a state consumer, so the OIDC
   identity and the `dev` environment's `AZURE_*` variables exist.
2. **Merge the first pull request.** `cd-tag` releases and publishes
   `ghcr.io/jay-withers/observability/obsview`. Its deploy job fails, because
   the app does not exist yet: expected, and the `deploy-failed` issue closes
   on the next successful deploy. Terraform never names a release: it creates
   the app on dev-containers' `placeholder` image, which answers 200 on every
   path, and the first deploy puts obsview on.
3. **Make the package public**: GitHub → your packages →
   `observability/obsview` → Package settings → Change visibility. New
   packages start private whatever the repository's visibility, and the
   container app has no pull secret.
4. **`make apply` locally** (as yourself, `az login` first). It creates the
   workspace role assignment, which is outside this project's resource group
   and so beyond the pipeline identity. The group itself comes from
   github-repos (`create_resource_groups`, step 5), which must be applied
   first.
5. **github-repos**: add `deploy_resource_groups = ["rg-observability-dev"]`,
   `create_resource_groups = true`, the `cae-platform-dev` join and
   `send_events = true` to this repo's `state_consumers` entry, and
   `make apply` there. (It was done in this order the first time, with a
   local apply creating the group; on a rebuild, do this before step 4.) From then on cd-terraform
   applies, cd-publish deploys, and every deploy is recorded like any app's.
   Re-run the failed deploy from step 2 (or `make deploy IMAGE_TAG=<release>`)
   to replace the placeholder with obsview.
6. **`make url`**, and sign in with `make passcode`.

## The custom domain

obsview is served at `observability.jaywithers.uk` (`custom_domain_name` in
`environments/dev.tfvars`) with a free Azure-managed certificate, the same
way finances, health and career are. Its CNAME and `asuid` TXT records are
the `observability` entry in [jay-withers/cloudflare](https://github.com/jay-withers/cloudflare)'s `app_domains`, **DNS
only**, never proxied, or certificate validation fails (`make dns` prints
what they should be). Both must resolve before the apply.

Adding or changing the domain is a **local** `make apply`, then
`make bind-domain`: the certificate is created in the platform's resource
group, which cd-terraform's identity cannot write to, and the provider cannot
finish the bind itself (hashicorp/terraform-provider-azurerm#27362).
obsview's links are all relative, so it needs no setting for its own URL.
The default `*.azurecontainerapps.io` URL keeps working, and the smoke test
uses it.

## Commands

```bash
make install          # mise tools and pre-commit hooks
make lint             # every hook against every file
make test             # the app's tests and the check scripts' tests (Azure stubbed out)
make app-run          # serve obsview locally (demo data unless app/.env sets WORKSPACE_ID)
make validate         # terraform init (no backend) + validate
make init             # terraform init against dev's backend
make fmt              # terraform fmt -recursive
make plan             # terraform plan for dev
make apply            # the first apply, and custom-domain changes; cd-terraform otherwise
make outputs          # this deployment's outputs
make build            # build the obsview image for linux/amd64
make push             # push it to ghcr.io (needs write:packages)
make deploy IMAGE_TAG=vX.Y.Z
make deployed-tag     # the image tag live on obsview
make smoke IMAGE_TAG=vX.Y.Z
make url              # obsview's URL
make dns              # the DNS records the custom domain needs
make bind-domain      # finish binding the managed certificate after an apply
make passcode         # obsview's passcode
make expiry           # the TLS expiry rows schedule-expiry would record
make sessions         # the session-resource rows schedule-session-resources would record
make costs            # the Costs_CL rows schedule-costs would record
```

## CI/CD

Every workflow calls a shared one in
[jay-withers/workflows](https://github.com/jay-withers/workflows), pinned by
SHA, apart from the scheduled checks, which run this repo's scripts.

- **ci-pre-commit**, **ci-python** (obsview's tests), **ci-container-build** (build plus Trivy) and
  **ci-terraform** (validate, then a real plan of dev on the PR) on pull
  requests. **ci-codeql** scans the workflows.
- **cd-tag** → **cd-publish**: a release on every merge, publishing
  `ghcr.io/jay-withers/observability/obsview` and deploying it with the
  shared smoke test and rollback, like any app.
- **cd-terraform**: plans dev on merge and opens a `terraform-pending` issue;
  run it by hand to apply.
- **schedule-expiry**, **schedule-heartbeats**, **schedule-session-resources**
  and **schedule-costs**: the nightly and daily checks described in
  [How it fits together](#how-it-fits-together). Each can also be run by hand.
- **schedule-drift**: a nightly plan of dev, recorded in `OpsEvents_CL`.
- **manual-terraform-unlock**: releases a stuck state lock.

## Changing a view

A view is one KQL query for raw rows (`kql` and `window` in its module under
`app/src/obsview/`) and a pure function that turns them into tiles and chart
data (`build`), with its tests in `app/tests/`. The page is a Jinja template;
the charts are drawn by `static/app.js` from the JSON the template embeds.
`make app-run` serves it on demo data.

The home page, `/`, is a tile for each of the pages, **Apps**, **SLOs**,
**Platform** and **Overall**, with its headline: the apps' deploys, problems
and errors, how many apps are within their error budgets, the platform's
problems now and errors, and the month's spend against the budget.
`/apps` has a tile per app and,
under them, every app's deploys together (deploys are all apps': only
container-apps-deploy records them). Each app has its own page,
`/apps/<repo>`, with that app's deploys, checks, failed runs and crashes,
spend and logged errors over the selected range. `/platform` is everything no
app owns: landing zone, shared environment, Cloudflare and AKS drift, session
resources, the cost feed and the shared resource groups' spend. An app's and
the platform's costs are only their own rows, stacked by their own services.
`/overall` is the whole subscription's costs: the month against the budget,
daily spend stacked by area, the treemap (area, resource group, resource,
service) and the range's spend by area, resource group, service and
resource. Every page opens on the last 7 days, and its section links stay
pinned while it scrolls. Which rows belong to
an app is set in `app/src/obsview/apps.py`: add a row there when a new app
joins the estate, or its rows show up under Platform. The old
`/deployments`, `/ops` and `/costs` URLs redirect (`/costs` to Overall).

## SLOs

`/slo` is a tile per app with its service level objectives over a rolling
28 days: its worst status, the error budget it has least of, and each SLI
against its target. Open one for `/slo/<app>`: each SLI's value, budget left,
bad events against those allowed, burn rate over the last day and week, and
the budget's burn-down by day.

| SLI | Good events | Target | Source |
| --- | --- | --- | --- |
| Availability | Requests answered without a 5xx | 99.5% | `ContainerAppHTTPLogs` |
| Latency | Requests answered within 1,000 ms | 95% | `ContainerAppHTTPLogs` |
| Job runs | Job executions that completed | 95% | `ContainerAppSystemLogs_CL` |
| Deploys | Deploys that succeeded | 90% | `Deployments_CL` |

Availability and latency apply to apps with ingress; repo-agent and
security-testing run jobs only. An app is *within budget* with a quarter or
more of every budget left, *at risk* under a quarter, and *budget spent* at
none. The targets are `OBJECTIVES` in `app/src/obsview/slo.py`. Alerting
stays in Azure Monitor: these pages only show the figures.

## Costs

The container app scales to zero, so it bills only while someone has it open.
It keeps no database, so it has no cost of its own. The scheduled checks run on GitHub-hosted runners and
add a few hundred rows a night to the workspace, inside its free grant.
