# Contributing to Commandarr

Thank you for your interest in contributing to Commandarr! This document provides guidelines and instructions for contributing.

## Code of Conduct

This project adheres to a code of conduct. By participating, you are expected to uphold this code:
- Be respectful and inclusive
- Welcome newcomers
- Focus on constructive criticism
- Accept responsibility for mistakes

## How Can I Contribute?

### Reporting Bugs

Before creating bug reports, please check existing issues. When creating a bug report, include:

- **Clear title** - Descriptive summary of the issue
- **Steps to reproduce** - Detailed steps to reproduce the behavior
- **Expected behavior** - What you expected to happen
- **Actual behavior** - What actually happened
- **Environment** - OS, .NET version, Docker version, etc.
- **Logs** - Relevant log output
- **Screenshots** - If applicable

**Template:**
```markdown
**Description:**
Brief description of the bug

**Steps to Reproduce:**
1. Go to '...'
2. Click on '...'
3. See error

**Expected Behavior:**
What should happen

**Actual Behavior:**
What actually happens

**Environment:**
- OS: Windows 11 / Linux / macOS
- .NET Version: 10.0
- Docker Version: 24.0.0
- Commandarr Version: 1.0.0

**Logs:**
```
[paste relevant logs]
```
```

### Suggesting Enhancements

Enhancement suggestions are tracked as GitHub issues. When creating an enhancement suggestion, include:

- **Clear title** - Descriptive summary
- **Detailed description** - Explain the enhancement
- **Use cases** - Why is this enhancement useful?
- **Alternatives** - Other solutions you've considered
- **Additional context** - Screenshots, mockups, examples

### Pull Requests

1. **Fork the repository** and create your branch from `master`
2. **Make your changes** following the coding standards
3. **Test your changes** thoroughly
4. **Update documentation** as needed
5. **Commit your changes** with clear commit messages
6. **Push to your fork** and submit a pull request

## Development Setup

### Prerequisites

- .NET 10.0 SDK or later
- Node.js 18+ (for frontend)
- Docker (optional)
- Git
- IDE: Visual Studio 2022, Rider, or VS Code

### Clone and Build

```bash
# Clone your fork
git clone https://github.com/YOUR_USERNAME/commandarr.git
cd commandarr

# Add upstream remote
git remote add upstream https://github.com/ORIGINAL_OWNER/commandarr.git

# Install frontend dependencies
cd src/Commandarr.WebUI/ClientApp
npm install
cd ../../..

# Restore .NET packages
dotnet restore

# Build
dotnet build

# Run tests (when implemented)
dotnet test
```

### Project Structure

```
Commandarr/
├── src/
│   ├── Commandarr.Core/           # Domain models and interfaces
│   ├── Commandarr.Infrastructure/ # External integrations
│   ├── Commandarr.WebUI/          # Web application
│   ├── Commandarr.Workers/        # Background workers
│   └── Commandarr.Host/           # Process orchestrator
├── tests/                          # Test projects (future)
└── docs/                           # Additional documentation (future)
```

## Coding Standards

### C# Code Style

- **Naming:**
  - PascalCase for classes, methods, properties
  - camelCase for parameters, local variables
  - Prefix interfaces with `I` (e.g., `ITorrentProcessor`)

- **Formatting:**
  - 4 spaces for indentation
  - Opening braces on new line (Allman style)
  - One statement per line

- **Best Practices:**
  - Use async/await for I/O operations
  - Prefer LINQ over loops where appropriate
  - Use dependency injection
  - Add XML documentation comments for public APIs
  - Follow SOLID principles

**Example:**
```csharp
/// <summary>
/// Processes torrents for a specific category.
/// </summary>
/// <param name="category">The category to process</param>
/// <param name="cancellationToken">Cancellation token</param>
/// <returns>Processing statistics</returns>
public async Task<TorrentProcessingStats> ProcessTorrentsAsync(
    string category,
    CancellationToken cancellationToken = default)
{
    // Implementation
}
```

### React/JavaScript Code Style

- **Naming:**
  - PascalCase for components (e.g., `Dashboard.js`)
  - camelCase for functions and variables

- **Formatting:**
  - 2 spaces for indentation
  - Use functional components with hooks
  - Destructure props

- **Best Practices:**
  - Use `async/await` for API calls
  - Handle loading and error states
  - Clean up effects with return functions
  - Use meaningful component and variable names

**Example:**
```javascript
function Dashboard({ status }) {
  const [stats, setStats] = useState(null);

  useEffect(() => {
    fetchStats();
  }, []);

  const fetchStats = async () => {
    // Implementation
  };

  return (
    <div>
      {/* JSX */}
    </div>
  );
}
```

## Commit Message Guidelines

Follow [Conventional Commits](https://www.conventionalcommits.org/):

### Format
```
<type>(<scope>): <subject>

<body>

<footer>
```

### Types
- `feat`: New feature
- `fix`: Bug fix
- `docs`: Documentation changes
- `style`: Formatting, missing semicolons, etc.
- `refactor`: Code refactoring
- `test`: Adding tests
- `chore`: Maintenance tasks
- `perf`: Performance improvements

### Examples

```
feat(api): add endpoint for torrent statistics

Implemented /api/torrents/stats endpoint that returns detailed
statistics about torrent processing including counts, states,
and historical data.

Closes #123
```

```
fix(seeding): correct H&R protection logic for tracker rules

The tracker-specific Hit & Run protection was not properly
checking seeding time. Updated to use correct time calculation.

Fixes #456
```

## Testing

### Writing Tests (When Implementing)

```csharp
[Fact]
public async Task ProcessTorrentsAsync_WithValidCategory_ReturnsStats()
{
    // Arrange
    var processor = new TorrentProcessor(/* dependencies */);

    // Act
    var result = await processor.ProcessTorrentsAsync("movies");

    // Assert
    Assert.NotNull(result);
    Assert.True(result.TorrentsProcessed > 0);
}
```

### Running Tests

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/Commandarr.Core.Tests

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

## Pull Request Process

1. **Update documentation** if adding features
2. **Update README.md** if changing setup/usage
3. **Add tests** for new functionality
4. **Ensure all tests pass** locally
5. **Update CHANGELOG.md** (if exists)
6. **Fill out PR template** completely

### PR Checklist

- [ ] Code follows project style guidelines
- [ ] Self-review completed
- [ ] Comments added for complex code
- [ ] Documentation updated
- [ ] Tests added/updated
- [ ] All tests pass locally
- [ ] No new warnings introduced
- [ ] Commit messages follow conventions

## Documentation

### Code Documentation

- Add XML comments for all public APIs
- Include parameter descriptions
- Document exceptions thrown
- Provide usage examples for complex methods

### User Documentation

- Update README.md for user-facing changes
- Update DOCKER.md for deployment changes
- Add guides to docs/ folder for major features

## Release Process (Maintainers)

1. Update version in project files
2. Update CHANGELOG.md
3. Create release branch
4. Tag release: `git tag v1.0.0`
5. Build Docker images
6. Publish to Docker Hub
7. Create GitHub release with notes

## Questions?

- Open an issue for general questions
- Join discussions for feature proposals
- Check existing issues and PRs first

## Recognition

Contributors will be recognized in:
- README.md contributors section
- Release notes
- Project documentation

Thank you for contributing to Commandarr! 🚀
