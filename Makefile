TF_DIR := terraform
ENV ?= dev

IMAGE_REGISTRY ?= ghcr.io/jay-withers/observability
# Defaults to the local commit, which is what you want when iterating.
IMAGE_TAG ?= $(shell git rev-parse --short HEAD)
# `file` means the ?= default fired rather than the caller passing one.
IMAGE_TAG_EXPLICIT := $(filter-out file,$(origin IMAGE_TAG))

.DEFAULT_GOAL := help

.PHONY: help install lint init fmt validate plan apply outputs build push deploy deployed-tag smoke url admin-password db-role

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) \
		| awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-15s\033[0m %s\n", $$1, $$2}'

install: ## Install pre-commit hooks (run once after cloning)
	pre-commit install
	pre-commit install --hook-type commit-msg

lint: ## Run all pre-commit hooks against every file
	pre-commit run --all-files

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

build: ## Build the Grafana image for linux/amd64 (set IMAGE_TAG, defaults to the git SHA)
	docker buildx build --platform linux/amd64 --load \
		-t $(IMAGE_REGISTRY)/grafana:$(IMAGE_TAG) grafana

push: ## Push the image to ghcr.io (needs write:packages)
	gh auth token | docker login ghcr.io -u $$(gh api user --jq .login) --password-stdin
	docker push $(IMAGE_REGISTRY)/grafana:$(IMAGE_TAG)

# A bare `make deploy` is a hard error: the default tag is whatever commit is
# checked out, which may never have been pushed.
deploy: ## Roll an image tag onto Grafana (IMAGE_TAG required)
	@if [ -z "$(IMAGE_TAG_EXPLICIT)" ]; then \
		echo "error: pass a tag explicitly, e.g. make deploy IMAGE_TAG=v0.1.0" >&2; exit 1; fi
	@case "$(IMAGE_TAG)" in latest|main|unset) \
		echo "error: $(IMAGE_TAG) is a moving tag. Container Apps only creates a revision when the template changes, so re-pushing one deploys nothing and reports success." >&2; exit 1;; esac
	terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl
	az containerapp update \
		--name "$$(terraform -chdir=$(TF_DIR) output -raw container_app_name)" \
		--resource-group "$$(terraform -chdir=$(TF_DIR) output -raw resource_group_name)" \
		--image $(IMAGE_REGISTRY)/grafana:$(IMAGE_TAG)

# What cd-publish rolls back to when a deploy fails its smoke test: the tag
# live now, or nothing on the first deploy.
deployed-tag: ## Print the image tag live on Grafana
	@terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl >/dev/null
	@az containerapp show \
		--name "$$(terraform -chdir=$(TF_DIR) output -raw container_app_name)" \
		--resource-group "$$(terraform -chdir=$(TF_DIR) output -raw resource_group_name)" \
		--query 'properties.template.containers[0].image' -o tsv \
		| sed -n 's|^$(IMAGE_REGISTRY)/grafana:||p'

smoke: ## Check IMAGE_TAG is live and Grafana reaches its database (IMAGE_TAG required)
	@if [ -z "$(IMAGE_TAG_EXPLICIT)" ]; then \
		echo "error: pass the tag to check, e.g. make smoke IMAGE_TAG=v0.1.0" >&2; exit 1; fi
	@terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl >/dev/null
	scripts/smoke.sh $(IMAGE_TAG) \
		"$$(terraform -chdir=$(TF_DIR) output -raw resource_group_name)" \
		$(IMAGE_REGISTRY)/grafana \
		"$$(terraform -chdir=$(TF_DIR) output -raw container_app_name)"

url: ## Print Grafana's URL
	@terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl >/dev/null
	@terraform -chdir=$(TF_DIR) output -raw grafana_url; echo

admin-password: ## Print the password of Grafana's admin user
	@terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl >/dev/null
	@terraform -chdir=$(TF_DIR) output -raw admin_password; echo

# Run once after the first apply, and again after rotating the password. As
# the server's Entra administrator (your `az login`), from this machine: the
# server only admits Azure IPs, so this opens a firewall rule for your public
# IP for the length of the command and removes it after, success or not.
db-role: ## Create or re-password Grafana's role on the shared Postgres server
	@terraform -chdir=$(TF_DIR) init -reconfigure -backend-config=backends/$(ENV).hcl >/dev/null
	@set -eu; \
	fqdn="$$(terraform -chdir=$(TF_DIR) output -raw postgres_fqdn)"; \
	server="$${fqdn%%.*}"; \
	ip="$$(curl -fsS https://api.ipify.org)"; \
	rule="db-role-$$(date +%s)"; \
	trap 'az postgres flexible-server firewall-rule delete -g rg-platform-$(ENV) -n "$$server" -r "$$rule" --yes -o none' EXIT; \
	az postgres flexible-server firewall-rule create -g rg-platform-$(ENV) -n "$$server" -r "$$rule" \
		--start-ip-address "$$ip" --end-ip-address "$$ip" -o none; \
	PGPASSWORD="$$(az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv)" \
	psql "host=$$fqdn dbname=grafana sslmode=require user=$$(az ad signed-in-user show --query userPrincipalName -o tsv)" \
		-v pw="$$(terraform -chdir=$(TF_DIR) output -raw database_password)" -f sql/grafana-role.sql
