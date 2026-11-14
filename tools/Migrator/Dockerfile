# Base and SDK images are pinned by digest. Dependabot updates the tag and digest together.
# Build context is the repository root, so the shared build files are available.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY tools/Migrator/ tools/Migrator/

RUN dotnet publish tools/Migrator/SmeDemo.Migrator.csproj \
    --configuration Release \
    --output /out \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:80a51954ac70925e5783cb1ed35129a4243dcb1cddef20066b67346e17db284d AS runtime
WORKDIR /app

COPY --from=build /out ./
COPY db/migrations ./migrations

USER $APP_UID
ENTRYPOINT ["dotnet", "SmeDemo.Migrator.dll"]
