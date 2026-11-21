TF_DIR := terraform
ENV ?= dev

IMAGE_REGISTRY ?= ghcr.io/jay-withers/observability
# Defaults to the local commit, which is what you want when iterating.
IMAGE_TAG ?= $(shell git rev-parse --short HEAD)
# `file` means the ?= default fired rather than the caller passing one.
IMAGE_TAG_EXPLICIT := $(filter-out file,$(origin IMAGE_TAG))

.DEFAULT_GOAL := help

.PHONY: help install lint test app-test app-vendor app-run init fmt validate plan apply outputs build push deploy deployed-tag smoke url dns bind-domain passcode expiry sessions costs

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) \
		| awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-15s\033[0m %s\n", $$1, $$2}'

install: ## Install mise tools, pre-commit hooks and the app's Python dependencies
	mise install
	pre-commit install
	pre-commit install --hook-type commit-msg
	cd app && uv sync --extra dev

lint: ## Run all pre-commit hooks against every file
	pre-commit run --all-files

test: app-test ## Test the check scripts (stubbed az, curl, openssl) and the app
	tests/run.sh

# --extra dev: pytest is an extra, so `uv run` alone would not install it.
app-test: ## Test and type-check the obsview app (app/)
	cd app && uv run --extra dev pytest -q
	cd app && uv run --extra dev mypy

# The chart library is not committed (over the large-file limit). Its URL and
# hash are read from app/Dockerfile's ADD, so the image and `make app-run`
# cannot serve different files.
ECHARTS_URL = $(shell grep -o 'https://cdn.jsdelivr.net/npm/echarts@[^ ]*' app/Dockerfile)
ECHARTS_SHA256 = $(shell grep -o 'checksum=sha256:[0-9a-f]*' app/Dockerfile | cut -d: -f2)

app-vendor: ## Fetch the pinned chart library (as app/Dockerfile pins it) into app/vendor
	@test -n "$(ECHARTS_URL)" && test -n "$(ECHARTS_SHA256)" || \
		{ echo "error: no echarts ADD --checksum line in app/Dockerfile" >&2; exit 1; }
	mkdir -p app/vendor
	curl -fsSL -o app/vendor/echarts.min.js $(ECHARTS_URL)
	echo "$(ECHARTS_SHA256)  app/vendor/echarts.min.js" | sha256sum -c -

# No WORKSPACE_ID means demo data, so this works with no Azure at all. Set
# WORKSPACE_ID (the workspace's customer ID) in app/.env, after `az login`,
# to read the real tables as yourself.
PORT ?= 8000
app-run: ## Serve the app on PORT (demo data unless app/.env sets WORKSPACE_ID)
	@test -f app/vendor/echarts.min.js || $(MAKE) app-vendor
	cd app && APP_PASSCODE=$${APP_PASSCODE:-local} uv run obsview serve --reload --port $(PORT)

init: ## terraform init against ENV's backend
	terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl

fmt: ## terraform fmt -recursive
	terraform -chdir=$(TF_DIR) fmt -recursive

validate: ## terraform init (no backend) + validate
	terraform -chdir=$(TF_DIR) init -backend=false -input=false
	terraform -chdir=$(TF_DIR) validate

plan: init ## terraform plan (ENV=dev)
	terraform -chdir=$(TF_DIR) plan -var-file=environments/$(ENV).tfvars

# Only the first apply is local: it creates the resource group and the
# workspace role assignment, which cd-terraform's identity cannot. After that,
# run cd-terraform from the Actions tab.
apply: init ## terraform apply (ENV=dev). Local for the first apply only
	terraform -chdir=$(TF_DIR) apply -var-file=environments/$(ENV).tfvars

outputs: init ## Print this deployment's outputs
	terraform -chdir=$(TF_DIR) output

build: ## Build the obsview image for linux/amd64 (set IMAGE_TAG, defaults to the git SHA)
	docker buildx build --platform linux/amd64 --load \
		-t $(IMAGE_REGISTRY)/obsview:$(IMAGE_TAG) app

push: ## Push the image to ghcr.io (needs write:packages)
	gh auth token | docker login ghcr.io -u $$(gh api user --jq .login) --password-stdin
	docker push $(IMAGE_REGISTRY)/obsview:$(IMAGE_TAG)

# A bare `make deploy` is a hard error: the default tag is whatever commit is
# checked out, which may never have been pushed.
#
# **This sets the whole env** (`--replace-env-vars`, where health uses
# --set-env-vars), which Terraform ignores:
# IMAGE_TAG changes every release, so it can't be Terraform's. The rest come
# from Terraform's outputs, so this is the one place the env is written down.
# Replacing rather than adding is also what cleared Grafana's GF_* values at
# the cutover. A new setting goes here, and lands on the next deploy.
deploy: ## Roll an image tag onto obsview, with its env (IMAGE_TAG required)
	@if [ -z "$(IMAGE_TAG_EXPLICIT)" ]; then \
		echo "error: pass a tag explicitly, e.g. make deploy IMAGE_TAG=v0.1.0" >&2; exit 1; fi
	@case "$(IMAGE_TAG)" in latest|main|unset) \
		echo "error: $(IMAGE_TAG) is a moving tag. Container Apps only creates a revision when the template changes, so re-pushing one deploys nothing and reports success." >&2; exit 1;; esac
	terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl
	@# Every output read up front, each one checked: inside the update's
	@# arguments a failed read would become an empty string, and
	@# --replace-env-vars would write it over the good value.
	set -eu; \
	out() { v="$$(terraform -chdir=$(TF_DIR) output -raw "$$1")" && [ -n "$$v" ] \
		|| { echo "error: terraform output $$1 is missing or empty; apply first" >&2; exit 1; }; \
		printf '%s' "$$v"; }; \
	app="$$(out container_app_name)"; \
	rg="$$(out resource_group_name)"; \
	workspace="$$(out workspace_id)"; \
	client="$$(out identity_client_id)"; \
	az containerapp update --name "$$app" --resource-group "$$rg" \
		--image $(IMAGE_REGISTRY)/obsview:$(IMAGE_TAG) \
		--replace-env-vars \
			IMAGE_TAG=$(IMAGE_TAG) \
			ENVIRONMENT=$(ENV) \
			PORT=8000 \
			WORKSPACE_ID="$$workspace" \
			AZURE_CLIENT_ID="$$client" \
			APP_PASSCODE=secretref:passcode

# What cd-publish rolls back to when a deploy fails its smoke test: the obsview
# tag live now, or nothing on the first deploy (the placeholder).
deployed-tag: ## Print the image tag live on obsview
	@terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl >/dev/null
	@az containerapp show \
		--name "$$(terraform -chdir=$(TF_DIR) output -raw container_app_name)" \
		--resource-group "$$(terraform -chdir=$(TF_DIR) output -raw resource_group_name)" \
		--query 'properties.template.containers[0].image' -o tsv \
		| sed -n 's|^$(IMAGE_REGISTRY)/obsview:||p'

# The smoke test is jay-withers/workflows' scripts/smoke.sh, fetched at the
# commit cd-publish pins container-apps-deploy.yml to: one copy for every app
# repo, the same version in CI and in a local run, and Renovate's bump of that
# pin moves it.
WORKFLOWS_SHA = $(shell sed -n 's|.*container-apps-deploy\.yml@\([0-9a-f]\{40\}\).*|\1|p' .github/workflows/cd-publish.yml | head -n 1)
SMOKE_SH_URL = https://raw.githubusercontent.com/jay-withers/workflows/$(WORKFLOWS_SHA)/scripts/smoke.sh

# The app repos' shared smoke test: /healthz must report IMAGE_TAG, then
# /readyz (which queries the workspace) must answer 200.
smoke: ## Check IMAGE_TAG is live on obsview and it can read the workspace (IMAGE_TAG required)
	@if [ -z "$(IMAGE_TAG_EXPLICIT)" ]; then \
		echo "error: pass the tag to check, e.g. make smoke IMAGE_TAG=v0.1.0" >&2; exit 1; fi
	@terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl >/dev/null
	@[ -n "$(WORKFLOWS_SHA)" ] || { echo "smoke: no container-apps-deploy.yml@<sha> pin in .github/workflows/cd-publish.yml" >&2; exit 1; }
	@script="$$(mktemp)"; trap 'rm -f "$$script"' EXIT; \
	curl -fsSL --retry 3 -o "$$script" "$(SMOKE_SH_URL)" || { echo "smoke: can't fetch $(SMOKE_SH_URL)" >&2; exit 1; }; \
	bash "$$script" $(IMAGE_TAG) \
		"$$(terraform -chdir=$(TF_DIR) output -raw resource_group_name)" \
		$(IMAGE_REGISTRY)/obsview \
		app:"$$(terraform -chdir=$(TF_DIR) output -raw container_app_name)"

url: ## Print obsview's URL
	@terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl >/dev/null
	@terraform -chdir=$(TF_DIR) output -raw url; echo

dns: ## Print the DNS records the custom domain needs
	@terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl >/dev/null
	@host="$$(terraform -chdir=$(TF_DIR) output -raw custom_domain_name)"; \
		if [ -z "$$host" ]; then echo "no custom_domain_name set; nothing to do"; exit 0; fi; \
		label="$${host%%.*}"; \
		echo "CNAME  $$label  ->  $$(terraform -chdir=$(TF_DIR) output -raw default_url | sed 's|^https://||')"; \
		echo "TXT    asuid.$$label  ->  $$(terraform -chdir=$(TF_DIR) output -raw custom_domain_verification_id)"

# The apply cannot finish the binding itself: ARM needs the managed
# certificate's id in the bind request and the provider has no field to put it
# in (hashicorp/terraform-provider-azurerm#27362, open). Run this after any
# apply that created or recreated the custom domain or the certificate. A plan
# afterwards reports no diff.
bind-domain: ## Finish binding the managed certificate (manual, see #27362)
	@terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl >/dev/null
	@host="$$(terraform -chdir=$(TF_DIR) output -raw custom_domain_name)"; \
		if [ -z "$$host" ]; then echo "no custom_domain_name set; nothing to do"; exit 0; fi; \
		app="$$(terraform -chdir=$(TF_DIR) output -raw container_app_name)"; \
		rg="$$(terraform -chdir=$(TF_DIR) output -raw resource_group_name)"; \
		env_id="$$(az containerapp show --name "$$app" --resource-group "$$rg" --query properties.environmentId -o tsv)"; \
		cert_id="$$(az containerapp env certificate list --ids "$$env_id" --managed-certificates-only \
			--query "[?properties.subjectName=='$$host'].id | [0]" -o tsv)"; \
		az containerapp hostname bind --name "$$app" --resource-group "$$rg" \
			--hostname "$$host" --certificate "$$cert_id" --environment "$$env_id"

passcode: ## Print obsview's passcode
	@terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl >/dev/null
	@terraform -chdir=$(TF_DIR) output -raw passcode; echo


expiry: ## Print the TLS expiry rows schedule-expiry.yml would record
	scripts/check-expiry.sh scripts/expiry-hosts.txt

sessions: ## Print the session-resource rows schedule-session-resources.yml would record
	scripts/check-session-resources.sh "$$(az account show --query id -o tsv)"

costs: ## Print the Costs_CL rows schedule-costs.yml would record
	scripts/check-costs.sh "$$(az account show --query id -o tsv)"
