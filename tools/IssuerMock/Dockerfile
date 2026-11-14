# Base and SDK images are pinned by digest. Dependabot updates the tag and digest together.
# Build context is the repository root, so the shared build files are available.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY tools/IssuerMock/ tools/IssuerMock/

RUN dotnet publish tools/IssuerMock/SmeDemo.IssuerMock.csproj \
    --configuration Release \
    --output /out \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS runtime
WORKDIR /app

# curl is used by the Compose healthcheck.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /out ./

USER $APP_UID
EXPOSE 8084
ENTRYPOINT ["dotnet", "SmeDemo.IssuerMock.dll"]
