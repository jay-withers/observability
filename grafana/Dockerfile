# syntax=docker/dockerfile:1

# Grafana with this repo's data source and dashboards baked in, so what the UI
# shows is exactly what is in git: provisioned dashboards are read-only there,
# and a change is a pull request like any other.
#
# Baked into the image rather than applied with the Grafana Terraform
# provider, because that provider has to reach a running Grafana at plan time,
# and this one is created by the same apply and scales to zero.
FROM grafana/grafana:13.2.5@sha256:9f7aa1e1e3246aade5792d35a738049f7f32f89cd51d1956cf426a4e86268a84

# Links the pushed package to this repository, which is what gives the
# publishing workflow's GITHUB_TOKEN write access to it.
LABEL org.opencontainers.image.source="https://github.com/jay-withers/observability"

COPY provisioning/ /etc/grafana/provisioning/
# Templates, not what Grafana reads: entrypoint.sh renders them into
# /var/lib/grafana/dashboards with this deployment's workspace at start-up.
COPY dashboards/ /etc/grafana/dashboards/
COPY --chmod=755 entrypoint.sh /entrypoint.sh

# The image's own user (uid 472), numeric so a runtime enforcing runAsNonRoot
# can resolve it without reading /etc/passwd.
USER 472

EXPOSE 3000

ENTRYPOINT ["/entrypoint.sh"]
