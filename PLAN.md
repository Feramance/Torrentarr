# Implementation Plan: Fix Workflows & Add GitHub Infrastructure

## Status: Testing patch release v5.9.3 (attempt 3)

### Remaining Issues
- **CodeQL**: Requires GitHub Advanced Security (paid feature). Added `continue-on-error: true` so it doesn't block CI.
- **Dependabot PRs**: Using old workflow file (before `cache: true` fix). Will be resolved when PRs are closed/rebased.
- **Docker build**: Takes 15+ min due to multi-platform (amd64 + arm64) builds. This is expected behavior.

### Release Workflow Fixes
1. Fixed bump2version search pattern for TorrentarrConfig.cs
2. Escaped braces in search pattern (`{{ get; set; }}`)

---

## 1. Version Management (bump2version)

- [x] Create `.bumpversion.cfg` - Config for bump2version
- [x] Create `Directory.Build.props` - Central version for all .NET projects
- [x] Create `global.json` - Pin .NET SDK to 10.0.x

**Version:** `5.9.1` (matches qBitrr config compatibility)

**Files updated by bump2version:**
- `Directory.Build.props` - `<Version>x.x.x</Version>`
- `src/Torrentarr.Core/Configuration/TorrentarrConfig.cs` - `ConfigVersion = "x.x.x"`

---

## 2. Fix WebUI Logo

- [x] Create `webui/src/icons/logov2-clean.svg` (copy from public/)
- [x] Modify `webui/src/App.tsx` - Fix logo import

---

## 3. GitHub Workflows

- [x] Modify `.github/workflows/build.yml` - Fix action versions, add NuGet caching, use ghcr.io only
- [x] Create `.github/workflows/codeql.yml` - Security analysis for C#
- [x] Create `.github/workflows/release.yml` - bump2version, changelog, build, publish to ghcr.io
- [x] Create `.github/workflows/pull_requests.yml` - Build + test matrix for PRs
- [x] Create `.github/workflows/dependabot-auto-merge.yml` - Auto-merge dependabot PRs

---

## 4. GitHub Config Files

- [x] Create `.github/dependabot.yml` - Enable dependabot for nuget, npm, github-actions, docker
- [x] Create `.github/pull_request_template.md` - PR template
- [x] Create `.github/FUNDING.yml` - Sponsorship links
- [x] Create `.github/ISSUE_TEMPLATE/bug_report.yml` - Bug report form
- [x] Create `.github/ISSUE_TEMPLATE/feature_request.yml` - Feature request form

---

## 5. Pre-commit Config

- [x] Create `.pre-commit-config.yaml` - Hooks for code quality

---

## 6. Supporting Files

- [x] Create `CHANGELOG.md` - Start with v5.9.1 entry

---

## 7. Release Workflow Flow

```
1. Trigger: [patch], [minor], or [major] in commit message OR workflow_dispatch
2. Determine release type
3. Run bump2version {patch|minor|major}
4. Generate changelog entry from commits since last tag
5. Prepend changelog entry to CHANGELOG.md
6. Commit version bump + changelog (GPG signed)
7. Build .NET solution (Release)
8. Build React frontend
9. Build Docker image → push to ghcr.io
10. Build binaries for Windows/macOS/Linux (optional)
11. Create/publish GitHub release with notes from changelog
```

---

## Files Summary

| Status | File |
|--------|------|
| [x] | `.bumpversion.cfg` |
| [x] | `Directory.Build.props` |
| [x] | `global.json` |
| [x] | `CHANGELOG.md` |
| [x] | `.pre-commit-config.yaml` |
| [x] | `.github/dependabot.yml` |
| [x] | `.github/pull_request_template.md` |
| [x] | `.github/FUNDING.yml` |
| [x] | `.github/workflows/codeql.yml` |
| [x] | `.github/workflows/release.yml` |
| [x] | `.github/workflows/pull_requests.yml` |
| [x] | `.github/workflows/dependabot-auto-merge.yml` |
| [x] | `.github/ISSUE_TEMPLATE/bug_report.yml` |
| [x] | `.github/ISSUE_TEMPLATE/feature_request.yml` |
| [x] | `webui/src/icons/logov2-clean.svg` |
| [x] | `.github/workflows/build.yml` (modify) |
| [x] | `webui/src/App.tsx` (modify) |
