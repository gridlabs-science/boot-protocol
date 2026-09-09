ARG GRIDPOOL_RELEASE_VERSION=dev

FROM node:24-bookworm-slim@sha256:ba849c60be29959425b8734d57b8b4b7d56f98edd9504c9af091d5281095a71e AS dashboard-build
WORKDIR /src/boot_portal/ui
COPY boot_portal/ui/package.json boot_portal/ui/package-lock.json ./
RUN npm ci
COPY boot_portal/ui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:4ea6fe75dd36706bb6d8c3c293d4c4315840f5d76ea28ac97def77e3ec487fa5 AS build
WORKDIR /src

COPY boot_portal/boot_portal.csproj boot_portal/
RUN dotnet restore boot_portal/boot_portal.csproj --locked-mode

COPY . .
COPY --from=dashboard-build /src/boot_portal/wwwroot/dashboard boot_portal/wwwroot/dashboard
RUN dotnet publish boot_portal/boot_portal.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:1fe86375600b62e6566b465da9553eef0621f13c67f40fe764cd8dbb1dee1497 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends libsodium23 ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data /app \
    && chown -R 1000:1000 /data /app

ARG GRIDPOOL_RELEASE_VERSION
WORKDIR /app

ENV BOOT_PORTAL_CONFIG_PATH=/data/boot_portal_config.json
ENV BOOT_PORTAL_STATE_PATH=/data/pool_state.json
ENV GRIDPOOL_RELEASE_VERSION=${GRIDPOOL_RELEASE_VERSION}

VOLUME ["/data"]

EXPOSE 5000 3008

COPY --from=build /app/publish .
COPY docker/boot_portal_config.sample.json /app/defaults/boot_portal_config.sample.json
RUN chown -R 1000:1000 /app

USER 1000:1000

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:5000/health/live || exit 1

ENTRYPOINT ["/bin/sh", "-c", "mkdir -p \"$(dirname \"$BOOT_PORTAL_CONFIG_PATH\")\" \"$(dirname \"$BOOT_PORTAL_STATE_PATH\")\" && if [ ! -f \"$BOOT_PORTAL_CONFIG_PATH\" ]; then cp /app/defaults/boot_portal_config.sample.json \"$BOOT_PORTAL_CONFIG_PATH\"; fi; exec dotnet boot_portal.dll"]
