#!/bin/sh

# Torrentarr entrypoint script
# Creates necessary directories with proper permissions

# Create config directory if it doesn't exist
if [ ! -d "/config" ]; then
    echo "Creating /config directory..."
    mkdir -p /config
    chown -R torrentarr:torrentarr /config
fi

# Create logs subdirectory if it doesn't exist
if [ ! -d "/config/logs" ]; then
    echo "Creating /config/logs directory..."
    mkdir -p /config/logs
    chown -R torrentarr:torrentarr /config/logs
fi

# Run the application
exec "$@"