# syntax=docker/dockerfile:1@sha256:572cebbc2b15e6f84c077742952674889cb63cd5e9b80abdffcbe587b6f19183

# Two stages from jay-withers/dev-containers: the builder is dotnet-app plus
# the SDK, so the app is built against exactly the runtime and OS it runs on.
# Keep the two at the same version (Renovate's "dotnet app base images" group
# moves them together), and the runtime's major.minor equal to mise.toml's
# dotnet SDK, which container-build checks. Build for linux/amd64; `make
# build` does.
FROM ghcr.io/jay-withers/dev-containers/dotnet-app-builder:v0.24.1@sha256:88af7d7c8681cd39237b5dc762b656cbf8ab1941007c1e05939ed75d61a76c27 AS builder

WORKDIR /src

# The project files and lock files first, so a code change reuses the
# restore layer. --locked-mode: the image installs exactly what CI tested.
COPY global.json Directory.Build.props Obsview.slnx ./
COPY src/Obsview/Obsview.csproj src/Obsview/packages.lock.json src/Obsview/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/Obsview/Obsview.csproj --locked-mode

COPY src ./src
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/Obsview/Obsview.csproj --configuration Release --no-restore --output /out

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


FROM ghcr.io/jay-withers/dev-containers/dotnet-app:v0.24.1@sha256:553c52ab245181b443de271c0f8157585c1a6009a3ddab8d19bc509162e0a0b0

LABEL org.opencontainers.image.source="https://github.com/jay-withers/observability"

COPY --from=builder --chown=10001:10001 /out /app
COPY --from=builder /vendor /app/vendor
# PORT is what the app binds and the ingress targets (`make deploy` sets it
# too). The base's ASPNETCORE_HTTP_PORTS is cleared, so the two never disagree.
ENV VENDOR_DIR=/app/vendor \
    PORT=8000 \
    ASPNETCORE_HTTP_PORTS=

USER 10001

EXPOSE 8000

# /healthz answers from memory, so this restarts a wedged process and never
# one that is merely waiting on Log Analytics. The runtime image has no curl;
# bash's /dev/tcp makes the request.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8000 && printf 'GET /healthz HTTP/1.0\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]

# The single source of truth for the executable: Terraform sets no command
# or args, and the app takes none.
ENTRYPOINT ["dotnet", "/app/obsview.dll"]
