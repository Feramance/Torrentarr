# Contributing to Torrentarr

Thank you for your interest in contributing to Torrentarr! This document provides guidelines and instructions for contributing.

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
````markdown
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
- Torrentarr Version: 1.0.0

**Logs:**
```
[paste relevant logs]
```
````

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

Use Node.js 22.22.2+ (22.x), 24.15+, or 26+ for frontend development. Follow the [Development guide](https://feramance.github.io/Torrentarr/development/) for the full setup and build commands.

Install the hooks with `pre-commit install`. Run `dotnet restore` and `npm ci --prefix webui` first so the hooks can run both builds. Use `pre-commit run --all-files` to check the full checkout. Keep commit hooks enabled.

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

## Pull Request Checklist

- Follow the [code style](https://feramance.github.io/Torrentarr/development/code-style/) and [testing guidance](https://feramance.github.io/Torrentarr/development/testing/). Add or update tests for behavior changes and include relevant results in the PR.
- Describe the motivation, link related issues, and include screenshots for visual changes.
- Update setup, deployment, or feature docs when the corresponding user workflows change.
- Regenerate the [OpenAPI spec](https://feramance.github.io/Torrentarr/development/release-process/#regenerating-the-openapi-spec) when changing API endpoints.
- Follow [Manage verification](https://feramance.github.io/Torrentarr/development/maintenance-verification/) when changing maintenance behavior or controls.

Maintainers should follow the [Release Process](https://feramance.github.io/Torrentarr/development/release-process/).

## Questions?

- Open an issue for general questions
- Join discussions for feature proposals
- Check existing issues and PRs first

## Recognition

Contributors will be recognized in:
- README.md contributors section
- Release notes
- Project documentation

Thank you for contributing to Torrentarr! 🚀
