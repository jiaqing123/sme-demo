# Build context is the repository root, so the shared build files are available.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/SmeDemo.Auth/ src/SmeDemo.Auth/

RUN dotnet publish src/SmeDemo.Auth/SmeDemo.Auth.csproj \
    --configuration Release \
    --output /out \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is used by the Compose healthcheck. The key directory must be writable
# by the non-root application user when Docker initialises the named volume.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /keys \
    && chown $APP_UID:$APP_UID /keys

COPY --from=build /out ./

USER $APP_UID
EXPOSE 8081
ENTRYPOINT ["dotnet", "SmeDemo.Auth.dll"]
