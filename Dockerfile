FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:35048e3a81e6a07c316e7bbbd80d80d2ba705fe5f23a8ed42b6638c8f4c20d30 AS build
WORKDIR /src

COPY boot_portal/boot_portal.csproj boot_portal/
RUN dotnet restore boot_portal/boot_portal.csproj --locked-mode

COPY . .
RUN dotnet publish boot_portal/boot_portal.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim@sha256:4e376dd15bbc8437d4892367ab0ea06a3ac9fea482d10f92f3c493fe1a2219ad AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends libsodium23 ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 1000 boot \
    && useradd --uid 1000 --gid boot --create-home --shell /usr/sbin/nologin boot \
    && mkdir -p /data /app \
    && chown -R boot:boot /data /app

WORKDIR /app

ENV BOOT_PORTAL_CONFIG_PATH=/data/boot_portal_config.json
ENV BOOT_PORTAL_STATE_PATH=/data/pool_state.json

VOLUME ["/data"]

EXPOSE 5000 3008

COPY --from=build /app/publish .
COPY docker/boot_portal_config.sample.json /app/defaults/boot_portal_config.sample.json
RUN chown -R boot:boot /app

USER boot

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:5000/health/live || exit 1

ENTRYPOINT ["/bin/sh", "-c", "mkdir -p \"$(dirname \"$BOOT_PORTAL_CONFIG_PATH\")\" \"$(dirname \"$BOOT_PORTAL_STATE_PATH\")\" && if [ ! -f \"$BOOT_PORTAL_CONFIG_PATH\" ]; then cp /app/defaults/boot_portal_config.sample.json \"$BOOT_PORTAL_CONFIG_PATH\"; fi; exec dotnet boot_portal.dll"]
