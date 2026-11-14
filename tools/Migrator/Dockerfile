# Base and SDK images are pinned by digest. Dependabot updates the tag and digest together.
# Build context is the repository root, so the shared build files are available.
FROM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY tools/Migrator/ tools/Migrator/

RUN dotnet publish tools/Migrator/SmeDemo.Migrator.csproj \
    --configuration Release \
    --output /out \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:11.0@sha256:3fe5f5495141c97986a49d3a4e76061f6225c3f93f94e8a7e522781376193863 AS runtime
WORKDIR /app

COPY --from=build /out ./
COPY db/migrations ./migrations

USER $APP_UID
ENTRYPOINT ["dotnet", "SmeDemo.Migrator.dll"]
