# Binary Installation

Download and run pre-built Torrentarr binaries for Linux, macOS, or Windows. No Python installation required!

!!! info "Binary Releases"
    Pre-built binaries are generated for each release using .NET's self-contained single-file publish. They include the .NET runtime and all dependencies — no separate installation required.

## Prerequisites

- 64-bit operating system (Linux, macOS, or Windows)
- qBittorrent running and accessible
- At least one Arr instance (Radarr, Sonarr, or Lidarr)

## Download

### Latest Release

Visit the [GitHub Releases page](https://github.com/Feramance/Torrentarr/releases) and download the binary for your platform:

| Platform | File |
|----------|------|
| Linux | `torrentarr-linux-x64` |
| macOS | `torrentarr-macos-x64` |
| Windows | `torrentarr-windows-x64.exe` |

### Command Line Download

=== "Linux"

    ```bash
    # Download latest release
    curl -L -o torrentarr https://github.com/Feramance/Torrentarr/releases/latest/download/torrentarr-linux-x64

    # Make executable
    chmod +x torrentarr

    # Run
    ./torrentarr
    ```

=== "macOS"

    ```bash
    # Download latest release
    curl -L -o torrentarr https://github.com/Feramance/Torrentarr/releases/latest/download/torrentarr-macos-x64

    # Make executable
    chmod +x torrentarr

    # Run (you may need to allow in Security settings)
    ./torrentarr
    ```

=== "Windows"

    ```powershell
    # Download with PowerShell
    Invoke-WebRequest -Uri https://github.com/Feramance/Torrentarr/releases/latest/download/torrentarr-windows-x64.exe -OutFile torrentarr.exe

    # Run
    .\torrentarr.exe
    ```

## Installation

### Linux

1. **Download and install:**
   ```bash
   sudo curl -L -o /usr/local/bin/torrentarr \
     https://github.com/Feramance/Torrentarr/releases/latest/download/torrentarr-linux-x64

   sudo chmod +x /usr/local/bin/torrentarr
   ```

2. **Run:**
   ```bash
   torrentarr
   ```

### macOS

1. **Download:**
   ```bash
   curl -L -o ~/Downloads/torrentarr \
     https://github.com/Feramance/Torrentarr/releases/latest/download/torrentarr-macos-x64

   chmod +x ~/Downloads/torrentarr
   ```

2. **Move to Applications (optional):**
   ```bash
   sudo mv ~/Downloads/torrentarr /usr/local/bin/
   ```

3. **First run (security prompt):**
   ```bash
   ./torrentarr
   ```

   If macOS blocks it:
   - Go to System Preferences → Security & Privacy
   - Click "Allow Anyway" next to the torrentarr message
   - Run again

### Windows

1. **Download** `torrentarr-windows-x64.exe` from releases

2. **Move to a permanent location:**
   ```
   C:\Program Files\Torrentarr\torrentarr.exe
   ```

3. **Run:**
   - Double-click `torrentarr.exe`
   - Or run from PowerShell: `.\torrentarr.exe`

4. **Add to PATH (optional):**
   - Search for "Environment Variables"
   - Edit "Path" system variable
   - Add `C:\Program Files\Torrentarr`

## First Run

1. **Start Torrentarr:**
   ```bash
   ./torrentarr  # or torrentarr.exe on Windows
   ```

2. **Configuration file created:**

   Binary installations use these default paths:

   === "Linux/macOS"
       ```
       ~/.config/torrentarr/config.toml
       ~/.local/share/torrentarr/logs/
       ```

   === "Windows"
       ```
       %APPDATA%\torrentarr\config.toml
       %APPDATA%\torrentarr\logs\
       ```

3. **Stop Torrentarr:**
   Press ++ctrl+c++

4. **Edit configuration:**
   See [First Run Guide](../quickstart.md)

5. **Start again:**
   ```bash
   ./torrentarr
   ```

## Configuration Location

### Custom Config Path

Set a custom config directory:

=== "Linux/macOS"

    ```bash
    export QBITRR_CONFIG_PATH=/path/to/config
    ./torrentarr
    ```

=== "Windows"

    ```powershell
    $env:QBITRR_CONFIG_PATH = "C:\path\to\config"
    .\torrentarr.exe
    ```

## Running as a Service

### Linux (systemd)

Create `/etc/systemd/system/torrentarr.service`:

```ini
[Unit]
Description=Torrentarr
After=network.target

[Service]
Type=simple
User=your-user
ExecStart=/usr/local/bin/torrentarr
Restart=always

[Install]
WantedBy=multi-user.target
```

Enable and start:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now torrentarr
```

### Windows

Use Task Scheduler:

1. Open Task Scheduler
2. Create Basic Task
3. Name: "Torrentarr"
4. Trigger: "When the computer starts"
5. Action: "Start a program"
6. Program: `C:\Program Files\Torrentarr\torrentarr.exe`
7. Finish

Or use [NSSM](https://nssm.cc/):

```powershell
nssm install Torrentarr "C:\Program Files\Torrentarr\torrentarr.exe"
nssm start Torrentarr
```

### macOS (LaunchAgent)

Create `~/Library/LaunchAgents/com.torrentarr.plist`:

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
</dict>
</plist>
```

Load:

```bash
launchctl load ~/Library/LaunchAgents/com.torrentarr.plist
```

## Updating

Binary installations do not support auto-update. You must manually download and replace the binary.

### Linux/macOS

```bash
# Backup current binary
sudo mv /usr/local/bin/torrentarr /usr/local/bin/torrentarr.bak

# Download latest
sudo curl -L -o /usr/local/bin/torrentarr \
  https://github.com/Feramance/Torrentarr/releases/latest/download/torrentarr-linux-x64

sudo chmod +x /usr/local/bin/torrentarr

# Restart service
sudo systemctl restart torrentarr  # if using systemd
```

### Windows

1. Stop Torrentarr (or the service)
2. Download new `torrentarr-windows-x64.exe`
3. Replace old file
4. Start Torrentarr again

## Troubleshooting

### Binary Won't Run

=== "Linux"

    Check dependencies:
    ```bash
    ldd ./torrentarr
    ```

    Most common issues:
    - Missing `glibc` (too old — binary requires glibc 2.17+)
    - Missing `libssl`

    Solution: Use [dotnet tool installation](dotnet.md) instead.

=== "macOS"

    If blocked by security:
    ```bash
    # Remove quarantine flag
    xattr -d com.apple.quarantine ./torrentarr
    ```

=== "Windows"

    If blocked by Windows Defender:
    - Add exception in Windows Security
    - Or use [dotnet tool installation](dotnet.md)

### Permission Denied

=== "Linux/macOS"

    ```bash
    chmod +x torrentarr
    ```

=== "Windows"

    Run as Administrator (right-click → Run as administrator)

### Config File Not Found

Check config location:

```bash
./torrentarr --show-config-path
```

Create config directory manually:

=== "Linux/macOS"

    ```bash
    mkdir -p ~/.config/torrentarr
    ```

=== "Windows"

    ```powershell
    New-Item -ItemType Directory -Path "$env:APPDATA\torrentarr"
    ```

### Large Binary Size

Binary files are 50-100MB because they include:
- .NET runtime
- All application dependencies
- Compiled native libraries

This is normal for .NET self-contained single-file binaries.

## Advantages & Disadvantages

### ✅ Advantages

- No Python installation required
- Single file distribution
- Easy to deploy
- Consistent across systems
- No dependency conflicts

### ❌ Disadvantages

- Large file size (50-100MB)
- No auto-update support
- Manual updates required
- May trigger antivirus warnings
- Slower startup than dotnet tool install

## Building from Source

To build your own self-contained binary:

```bash
# Clone repository
git clone https://github.com/Feramance/Torrentarr.git
cd Torrentarr

# Publish as self-contained single-file binary
dotnet publish src/Torrentarr.Host \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  --output ./dist

# Binary in dist/
ls dist/
```

See the [Development Guide](../../development/index.md) for more details.

## Next Steps

- [First Run Guide](../quickstart.md)
- [Configure qBittorrent](../../configuration/qbittorrent.md)
- [Configure Arr Instances](../../configuration/arr/index.md)
- [Troubleshooting](../../troubleshooting/index.md)
