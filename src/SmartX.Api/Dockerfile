# syntax=docker/dockerfile:1

# =============================================================================
# BUILD STAGE
# =============================================================================
#
# The SDK image is used only to restore and publish the Smart-X API.
# Build tooling is deliberately excluded from the final runtime image.
#
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy files that influence restore/build behaviour first.
# Keeping these layers separate allows Docker to reuse the NuGet restore cache
# when ordinary source files change.
COPY global.json ./
COPY Directory.Build.props ./
COPY SmartX.sln ./

# Copy project files while preserving the repository project structure.
# SmartX.Api depends on Application, Infrastructure and Contracts, which in
# turn depend on the Domain project.
COPY src/SmartX.Domain/SmartX.Domain.csproj src/SmartX.Domain/
COPY src/SmartX.Contracts/SmartX.Contracts.csproj src/SmartX.Contracts/
COPY src/SmartX.Application/SmartX.Application.csproj src/SmartX.Application/
COPY src/SmartX.Infrastructure/SmartX.Infrastructure.csproj src/SmartX.Infrastructure/
COPY src/SmartX.Api/SmartX.Api.csproj src/SmartX.Api/

# Restore only the production API dependency graph.
RUN dotnet restore src/SmartX.Api/SmartX.Api.csproj

# Copy the application source only after dependency restoration so normal code
# changes do not unnecessarily invalidate the NuGet restore layer.
COPY src/ src/

# Produce a framework-dependent Release deployment.
RUN dotnet publish src/SmartX.Api/SmartX.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false


# =============================================================================
# RUNTIME STAGE
# =============================================================================
#
# The ASP.NET runtime image is substantially smaller than the SDK image and
# contains only what is required to execute the published Smart-X API.
#
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

WORKDIR /app

# Administrative work happens before the runtime switches to the non-root
# built-in .NET "app" account.
USER root

# curl is used exclusively by the container health check.
#
# /data/attachments is intentionally separate from the application binaries.
# Docker Compose will later mount a persistent named volume at this path.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/attachments \
    && chown -R app:app /data/attachments

COPY --from=build --chown=app:app /app/publish ./

# Smart-X listens on an internal unencrypted HTTP endpoint inside the Docker
# network. The frontend reverse proxy acts as the external application
# boundary.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Attachments__StoragePath=/data/attachments

EXPOSE 8080

# Document the location designed for persistent encrypted attachment data.
VOLUME ["/data/attachments"]

# Application code does not require root privileges.
USER app

# Reuse the real Smart-X health endpoint rather than inventing a
# container-specific health mechanism.
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=5 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "SmartX.Api.dll"]
