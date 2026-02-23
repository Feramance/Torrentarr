# Implementation Plan: Fix Workflows & Add GitHub Infrastructure

## Status: Completed

All workflows are now working correctly. Release v5.9.1 was published successfully.

---

## Summary of Changes

### 1. Version Management (bump2version)
- ✅ `.bumpversion.cfg` - Config for bump2version
- ✅ `Directory.Build.props` - Central version for all .NET projects
- ✅ `global.json` - Pin .NET SDK to 10.0.x

### 2. GitHub Workflows
- ✅ `build.yml` - Fixed action versions, added caching, use ghcr.io only
- ✅ `codeql.yml` - Security analysis for C# (with continue-on-error)
- ✅ `release.yml` - bump2version, changelog, build, publish to ghcr.io
- ✅ `pull_requests.yml` - Build + test matrix for PRs
- ✅ `dependabot-auto-merge.yml` - Auto-merge dependabot PRs

### 3. GitHub Config Files
- ✅ `dependabot.yml` - Enable dependabot for nuget, npm, github-actions, docker
- ✅ `pull_request_template.md` - PR template
- ✅ `FUNDING.yml` - Sponsorship links
- ✅ `ISSUE_TEMPLATE/bug_report.yml` - Bug report form
- ✅ `ISSUE_TEMPLATE/feature_request.yml` - Feature request form

### 4. Other Files
- ✅ `CHANGELOG.md` - Auto-generated during releases
- ✅ `.pre-commit-config.yaml` - Hooks for code quality
- ✅ `Dockerfile` - Fixed npm timeout settings
- ✅ `webui/src/icons/logov2-clean.svg` - Correct qBitrr logo
- ✅ Test fix - ConfigVersion test now uses regex match

---

## Release Workflow

The release workflow is triggered by:
1. Commit message starting with `[patch]`, `[minor]`, or `[major]`
2. Manual workflow dispatch

### Flow:
1. **prepare_release** - Runs bump2version, commits version bump
2. **create_release** - Creates draft GitHub release
3. **build_and_push_docker** - Builds multi-platform image (amd64 + arm64), pushes to ghcr.io
4. **changelog** - Generates changelog from commits, commits to repo
5. **publish_release** - Publishes the GitHub release

---

## Known Issues / Notes

1. **CodeQL** - Requires GitHub Advanced Security (paid feature). Added `continue-on-error: true` so it doesn't block CI.

2. **Docker build time** - Takes ~11-15 minutes due to multi-platform (amd64 + arm64) builds. This is expected behavior.

3. **npm ci timeouts** - Fixed with `NPM_CONFIG_FETCH_TIMEOUT` and cache mount in Dockerfile.
