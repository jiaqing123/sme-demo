# Base and SDK images are pinned by digest. Dependabot updates the tag and digest together.
# Build context is the repository root, so the shared build files are available.
#
# The stage names matter to Visual Studio: in Fast mode (the Debug default) it
# builds the `base` stage only, bind-mounts the project directory over /app and
# launches the locally built assembly there. Keep `base` as the runtime image.

FROM mcr.microsoft.com/dotnet/aspnet:11.0@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242 AS base
WORKDIR /app

# curl is used by the Compose healthcheck. The key directory must be writable
# by the non-root application user when Docker initialises the named volume.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /keys \
    && chown $APP_UID:$APP_UID /keys

USER $APP_UID
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/SmeDemo.Auth/ src/SmeDemo.Auth/

RUN dotnet publish src/SmeDemo.Auth/SmeDemo.Auth.csproj \
    --configuration "$BUILD_CONFIGURATION" \
    --output /out \
    /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /out ./
ENTRYPOINT ["dotnet", "SmeDemo.Auth.dll"]
