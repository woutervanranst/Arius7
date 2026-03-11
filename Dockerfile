# ── Stage 1: Build Vue SPA ────────────────────────────────────────────────────
FROM node:22-alpine AS web-build

WORKDIR /src/Arius.Web

COPY src/Arius.Web/package.json src/Arius.Web/package-lock.json* ./
RUN npm ci --prefer-offline

COPY src/Arius.Web/ ./
# Build outputs to ../Arius.Api/wwwroot (vite.config.ts outDir)
RUN npm run build

# ── Stage 2: Build .NET API ───────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build

WORKDIR /repo

# Copy solution and project files first (layer cache for NuGet restore)
COPY global.json nuget.config ./
COPY src/Arius.Core/Arius.Core.csproj       src/Arius.Core/
COPY src/Arius.Azure/Arius.Azure.csproj     src/Arius.Azure/
COPY src/Arius.Api/Arius.Api.csproj         src/Arius.Api/
COPY src/Arius.Cli/Arius.Cli.csproj         src/Arius.Cli/

RUN dotnet restore src/Arius.Api/Arius.Api.csproj

# Copy all source
COPY src/ src/

# Copy the pre-built Vue static files into the API's wwwroot
COPY --from=web-build /src/Arius.Api/wwwroot src/Arius.Api/wwwroot

RUN dotnet publish src/Arius.Api/Arius.Api.csproj \
    -c Release \
    -o /publish \
    --no-restore

# ── Stage 3: Runtime image ────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

# Non-root user for security
RUN addgroup --system arius && adduser --system --ingroup arius arius

COPY --from=api-build --chown=arius:arius /publish ./

USER arius

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Arius.Api.dll"]
