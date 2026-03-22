FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Arius.Core/Arius.Core.csproj", "Arius.Core/"]
COPY ["Arius.AzureBlob/Arius.AzureBlob.csproj", "Arius.AzureBlob/"]
COPY ["Arius.Cli/Arius.Cli.csproj", "Arius.Cli/"]
RUN dotnet restore "Arius.Cli/Arius.Cli.csproj"

COPY . .
WORKDIR "/src/Arius.Cli"
RUN dotnet publish "Arius.Cli.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Cache directory: mount this volume to persist across runs
VOLUME /root/.arius/cache

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Arius.Cli.dll"]
