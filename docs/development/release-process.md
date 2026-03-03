# Release Process

Torrentarr uses automated releases via GitHub Actions. This document describes the release workflow for maintainers.

## Release Workflow

### 1. Version Bumping

Torrentarr uses **version in .NET project files** (e.g. `Directory.Build.props` or the main Host project). Update the version there, then create a tag:

```bash
# Example: set Version to 5.6.0 in project file, then:
git tag -a v5.6.0 -m "Release v5.6.0"
git push origin v5.6.0
```

**What to update:**
- `Directory.Build.props` or main project `Version` / `AssemblyVersion` / `FileVersion`
- Changelog / release notes as needed

### 2. Changelog Generation

Torrentarr uses **gren** (GitHub Release Notes generator):

```bash
# Generate release notes from commits
gren release --override

# Or manually edit CHANGELOG.md
```

**Changelog format:**

```markdown
## [5.6.0] - 2024-12-09

### Added
- New feature X
- New feature Y

### Changed
- Updated behavior of Z
- Improved performance of W

### Fixed
- Bug fix A
- Bug fix B

### Security
- Security fix C
```

### 3. Create Release

#### Option A: Automated (Recommended)

```bash
# 1. Update version in project file(s)
# 2. Push to master, then create and push tag
git tag -a v5.6.0 -m "Release v5.6.0"
git push origin v5.6.0

# GitHub Actions automatically:
#    - Builds .NET and WebUI
#    - Runs tests
#    - Builds Docker image
#    - Pushes to Docker Hub
#    - Creates GitHub Release
```

#### Option B: Manual

```bash
# 1. Create tag
git tag -a v5.6.0 -m "Release v5.6.0"
git push origin v5.6.0

# 2. Build (see build.sh / build.bat or dotnet build + webui build)
./build.sh   # or build.bat on Windows

# 3. Build Docker image
docker build -t feramance/torrentarr:5.6.0 .
docker build -t feramance/torrentarr:latest .

# 4. Push to Docker Hub
docker push feramance/torrentarr:5.6.0
docker push feramance/torrentarr:latest

# 5. Create GitHub Release manually and attach binaries if desired
```

## Release Types

### Patch Release (5.5.4 → 5.5.5)

**When:** Bug fixes only, no new features

**Process:**
1. Merge bug fix PRs to `master`
2. Update version in project file(s)
3. Tag and push

**Example commits:**
- `fix(radarr): resolve import path issue`
- `fix(webui): correct API token validation`

### Minor Release (5.5.5 → 5.6.0)

**When:** New features, backward-compatible changes

**Process:**
1. Merge feature PRs to `master`
2. Update version and documentation
3. Tag and push

**Example commits:**
- `feat(lidarr): add Lidarr v2.0 support`
- `feat(webui): add dark mode toggle`

### Major Release (5.6.0 → 6.0.0)

**When:** Breaking changes, major features

**Process:**
1. Create `v6-dev` branch for development
2. Merge all v6 features
3. Update documentation
4. Test thoroughly
5. Merge to `master`
6. Update version and tag
7. Write migration guide

**Example commits:**
- `feat!: replace SQLite with PostgreSQL`
- `refactor!: new configuration schema`

## CI/CD Pipelines

### Release Workflow

**File:** `.github/workflows/release.yml` (or equivalent)

**Triggers:**
- Push tags matching `v*.*.*`

**Steps:**
1. Checkout code
2. Set up .NET (e.g. dotnet-version: '10.0.x')
3. Set up Node for WebUI build
4. Restore and build .NET
5. Run tests (`dotnet test --filter "Category!=Live"`)
6. Build WebUI (`cd webui && npm ci && npm run build`)
7. Build Docker image (multi-platform: amd64, arm64)
8. Push to Docker Hub with tags:
   - `feramance/torrentarr:5.6.0`
   - `feramance/torrentarr:5.6`
   - `feramance/torrentarr:5`
   - `feramance/torrentarr:latest`
9. Create GitHub Release with changelog

### Nightly Builds

**File:** `.github/workflows/nightly.yml`

**Trigger:** Daily at 00:00 UTC

**Output:** `feramance/torrentarr:nightly`

**Purpose:** Test bleeding-edge changes

## Version Numbering

Torrentarr follows **Semantic Versioning** (semver):

```
MAJOR.MINOR.PATCH

5.6.2
│ │ │
│ │ └─ Patch: Bug fixes, security fixes
│ └─── Minor: New features, backward-compatible
└───── Major: Breaking changes
```

### Pre-release Versions

```
5.6.0-alpha.1  # Alpha release
5.6.0-beta.1   # Beta release
5.6.0-rc.1     # Release candidate
```

**Create pre-release:**

```bash
# Tag manually
git tag v5.6.0-rc.1
git push origin v5.6.0-rc.1
```

## Docker Image Tags

### Tag Strategy

| Tag | Description | Example |
|-----|-------------|---------|
| `latest` | Latest stable release | `5.6.2` |
| `nightly` | Daily build from master | Today's date |
| `X.Y.Z` | Specific version | `5.6.2` |
| `X.Y` | Latest patch in minor | `5.6` → `5.6.2` |
| `X` | Latest minor in major | `5` → `5.6.2` |

### Multi-Platform Builds

Torrentarr supports multiple architectures:

- `linux/amd64` - x86_64 (most common)
- `linux/arm64` - ARM 64-bit (Raspberry Pi 4, Apple Silicon)
- `linux/arm/v7` - ARM 32-bit (older Raspberry Pi)

**Build command:**

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64,linux/arm/v7 \
  -t feramance/torrentarr:5.6.0 \
  --push \
  .
```

## PyPI Publishing

Torrentarr does **not** publish to PyPI; it is a .NET application. Distribution is via GitHub Releases (binaries), Docker Hub, and optionally NuGet (e.g. as a dotnet global tool). Skip any PyPI-related steps.

## Post-Release

### 1. Verify Release

```bash
# Check Docker Hub
docker pull feramance/torrentarr:5.6.0

# Check GitHub Release
# Visit: https://github.com/Feramance/Torrentarr/releases
```

### 2. Update Documentation

Ensure docs are deployed:

- GitHub Pages: https://feramance.github.io/Torrentarr/
- Docker Hub: Update description if needed

### 3. Announce Release

- GitHub Discussions: Post announcement
- Discord/Community: Share release notes
- Reddit: Post in relevant subreddits (r/radarr, r/sonarr)

### 4. Monitor Issues

Watch for issues related to new release:
- GitHub Issues
- Discord messages
- Reddit comments

## Hotfix Process

For critical bugs in production:

**1. Create hotfix branch:**

```bash
git checkout -b hotfix/5.6.1 v5.6.0
```

**2. Fix the bug:**

```bash
# Make minimal changes
git commit -m "fix(critical): resolve data loss issue"
```

**3. Test thoroughly**

**4. Release:**

```bash
# Update version to 5.6.1 in project file(s)
git tag -a v5.6.1 -m "Hotfix v5.6.1"
git push origin hotfix/5.6.1 --tags
```

**5. Merge back:**

```bash
# Merge to master
git checkout master
git merge --no-ff hotfix/5.6.1
git push origin master
```

## Release Checklist

Before releasing:

- [ ] All tests pass (`dotnet test --filter "Category!=Live"`, WebUI tests if applicable)
- [ ] Documentation updated
- [ ] Changelog generated
- [ ] Version bumped in project file(s)
- [ ] Tag created and pushed
- [ ] No open critical issues

After releasing:

- [ ] Docker images pushed
- [ ] GitHub Release created (with binaries if applicable)
- [ ] Documentation deployed
- [ ] Announcement posted
- [ ] Monitor for issues

## Rollback Procedure

If a release has critical issues:

**1. Pull Docker images:**

```bash
# Users can rollback
docker pull feramance/torrentarr:5.5.5
```

**2. Yank PyPI package:** N/A — Torrentarr does not publish to PyPI.

**3. Create hotfix release:**

```bash
# Fix issue and release 5.6.1
```

## Related Documentation

- [Contributing](contributing.md) - Contribution guidelines
- [Development Guide](index.md) - Development setup
- [GitHub Actions Workflows](https://github.com/Feramance/Torrentarr/tree/master/.github/workflows) - CI/CD configuration
