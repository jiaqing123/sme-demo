# Base and SDK images are pinned by digest. Dependabot updates the tag and digest together.
# Build context is the repository root, so the shared build files are available.
#
# The stage names matter to Visual Studio: in Fast mode (the Debug default) it
# builds the `base` stage only, bind-mounts the project directory over /app and
# launches the locally built assembly there. Keep `base` as the runtime image.

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS base
WORKDIR /app

# curl is used by the Compose healthcheck.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

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
