# Code Style

Torrentarr follows strict code style guidelines to ensure consistency and maintainability.

## C# Code Style

### Formatting

Torrentarr uses `dotnet format` for automated code formatting:

```bash
# Format all C# code
dotnet format

# Check formatting without applying changes
dotnet format --verify-no-changes
```

### Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Variables | `camelCase` | `torrentHash`, `arrInstance` |
| Properties | `PascalCase` | `TorrentHash`, `ArrInstance` |
| Methods | `PascalCase` | `ProcessTorrent()`, `CheckHealth()` |
| Classes | `PascalCase` | `ArrManager`, `RadarrManager` |
| Interfaces | `IPascalCase` | `IArrManager`, `IConfigLoader` |
| Constants | `PascalCase` | `MaxRetries`, `DefaultTimeout` |
| Private fields | `_camelCase` | `_torrentHash`, `_logger` |
| Namespaces | `PascalCase` | `Torrentarr.Core`, `Torrentarr.Host` |

### Type Usage

Prefer explicit types over `var` for public APIs; use `var` freely for local variables where the type is obvious:

```csharp
// Good - type obvious from right-hand side
var torrents = new List<TorrentInfo>();
var hash = torrent.Hash;

// Good - explicit for non-obvious types
TorrentState state = GetState(hash);

// Bad - var where type is unclear
var result = ProcessTorrent(hash);
```

### Nullable Reference Types

Nullable reference types are enabled project-wide. Annotate all APIs correctly:

```csharp
// Non-nullable: guaranteed to be set
public string Name { get; set; } = string.Empty;

// Nullable: may be absent
public string? CustomCategory { get; set; }

// Null-check before use
if (torrent.Category is not null)
    ProcessCategory(torrent.Category);
```

### Async/Await

All I/O operations must be async:

```csharp
// Good
public async Task<IEnumerable<TorrentInfo>> GetTorrentsAsync(
    string category,
    CancellationToken cancellationToken = default)
{
    return await _qbitClient.GetTorrentsAsync(category, cancellationToken);
}

// Bad - blocking call
public IEnumerable<TorrentInfo> GetTorrents(string category)
{
    return _qbitClient.GetTorrentsAsync(category).Result;
}
```

### Error Handling

Use typed exceptions that inherit from `TorrentarrException`:

```csharp
// Torrentarr/Core/Errors.cs
public class ConfigurationException : TorrentarrException
{
    public ConfigurationException(string field, string reason)
        : base($"Configuration error for '{field}': {reason}") { }
}

// Usage — provide context
throw new ConfigurationException(
    "CheckInterval",
    "Must be between 10 and 3600 seconds."
);
```

### Logging

Use `ILogger<T>` with structured logging:

```csharp
public class ArrManager
{
    private readonly ILogger<ArrManager> _logger;

    public ArrManager(ILogger<ArrManager> logger)
    {
        _logger = logger;
    }

    public void ProcessTorrent(string hash)
    {
        _logger.LogDebug("Checking torrent {Hash}", hash);          // Verbose details
        _logger.LogInformation("Imported torrent {Hash}", hash);    // User-facing
        _logger.LogWarning("ETA exceeds threshold for {Hash}", hash); // Potential issue
        _logger.LogError("Failed to connect to qBittorrent");       // Error occurred
        _logger.LogCritical("Database corrupted, shutting down");   // Fatal error
    }
}
```

**Always use message templates, not string interpolation**, so structured logging sinks (e.g., Serilog) can capture the raw values.

### Indentation

**4 spaces** (no tabs):

```csharp
public void Example()
{
    if (condition)
    {
        DoSomething();
        if (nested)
        {
            DoMore();
        }
    }
}
```

### Braces

Always use braces for control flow blocks, even single-line:

```csharp
// Good
if (condition)
{
    DoSomething();
}

// Bad
if (condition)
    DoSomething();
```

### Line Endings

**Unix line endings (LF) only** — enforced via `.editorconfig`:

```ini
[*.cs]
end_of_line = lf
```

## TypeScript/React Code Style

### ESLint Configuration

```bash
# Lint WebUI code
cd webui
npm run lint

# Auto-fix issues
npm run lint -- --fix
```

### TypeScript Standards

**Strict mode enabled:**

```typescript
// tsconfig.json
{
  "compilerOptions": {
    "strict": true,
    "noImplicitAny": true,
    "strictNullChecks": true
  }
}
```

**Explicit return types:**

```typescript
// Bad
function fetchTorrents() {
  return api.get('/torrents')
}

// Good
function fetchTorrents(): Promise<Torrent[]> {
  return api.get<Torrent[]>('/torrents')
}
```

**Interfaces over types (unless needed):**

```typescript
// Preferred
interface Torrent {
  hash: string
  name: string
  progress: number
}

// Only use type for unions/intersections
type TorrentState = 'downloading' | 'completed' | 'failed'
```

### React Component Style

**Functional components only:**

```typescript
import { FC } from 'react'

interface Props {
  torrent: Torrent
  onDelete: (hash: string) => void
}

const TorrentCard: FC<Props> = ({ torrent, onDelete }) => {
  return (
    <div className="torrent-card">
      <span>{torrent.name}</span>
      <button onClick={() => onDelete(torrent.hash)}>Delete</button>
    </div>
  )
}

export default TorrentCard
```

**Hooks naming:**

```typescript
// Custom hooks start with 'use'
function useDataSync(interval: number) {
  const [data, setData] = useState(null)

  useEffect(() => {
    const timer = setInterval(() => fetchData(), interval)
    return () => clearInterval(timer)
  }, [interval])

  return data
}
```

### Naming Conventions (TypeScript)

| Element | Convention | Example |
|---------|------------|---------|
| Variables | `camelCase` | `torrentHash`, `arrInstance` |
| Functions | `camelCase` | `processTorrent()`, `checkHealth()` |
| Components | `PascalCase` | `TorrentCard`, `LogViewer` |
| Interfaces | `PascalCase` | `Torrent`, `ArrConfig` |
| Types | `PascalCase` | `TorrentState`, `ApiResponse` |
| Constants | `SCREAMING_SNAKE_CASE` | `MAX_RETRIES`, `API_BASE_URL` |

### Import Order (TypeScript)

```typescript
// React imports
import { FC, useState, useEffect } from 'react'

// Third-party libraries
import axios from 'axios'

// Local modules
import { api } from '@/api/client'
import { Torrent } from '@/api/types'

// Local components
import TorrentCard from '@/components/TorrentCard'

// Icons/assets
import DeleteIcon from '@/icons/Delete.svg?react'
```

### Indentation (TypeScript)

**2 spaces:**

```typescript
function example() {
  if (condition) {
    doSomething()
    if (nested) {
      doMore()
    }
  }
}
```

## General Guidelines

### Comments

**When to comment:**

- Complex algorithms that aren't immediately obvious
- Business logic rationale
- Workarounds for bugs in dependencies
- TODO items with issue numbers

**When NOT to comment:**

- Obvious code
- Outdated comments
- Commented-out code (use git history instead)

**Good comments:**

```csharp
// Workaround for qBittorrent API v4.3.9 bug where category is null
// for torrents with uppercase tags. Fixed in v4.4.0.
// See: https://github.com/qbittorrent/qBittorrent/issues/12345
if (torrent.Category is null && torrent.Tags.Count > 0)
    torrent = torrent with { Category = _defaultCategory };
```

## Automated Enforcement

### .editorconfig

```ini
root = true

[*]
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true

[*.cs]
indent_style = space
indent_size = 4
charset = utf-8

[*.{ts,tsx,js,json}]
indent_style = space
indent_size = 2
charset = utf-8
```

### CI/CD Enforcement

The CI pipeline runs:

```bash
dotnet format --verify-no-changes   # C# formatting
cd webui && npm run lint             # TypeScript linting
cd webui && npm run build            # TypeScript type-check
```

## IDE Configuration

### VS Code

**Recommended extensions:**

- C# Dev Kit (Microsoft)
- ESLint
- Prettier
- EditorConfig for VS Code

**settings.json:**

```json
{
  "editor.formatOnSave": true,
  "[csharp]": {
    "editor.defaultFormatter": "ms-dotnettools.csharp"
  },
  "[typescript]": {
    "editor.defaultFormatter": "esbenp.prettier-vscode"
  },
  "[typescriptreact]": {
    "editor.defaultFormatter": "esbenp.prettier-vscode"
  }
}
```

### JetBrains Rider

1. Settings → Editor → Code Style → C#
   - Set line endings to LF
   - Indentation: 4 spaces

2. Settings → Tools → Actions on Save
   - Enable "Reformat Code"
   - Enable "Optimize Imports"

## Related Documentation

- [Contributing](contributing.md) - Contribution guidelines
- [Development Guide](index.md) - Complete development setup
- [Testing](testing.md) - Testing your code
- [AGENTS.md](https://github.com/Feramance/Torrentarr/blob/master/AGENTS.md) - AI agent coding guidelines
