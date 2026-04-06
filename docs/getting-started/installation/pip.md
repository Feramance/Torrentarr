# Pip installation (not available)

Torrentarr is a **C#/.NET** application and does **not** ship via PyPI or pip. The original [qBitrr](https://github.com/Feramance/qBitrr) (Python) can be installed with pip; Torrentarr is a separate port.

**To install Torrentarr, use one of these methods instead:**

- **[Binary](binary.md)** — Download a pre-built self-contained executable
- **[Docker](docker.md)** — `feramance/torrentarr:latest`
- **[Systemd](systemd.md)** — Run as a Linux service (usually with a binary install)
- **From source** — Clone the repo and `dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj` (see [Development Guide](../../development/index.md))

See the [Installation overview](index.md) for comparison and links.
