# syntax=docker/dockerfile:1@sha256:572cebbc2b15e6f84c077742952674889cb63cd5e9b80abdffcbe587b6f19183

# The same two-stage shape as health, from jay-withers/dev-containers. Keep the
# two at the same version: python-app-builder is built FROM python-app, and a
# virtualenv is bound to the minor Python that created it (market-agent's
# "No module named" outage). Build for linux/amd64; `make build` does.
FROM ghcr.io/jay-withers/dev-containers/python-app-builder:v0.23.33@sha256:4d0aafcddca7eebbc3de71e264c16fc465bac2265063331e9783d489b57dbcc2 AS builder

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


FROM ghcr.io/jay-withers/dev-containers/python-app:v0.23.33@sha256:4713f41a77cb2f3c4ec6c48c01a8404a7eabb056459b3dd79dbad6a2897ac4e5

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
