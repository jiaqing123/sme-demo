# Base and SDK images are pinned by digest. Dependabot updates the tag and digest together.
# Build context is the repository root, so the shared build files are available.
#
# The runtime is the chiseled-extra .NET runtime image: no shell, no package manager
# and no curl. ICU and tzdata are included because Microsoft.Data.SqlClient requires
# globalization support; the plain chiseled variant's invariant mode is rejected at
# runtime.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY tools/Migrator/ tools/Migrator/

RUN dotnet publish tools/Migrator/SmeDemo.Migrator.csproj \
    --configuration Release \
    --output /out \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra@sha256:b64d614c3c1c440abe6d57f510c5a4cfc0ccd56a9b96f96c1503efe4a7ac3e56 AS runtime
WORKDIR /app

COPY --from=build /out ./
COPY db/migrations ./migrations
COPY db/bootstrap ./bootstrap

USER $APP_UID
ENTRYPOINT ["dotnet", "SmeDemo.Migrator.dll"]
