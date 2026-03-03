# Stage 1: Build React Frontend
FROM node:25-alpine AS frontend-build
WORKDIR /app/frontend

# Set npm timeout settings to prevent hanging
ENV NPM_CONFIG_FETCH_TIMEOUT=600000 \
    NPM_CONFIG_FETCH_RETRY=5 \
    NPM_CONFIG_FETCH_RETRY_MINTIMEOUT=20000 \
    NPM_CONFIG_FETCH_RETRY_MAXTIMEOUT=120000

# Copy package files and npm config from webui/
COPY webui/package*.json webui/.npmrc* ./

# Install dependencies with cache mount and timeout settings
RUN --mount=type=cache,target=/root/.npm \
    npm ci --prefer-offline --no-audit --loglevel=warn

# Copy frontend source
COPY webui/ ./

# Build production bundle
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
COPY Directory.Build.props ./

# Restore only the Host project and its transitive dependencies
RUN dotnet restore src/Torrentarr.Host/Torrentarr.Host.csproj

# Copy source code
COPY src/ ./src/
COPY nuget.config ./

# Overlay the built React app from the frontend stage
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
    chown -R torrentarr:torrentarr /config /data && \
    mkdir -p /config /data && \
    chown -R torrentarr:torrentarr /config /data

# Copy published application from build stage
COPY --from=backend-build /app/publish ./

# Switch to non-root user
USER torrentarr

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:6969 \
    ASPNETCORE_CONTENTROOT=/app \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    TORRENTARR_CONFIG=/config/config.toml

# Expose ports
EXPOSE 6969

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD curl -f http://localhost:6969/health || exit 1

# Volume mounts
VOLUME ["/config", "/data"]

# Run the Host orchestrator
ENTRYPOINT ["/app/Torrentarr.Host"]
