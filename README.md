# observability

Grafana over the shared Log Analytics workspace, for every app on the
[shared Container Apps environment](https://github.com/jay-withers/azure-container-apps):
did the last deploy work, is the app healthy, are its jobs running, and what
is it costing.

It is phase 1 of the observability plan: the **Deployments** dashboard, read
from the `Deployments_CL` table that
[`container-apps-deploy.yml`](https://github.com/jay-withers/workflows) writes
on every deploy. Later phases add dashboards for jobs and operations
(`OpsEvents_CL`), app health, delivery and cost.

## How it fits together

- **Grafana** runs as a scale-to-zero container app on the shared environment,
  in this repo's own resource group (`rg-observability-dev`). The first page
  load after an idle spell waits on a cold start.
- **Its state** (users, preferences) is in the `grafana` database on the
  platform's shared Postgres server, owned by azure-container-apps.
- **What it reads** is the shared workspace, as its own managed identity
  (`Log Analytics Reader` on the workspace and nothing else).
- **Dashboards and the data source are files in `grafana/`, baked into the
  image.** They are read-only in the UI: change them here, by pull request.
  The panels name the workspace by resource ID, which `grafana/entrypoint.sh`
  fills in at start-up from `WORKSPACE_RESOURCE_ID`, so the JSON in git
  carries no subscription.
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
   resource group and the workspace role assignment, which the pipeline
   identity cannot.
5. **`make db-role`.** Creates Grafana's `grafana` role on the shared server,
   with the password from this state, and gives it the `grafana` database.
   It needs `psql` and briefly opens a firewall rule for your public IP,
   because the server only admits Azure IPs. Grafana reports unhealthy until
   this has run.
6. **github-repos**: add `deploy_resource_groups = ["rg-observability-dev"]`,
   the `cae-platform-dev` join and `send_events = true` to this repo's
   `state_consumers` entry, and `make apply` there. From then on cd-terraform
   applies, cd-publish deploys, and every deploy is recorded like any app's.
   Re-run the failed deploy from step 2 (or `make deploy IMAGE_TAG=<release>`)
   to replace the placeholder with Grafana.
7. **`make url`**, sign in, open *Platform → Deployments*.

## The custom domain

Grafana is served at `observability.jaywithers.uk` (`custom_domain_name` in
`environments/dev.tfvars`) with a free Azure-managed certificate, the same
way finances, gym-log and career are. `make dns` prints the CNAME and
`asuid` TXT records; on Cloudflare they are **DNS only**, never proxied, or
certificate validation fails. Both must resolve before the apply.

Adding or changing the domain is a **local** `make apply`, then
`make bind-domain`: the certificate is created in the platform's resource
group, which cd-terraform's identity cannot write to, and the provider cannot
finish the bind itself (hashicorp/terraform-provider-azurerm#27362). Grafana's
root URL moves to the domain in the same apply, so its links point there.
The default `*.azurecontainerapps.io` URL keeps working, and the smoke test
uses it.

## Commands

```bash
make install          # pre-commit hooks
make lint             # every hook against every file
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
make db-role          # create or re-password Grafana's database role
```

## Changing a dashboard

Edit the JSON under `grafana/dashboards/`, keeping `__WORKSPACE_RESOURCE_ID__`
in each query's `resources`. To work on one interactively, export it from a
local Grafana (`make build`, then run the image with `WORKSPACE_RESOURCE_ID`
set) and replace the workspace ID with the placeholder before committing.

## Costs

The container app scales to zero, so it bills only while someone has it open.
Its database is on the platform's shared Postgres server, whose cost belongs to
azure-container-apps.
