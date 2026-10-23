# syntax=docker/dockerfile:1
# The build stage runs on the builder's own platform: the published output is framework-dependent IL plus the
# WASM client and SQLite's native libraries for every platform, so one build serves amd64 and arm64 images.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the layer is cached until a project file changes.
COPY DoiQ.slnx ./
COPY src/DoiQ.Shared/DoiQ.Shared.csproj src/DoiQ.Shared/
COPY src/DoiQ.Core/DoiQ.Core.csproj src/DoiQ.Core/
COPY src/DoiQ.Client/DoiQ.Client.csproj src/DoiQ.Client/
COPY src/DoiQ.Server/DoiQ.Server.csproj src/DoiQ.Server/
RUN dotnet restore src/DoiQ.Server/DoiQ.Server.csproj

COPY src/ src/
# Publishing the server also publishes the Blazor WASM client into its wwwroot.
RUN dotnet publish src/DoiQ.Server/DoiQ.Server.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data && chown app:app /data
WORKDIR /app
COPY --from=build /app ./

# The only setting the image needs: keep the SQLite file on the /data volume.
# Everything else (password, agents, webhooks) is configured in the app and stored in the database.
# (The aspnet base image already listens on port 8080 via ASPNETCORE_HTTP_PORTS.)
ENV ConnectionStrings__Db="Data Source=/data/doiq.db"

USER app
EXPOSE 8080
VOLUME /data
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "DoiQ.Server.dll"]
