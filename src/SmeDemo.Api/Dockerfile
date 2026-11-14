# Base and SDK images are pinned by digest. Dependabot updates the tag and digest together.
# Build context is the repository root, so the shared build files are available.
#
# The stage names matter to Visual Studio: in Fast mode (the Debug default) it
# builds the `base` stage only, bind-mounts the project directory over /app and
# launches the locally built assembly there. Keep `base` as the runtime image and
# keep the health probe in it, because Fast mode still runs the Compose
# healthcheck.
#
# The runtime is the chiseled-extra aspnet image: no shell, no package manager and
# no curl. ICU and tzdata are included because Microsoft.Data.SqlClient requires
# globalization support; the plain chiseled variant's invariant mode is rejected
# at runtime. The Compose healthcheck is tools/HealthProbe.

# The probe is a dependency-free console app shipped beside the service. It is
# built before `base` because Docker cannot copy from a later stage; only the
# published output reaches the runtime image, never the SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS healthprobe
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY tools/HealthProbe/ tools/HealthProbe/

RUN dotnet publish tools/HealthProbe/SmeDemo.HealthProbe.csproj \
    --configuration Release \
    --output /healthprobe \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:cae5b962bfb786606def03d250c6a645cc98839095c89e806e4bd8978a68115b AS base
WORKDIR /app
COPY --from=healthprobe /healthprobe /healthprobe
USER $APP_UID
EXPOSE 8082

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/SmeDemo.Api/ src/SmeDemo.Api/
COPY src/SmeDemo.Application/ src/SmeDemo.Application/
COPY src/SmeDemo.Domain/ src/SmeDemo.Domain/
COPY src/SmeDemo.Infrastructure/ src/SmeDemo.Infrastructure/

RUN dotnet publish src/SmeDemo.Api/SmeDemo.Api.csproj \
    --configuration "$BUILD_CONFIGURATION" \
    --output /out \
    /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /out ./
ENTRYPOINT ["dotnet", "SmeDemo.Api.dll"]
