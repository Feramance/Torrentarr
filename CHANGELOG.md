# Changelog

## v6.0.0 (2026-03-03)

### Features
- [major] First initial release of Torrentarr ([cad60cd](https://github.com/Feramance/Torrentarr/commit/cad60cd977975b99dedadb86123df5fd908d4f55))
- Add Docker Hub pulls badge to README and docs ([df9ac31](https://github.com/Feramance/Torrentarr/commit/df9ac312a380534670cf34d03dac84e5c20e386f))
- Add pre-commit auto-fix workflow; update dockerhub-description, docs workflow, environment docs ([fe96ea3](https://github.com/Feramance/Torrentarr/commit/fe96ea33df40490f2cdfac463191ba4647163468))
- feat: complete qBitrr parity — all gaps implemented, tests, and docs updated ([f54c7cb](https://github.com/Feramance/Torrentarr/commit/f54c7cb1b0a7a1abc589c4c94c1eb9c30de83093))

### Bug Fixes
- fix: Lidarr empty state and Host API test parallelization ([dd67652](https://github.com/Feramance/Torrentarr/commit/dd676523246396bf941751f300379d2a2cc702aa))
- Fix ESLint: hook deps, Fast Refresh, TanStack Table incompatible-library ([04b6d9a](https://github.com/Feramance/Torrentarr/commit/04b6d9a167e8e388b1e9f41e84a68f39f9983305))
- fix: 10 bugs found in deep codebase review ([04250aa](https://github.com/Feramance/Torrentarr/commit/04250aa9c39086206378caf12c033cd5eb6351f2))
- fix: address 10 bugs found in deep codebase review ([ab4b510](https://github.com/Feramance/Torrentarr/commit/ab4b5105d473cd1a5a551ffa8506e0c3e1b2d5c9))
- fix: remove CS8620 warnings and add missing frontend API client functions ([d619864](https://github.com/Feramance/Torrentarr/commit/d6198640dd517f1537c488fa1b290ef6abbdbeb5))
- fix: implement tagless free space inline and remove dead IFreeSpaceService ([4e2133c](https://github.com/Feramance/Torrentarr/commit/4e2133cceb9380b8cb2e4b1413370fe5d9e066a1))
- fix: wire restart endpoints, meta force-refresh, and CA2017 log warnings ([f0852b2](https://github.com/Feramance/Torrentarr/commit/f0852b267972b96e1ebbec19e09d3dbb3a92f48a))

### Documentation
- docs: add Git commit rule to not use --no-verify in AGENTS.md and CLAUDE.md ([a46fffd](https://github.com/Feramance/Torrentarr/commit/a46fffda6ef5b669d9730a83251097dbdaddca8b))
- docs: clarify DB is torrentarr.db not qbitrr.db (config/schema compatible) ([bac3e0f](https://github.com/Feramance/Torrentarr/commit/bac3e0f5fcea983d9d8668085bd83b10f80aa4d5))
- docs: fix broken config-editor link in configuration/webui.md ([8176c0d](https://github.com/Feramance/Torrentarr/commit/8176c0d45f9578dc687645297613918ba6dd1f56))
- docs: replace remaining Torrentarr.db with qbitrr.db ([0ec5d78](https://github.com/Feramance/Torrentarr/commit/0ec5d782e35401e592d299241cd40473ac607e9d))
- docs: complete Torrentarr vs qBitrr plan (remaining items) ([77f6939](https://github.com/Feramance/Torrentarr/commit/77f693968e9be4d0b39e6b70d0f0bd12bb12f699))
- docs: align documentation with Torrentarr (C#) vs qBitrr ([42b2566](https://github.com/Feramance/Torrentarr/commit/42b25666cc51eb21fa5d9c2f5d95b3d66df4b935))
- docs: fix MkDocs build errors and warnings for GitHub Pages ([0597580](https://github.com/Feramance/Torrentarr/commit/0597580d2b6cfdc7c54f77a302ed41ef26cd5054))

### Maintenance
- fix(workflow): retry git push in pre-commit-autofix on transient failures ([043780f](https://github.com/Feramance/Torrentarr/commit/043780f224e23f7db1cab15cbd17a32b5949390d))
- fix(workflow): fix YAML syntax in pre-commit-autofix commit message ([0e5e915](https://github.com/Feramance/Torrentarr/commit/0e5e915b1f1b383ff0a1d5c9ef36ab64a1beeb8a))
- ci: add Docker Hub push and README sync workflow ([c63f58f](https://github.com/Feramance/Torrentarr/commit/c63f58f84898e5c05355e1a12abcfcaf5c63c357))
- chore: use torrentarr.db as database filename everywhere ([929be37](https://github.com/Feramance/Torrentarr/commit/929be3779f9610767830c5333910720b1ae0d6f5))
- fix(webui): ConfigVersionWarning.currentVersion as string, installation_type union ([c491017](https://github.com/Feramance/Torrentarr/commit/c491017670ae3b51bfededb6e72a3cdba2c55cf7))
- ci: add MkDocs workflow for GitHub Pages (docs build and deploy) ([2504054](https://github.com/Feramance/Torrentarr/commit/2504054b17755eab19d66a6eefa41a6b0fb14626))
- chore: apply pre-commit (line endings, format, exclude mkdocs.yml and webui from check-yaml/pretty-json) ([206a73a](https://github.com/Feramance/Torrentarr/commit/206a73abba75d15efc4e300409d73cfe3bf52de7))
- chore: sync updates, ConfigView lint fix, pre-commit install, docker-compose ([c39c839](https://github.com/Feramance/Torrentarr/commit/c39c83972daf99b03530bbd9e915796634595da8))
- chore: stop tracking wwwroot; build output only ([f6cb7f3](https://github.com/Feramance/Torrentarr/commit/f6cb7f3fc7e6d9bd63c58b543d8293253f4a64a9))
- Move torrent handling summary to top of Arr/qBit config modals (match qBitrr position) ([38f3058](https://github.com/Feramance/Torrentarr/commit/38f305897e9cdeaac0aac0a8e6d7500c2eea1af3))
- WebUI parity with qBitrr: API tokens, duration input, torrent summary, ErrorBoundary, AlreadyUpToDateModal, branding, a11y ([318e7de](https://github.com/Feramance/Torrentarr/commit/318e7de92d293611855b991155946387c294b89d))
- ci: add nightly and pre-commit workflows, extend CodeQL and Dependabot ([54cbee3](https://github.com/Feramance/Torrentarr/commit/54cbee3196e4fcd01c1ddbcae6fa64053cad4608))
- Bump Microsoft.AspNetCore.Mvc.Testing from 9.0.0 to 10.0.3 ([37a07b9](https://github.com/Feramance/Torrentarr/commit/37a07b9e9e179240da7903d561698192a5af0b65))
- Bump coverlet.collector from 6.0.2 to 8.0.0 ([2304c37](https://github.com/Feramance/Torrentarr/commit/2304c3724488ce400c4d57a3b655ed42f13be543))
- chore(deps-dev): bump @vitest/coverage-v8 from 3.2.4 to 4.0.18 in /webui ([0157baa](https://github.com/Feramance/Torrentarr/commit/0157baa714e30c24a9e3e5b1a3017870cda07271))
- Bump Microsoft.Extensions.Configuration and Microsoft.Extensions.Configuration.Binder (#16) ([f8a0baf](https://github.com/Feramance/Torrentarr/commit/f8a0bafa810f0d5be11a409bad9a89618986d599))
- chore(deps-dev): bump jsdom from 26.1.0 to 28.1.0 in /webui (#3) ([c12656b](https://github.com/Feramance/Torrentarr/commit/c12656b11b2e80dcba1c08f75702290883527dfa))
- chore(deps): bump actions/upload-artifact from 4 to 7 ([e556d9d](https://github.com/Feramance/Torrentarr/commit/e556d9df8e41e8274658c318cdcaafc08d724438))
- Remove unessary files ([3e8fca7](https://github.com/Feramance/Torrentarr/commit/3e8fca722cefc22278e68586279cbdc4a6b3c505))
- fix(ci): use ConfigVersion 5.9.2 in Host test config so GET /web/config returns flat shape ([c8ea19d](https://github.com/Feramance/Torrentarr/commit/c8ea19d9c48051a565eb07a9464438748cafc710))
- qBitrr parity: implement remaining 12 gaps ([411823b](https://github.com/Feramance/Torrentarr/commit/411823bab2d1c961760d296dc28d3250d8f6d213))
- chore(deps-dev): bump globals from 17.3.0 to 17.4.0 in /webui (#21) ([4eb57ea](https://github.com/Feramance/Torrentarr/commit/4eb57ea087e9bd7bce436cfae960d0be200c686e))
- chore(deps-dev): bump eslint-plugin-react-refresh in /webui (#20) ([ec339bb](https://github.com/Feramance/Torrentarr/commit/ec339bbbe4f285f972e64e4fa1f0c8be5e111913))
- chore(deps): bump react-syntax-highlighter in /webui (#19) ([6b52ba2](https://github.com/Feramance/Torrentarr/commit/6b52ba2958e0ea4ff76af13e808717274a8119a4))
- chore(deps-dev): bump tailwindcss from 4.2.0 to 4.2.1 in /webui (#18) ([751b096](https://github.com/Feramance/Torrentarr/commit/751b096b1a2e7eb045c9f0b890e75f1ac8fd734d))
- test: add 24 tests for ArrSyncService, TorrentProcessor, and ArrView ([c8810e4](https://github.com/Feramance/Torrentarr/commit/c8810e41472fd300d9057039bb22305eac47063e))
- test: add 65 tests for Infrastructure services and frontend components ([71d3c35](https://github.com/Feramance/Torrentarr/commit/71d3c3597744435be37c6099ad02abf7625069a4))
- test: add remaining service and page view tests ([0fd8b4f](https://github.com/Feramance/Torrentarr/commit/0fd8b4f69d2e71b4cabfade62725ec0284c013ab))
- test: add 57 infrastructure tests for service logic and private methods ([ece418b](https://github.com/Feramance/Torrentarr/commit/ece418bbbea66dbad183ab2804f5e964d9bdd853))
- test: add 74 tests covering previously untested endpoints and helpers ([e039ec9](https://github.com/Feramance/Torrentarr/commit/e039ec9d28938bea6d0616f3008c1dc684391db2))
- Bump Microsoft.AspNetCore.Mvc.NewtonsoftJson from 9.0.0 to 10.0.3 ([c042ea1](https://github.com/Feramance/Torrentarr/commit/c042ea194e3b88b0105c4e50196c047035d22226))
- chore(deps): bump node from 22-alpine to 25-alpine ([d2d4c19](https://github.com/Feramance/Torrentarr/commit/d2d4c199d768713305fb7c993dfacd3303d34f13))

---

## v5.9.1 (2026-02-22)

### Features
- chore: Add project essentials for open source release ([c189dd6](https://github.com/Torrentarr/Torrentarr/commit/c189dd619ffa62d486bffcb841b69d8a03a4efbf))
- docs: Add comprehensive project summary ([7aad038](https://github.com/Torrentarr/Torrentarr/commit/7aad038bb90afec87b2b2b7d39a9c446c70c8cc3))
- feat: Add comprehensive Docker support with multi-stage builds ([5cde09b](https://github.com/Torrentarr/Torrentarr/commit/5cde09b4af54299617979c9dbf4287e854d942f7))
- feat: Add React frontend with dashboard and media management ([219acda](https://github.com/Torrentarr/Torrentarr/commit/219acdac8c3deb9411ca46aa76795c4523614c95))
- feat: Integrate services into Workers and add comprehensive WebUI API endpoints ([62cf74e](https://github.com/Torrentarr/Torrentarr/commit/62cf74e56608201561feff5af352df05803188d9))
- feat: Add Arr media, seeding, and free space management services ([b248e42](https://github.com/Torrentarr/Torrentarr/commit/b248e420490672f95b9a2ec4648c6bacb10cd6e6))
- feat: Integrate torrent processing services into Workers ([4469512](https://github.com/Torrentarr/Torrentarr/commit/4469512d43692ebfd7b07efde05e3ac75fa0c952))
- Implement database models, API clients, orchestrator, and workers ([7353c23](https://github.com/Torrentarr/Torrentarr/commit/7353c23844c36bb95555e62ac9f88f9868877f4a))
- Add comprehensive README with setup instructions and project overview ([6b9db04](https://github.com/Torrentarr/Torrentarr/commit/6b9db046f447f902f4287355abc5dbe314bddd29))
- Implement core infrastructure: config, database, API clients, WebUI ([18c4c64](https://github.com/Torrentarr/Torrentarr/commit/18c4c64eb399f032ec29a2e7621875e2450bdcff))

### Bug Fixes
- [patch] test: trigger patch release v5.9.5 ([3608984](https://github.com/Torrentarr/Torrentarr/commit/36089848f449b0b2b647fc091985299ed316d6ef))
- [patch] test: trigger patch release v5.9.3 (attempt 3) ([4653c5f](https://github.com/Torrentarr/Torrentarr/commit/4653c5f7b7f5a1f0f1272083e07af8691f754eae))
- [patch] test: trigger patch release v5.9.3 ([8da047c](https://github.com/Torrentarr/Torrentarr/commit/8da047c09feb3f8260b7c3be5c67cb10802a38a6))
- [patch] test: trigger patch release to verify workflows ([1339ec4](https://github.com/Torrentarr/Torrentarr/commit/1339ec40d9e759530322a7e0267ea60d8ad1dc41))
- [patch] docs: update PLAN.md with release workflow notes ([4a9b4a1](https://github.com/Torrentarr/Torrentarr/commit/4a9b4a1e9af69a1db3f4b2485033830038619233))
- [patch] docs: update PLAN.md with docker build notes ([f3e9f6b](https://github.com/Torrentarr/Torrentarr/commit/f3e9f6b3353ce0bb28588c0338ea5802102f29ac))
- [patch] fix(ci): fix release workflow and CodeQL ([0489ac0](https://github.com/Torrentarr/Torrentarr/commit/0489ac0cc6577b1062a0d11a481eea7638a2996e))

### Documentation
- docs: Update README with current implementation status ([d530c0c](https://github.com/Torrentarr/Torrentarr/commit/d530c0c3559990edba52399c8eca88e3af438c59))

### Refactoring
- refactor: unify qBit instances and add comprehensive test suite ([94d5cba](https://github.com/Torrentarr/Torrentarr/commit/94d5cba2ea14599d38710236a279b2ebd819e911))

### Maintenance
- fix(ci): add stash/pull/pop before changelog commit ([e0abf30](https://github.com/Torrentarr/Torrentarr/commit/e0abf30d7d4f089e8ab3580332a6bd4269ea2c2e))
- fix(ci): remove redundant pull step in changelog job ([cff5e9a](https://github.com/Torrentarr/Torrentarr/commit/cff5e9a94bf51cd469ba9ee89447bc204ce46470))
- fix(ci): fix bump2version config and add version sync step ([c73fec9](https://github.com/Torrentarr/Torrentarr/commit/c73fec9769b00a7a00aac0b3f64a0db6739a588a))
- fix(docker): add npm timeout settings and cache mount for faster builds ([30b3a5b](https://github.com/Torrentarr/Torrentarr/commit/30b3a5bf677978c644327912b4384c4f46023bcb))
- fix(ci): add git pull rebase before changelog commit ([752b62d](https://github.com/Torrentarr/Torrentarr/commit/752b62d2ec317100c41502b5027ade24a24cd354))
- fix(ci): update all version fields in bump2version config ([2ad487b](https://github.com/Torrentarr/Torrentarr/commit/2ad487b04e52b635cf428771e9931f8803be63ad))
- fix(ci): escape braces in bump2version config ([e54a7d0](https://github.com/Torrentarr/Torrentarr/commit/e54a7d0ae5753066b1d8347958bfaa94275bc8bc))
- fix(ci): fix bump2version search pattern for TorrentarrConfig.cs ([e1e47f8](https://github.com/Torrentarr/Torrentarr/commit/e1e47f8225f3132b9ee67f9eb28c0689a150f7cb))
- fix(ci): fix workflow failures ([c37cfcc](https://github.com/Torrentarr/Torrentarr/commit/c37cfccc47eb3d15ae15bc44e31a3330e59b60c9))
- chore(deps-dev): bump @tailwindcss/postcss in /webui (#8) ([c55ec66](https://github.com/Torrentarr/Torrentarr/commit/c55ec6681b2f7d1d86ceccbc3cdebd50316f07ac))
- chore(deps-dev): bump tailwindcss from 4.1.18 to 4.2.0 in /webui (#6) ([8a71c60](https://github.com/Torrentarr/Torrentarr/commit/8a71c60ae40711ddb2dfc6824fcd6442426f0fb8))
- chore(deps): bump react-hook-form from 7.71.1 to 7.71.2 in /webui (#4) ([5949ade](https://github.com/Torrentarr/Torrentarr/commit/5949ade09336b6750f67cd6c8095e38386783f26))
- chore: add GitHub workflows, bump2version, issue templates, and fix logo ([d53bf97](https://github.com/Torrentarr/Torrentarr/commit/d53bf9737596edce71c0c6f916865c542629d3b9))
- refactor(seeding): move HnR to tracker-only config, add state-based removal logic ([8eb4ce5](https://github.com/Torrentarr/Torrentarr/commit/8eb4ce5008067eeb9b976a1c7220d383ce4a585e))
- chore: remove stale Commandarr.* files from pre-rename scaffold ([11dfaeb](https://github.com/Torrentarr/Torrentarr/commit/11dfaeb0606ea24521c733b1c8bf367ca099d98b))
- fix(docker): fix content root and config path in runtime container ([1dcbb8e](https://github.com/Torrentarr/Torrentarr/commit/1dcbb8eff574a35e5ace2f450cd19acd9d7d27fc))
- fix(docker): fix Dockerfile to build correctly with Vite frontend ([ebb3a4a](https://github.com/Torrentarr/Torrentarr/commit/ebb3a4a010fb62451772d727b049da097cb3164a))
- fix(webui/logs): security, reliability, and UX improvements ([28a7e07](https://github.com/Torrentarr/Torrentarr/commit/28a7e07aa37b530d27ba650b6ffb91a264fa9d9f))
- Initial project structure with .NET solution and NuGet packages ([7b54c13](https://github.com/Torrentarr/Torrentarr/commit/7b54c13e6faa976716ba3b45268538bf7e691e92))

---

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## v5.9.1

### Features

- C# port of qBitrr with same config.toml format and SQLite schema; database file is `torrentarr.db` (not `qbitrr.db`)
- Multi-qBittorrent instance support for load balancing and VPN isolation
- Process-isolated architecture - WebUI stays online even if workers crash
- Hit and Run protection with tracker-based seeding rules
- Per-torrent free space management with auto-pause/resume
- Missing media search and quality upgrade automation
- Media validation with ffprobe integration
- Real-time WebUI with React dashboard

### Technical

- Built on .NET 10 and ASP.NET Core
- Entity Framework Core with SQLite (WAL mode)
- RestSharp for API clients (qBittorrent, Radarr, Sonarr, Lidarr)
- Tomlyn for TOML configuration parsing
- Serilog for structured logging
- React 18 + TypeScript frontend with Vite
