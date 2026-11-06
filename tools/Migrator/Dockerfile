# Build context is the repository root, so the shared build files are available.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY tools/Migrator/ tools/Migrator/

RUN dotnet publish tools/Migrator/SmeDemo.Migrator.csproj \
    --configuration Release \
    --output /out \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

COPY --from=build /out ./
COPY db/migrations ./migrations

USER $APP_UID
ENTRYPOINT ["dotnet", "SmeDemo.Migrator.dll"]
