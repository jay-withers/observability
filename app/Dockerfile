# syntax=docker/dockerfile:1@sha256:572cebbc2b15e6f84c077742952674889cb63cd5e9b80abdffcbe587b6f19183

# The same two-stage shape as health, from jay-withers/dev-containers. Keep the
# two at the same version: python-app-builder is built FROM python-app, and a
# virtualenv is bound to the minor Python that created it (market-agent's
# "No module named" outage). Build for linux/amd64; `make build` does.
FROM ghcr.io/jay-withers/dev-containers/python-app-builder:v0.24.2@sha256:1a87995ea7ea01803421c1a20d8de386f73de2b2381baccdded962c4bc4451ca AS builder

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


FROM ghcr.io/jay-withers/dev-containers/python-app:v0.24.2@sha256:e73c66b05bbcf150d13c4f6366994f55002d4cf03b9b079196b97325388db3a1

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
