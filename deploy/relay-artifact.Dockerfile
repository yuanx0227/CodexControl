FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:b0beb9cc1dee1c1b0749796110d4734292071b814207ad0d4f40611f7db04f7b
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && addgroup --system --gid 10001 codex \
    && adduser --system --uid 10001 --ingroup codex codex \
    && mkdir -p /data \
    && chown -R codex:codex /app /data
COPY --chown=codex:codex relay/ ./
USER codex
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    CODEX_CONTROL_DB=/data/codex-control.db
EXPOSE 8080
ENTRYPOINT ["dotnet", "CodexControlRelay.dll"]
