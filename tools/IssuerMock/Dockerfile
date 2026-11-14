# Base and SDK images are pinned by digest. Dependabot updates the tag and digest together.
# Build context is the repository root, so the shared build files are available.
#
# The runtime is the chiseled-extra aspnet image: no shell, no package manager and
# no curl. It is the same base as the DB-backed services, whose SqlClient needs
# ICU/globalization support; one base keeps behaviour uniform. The Compose
# healthcheck is tools/HealthProbe.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY tools/IssuerMock/ tools/IssuerMock/

RUN dotnet publish tools/IssuerMock/SmeDemo.IssuerMock.csproj \
    --configuration Release \
    --output /out \
    /p:UseAppHost=false

# The probe is a dependency-free console app shipped beside the service. It gets
# its own stage so its restore and publish do not enlarge the app publish layer.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS healthprobe
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY tools/HealthProbe/ tools/HealthProbe/

RUN dotnet publish tools/HealthProbe/SmeDemo.HealthProbe.csproj \
    --configuration Release \
    --output /healthprobe \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:cae5b962bfb786606def03d250c6a645cc98839095c89e806e4bd8978a68115b AS runtime
WORKDIR /app

COPY --from=build /out ./
COPY --from=healthprobe /healthprobe /healthprobe

USER $APP_UID
EXPOSE 8084
ENTRYPOINT ["dotnet", "SmeDemo.IssuerMock.dll"]
