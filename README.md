# observability

Grafana over the shared Log Analytics workspace, for every app on the
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

## How it fits together

- **Grafana** runs as a scale-to-zero container app on the shared environment,
  in this repo's own resource group (`rg-observability-dev`). The first page
  load after an idle spell waits on a cold start.
- **Its state** is Grafana's default SQLite file on the replica's own disk,
  so it starts empty on every new replica. Dashboards, the data source and the
  admin password all come back from the image and the app's settings, so
  only UI preferences are lost. No database server, so nothing bills while
  it sleeps.
- **What it reads** is the shared workspace, as its own managed identity
  (`Log Analytics Reader` on the workspace and nothing else).
- **Dashboards and the data source are files in `grafana/`, baked into the
  image.** They are read-only in the UI: change them here, by pull request.
  The panels name the workspace by resource ID, which `grafana/entrypoint.sh`
  fills in at start-up from `WORKSPACE_RESOURCE_ID`, so the JSON in git
  carries no subscription.
- **Certificate expiry is checked here, nightly.** `schedule-expiry.yml` runs
  `scripts/check-expiry.sh` over `scripts/expiry-hosts.txt` (a TLS handshake
  per host, which doesn't wake a scale-to-zero app) and records one
  `OpsEvents_CL` row per host. `make expiry` prints the same rows locally. Add
  a host to the file when a custom domain is bound.
- **Job heartbeats are computed here, daily.** `schedule-heartbeats.yml` runs
  `scripts/check-heartbeats.sh`, which learns each job's cadence from 30 days
  of `ContainerAppSystemLogs_CL` (no job list to maintain) and records one
  `heartbeat` row per job: `overdue` once its last success is older than 1.5x
  its longest normal gap. A log alert can look back only two days, too short
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
  per resource group and service (Azure revises a day for about three) and
  posts them to `Costs_CL`. The run also records a `cost-feed` row in
  `OpsEvents_CL`, so a broken feed alerts instead of leaving the dashboard
  stale. Both checks run as this repo's pipeline identity, whose subscription
  Reader is enough for Resource Graph and Cost Management. `make costs` prints
  the rows locally.
- **Alerting is not here.** It stays in Azure Monitor (azure-container-apps),
  so nothing depends on Grafana being awake.

Sign in as `admin` with the password from `make admin-password`. There is no
Entra single sign-on, by decision: it would need an app registration.

## First-time setup

In order, once:

1. **github-repos** already lists this repo as a state consumer, so the OIDC
   identity and the `dev` environment's `AZURE_*` variables exist.
2. **Merge the first pull request.** `cd-tag` releases and publishes
   `ghcr.io/jay-withers/observability/grafana`. Its deploy job fails, because
   the app does not exist yet: expected, and the `deploy-failed` issue closes
   on the next successful deploy. Terraform never names a release: it creates
   the app on dev-containers' `placeholder` image, which answers 200 on every
   path, and the first deploy puts Grafana on.
3. **Make the package public**: GitHub → your packages →
   `observability/grafana` → Package settings → Change visibility. New
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
   to replace the placeholder with Grafana.
7. **`make url`**, sign in, open *Platform → Deployments*.

## The custom domain

Grafana is served at `observability.jaywithers.uk` (`custom_domain_name` in
`environments/dev.tfvars`) with a free Azure-managed certificate, the same
way finances, gym-log and career are. Its CNAME and `asuid` TXT records are
the `observability` entry in [jay-withers/cloudflare](https://github.com/jay-withers/cloudflare)'s `app_domains`, **DNS
only**, never proxied, or certificate validation fails (`make dns` prints
what they should be). Both must resolve before the apply.

Adding or changing the domain is a **local** `make apply`, then
`make bind-domain`: the certificate is created in the platform's resource
group, which cd-terraform's identity cannot write to, and the provider cannot
finish the bind itself (hashicorp/terraform-provider-azurerm#27362). Grafana's
root URL moves to the domain in the same apply, so its links point there.
The default `*.azurecontainerapps.io` URL keeps working, and the smoke test
uses it.

## Grafana Cloud

The same dashboards are moving to a free Grafana Cloud stack, which is always
on, so there is no cold start. Log Analytics stays the data source and Azure
Monitor keeps the alerting. Only where Grafana runs changes. While both exist,
the container app above is the fallback. Retiring it (the app, image, custom
domain and its Cloudflare records) is a separate change, once the stack is
confirmed working.

`cd-grafana-cloud.yml` runs `scripts/sync-grafana-cloud.sh` on every merge
that touches `grafana/dashboards/`, and on demand. It writes the
`azure-monitor` data source, the Platform folder and every dashboard through
the stack's API, and deletes any dashboard in that folder that git no longer
has. The UI does not lock dashboards: an edit made there is overwritten by
the next run, so change the JSON here.

Grafana Cloud runs outside Azure and cannot use a managed identity, so it
reads the workspace as an **app registration with a client secret**. That is
the one secret this setup adds, and it expires (see step 3).

Set up once, in order:

1. **Create the stack.** Sign up at grafana.com (free tier) and create a stack,
   e.g. `https://jaywithers.grafana.net`. In the stack, go to Administration →
   Users and access → Service accounts and add one with role **Admin**
   (Editor cannot write data sources). Then create a token for it.
2. **Create the app registration** that the stack reads Log Analytics as
   (`az login` first, as someone who can create app registrations):

   ```bash
   app=$(az ad app create --display-name grafana-cloud-observability --query appId -o tsv)
   az ad sp create --id "$app"
   az role assignment create --assignee "$app" --role "Log Analytics Reader" \
     --scope "$(az monitor log-analytics workspace show -g rg-platform-dev -n log-platform-dev --query id -o tsv)"
   ```

3. **Store the settings on the `dev` environment.** The secrets go through a
   pipe, never onto the command line or the screen:

   ```bash
   R=jay-withers/observability
   gh variable set GRAFANA_URL --env dev -R $R --body https://jaywithers.grafana.net
   gh variable set GRAFANA_AZURE_CLIENT_ID --env dev -R $R --body "$app"
   az ad app credential reset --id "$app" --display-name grafana-cloud --years 1 \
     --query password -o tsv | gh secret set GRAFANA_AZURE_CLIENT_SECRET --env dev -R $R
   gh secret set GRAFANA_TOKEN --env dev -R $R   # paste the token from step 1
   ```

   The client secret lasts a year. To renew it, rerun the `credential reset`
   line, then run the workflow again.
4. **Run `cd-grafana-cloud`** from the Actions tab, then open
   *Dashboards → Platform* on the stack.


```bash
make install          # pre-commit hooks
make lint             # every hook against every file
make test             # the check scripts' tests (bash and jq, Azure stubbed out)
make validate         # terraform init (no backend) + validate
make plan             # terraform plan for dev
make apply            # the first apply only; cd-terraform after that
make build            # build the Grafana image for linux/amd64
make deploy IMAGE_TAG=vX.Y.Z
make smoke IMAGE_TAG=vX.Y.Z
make url              # Grafana's URL
make dns              # the DNS records the custom domain needs
make bind-domain      # finish binding the managed certificate after an apply
make admin-password   # the admin password
make expiry           # the TLS expiry rows schedule-expiry would record
make sessions         # the session-resource rows schedule-session-resources would record
make costs            # the Costs_CL rows schedule-costs would record
```

## Changing a dashboard

Edit the JSON under `grafana/dashboards/`, keeping `__WORKSPACE_RESOURCE_ID__`
in each query's `resources`. To work on one interactively, export it from a
local Grafana (`make build`, then run the image with `WORKSPACE_RESOURCE_ID`
set) and replace the workspace ID with the placeholder before committing.

## Costs

The container app scales to zero, so it bills only while someone has it open.
Its database is a SQLite file inside the container, so it has no cost of its own. The scheduled checks run on GitHub-hosted runners and
add a few hundred rows a night to the workspace, inside its free grant.
