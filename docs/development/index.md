# Development

## Prerequisites

- Git and the .NET 10.0 SDK
- Node.js 22.22.2+ (22.x), 24.15+, or 26+ for builds that include the WebUI
- npm 9 or later for installing frontend dependencies

## Build and run

```bash
git clone https://github.com/Feramance/Torrentarr.git
cd Torrentarr
npm ci --prefix webui
npm run build --prefix webui
dotnet restore
dotnet build
dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj
```

The frontend build writes the assets into the Host's `wwwroot`. Build it before the Host so the local instance can serve the WebUI.

## Contributor guides

- [Contributing](https://github.com/Feramance/Torrentarr/blob/master/CONTRIBUTING.md) covers issues, commits, and pull requests.
- [Architecture](../advanced/architecture.md) explains project boundaries, workers, and data flow.
- [Code Style](code-style.md) and [Testing](testing.md) cover local checks.
- [WebUI Development](webui.md) covers the Vite dev server and frontend build.
- [Manage verification](maintenance-verification.md) covers maintenance safety cases and documentation validation.
- [Release Process](release-process.md) covers versioning, distribution, and OpenAPI regeneration.

## Preview the documentation

```bash
python3 -m pip install -r requirements.docs.txt
mkdocs serve
```

Build and verify links and TOML examples before submitting documentation changes.

```bash
TORRENTARR_DOCS_GIT_DATES=false mkdocs build --strict
python3 scripts/check-docs.py
```
