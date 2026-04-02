# Development

Contribute to Torrentarr development! This guide covers setting up a development environment and contributing code.

## Quick Start

```bash
# Clone the repository
git clone https://github.com/Feramance/Torrentarr.git
cd Torrentarr

# Restore and build
dotnet restore
dotnet build

# Run Torrentarr (Host includes WebUI and workers)
dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj
```

For WebUI development with hot reload, see [WebUI Development](webui.md).

## Development Setup

### Prerequisites

- **.NET 8.0+ SDK** - Backend and tooling
- **Node.js 18+** - For WebUI development
- **Git** - Version control

### Repository Structure

```
Torrentarr/
├── src/
│   ├── Torrentarr.Host/       # Orchestrator, WebUI host, free space
│   ├── Torrentarr.WebUI/      # ASP.NET Core API (standalone mode)
│   ├── Torrentarr.Workers/    # Per-Arr worker entry point
│   ├── Torrentarr.Core/       # Interfaces, config models
│   └── Torrentarr.Infrastructure/  # API clients, EF Core, services
├── webui/                     # React frontend (Vite)
│   ├── src/
│   └── package.json
├── tests/                     # xUnit test projects
├── docs/                      # MkDocs documentation
└── config.example.toml
```

### Environment Setup

#### Backend Development

```bash
# Restore and build
dotnet restore
dotnet build

# Run the full Host (WebUI + workers)
dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj

# Or run WebUI only (for API development)
dotnet run --project src/Torrentarr.WebUI/Torrentarr.WebUI.csproj
```

Optional: install pre-commit hooks for docs/linting (`pre-commit install`).

#### WebUI Development

```bash
# Navigate to WebUI directory
cd webui

# Install dependencies
npm ci

# Start development server
npm run dev

# WebUI will be at http://localhost:5173
```

## Code Style

### C# / .NET

Torrentarr follows standard C# conventions:

- **EditorConfig** - Shared formatting and analysis (see repo root)
- **dotnet format** - Apply formatting
- **Nullable reference types** - Enabled; use `?` and null checks appropriately
- **Async** - Prefer async/await for I/O
- **PascalCase** for public members, camelCase for local variables

**Format code:**
```bash
dotnet format
```

**Key conventions:**
- 4-space indentation
- XML doc comments for public APIs
- `PascalCase` for types and public members
- `camelCase` for parameters and locals

### TypeScript/React

WebUI follows these standards:

- **ESLint** - Linting with TypeScript rules
- **Prettier** - Code formatting (via ESLint)
- **2-space indentation**
- **Functional components only**
- **Explicit return types**

**Lint code:**
```bash
cd webui
npm run lint
```

## Making Changes

### Workflow

1. **Create a branch:**
   ```bash
   git checkout -b feature/my-feature
   ```

2. **Make changes** - Follow code style guidelines

3. **Test changes:**
   ```bash
   dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj
   # Or: dotnet test --filter "Category!=Live"
   ```

4. **Commit:**
   ```bash
   git add .
   git commit -m "feat: Add my feature"
   ```

5. **Push and create PR:**
   ```bash
   git push origin feature/my-feature
   ```

### Commit Messages

Follow conventional commits:

- `feat:` - New feature
- `fix:` - Bug fix
- `docs:` - Documentation changes
- `style:` - Code style changes
- `refactor:` - Code refactoring
- `test:` - Test additions/changes
- `chore:` - Maintenance tasks

**Examples:**
```
feat: Add support for Lidarr v2.0
fix: Resolve stalled torrent detection issue
docs: Update installation guide for Docker
```

## Testing

- **Unit and integration tests:** `dotnet test --filter "Category!=Live"`
- **Live tests** (require real qBit/Arr): `dotnet test --filter "Category=Live"`
- **Single project:** `dotnet test tests/Torrentarr.Infrastructure.Tests/`

See [Testing](testing.md) for details.

## Building

### Backend

```bash
dotnet build
# Release: dotnet build -c Release
```

### Full stack (Host + embedded WebUI)

```bash
# Build WebUI into Host wwwroot (see build.sh / build.bat)
cd webui && npm run build && cd ..
dotnet build src/Torrentarr.Host/Torrentarr.Host.csproj
```

### WebUI only (dev)

```bash
cd webui
npm run build
# Output: webui/dist/ (copied to Host wwwroot by build script)
```

### Docker Image

```bash
docker build -t torrentarr:test .
docker run -d --name torrentarr-test -p 6969:6969 -v $(pwd)/config:/config torrentarr:test
```

## Documentation

### Writing Documentation

Documentation uses MkDocs with Material theme:

```bash
# Install docs dependencies
make docs-install

# Serve locally
make docs-serve
# Visit http://127.0.0.1:8000

# Build
make docs-build
```

**Guidelines:**
- Use clear, concise language
- Include code examples
- Add screenshots where helpful
- Test all commands/examples
- Link to related pages

### Documentation Structure

See [docs/README.md](https://github.com/Feramance/Torrentarr/blob/master/docs/README.md) for full guidelines.

## Debugging

### Debug Mode

Enable debug logging:

```toml
[Settings]
LogLevel = "DEBUG"
```

### IDE Setup

#### VSCode / Cursor

Recommended extensions:

- C#
- C# Dev Kit (or ms-dotnettools.csharp)
- ESLint, Prettier (for webui)
- Docker

**launch.json** (optional): use "Run and Debug" with profile for `Torrentarr.Host` or run from terminal: `dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj`.

#### Visual Studio / Rider

Open the solution (e.g. `Torrentarr.sln` if present) or the folder; set `Torrentarr.Host` as startup project and run.

## Contributing Guidelines

### Before Submitting

- [ ] Code follows style guidelines
- [ ] Pre-commit hooks pass
- [ ] Changes tested locally
- [ ] Documentation updated
- [ ] Commit messages follow convention

### Pull Request Process

1. **Create descriptive PR:**
   - Clear title
   - Description of changes
   - Related issues (if any)

2. **Code review:**
   - Address review comments
   - Keep PR focused and atomic

3. **CI/CD:**
   - Ensure all checks pass
   - Fix any failing builds

4. **Merge:**
   - Squash commits if needed
   - Delete branch after merge

## Architecture

Torrentarr's backend is **.NET (C#)** with ASP.NET Core and separate worker processes. Key points:

- **Torrentarr.Host** — Orchestrator: hosts WebUI (ASP.NET Core minimal API), runs **HostWorkerManager** (Failed/Recheck/free space/tracker sort loops with auto-restart), spawns per-Arr **Torrentarr.Workers** processes.
- **Torrentarr.Infrastructure** — EF Core (SQLite), qBittorrent/Arr API clients, services (TorrentProcessor, SeedingService, ArrSyncService, etc.).
- **Torrentarr.Core** — Config models, interfaces.

See [Architecture](../advanced/architecture.md) for diagrams and data flow.

### React Frontend

The WebUI is a modern React SPA built with TypeScript and Mantine components:

#### Frontend Stack

**React 18** - UI Framework
- Functional components with hooks
- Context API for global state (`SearchContext`, `ToastContext`, `WebUIContext`)
- React Router for navigation
- Strict mode enabled

**TypeScript** - Type Safety
- Strict type checking enabled
- Interfaces for all API responses
- Type-safe API client
- No `any` types (use `unknown` if needed)

**Mantine** - Component Library
- v8 with dark/light theme support
- Responsive layout components
- Form validation with `react-hook-form`
- Notifications via `@mantine/notifications`

**Vite** - Build Tool
- Fast HMR (Hot Module Replacement)
- ESBuild for transpilation
- Code splitting and lazy loading
- Environment variable support

**TanStack Table** - Data Tables
- Sorting, filtering, pagination
- Virtual scrolling for large datasets
- Customizable column rendering
- Export functionality

#### Frontend Architecture

```
webui/src/
├── api/
│   ├── client.ts          # Axios client with auth
│   └── types.ts           # TypeScript interfaces
├── components/
│   ├── ConfirmDialog.tsx  # Reusable confirmation
│   ├── LogViewer.tsx      # Log display component
│   ├── ProcessCard.tsx    # Process status card
│   └── ...
├── context/
│   ├── SearchContext.tsx  # Search state management
│   ├── ToastContext.tsx   # Notification system
│   └── WebUIContext.tsx   # Global settings
├── hooks/
│   ├── useDataSync.ts     # Auto-refresh hook
│   ├── useWebSocket.ts    # WebSocket connection
│   └── ...
├── pages/
│   ├── Dashboard.tsx      # Main dashboard
│   ├── Processes.tsx      # Process management
│   ├── Logs.tsx           # Log viewer
│   ├── Radarr.tsx         # Radarr view
│   ├── Sonarr.tsx         # Sonarr view
│   ├── Lidarr.tsx         # Lidarr view
│   └── Config.tsx         # Config editor
└── App.tsx                # Root component
```

### Key Concepts

- **Process isolation:** Each Arr instance runs in a separate **Torrentarr.Workers** process (spawned by Host). Failures are isolated; WebUI stays up.
- **Event loops:** Each worker runs a loop: fetch torrents, health checks, trigger imports, search, cleanup. Implemented in `Torrentarr.Infrastructure` (e.g. TorrentProcessor, SeedingService).
- **Health monitoring:** Stalled detection, ETA limits, FFprobe validation, tracker checks — see config options and `TorrentProcessor` / `SeedingService`.
- **Instant import:** On completion, workers call Arr's `DownloadedMoviesScan` (or equivalent) and update the shared SQLite DB.
- **Database:** Single `torrentarr.db` (EF Core); workers coordinate via the shared file. See [Database](../advanced/database.md) and [Architecture](../advanced/architecture.md).

For implementation details, browse `src/Torrentarr.Infrastructure` and `src/Torrentarr.Workers`.

## Common Development Tasks

### Adding a New Feature

1. Add or extend services in `Torrentarr.Infrastructure` (or Core for interfaces).
2. Add config options in `Torrentarr.Core` (e.g. `TorrentarrConfig`) and `ConfigurationLoader`.
3. Register services and call from workers or API in `Torrentarr.Host` / `Torrentarr.WebUI`.
4. Add WebUI types and API in `webui/src/api/` and pages as needed.
5. Update docs under `docs/` and config-file.md.

### Adding a New Arr Type

Torrentarr supports Radarr, Sonarr, and Lidarr. Adding another *Arr variant would require: a new config section in `TorrentarrConfig`, an API client in `Torrentarr.Infrastructure`, and worker logic. See existing `Radarr*`, `Sonarr*`, `Lidarr*` code paths.

### Modifying the Database Schema

EF Core migrations: add or change entities in `Torrentarr.Infrastructure/Database/`, then add a migration. See [Database](../advanced/database.md). Config version is in `ConfigurationLoader.ExpectedConfigVersion` and validated on load.

### Adding a WebUI Feature

Add API endpoints in `Torrentarr.Host/Program.cs` (or WebUI project) with `app.MapGet`/`MapPost`. Add React components in `webui/src/`, types in `webui/src/api/types.ts`, and call from pages. See [WebUI Development](webui.md).

### Debugging a Complex Issue

- Set `ConsoleLevel = "DEBUG"` in config or use the WebUI log level control.
- Check logs in `~/config/logs/` or Docker logs.
- Use `dotnet run` with a debugger (F5 in VS/ Rider) or add breakpoints. For database state, use `sqlite3 ~/config/torrentarr.db` or the Host's `--repair-database` for integrity checks.

## Performance Optimization

### Database Optimization

Use EF Core indexes where needed; see `Torrentarr.Infrastructure` entity configuration. For ad-hoc SQLite indexes, use `sqlite3` or migration. See [Database](../advanced/database.md).

### API Call Reduction

Batch Arr API calls where possible; the existing services (e.g. ArrSyncService) already use bulk fetches. Profile with logs or a debugger to find hot paths.

### Memory Optimization

Use streaming/pagination for large result sets; avoid loading full collections into memory. The workers process in loops with configurable limits.

## Testing Strategies

- **Unit tests:** `dotnet test tests/Torrentarr.Infrastructure.Tests/` (and other test projects). Use Moq for dependencies.
- **Integration:** `dotnet test --filter "Category=Live"` (requires real config and services). See [Testing](testing.md).
- **Manual:** Use the checklist below when testing changes.

### Manual Testing Checklist

When testing changes manually:

- [ ] **Fresh install** - Test with new config
- [ ] **Migration** - Test upgrading from previous version
- [ ] **Multiple Arr instances** - Test with 2+ of each type
- [ ] **Failed torrents** - Test stalled, corrupted, dead trackers
- [ ] **Successful imports** - Test movies, TV shows, music
- [ ] **Search automation** - Test missing content search
- [ ] **WebUI** - Test all pages and actions
- [ ] **API** - Test all endpoints with/without token
- [ ] **Edge cases** - Empty libraries, network errors, disk full

## Resources

### Official Resources

- **Repository:** [github.com/Feramance/Torrentarr](https://github.com/Feramance/Torrentarr)
- **Issues:** [github.com/Feramance/Torrentarr/issues](https://github.com/Feramance/Torrentarr/issues)
- **Discussions:** [github.com/Feramance/Torrentarr/discussions](https://github.com/Feramance/Torrentarr/discussions)
- **Docker Hub:** [![Docker Pulls](https://img.shields.io/docker/pulls/feramance/torrentarr.svg?cacheSeconds=3600)](https://hub.docker.com/r/feramance/torrentarr)
- **Releases:** [github.com/Feramance/Torrentarr/releases](https://github.com/Feramance/Torrentarr/releases)

### Development Guides

- **[AGENTS.md](https://github.com/Feramance/Torrentarr/blob/master/AGENTS.md)** - Comprehensive development guidelines for AI agents
- **[Contributing Guide](contributing.md)** - Contribution guidelines and code of conduct
- **[API Reference](../webui/api.md)** - Complete API reference with examples

### External Documentation

- **qBittorrent API:** [github.com/qbittorrent/qBittorrent/wiki/WebUI-API](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-(qBittorrent-4.1))
- **Radarr API:** [radarr.video/docs/api/](https://radarr.video/docs/api/)
- **Sonarr API:** [sonarr.tv/docs/api/](https://sonarr.tv/docs/api/)
- **Lidarr API:** [lidarr.audio/docs/api/](https://lidarr.audio/docs/api/)
- **Peewee ORM:** [docs.peewee-orm.com](http://docs.peewee-orm.com/)
- **Flask:** [flask.palletsprojects.com](https://flask.palletsprojects.com/)
- **React:** [react.dev](https://react.dev/)
- **Mantine:** [mantine.dev](https://mantine.dev/)

## Community

### Getting Help

- **GitHub Discussions** - Ask questions, share ideas
- **GitHub Issues** - Report bugs, request features
- **Discord** - Real-time chat with community and maintainers
- **Reddit** - r/Torrentarr for community support

### Contributing

We welcome contributions of all types:

- **Code** - Bug fixes, new features, performance improvements
- **Documentation** - Guides, examples, typo fixes
- **Testing** - Manual testing, bug reports, edge case discovery
- **Design** - WebUI improvements, icons, themes
- **Translations** - Internationalization support (future)

### Recognition

Contributors are recognized in:

- **[README.md](https://github.com/Feramance/Torrentarr/blob/master/README.md)** - Contributors section with avatars
- **Release Notes** - Feature/fix attribution
- **GitHub Contributors Graph** - Automatic tracking
- **Special Thanks** - Major contributors get shoutouts

### Code of Conduct

We follow the [Contributor Covenant Code of Conduct](https://www.contributor-covenant.org/version/2/1/code_of_conduct/):

- Be respectful and inclusive
- Accept constructive criticism
- Focus on what's best for the community
- Show empathy towards others

## Release Process

### Versioning

Torrentarr follows [Semantic Versioning](https://semver.org/):

- **MAJOR** - Breaking changes (e.g., 5.0.0 → 6.0.0)
- **MINOR** - New features, backwards compatible (e.g., 5.1.0 → 5.2.0)
- **PATCH** - Bug fixes (e.g., 5.1.1 → 5.1.2)

### Release Workflow

1. **Prepare release:**
   ```bash
   # Update version
   bump2version minor  # or major/patch

   # Generate changelog
   make changelog
   ```

2. **Create release:**
   ```bash
   # Tag and push
   git push origin master --tags
   ```

3. **Automated CI/CD:**
   - Build .NET and Docker image → GitHub Releases, Docker Hub
   - Generate GitHub release notes
   - Update documentation

4. **Announce:**
   - GitHub Releases
   - Discord announcement
   - Reddit post
   - Update documentation site

## License

Torrentarr is licensed under the **MIT License**. See [LICENSE](https://github.com/Feramance/Torrentarr/blob/master/LICENSE) for full details.

### What This Means

✅ Commercial use allowed
✅ Modification allowed
✅ Distribution allowed
✅ Private use allowed
❌ Liability - Software provided "as is"
❌ Warranty - No warranty provided

## Next Steps

Ready to contribute? Here's how to get started:

1. **⭐ Star the repository** - Show your support!
2. **🍴 Fork the repository** - Create your own copy
3. **💻 Set up development environment** - Follow the setup guide above
4. **🔍 Pick an issue** - Look for "good first issue" label
5. **🚀 Submit a pull request** - Share your contribution!

### Good First Issues

Looking for something to work on? Check out issues labeled:

- `good first issue` - Beginner-friendly tasks
- `help wanted` - Community input needed
- `documentation` - Docs improvements
- `enhancement` - Feature requests
- `bug` - Bug fixes needed

### Questions?

- 💬 **Ask in Discussions** - [github.com/Feramance/Torrentarr/discussions](https://github.com/Feramance/Torrentarr/discussions)
- 📧 **Email maintainers** - See [Contributing Guide](contributing.md) for contact info
- 🐛 **Report bugs** - [github.com/Feramance/Torrentarr/issues/new](https://github.com/Feramance/Torrentarr/issues/new)

---

Thank you for contributing to Torrentarr! Every contribution, big or small, helps make Torrentarr better for everyone. 🚀

---

## Related Documentation

- [Installation Guide](../getting-started/installation/index.md) - Install Torrentarr for development
- [Configuration Reference](../configuration/config-file.md) - All config options
- [API Reference](../webui/api.md) - REST API documentation
- [Troubleshooting](../troubleshooting/index.md) - Common development issues
- [FAQ](../faq.md) - Frequently asked questions
