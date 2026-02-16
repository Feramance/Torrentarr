# Stage 1: Build React Frontend
FROM node:18-alpine AS frontend-build
WORKDIR /app/frontend

# Copy package files
COPY src/Commandarr.WebUI/ClientApp/package*.json ./

# Install dependencies
RUN npm ci --only=production

# Copy frontend source
COPY src/Commandarr.WebUI/ClientApp/ ./

# Build production bundle
RUN npm run build

# Stage 2: Build .NET Backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /app

# Copy solution and project files
COPY Commandarr.slnx ./
COPY src/Commandarr.Core/*.csproj ./src/Commandarr.Core/
COPY src/Commandarr.Infrastructure/*.csproj ./src/Commandarr.Infrastructure/
COPY src/Commandarr.WebUI/*.csproj ./src/Commandarr.WebUI/
COPY src/Commandarr.Workers/*.csproj ./src/Commandarr.Workers/
COPY src/Commandarr.Host/*.csproj ./src/Commandarr.Host/

# Restore dependencies
RUN dotnet restore

# Copy source code
COPY src/ ./src/
COPY nuget.config ./

# Copy built React app from frontend stage
COPY --from=frontend-build /app/frontend/build ./src/Commandarr.WebUI/ClientApp/build

# Build and publish
RUN dotnet publish src/Commandarr.Host/Commandarr.Host.csproj \
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

# Create non-root user
RUN useradd -m -u 1000 commandarr && \
    mkdir -p /config /data && \
    chown -R commandarr:commandarr /config /data

# Copy published application
COPY --from=backend-build /app/publish ./

# Copy example config
COPY config.example.toml /config/config.example.toml

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:6969 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Switch to non-root user
USER commandarr

# Expose ports
EXPOSE 6969

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD curl -f http://localhost:6969/health || exit 1

# Volume mounts
VOLUME ["/config", "/data"]

# Set working directory for config lookup
WORKDIR /config

# Run the Host orchestrator
ENTRYPOINT ["/app/Commandarr.Host"]
