# PyPI Installation

Install Torrentarr directly from PyPI using pip. This method is ideal for users who prefer native installations or are already using Python for other tools.

## Prerequisites

- **Python 3.11 or higher** ([Download Python](https://www.python.org/downloads/))
- **pip** (included with Python 3.4+)
- qBittorrent running and accessible
- At least one Arr instance (Radarr, Sonarr, or Lidarr)

## Quick Start

=== "Linux/macOS"

    ```bash
    # Install Torrentarr
    dotnet tool install -g torrentarr

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

=== "Virtual Environment (Recommended)"

    ```bash
    # Create virtual environment
    python3 -m venv torrentarr-env

    # Activate it
    source torrentarr-env/bin/activate  # Linux/macOS
    # or
    torrentarr-env\Scripts\activate  # Windows

    # Install Torrentarr
    dotnet tool install -g torrentarr

    # Run Torrentarr
    torrentarr
    ```

## Installation

### Check Python Version

```bash
python3 --version
```

Make sure you have Python 3.11 or higher.

### Install Torrentarr

```bash
dotnet tool install -g torrentarr
```

!!! tip "Why Torrentarr?"
    The package is named `Torrentarr` on PyPI (the original `Torrentarr` was taken), but the command is still `torrentarr`.

### Install with Optional Dependencies

For faster JSON parsing:

```bash
dotnet tool install -g torrentarr[fast]
```

For development dependencies:

```bash
dotnet tool install -g torrentarr[dev]
```

For all optional dependencies:

```bash
dotnet tool install -g torrentarr[all]
```

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

```bash
export QBITRR_CONFIG_PATH=/path/to/config
torrentarr
```

Or on Windows:

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
5. Program: `C:\Python311\Scripts\torrentarr.exe`
6. Finish

Or use [NSSM](https://nssm.cc/) (Non-Sucking Service Manager):

```powershell
# Install NSSM
choco install nssm

# Create service
nssm install Torrentarr "C:\Python311\Scripts\torrentarr.exe"
nssm start Torrentarr
```

### macOS

Create a LaunchAgent:

1. Create `~/Library/LaunchAgents/com.torrentarr.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.torrentarr</string>
    <key>ProgramArguments</key>
    <array>
        <string>/usr/local/bin/torrentarr</string>
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

2. Load the service:

```bash
launchctl load ~/Library/LaunchAgents/com.torrentarr.plist
```

## Updating

### Upgrade to Latest Version

```bash
dotnet tool update -g torrentarr
```

### Upgrade with Auto-Update

Torrentarr has a built-in auto-update feature:

1. Enable in `config.toml`:
   ```toml
   [Settings.CompletedDownloadFolder]
   AutoUpdate = true
   UpdateSchedule = "0 3 * * 0"  # Sunday at 3 AM
   ```

2. Torrentarr will automatically update itself from PyPI

## Uninstalling

```bash
pip uninstall Torrentarr
```

Your configuration files in `~/config/` will remain.

## Troubleshooting

### Command Not Found

If `torrentarr` command is not found after installation:

=== "Linux/macOS"

    ```bash
    # Find where pip installs scripts
    python3 -m site --user-base

    # Add to PATH (add to ~/.bashrc or ~/.zshrc)
    export PATH="$HOME/.local/bin:$PATH"
    ```

=== "Windows"

    Add Python Scripts directory to PATH:

    1. Search for "Environment Variables"
    2. Edit "Path" user variable
    3. Add `C:\Python311\Scripts`
    4. Restart terminal

### Permission Denied

On Linux/macOS, if you get permission errors:

```bash
# Install for current user only
pip install --user Torrentarr
```

### Python Version Too Old

If your system Python is too old:

```bash
# Install a newer Python version
sudo apt install python3.12  # Ubuntu/Debian
brew install python@3.12     # macOS

# Use specific version
python3.12 -m dotnet tool install -g torrentarr
python3.12 -m Torrentarr.main
```

### Virtual Environment Issues

If you have problems with system Python:

```bash
# Create fresh venv
python3 -m venv --clear torrentarr-env

# Activate and install
source torrentarr-env/bin/activate
pip install --upgrade pip
dotnet tool install -g torrentarr
```

### Import Errors

If you get import errors on startup:

```bash
# Reinstall with all dependencies
pip install --force-reinstall Torrentarr[all]
```

## Development Installation

To install from source for development:

```bash
# Clone repository
git clone https://github.com/Feramance/Torrentarr.git
cd Torrentarr

# Create virtual environment
python3 -m venv .venv
source .venv/bin/activate

# Install in development mode
pip install -e .[all]

# Run from source
python -m Torrentarr.main
```

See the [Development Guide](../../development/index.md) for more details.

## Advantages & Disadvantages

### ✅ Advantages

- Native performance (no containerization overhead)
- Easy integration with other Python tools
- Direct access to logs and config files
- Simple updates via pip
- Works on any platform with Python

### ❌ Disadvantages

- Requires Python 3.11+ installed
- Manual dependency management
- No built-in process management (need systemd/Task Scheduler)
- Path issues on some systems
- More complex multi-user setups

## Next Steps

- [Configure qBittorrent](../../configuration/qbittorrent.md)
- [Configure Arr Instances](../../configuration/arr/index.md)
- [Set up Systemd Service](systemd.md) (Linux)
- [First Run Guide](../quickstart.md)
