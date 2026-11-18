# syntax=docker/dockerfile:1@sha256:4edf897a3ffa55b89f906fc8cc78afdb3f1834cc9c7083565e611a8a7d5fe99e

# The same two-stage shape as gym-log, from jay-withers/dev-containers. Keep the
# two at the same version: python-app-builder is built FROM python-app, and a
# virtualenv is bound to the minor Python that created it (market-agent's
# "No module named" outage). Build for linux/amd64; `make build` does.
FROM ghcr.io/jay-withers/dev-containers/python-app-builder:v0.22.4@sha256:16b908e92ef5189327e9479677b80c8a09ab764bc5c42a59f0ead51b70e5b846 AS builder

WORKDIR /app

COPY pyproject.toml uv.lock ./
RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --frozen --no-install-project --no-dev --no-editable

COPY src ./src
# --no-editable: the runtime stage copies only the venv, so an editable
# install would leave a .pth pointing at a /app/src that is not there.
RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --frozen --no-dev --no-editable

# The chart library, fetched here rather than committed: at ~1 MB it is over
# the repo's 500 KB large-file hook, and a CDN at runtime would need the CSP
# opened to it. The checksum makes the build fail rather than ship anything
# but this exact file. The Makefile's `app-vendor` reads the URL and hash
# from this line, so upgrading is a change here alone. Fetched here so a RUN
# can set modes: ADD from a URL makes the file 0600 (unreadable by uid
# 10001), and `--chmod` applies to the directory it creates as well, leaving
# that untraversable.
ADD --checksum=sha256:bf4a223524e40b77c304bec67e1222cf551f14880cf42c69dc046558e11c07b1 \
    https://cdn.jsdelivr.net/npm/echarts@5.6.0/dist/echarts.min.js /vendor/echarts.min.js
RUN chmod 0755 /vendor && chmod 0644 /vendor/echarts.min.js


FROM ghcr.io/jay-withers/dev-containers/python-app:v0.22.4@sha256:777994918215030c82f8c5268e54b6a58205c06f72a0868cd7cc2df9dcef4de2

LABEL org.opencontainers.image.source="https://github.com/jay-withers/observability"

COPY --from=builder --chown=10001:10001 /app/.venv /app/.venv
# The chart library, fetched in the builder stage below `uv sync`.
COPY --from=builder /vendor /app/vendor
ENV VENDOR_DIR=/app/vendor

USER 10001

EXPOSE 8000

# /healthz answers from memory, so this restarts a wedged process and never
# one that is merely waiting on Log Analytics.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["python", "-c", "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8000/healthz', timeout=3).status == 200 else 1)"]

# The single source of truth for the executable's name: Terraform sets `args`
# only, never `command` (gym-log's outage).
ENTRYPOINT ["obsview"]
CMD ["serve"]
