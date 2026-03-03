# Systemd Service Setup

Run Torrentarr as a systemd service on Linux for automatic startup, restart management, and proper logging.

## Prerequisites

- Linux system with systemd (most modern distributions)
- .NET 8.0 SDK or higher ([Download .NET](https://dotnet.microsoft.com/download))
- Non-root user account (recommended for security)

## Quick Start

```bash
# Create Torrentarr user
sudo useradd -r -s /bin/bash -d /opt/torrentarr -m torrentarr

# Install Torrentarr to shared path
sudo dotnet tool install --tool-path /usr/local/bin torrentarr

# Create directories
sudo mkdir -p /opt/torrentarr/{config,logs}
sudo chown -R torrentarr:torrentarr /opt/torrentarr

# Create service file
sudo nano /etc/systemd/system/torrentarr.service

# Enable and start
sudo systemctl daemon-reload
sudo systemctl enable --now torrentarr
```

## Installation Steps

### 1. Create Dedicated User

Create a dedicated user for running Torrentarr (recommended for security):

```bash
sudo useradd -r -s /bin/bash -d /opt/torrentarr -m torrentarr
```

!!! tip "Why a dedicated user?"
    Running Torrentarr as a dedicated user improves security by limiting access to system resources and isolating potential issues.

### 2. Install Torrentarr

Install to a shared path accessible by the service user:

```bash
sudo dotnet tool install --tool-path /usr/local/bin torrentarr
```

This places the `torrentarr` binary at `/usr/local/bin/torrentarr`, which is readable by all users without home-directory PATH tricks.

### 3. Create Directory Structure

```bash
sudo mkdir -p /opt/torrentarr/config
sudo mkdir -p /opt/torrentarr/logs
sudo chown -R torrentarr:torrentarr /opt/torrentarr
```

### 4. Generate Configuration

Run Torrentarr once to generate the default configuration:

```bash
sudo -u torrentarr TORRENTARR_CONFIG=/opt/torrentarr/config/config.toml torrentarr
```

Press ++ctrl+c++ to stop, then edit:

```bash
sudo nano /opt/torrentarr/config/config.toml
```

See the [First Run Guide](../quickstart.md) for configuration details.

### 5. Create Systemd Service File

Create the service file:

```bash
sudo nano /etc/systemd/system/torrentarr.service
```

Paste this content:

```ini
[Unit]
Description=Torrentarr - Radarr/Sonarr/Lidarr Torrent Manager
Documentation=https://feramance.github.io/Torrentarr/
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=torrentarr
Group=torrentarr
WorkingDirectory=/opt/torrentarr

# Main process
ExecStart=/usr/local/bin/torrentarr

# Environment variables
Environment="TORRENTARR_CONFIG=/opt/torrentarr/config/config.toml"

# Restart policy
Restart=always
RestartSec=5

# Logging
StandardOutput=journal
StandardError=journal
SyslogIdentifier=torrentarr

# Security hardening (optional)
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

!!! tip "Binary Location"
    If you installed via `dotnet tool install -g torrentarr` for the torrentarr user, the binary will be at `/opt/torrentarr/.dotnet/tools/torrentarr`. Update `ExecStart` and add `Environment="HOME=/opt/torrentarr"` accordingly.

### 6. Enable and Start Service

```bash
# Reload systemd
sudo systemctl daemon-reload

# Enable auto-start on boot
sudo systemctl enable torrentarr

# Start immediately
sudo systemctl start torrentarr

# Check status
sudo systemctl status torrentarr
```

## Managing the Service

### Check Status

```bash
sudo systemctl status torrentarr
```

Example output:
```
● torrentarr.service - Torrentarr - Radarr/Sonarr/Lidarr Torrent Manager
     Loaded: loaded (/etc/systemd/system/torrentarr.service; enabled)
     Active: active (running) since Mon 2025-11-25 10:00:00 UTC
   Main PID: 1234 (torrentarr)
```

### View Logs

=== "Real-time"

    ```bash
    sudo journalctl -u torrentarr -f
    ```

=== "Last 100 lines"

    ```bash
    sudo journalctl -u torrentarr -n 100
    ```

=== "Since boot"

    ```bash
    sudo journalctl -u torrentarr -b
    ```

=== "Specific time range"

    ```bash
    sudo journalctl -u torrentarr \
      --since "2025-01-01 00:00:00" \
      --until "2025-01-01 23:59:59"
    ```

=== "Errors only"

    ```bash
    sudo journalctl -u torrentarr -p err -n 50
    ```

### Start/Stop/Restart

```bash
# Start
sudo systemctl start torrentarr

# Stop
sudo systemctl stop torrentarr

# Restart
sudo systemctl restart torrentarr

# Reload config (if supported)
sudo systemctl reload torrentarr
```

### Enable/Disable Auto-Start

```bash
# Enable auto-start on boot
sudo systemctl enable torrentarr

# Disable auto-start
sudo systemctl disable torrentarr

# Enable and start in one command
sudo systemctl enable --now torrentarr
```

## Auto-Update Behavior

When Torrentarr performs an auto-update or manual update via WebUI:

1. **Process replacement**: Torrentarr calls `os.execv()` to replace itself with the new version
2. **PID maintained**: The process keeps the same PID
3. **Systemd continues monitoring**: No service interruption
4. **Automatic restart**: `Restart=always` ensures service continues

The `RestartSec=5` setting adds a 5-second delay between restart attempts to prevent rapid restart loops.

## Configuration Options

### Custom Config Location

To use a different config location, update the service file:

```ini
[Service]
Environment="TORRENTARR_CONFIG=/etc/torrentarr/config.toml"
WorkingDirectory=/etc/torrentarr
ExecStart=/usr/local/bin/torrentarr
```

Then create the directory:

```bash
sudo mkdir -p /etc/torrentarr
sudo chown torrentarr:torrentarr /etc/torrentarr
```

### Environment Variables

Add environment variables to the service file:

```ini
[Service]
Environment="TORRENTARR_CONFIG=/opt/torrentarr/config/config.toml"
Environment="QBITRR_LOG_LEVEL=DEBUG"
Environment="TZ=America/New_York"
```

### Resource Limits

Limit CPU and memory usage:

```ini
[Service]
# Limit to 2GB RAM
MemoryMax=2G

# Limit to 50% CPU
CPUQuota=50%

# Limit open files
LimitNOFILE=65536
```

## Security Hardening

For enhanced security, add these options to the `[Service]` section:

```ini
[Service]
# Prevent privilege escalation
NoNewPrivileges=true

# Use private /tmp
PrivateTmp=true

# Protect system directories
ProtectSystem=strict
ProtectHome=true

# Only allow writes to specific directories
ReadWritePaths=/opt/torrentarr

# Restrict network access
RestrictAddressFamilies=AF_INET AF_INET6

# Disable other namespaces
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
```

!!! warning "Test Before Enabling"
    Some hardening options may interfere with Torrentarr's operation. Test thoroughly before deploying.

## Multiple Instances

Run multiple Torrentarr instances for different configurations:

### 1. Create Service Files

```bash
sudo cp /etc/systemd/system/torrentarr.service \
        /etc/systemd/system/torrentarr-movies.service

sudo cp /etc/systemd/system/torrentarr.service \
        /etc/systemd/system/torrentarr-tv.service
```

### 2. Modify Each Service

Edit `torrentarr-movies.service`:

```ini
[Service]
User=torrentarr-movies
WorkingDirectory=/opt/torrentarr-movies
Environment="TORRENTARR_CONFIG=/opt/torrentarr-movies/config/config.toml"
```

Edit `torrentarr-tv.service`:

```ini
[Service]
User=torrentarr-tv
WorkingDirectory=/opt/torrentarr-tv
Environment="TORRENTARR_CONFIG=/opt/torrentarr-tv/config/config.toml"
```

### 3. Create Users and Directories

```bash
# Movies instance
sudo useradd -r -s /bin/bash -d /opt/torrentarr-movies -m torrentarr-movies
sudo mkdir -p /opt/torrentarr-movies/{config,logs}
sudo chown -R torrentarr-movies:torrentarr-movies /opt/torrentarr-movies

# TV instance
sudo useradd -r -s /bin/bash -d /opt/torrentarr-tv -m torrentarr-tv
sudo mkdir -p /opt/torrentarr-tv/{config,logs}
sudo chown -R torrentarr-tv:torrentarr-tv /opt/torrentarr-tv
```

### 4. Enable and Start

```bash
sudo systemctl daemon-reload
sudo systemctl enable torrentarr-movies torrentarr-tv
sudo systemctl start torrentarr-movies torrentarr-tv
```

!!! tip "Different WebUI Ports"
    Configure different WebUI ports in each instance's `config.toml`:
    ```toml
    [Settings]
    WebUIPort = 6969  # movies
    WebUIPort = 6970  # tv
    ```

## Troubleshooting

### Service Fails to Start

Check status and logs:

```bash
sudo systemctl status torrentarr
sudo journalctl -u torrentarr -n 50 --no-pager
```

Common issues:

| Issue | Solution |
|-------|----------|
| Permission denied | `sudo chown -R torrentarr:torrentarr /opt/torrentarr` |
| Binary not found | Run `which torrentarr` and update `ExecStart` in service file |
| Config errors | Check syntax in `config.toml` |
| Port already in use | Change `WebUIPort` in config |

### Service Restarts Repeatedly

Check for crash logs:

```bash
sudo journalctl -u torrentarr -p err -n 100
```

Temporarily stop to investigate:

```bash
sudo systemctl stop torrentarr
sudo -u torrentarr /usr/local/bin/torrentarr
```

### Update Not Working

Manual update:

```bash
sudo -u torrentarr dotnet tool update -g torrentarr
sudo systemctl restart torrentarr
```

### Permission Issues

Fix ownership and permissions:

```bash
sudo chown -R torrentarr:torrentarr /opt/torrentarr
sudo chmod -R 755 /opt/torrentarr
sudo chmod 644 /opt/torrentarr/config/*.toml
```

### Can't Connect to WebUI

Check if Torrentarr is listening:

```bash
sudo netstat -tlnp | grep 6969
```

Check firewall:

```bash
sudo ufw allow 6969/tcp
```

## Complete Service File Example

Here's a production-ready service file with security hardening:

```ini
[Unit]
Description=Torrentarr - Radarr/Sonarr/Lidarr Torrent Manager
Documentation=https://feramance.github.io/Torrentarr/
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=torrentarr
Group=torrentarr
WorkingDirectory=/opt/torrentarr

# Main process
ExecStart=/usr/local/bin/torrentarr

# Environment
Environment="TORRENTARR_CONFIG=/opt/torrentarr/config/config.toml"
Environment="TZ=America/New_York"

# Restart policy
Restart=always
RestartSec=5

# Logging
StandardOutput=journal
StandardError=journal
SyslogIdentifier=torrentarr

# Resource limits
MemoryMax=2G
CPUQuota=50%
LimitNOFILE=65536

# Security hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/opt/torrentarr
RestrictAddressFamilies=AF_INET AF_INET6
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true

[Install]
WantedBy=multi-user.target
```

## Next Steps

- [Configure qBittorrent](../../configuration/qbittorrent.md)
- [Configure Arr Instances](../../configuration/arr/index.md)
- [First Run Guide](../quickstart.md)
- [Troubleshooting Guide](../../troubleshooting/index.md)
