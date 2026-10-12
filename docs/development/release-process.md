# Release Process

Torrentarr releases are prepared and published by **Create a Release** in
`.github/workflows/release.yml`. Version bumps and changelog updates are signed
commits pushed directly to `master`, following qBitrr's release approach.

## Starting a release

Run **Actions → Create a Release → Run workflow**, selecting `patch`, `minor`,
`major`, or `build` (default: `patch`). Preparation always checks out `master`.

Alternatively, push a commit to `master` whose message starts with `[patch]`,
`[minor]`, `[major]`, or `[build]`. Other pushes do not prepare a release.
Pushing a tag alone does not trigger this workflow.

The workflow uses `.bumpversion.cfg` to update the release version in
`Directory.Build.props`, Dockerfile, and documentation, and the numeric config
version in the config models and example. It also synchronizes assembly and file
versions. Do not manually bump these fields or create a tag before running it.

Torrentarr keeps its major version one ahead of qBitrr's; minor and patch versions
are independent. Confirm that policy before selecting a major bump. Versions use
`MAJOR.MINOR.PATCH-BUILD`; patch/minor/major bumps reset the build segment to 1,
while a build release increments it.

## Credentials and branch rules

Configure these repository Actions secrets:

- `PAT`: a token with repository contents write access, belonging to an account
  permitted to push directly to `master` under the repository's branch rules.
- `GPG_PRIVATE_KEY`: that account's signing key, with its public key registered
  on GitHub.
- `PASSPHRASE`: the signing key's passphrase, if the key is protected.
- `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`: Docker Hub publishing credentials.

Release automation does not open classification PRs. Ordinary PR classification
is unchanged. If branch rules require all updates through PRs, configure a
permitted release account before running this workflow. Missing credentials,
signing errors, conflicts, and rejected pushes stop the release; it does not
force-push or bypass commit hooks.

## Release stages

1. Bump version fields and push a signed `[skip ci]` version commit.
2. Capture its full SHA and create a GitHub release draft targeting that commit.
3. Build the WebUI, standalone Host and Worker binaries, and Docker images from
   that exact SHA. Run the Linux standalone smoke check and upload binaries.
4. Generate changelog notes through the captured revision, then push a signed
   `[skip ci]` changelog commit rebased onto current `master`. Conflicts fail.
5. Read the committed changelog and publish the draft only after binaries,
   Docker images, and changelog jobs succeed.

Release runs are serialized. A failure can leave a bumped version, draft release,
or already-pushed Docker images; inspect the failed run before retrying.
The release workflow does not run the full unit-test suites: verify the regular
build checks before releasing.

## Distribution

Docker images are published to Docker Hub and GHCR for `linux/amd64` and
`linux/arm64`:

| Tag | Updated by |
| --- | --- |
| `latest` | Every release |
| `stable` | Patch, minor, and major releases |
| `vX.Y.Z-N` and `X.Y.Z-N` | That exact release |
| `nightly` | The separate nightly workflow |

GitHub Releases contain standalone Host and Worker binaries for Linux x64/ARM64,
Windows x64, and macOS x64/ARM64. Torrentarr does not publish to PyPI.

The weekly workflow runs Monday at 06:00 UTC and can also be dispatched manually.
It merges eligible green Dependabot PRs and dispatches a build release when the
unreleased range passes its dependency/automation checks. Manual runs must select
`dispatch_build_release=true` to request a release.

## Release checklist

Before releasing:

- Run `dotnet test --filter "Category!=Live"` and `npx vitest run` in `webui/`.
- Confirm required build checks pass and no critical issues remain.
- Update documentation and regenerate OpenAPI when API routes change.
- Confirm release credentials and direct-push branch permissions.

After releasing:

- Verify the GitHub release, notes, and binary downloads.
- Pull the exact Docker version and check its reported version.
- Verify documentation deployment and monitor reported issues.

For a hotfix, merge the tested fix into `master` and dispatch a patch release.
Users can roll back by downloading an earlier release or selecting its exact
Docker tag; do not overwrite published version tags.

## Regenerating the OpenAPI spec

The documentation site embeds an interactive Swagger UI that loads the API spec from `docs/assets/openapi.json`. When you add or change WebUI API endpoints, regenerate this file so the published docs stay in sync:

1. **From qBitrr baseline (recommended for parity):** `python3 scripts/generate-openapi-from-qbitrr.py` — merges qBitrr latest `master` OpenAPI with Torrentarr extension paths by default, then edit any new route details as needed. Set `QBITRR_OPENAPI_REF` if you need to pin a specific upstream ref.
2. **From running Host (Swashbuckle export):** from the repository root, set the export environment variable and run the export test:
   - **Windows (PowerShell):** `$env:TORRENTARR_EXPORT_OPENAPI='1'; dotnet test tests/Torrentarr.Host.Tests --filter "FullyQualifiedName~ExportOpenApiSpec"`
   - **Linux/macOS:** `TORRENTARR_EXPORT_OPENAPI=1 dotnet test tests/Torrentarr.Host.Tests --filter "FullyQualifiedName~ExportOpenApiSpec"`
3. Run `bash scripts/check-openapi-drift.sh` to verify all qBitrr paths are covered.
4. Commit the updated `docs/assets/openapi.json` if it changed.

Include this step in your release checklist when you have modified API routes.

## Related Documentation

- [Contributing](contributing.md) - Contribution guidelines
- [Development Guide](index.md) - Development setup
- [GitHub Actions Workflows](https://github.com/Feramance/Torrentarr/tree/master/.github/workflows) - CI/CD configuration
