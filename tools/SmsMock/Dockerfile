# Build context is the repository root, so the shared build files are available.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY tools/SmsMock/ tools/SmsMock/

RUN dotnet publish tools/SmsMock/SmeDemo.SmsMock.csproj \
    --configuration Release \
    --output /out \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is used by the Compose healthcheck.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /out ./

USER $APP_UID
EXPOSE 8083
ENTRYPOINT ["dotnet", "SmeDemo.SmsMock.dll"]
