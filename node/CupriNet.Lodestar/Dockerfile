# CupriNet Lodestar — headless "keep the network alive" node.
# Build from the REPOSITORY ROOT so the project references under src/ are in the context:
#   docker build -f node/CupriNet.Lodestar/Dockerfile -t cuprinet-lodestar .

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Restore against nuget.org only: Lodestar's dependency graph is all public (no CupriTor / GitHub Packages),
# so this stays token-free regardless of the repo's nuget.config.
RUN dotnet restore node/CupriNet.Lodestar/CupriNet.Lodestar.csproj --source https://api.nuget.org/v3/index.json
RUN dotnet publish node/CupriNet.Lodestar/CupriNet.Lodestar.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
LABEL org.opencontainers.image.title="CupriNet Lodestar"
LABEL org.opencontainers.image.description="Headless CupriNet overlay node — keeps the network alive (L1 only, no L2)."
LABEL org.opencontainers.image.source="https://github.com/Wixely/CupriNet"
LABEL org.opencontainers.image.licenses="MIT"

WORKDIR /app
COPY --from=build /app .

# Persist identity + known-peer hot path to a mounted volume, owned by the image's built-in non-root `app` user.
ENV CUPRINET_LODESTAR_DataDirectory=/data
RUN mkdir -p /data && chown app:app /data
USER app
VOLUME ["/data"]

# Default listen port (override with CUPRINET_LODESTAR_ListenPort). Publish with `-p 43820:43820`.
EXPOSE 43820

ENTRYPOINT ["dotnet", "cuprinet-lodestar.dll"]
