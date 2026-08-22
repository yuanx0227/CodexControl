FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:306301580fcaa5b445180e759db59309979002d1000669cb4cf58a567d0014bc AS build
WORKDIR /src

COPY Directory.Build.props global.json ./
COPY src/shared/CodexControl.Protocol/CodexControl.Protocol.csproj src/shared/CodexControl.Protocol/
COPY src/relay/CodexControl.Relay.csproj src/relay/
RUN dotnet restore src/relay/CodexControl.Relay.csproj

COPY src/shared/CodexControl.Protocol/ src/shared/CodexControl.Protocol/
COPY src/relay/ src/relay/
RUN dotnet publish src/relay/CodexControl.Relay.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:b0beb9cc1dee1c1b0749796110d4734292071b814207ad0d4f40611f7db04f7b AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && addgroup --system --gid 10001 codex \
    && adduser --system --uid 10001 --ingroup codex codex \
    && mkdir -p /data \
    && chown -R codex:codex /app /data
COPY --from=build --chown=codex:codex /app/publish/ ./
USER codex
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    CODEX_CONTROL_DB=/data/codex-control.db
EXPOSE 8080
ENTRYPOINT ["dotnet", "CodexControlRelay.dll"]
