# CupriNet Lodestar — headless "keep the network alive" node.
# Build from the REPOSITORY ROOT so the project references under src/ are in the context:
#   docker build -f node/CupriNet.Lodestar/Dockerfile -t cuprinet-lodestar .

# ---- build ----
# Pin the build stage to the BUILDPLATFORM (the runner's native arch) so a multi-arch build doesn't emulate the
# whole SDK under QEMU. dotnet publish here produces portable IL (no RID, UseAppHost=false), which is arch-neutral —
# the same /app runs on every target. Only the runtime stage below is resolved per target arch (amd64 + arm64).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# The core now pulls CupriMark (version negotiation) from the Wixely GitHub Packages feed, so restore needs a
# read:packages token. Pass it as a BuildKit secret (empty is tolerated for a fully-public future):
#   docker build --secret id=packages_token,env=PACKAGES_TOKEN -f node/CupriNet.Lodestar/Dockerfile .
RUN --mount=type=secret,id=packages_token \
    sh -c 'TOKEN="$(cat /run/secrets/packages_token 2>/dev/null || true)"; \
      if [ -n "$TOKEN" ]; then \
        dotnet nuget update source GitHub-Wixely-Packages --username docker --password "$TOKEN" \
          --store-password-in-clear-text --configfile nuget.config; \
      fi; \
      dotnet restore node/CupriNet.Lodestar/CupriNet.Lodestar.csproj'
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

# Default overlay listen port (override with CUPRINET_LODESTAR_ListenPort). Publish with `-p 43820:43820`.
EXPOSE 43820
# Optional status page (enable with CUPRINET_LODESTAR_EnableWeb=true). Publish with `-p 8080:8080`.
EXPOSE 8080

ENTRYPOINT ["dotnet", "cuprinet-lodestar.dll"]
