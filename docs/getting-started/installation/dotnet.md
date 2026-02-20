# dotnet tool Installation

Install Torrentarr as a global .NET tool. This method is ideal for users who prefer native installations without containerization.

## Prerequisites

- **.NET 8.0 SDK or higher** ([Download .NET](https://dotnet.microsoft.com/download))
- qBittorrent running and accessible
- At least one Arr instance (Radarr, Sonarr, or Lidarr)

## Quick Start

=== "Linux/macOS"

    ```bash
    # Install Torrentarr
    dotnet tool install -g torrentarr

    # Add dotnet tools to PATH (if not already)
    export PATH="$PATH:$HOME/.dotnet/tools"

    # Run Torrentarr
    torrentarr
    ```

=== "Windows"

    ```powershell
    # Install Torrentarr
    dotnet tool install -g torrentarr

    # Run Torrentarr
    torrentarr
    ```

## Installation

### Check .NET Version

```bash
dotnet --version
```

Make sure you have .NET 8.0 or higher.

### Install Torrentarr

```bash
dotnet tool install -g torrentarr
```

!!! tip "Global Tool Path"
    Global .NET tools are installed to `~/.dotnet/tools` on Linux/macOS or `%USERPROFILE%\.dotnet\tools` on Windows. Make sure this directory is in your `PATH`.

### Add to PATH

=== "Linux/macOS"

    Add to `~/.bashrc` or `~/.zshrc`:

    ```bash
    export PATH="$PATH:$HOME/.dotnet/tools"
    source ~/.bashrc
    ```

=== "Windows"

    The .NET installer usually adds this automatically. If not, add `%USERPROFILE%\.dotnet\tools` to your system PATH:

    1. Search for "Environment Variables"
    2. Edit "Path" user variable
    3. Add `%USERPROFILE%\.dotnet\tools`
    4. Restart terminal

## First Run

1. **Start Torrentarr:**
   ```bash
   torrentarr
   ```

2. **Configuration file created:**
   Torrentarr will generate `~/config/config.toml` on first run

3. **Stop Torrentarr:**
   Press ++ctrl+c++

4. **Edit the configuration:**
   ```bash
   nano ~/config/config.toml
   ```

   Or open with your preferred editor.

5. **Start Torrentarr again:**
   ```bash
   torrentarr
   ```

See the [First Run Guide](../quickstart.md) for detailed configuration steps.

## Configuration

### Config File Location

By default, Torrentarr stores configuration in:

=== "Linux/macOS"

    ```
    ~/config/config.toml
    ~/logs/
    ```

=== "Windows"

    ```
    %USERPROFILE%\config\config.toml
    %USERPROFILE%\logs\
    ```

### Custom Config Path

Set a custom config directory:

=== "Linux/macOS"

    ```bash
    export QBITRR_CONFIG_PATH=/path/to/config
    torrentarr
    ```

=== "Windows"

    ```powershell
    $env:QBITRR_CONFIG_PATH = "C:\path\to\config"
    torrentarr
    ```

## Running as a Service

### Linux (systemd)

See the [Systemd Service Guide](systemd.md) for running Torrentarr as a system service.

### Windows

Use Task Scheduler to run Torrentarr at startup:

1. Open Task Scheduler
2. Create Basic Task
3. Trigger: "When the computer starts"
4. Action: Start a program
5. Program: `%USERPROFILE%\.dotnet\tools\torrentarr.exe`
6. Finish

Or use [NSSM](https://nssm.cc/) (Non-Sucking Service Manager):

```powershell
# Install NSSM
choco install nssm

# Create service (adjust path to your user)
nssm install Torrentarr "$env:USERPROFILE\.dotnet\tools\torrentarr.exe"
nssm start Torrentarr
```

### macOS

Create a LaunchAgent at `~/Library/LaunchAgents/com.torrentarr.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.torrentarr</string>
    <key>ProgramArguments</key>
    <array>
        <string>/Users/YOUR_USERNAME/.dotnet/tools/torrentarr</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/tmp/torrentarr.log</string>
    <key>StandardErrorPath</key>
    <string>/tmp/torrentarr.error.log</string>
</dict>
</plist>
```

Load the service:

```bash
launchctl load ~/Library/LaunchAgents/com.torrentarr.plist
```

## Updating

### Upgrade to Latest Version

```bash
dotnet tool update -g torrentarr
```

### Auto-Update

Torrentarr has a built-in auto-update feature that can download and apply updates automatically.

Enable in `config.toml`:

```toml
[Settings]
AutoUpdate = true
```

## Uninstalling

```bash
dotnet tool uninstall -g torrentarr
```

Your configuration files in `~/config/` will remain.

## Troubleshooting

### Command Not Found

If `torrentarr` is not found after installation:

=== "Linux/macOS"

    ```bash
    # Add dotnet tools to PATH
    export PATH="$PATH:$HOME/.dotnet/tools"

    # Make permanent
    echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.bashrc
    source ~/.bashrc
    ```

=== "Windows"

    Add `%USERPROFILE%\.dotnet\tools` to PATH:

    1. Search for "Environment Variables"
    2. Edit "Path" user variable
    3. Add `%USERPROFILE%\.dotnet\tools`
    4. Restart terminal

### .NET SDK Not Installed

Install .NET 8.0 or higher from [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download):

=== "Ubuntu/Debian"

    ```bash
    sudo apt-get update
    sudo apt-get install -y dotnet-sdk-8.0
    ```

=== "Fedora/RHEL"

    ```bash
    sudo dnf install dotnet-sdk-8.0
    ```

=== "macOS"

    ```bash
    brew install dotnet
    ```

=== "Windows"

    Download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download) and run the installer.

### Permission Denied

Global .NET tool installations are always per-user — no `sudo` is needed or recommended:

```bash
dotnet tool install -g torrentarr
```

If you see a permission error on the config directory:

```bash
mkdir -p ~/config ~/logs
```

### Tool Already Installed

If you get an error that torrentarr is already installed:

```bash
# Update instead
dotnet tool update -g torrentarr

# Or uninstall first then reinstall
dotnet tool uninstall -g torrentarr
dotnet tool install -g torrentarr
```

## Development Installation

To install from source for development:

```bash
# Clone repository
git clone https://github.com/Feramance/Torrentarr.git
cd Torrentarr

# Build and run
dotnet build
dotnet run --project src/Torrentarr.Host
```

See the [Development Guide](../../development/index.md) for more details.

## Advantages & Disadvantages

### ✅ Advantages

- Native performance (no containerization overhead)
- Easy updates via `dotnet tool update`
- Works on any platform with .NET 8+
- No additional runtime dependencies beyond the .NET SDK
- Direct access to logs and config files

### ❌ Disadvantages

- Requires .NET 8.0+ installed
- No built-in process management (need systemd/Task Scheduler)
- Manual PATH configuration may be needed on Linux/macOS

## Next Steps

- [Configure qBittorrent](../../configuration/qbittorrent.md)
- [Configure Arr Instances](../../configuration/arr/index.md)
- [Set up Systemd Service](systemd.md) (Linux)
- [First Run Guide](../quickstart.md)
