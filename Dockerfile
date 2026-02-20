# Stage 1: Build React Frontend
FROM node:22-alpine AS frontend-build
WORKDIR /app/frontend

# Copy package files and npm config from webui/
COPY webui/package*.json webui/.npmrc* ./

# Install all dependencies (devDependencies are required for Vite/TypeScript build)
RUN npm ci

# Copy frontend source
COPY webui/ ./

# Build production bundle.
# vite.config.ts resolves outDir to ../src/Torrentarr.Host/wwwroot relative to
# the config file, which in this container is /app/src/Torrentarr.Host/wwwroot.
RUN npm run build

# Stage 2: Build .NET Backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /app

# Copy solution and project files for restore layer caching
COPY Torrentarr.slnx ./
COPY src/Torrentarr.Core/*.csproj ./src/Torrentarr.Core/
COPY src/Torrentarr.Infrastructure/*.csproj ./src/Torrentarr.Infrastructure/
COPY src/Torrentarr.Host/*.csproj ./src/Torrentarr.Host/
COPY src/Torrentarr.WebUI/*.csproj ./src/Torrentarr.WebUI/
COPY src/Torrentarr.Workers/*.csproj ./src/Torrentarr.Workers/

# Restore only the Host project and its transitive dependencies.
# We do not restore the full solution because the solution file includes test
# projects that are not present in the Docker build context.
RUN dotnet restore src/Torrentarr.Host/Torrentarr.Host.csproj

# Copy source code
COPY src/ ./src/
COPY nuget.config ./

# Overlay the built React app from the frontend stage.
# Vite wrote to /app/src/Torrentarr.Host/wwwroot in the frontend stage;
# dotnet publish picks up wwwroot/ automatically (Microsoft.NET.Sdk.Web).
COPY --from=frontend-build /app/src/Torrentarr.Host/wwwroot ./src/Torrentarr.Host/wwwroot

# Build and publish
RUN dotnet publish src/Torrentarr.Host/Torrentarr.Host.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Stage 3: Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install runtime dependencies
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    curl \
    ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Create non-root user (UID 1001; the aspnet base image reserves UID 1000 for its own 'app' user)
RUN useradd -m -u 1001 torrentarr && \
    mkdir -p /config /data && \
    chown -R torrentarr:torrentarr /config /data

# Copy published application
COPY --from=backend-build /app/publish ./

# Copy example config
COPY config.example.toml /config/config.example.toml

# Set environment variables.
# TORRENTARR_CONFIG pins config to the mounted volume so the app never
# falls back to ~/config/config.toml inside the container.
# ASPNETCORE_CONTENTROOT must be /app so UseStaticFiles() finds wwwroot/.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:6969 \
    ASPNETCORE_CONTENTROOT=/app \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    TORRENTARR_CONFIG=/config/config.toml

# Switch to non-root user
USER torrentarr

# Expose ports
EXPOSE 6969

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD curl -f http://localhost:6969/health || exit 1

# Volume mounts
VOLUME ["/config", "/data"]

# Run the Host orchestrator
ENTRYPOINT ["/app/Torrentarr.Host"]
