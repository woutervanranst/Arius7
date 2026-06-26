# Arius.Web + Arius.Api — single container: Kestrel serves the built Angular SPA from wwwroot
# and the REST/SignalR API under /api and /hubs.

# ── Stage 1: build the Angular SPA ──────────────────────────────────────────
FROM node:22-alpine AS web
WORKDIR /web
COPY src/Arius.Web/package*.json ./
RUN npm ci
COPY src/Arius.Web/ ./
RUN npx ng build --configuration production

# ── Stage 2: publish the .NET API ───────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
# VERSION is the git tag (passed by the release workflow). It stamps Arius.Api.dll and its
# Arius.Core dependency so /api/info and snapshot manifests report the deployed version.
ARG VERSION=0.0.0-dev
WORKDIR /src
COPY src/ ./
RUN dotnet publish Arius.Api/Arius.Api.csproj -c Release -o /app -p:Version=$VERSION

# ── Stage 3: runtime ────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /web/dist/arius-web/browser ./wwwroot

# App SQLite, Data-Protection keys and Arius.Core's ~/.arius caches all live under /data (a volume).
ENV ASPNETCORE_URLS=http://+:8080
ENV HOME=/data
ENV Arius__AppDbPath=/data/arius-app.sqlite
ENV Arius__DataProtectionKeysPath=/data/keys
EXPOSE 8080
ENTRYPOINT ["dotnet", "Arius.Api.dll"]
