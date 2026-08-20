# Changelog

## v6.14.5-1 (2026-08-20)

### Bug Fixes
- fix: keep Arr timeouts in worker backoff (#390) ([fb0c981](https://github.com/Feramance/Torrentarr/commit/fb0c9810dda646c83477c19a1acd3d850217f554))
- fix: keep Arr timeouts in worker backoff (#390) ([5375472](https://github.com/Feramance/Torrentarr/commit/5375472f3916f40296bec4d8fdb5aa060517f603))

### Maintenance
- qBitrr 5.14.4-1 parity: stalledUP MaxSeedingTime clock and Sonarr HTTP skip (#389) ([4b0e910](https://github.com/Feramance/Torrentarr/commit/4b0e9100f667f0b795fb5115b836642881d688cd))

---

## v6.14.4-1 (2026-08-19)

Parity with qBitrr **v5.14.4-1** (Torrentarr schema **6.14.4**).

### Bug Fixes
- `MaxSeedingTime` is also met after Torrentarr has observed qBittorrent `stalledUP` for that many seconds (not `last_activity`). The first stalled loop never removes; leaving `stalledUP` or restarting Torrentarr resets the clock. Hit & Run still uses actual `seeding_time` / ratio and can block deletion of a long-stalled seed.
- Unmapped Arr HTTP errors (including 415 on Sonarr `GET /api/v3/episode`) skip that series instead of killing the worker. Transport failures abort remaining series. If every series episode fetch fails, sync is not treated as complete and the search loop backs off. SearchAgain episode prune skips when any series fetch fails so the library is not mass-deleted.
- [patch] qBitrr 5.14.3 parity: SearchByYear, ReSearch, WebUI schema/logs (#388) ([e5db230](https://github.com/Feramance/Torrentarr/commit/e5db2303108be89cdf2f8cb0c8eebce90e72d629))

---

## v6.14.3

Parity jump with qBitrr **v5.14.3-1** (Torrentarr schema **6.14.3**; no 6.13.x release).

### Features
- Readarr: `[Readarr-*]` config, API client, SQLite tables (including existing DBs), workers, and WebUI authors/books catalog
- `Settings.AutoUpdateChannel` (`latest` / `stable` / `nightly`); source/`dotnet run` builds never apply binaries
- Type-aware FileExtensionAllowlist defaults; Readarr ebook-only lists expand to include audiobooks

### Bug Fixes
- Retry qBit client init in Host and Workers instead of giving up permanently
- Tracker `-1`/unset no longer wipes positive Arr `SeedingMode` / CategorySeeding limits
- Isolate Lidarr/year-search candidate failures so one Arr type cannot stop the worker
- Parse fractional TOML durations (`1.5`, `1.5h`)
- Skip ffprobe for ebook/comic suffixes

### Documentation
- Pin parity matrix to qBitrr v5.14.3-1; keep 5.12.5–5.12.9 multi-instance rows as `partial`

## v6.12.5 (2026-08-17)

### Bug Fixes
- [patch] fix: Unraid/Docker operation and Radarr v6 docs (#387) ([966e791](https://github.com/Feramance/Torrentarr/commit/966e7916da6fd920b6c06761cfc667c6207ddcd5))

### Maintenance
- ci: add issue viability Cursor Automation for bug research and auto-fix PRs (#386) ([281e5c0](https://github.com/Feramance/Torrentarr/commit/281e5c08488a8ae773de125b173d28973c69a7a1))
- build(deps-dev): Bump typescript from 6.0.3 to 7.0.2 in /webui (#340) ([b98c06c](https://github.com/Feramance/Torrentarr/commit/b98c06cf34edaedd4e1955e7cbbe2a2da2292f3f))
- build(deps): Bump actions/setup-python from 6 to 7 (#360) ([2fa17b7](https://github.com/Feramance/Torrentarr/commit/2fa17b7e9b8c7381d55e8182d124a540c632f878))
- build(deps): Bump actions/setup-node from 6 to 7 (#341) ([e1a0625](https://github.com/Feramance/Torrentarr/commit/e1a06252860eba254e184e6cb02f82b954429b73))
- build(deps): Bump actions/setup-dotnet from 5.4.0 to 6.0.0 (#339) ([14ccb84](https://github.com/Feramance/Torrentarr/commit/14ccb84d512613aa959995297ebf5c7465ea2548))
- Bump xunit.runner.visualstudio from 3.1.5 to 4.0.0 (#384) ([c3eb076](https://github.com/Feramance/Torrentarr/commit/c3eb0767c8a52cb22def4088712984579f27bc51))
- build(deps-dev): Bump msw from 2.14.6 to 2.15.0 in /webui (#332) ([6a38dc5](https://github.com/Feramance/Torrentarr/commit/6a38dc5427499dd81999ebe065ae748b5661c9b6))
- Bump Microsoft.EntityFrameworkCore.Sqlite from 10.0.10 to 10.0.11 (#376) ([5b8c326](https://github.com/Feramance/Torrentarr/commit/5b8c326e8615c21eae2599423d1c68d648ddb8d6))
- build(deps-dev): Bump vitest from 4.1.9 to 4.1.10 in /webui (#336) ([3d1b79f](https://github.com/Feramance/Torrentarr/commit/3d1b79f5d9a3cc4b4a03200381870e675a4ce54e))
- build(deps-dev): Bump @vitejs/plugin-react from 6.0.2 to 6.0.3 in /webui (#322) ([b1225a9](https://github.com/Feramance/Torrentarr/commit/b1225a9c076478c2c05e8319906bdcb2342c8535))
- docs(openapi): regenerate spec from qBitrr master to fix CI drift check (#385) ([9e024e7](https://github.com/Feramance/Torrentarr/commit/9e024e757e1f7a24ad47a83ebc9b46488466e3b6))
- fix(import): finalize pending imports without file-filter gate; fail closed on Arr queue errors (#330) ([074d25e](https://github.com/Feramance/Torrentarr/commit/074d25e98acd461c80163385dd6c5b492968fcaa))
- Bump Microsoft.EntityFrameworkCore.InMemory from 10.0.10 to 10.0.11 (#375) ([81c257e](https://github.com/Feramance/Torrentarr/commit/81c257e477b370e2421d87dc20e96f6d85ca1c98))
- Bump Microsoft.AspNetCore.Mvc.NewtonsoftJson from 10.0.10 to 10.0.11 (#383) ([cad8061](https://github.com/Feramance/Torrentarr/commit/cad8061692e8cea68391780063a10fb7d3504556))
- Bump Microsoft.NET.Test.Sdk from 18.8.1 to 18.9.0 (#382) ([2b54fad](https://github.com/Feramance/Torrentarr/commit/2b54fad2a5af52b6b3bf0e2592944ac1b2861e03))
- Bump Microsoft.Extensions.Options and Microsoft.Extensions.Options.DataAnnotations (#381) ([3ee54af](https://github.com/Feramance/Torrentarr/commit/3ee54af4345dda8fe8af73662d5aa4bb3616072c))
- Bump Microsoft.AspNetCore.Mvc.NewtonsoftJson from 10.0.10 to 10.0.11 (#370) ([fd32d50](https://github.com/Feramance/Torrentarr/commit/fd32d509f63228f07ad4473cea2c3e2001ab4759))
- Bump Microsoft.Extensions.Hosting and Microsoft.Extensions.Logging.Abstractions (#379) ([3cc3403](https://github.com/Feramance/Torrentarr/commit/3cc3403607bc61eeb14536cdb2b7eb7fe139c946))
- Bump Microsoft.Extensions.Configuration from 10.0.10 to 10.0.11 (#377) ([1fe6897](https://github.com/Feramance/Torrentarr/commit/1fe6897c8bb93a245e05b9353ce872b3c88ddde4))
- Bump Microsoft.EntityFrameworkCore.Design from 10.0.10 to 10.0.11 (#374) ([c65bfc9](https://github.com/Feramance/Torrentarr/commit/c65bfc9d766b4e8818de67b2797c145300956026))
- Bump Microsoft.EntityFrameworkCore from 10.0.10 to 10.0.11 (#373) ([ff61873](https://github.com/Feramance/Torrentarr/commit/ff61873b9624971bbd329d5d02e0c057925ff132))
- Bump Microsoft.AspNetCore.SpaServices.Extensions from 10.0.10 to 10.0.11 (#372) ([69f084e](https://github.com/Feramance/Torrentarr/commit/69f084e29692dd023b389b4c3b10a0fa259d2cff))
- Bump Microsoft.AspNetCore.Mvc.Testing from 10.0.10 to 10.0.11 (#371) ([6d44f0a](https://github.com/Feramance/Torrentarr/commit/6d44f0a67d368a6e69ea9b7ab874a45d0321f6f4))
- Bump Microsoft.AspNetCore.Authentication.OpenIdConnect from 10.0.10 to 10.0.11 (#369) ([18ec12a](https://github.com/Feramance/Torrentarr/commit/18ec12ac8f819e98d564f26bd3e1d7939d8f215d))
- build(deps-dev): Bump undici (#367) ([790ea52](https://github.com/Feramance/Torrentarr/commit/790ea52adf0e7dda66a6900215d1830604413049))
- Bump SQLitePCLRaw.bundle_e_sqlite3 from 3.0.4 to 3.0.5 (#366) ([f8a1be5](https://github.com/Feramance/Torrentarr/commit/f8a1be54ae72479bf519806103b0f5fa6379cde7))
- Bump Microsoft.EntityFrameworkCore.InMemory from 10.0.7 to 10.0.10 (#365) ([cfde767](https://github.com/Feramance/Torrentarr/commit/cfde767f9836084eb6f5f959120e11101184b2d9))
- Bump Microsoft.AspNetCore.Mvc.Testing from 10.0.9 to 10.0.10 (#345) ([b714814](https://github.com/Feramance/Torrentarr/commit/b714814e95a4bc9ad3efb44fdb239d88c9704a66))
- Bump SQLitePCLRaw.bundle_e_sqlite3 from 3.0.3 to 3.0.4 (#359) ([e463a91](https://github.com/Feramance/Torrentarr/commit/e463a919d4ddbbc7423fd567ae178ea41787b7d8))
- Bump Microsoft.Extensions.Configuration.Binder from 10.0.9 to 10.0.10 (#363) ([e64a8d4](https://github.com/Feramance/Torrentarr/commit/e64a8d41a253b6f89ddabf59f99b0292eaec00d3))
- build(deps-dev): Bump postcss (#362) ([41e3830](https://github.com/Feramance/Torrentarr/commit/41e3830aed891ef88135f126572834e779a3bcfc))
- Bump Microsoft.AspNetCore.Mvc.NewtonsoftJson from 10.0.5 to 10.0.10 (#361) ([cb554e9](https://github.com/Feramance/Torrentarr/commit/cb554e91b6d9104867b24ffebcb070ef56215d50))
- Bump Microsoft.NET.Test.Sdk from 18.7.0 to 18.8.1 (#358) ([ee5cc5d](https://github.com/Feramance/Torrentarr/commit/ee5cc5d84891c0eef625e6bc73f982de0a02b91d))
- Bump Microsoft.Extensions.Options and Microsoft.Extensions.Options.DataAnnotations (#356) ([ff8bb42](https://github.com/Feramance/Torrentarr/commit/ff8bb426bb6beb93ed54a39b41da5fc87328f23d))
- Bump Microsoft.EntityFrameworkCore.Sqlite from 10.0.9 to 10.0.10 (#350) ([1ab2e21](https://github.com/Feramance/Torrentarr/commit/1ab2e21b048407864594181d3384869ff3614a07))
- Bump Microsoft.Extensions.Hosting and Microsoft.Extensions.Logging.Abstractions (#353) ([5d4b938](https://github.com/Feramance/Torrentarr/commit/5d4b93881db3a28c64f447d51fd08f83c655897e))
- Bump Microsoft.AspNetCore.Mvc.NewtonsoftJson from 10.0.9 to 10.0.10 (#344) ([ff2a49e](https://github.com/Feramance/Torrentarr/commit/ff2a49ec2ff7f410de37531a501d93bdc108fff7))
- Bump Microsoft.Extensions.Configuration from 10.0.9 to 10.0.10 (#351) ([88b25af](https://github.com/Feramance/Torrentarr/commit/88b25afb8239fa744f4fb54bb869df9aa52e8574))
- Bump Microsoft.EntityFrameworkCore.InMemory from 10.0.9 to 10.0.10 (#349) ([3ca2ff8](https://github.com/Feramance/Torrentarr/commit/3ca2ff87206477d3d512327b74cc8cb043ec0cb8))
- Bump Microsoft.EntityFrameworkCore.Design from 10.0.9 to 10.0.10 (#348) ([cdc732f](https://github.com/Feramance/Torrentarr/commit/cdc732f361a6ff4d76a93d03a5a107560d5f1b9c))
- Bump Microsoft.EntityFrameworkCore from 10.0.9 to 10.0.10 (#347) ([3cc680f](https://github.com/Feramance/Torrentarr/commit/3cc680f9c5bee92c7fb44405e8ca6a99e9238f04))
- Bump Microsoft.AspNetCore.SpaServices.Extensions from 10.0.9 to 10.0.10 (#346) ([d8d3d18](https://github.com/Feramance/Torrentarr/commit/d8d3d18471824d6cc7c44ce4bf2e1a7e37fa127f))
- Bump Microsoft.AspNetCore.Authentication.OpenIdConnect from 10.0.9 to 10.0.10 (#343) ([16c5adb](https://github.com/Feramance/Torrentarr/commit/16c5adba176ec76097141c735aa4c2ac0595e4f7))
- Bump FluentAssertions from 8.9.0 to 8.10.0 (#342) ([f231c0b](https://github.com/Feramance/Torrentarr/commit/f231c0bb2770b25534054b70edc95a210835daa5))
- [pre-commit] auto fixes from pre-commit hooks ([b2ea5cb](https://github.com/Feramance/Torrentarr/commit/b2ea5cbc4893af8bd35a7182ac5b526235f86459))
- Update star history chart in README ([b1c320e](https://github.com/Feramance/Torrentarr/commit/b1c320e5c263f6e3bb8e9c82b690e9b411bb600f))
- Bump Tomlyn from 2.9.0 to 2.10.1 (#329) ([16f8705](https://github.com/Feramance/Torrentarr/commit/16f8705dfd4381f5bf894aa4de1c6dee1e8a23cf))
- Bump Serilog from 4.3.1 to 4.4.0 (#338) ([ab63ecd](https://github.com/Feramance/Torrentarr/commit/ab63ecd88800ac16f72d14ad992d1d7e7a73169b))
- build(deps-dev): Bump typescript-eslint from 8.62.0 to 8.63.0 in /webui (#337) ([8f61cb5](https://github.com/Feramance/Torrentarr/commit/8f61cb562164928474d73785919e4f4f07ab0605))

---

## v6.12.4 (2026-07-08)

### Bug Fixes
- [patch] Version bump ([4487e38](https://github.com/Feramance/Torrentarr/commit/4487e38223a20ba0c09ed1636b3a6de95e97c066))
- Fix Dependabot vulnerability overrides (#320) ([770dc55](https://github.com/Feramance/Torrentarr/commit/770dc5526d3a4138065f17597ccdb736a77a6284))

### Maintenance
- Align parity with qBitrr latest main and fix import completion semantics. ([f49384b](https://github.com/Feramance/Torrentarr/commit/f49384b17a82a8349dfaeb1215bb3078b0abd1a8))
- build(deps-dev): Bump postcss from 8.5.15 to 8.5.16 in /webui (#327) ([e94fab5](https://github.com/Feramance/Torrentarr/commit/e94fab5a92d8b9337a283b433fda7c22f3994d0a))
- build(deps): Bump @mantine/dates from 9.4.0 to 9.4.1 in /webui (#326) ([743a190](https://github.com/Feramance/Torrentarr/commit/743a190d358890b9183a8cb8fcb2350cd448132b))
- build(deps): Bump @mantine/hooks from 9.4.0 to 9.4.1 in /webui (#324) ([31acb05](https://github.com/Feramance/Torrentarr/commit/31acb05db4b263b7a54fa05f79f884ec638b7bae))
- build(deps-dev): Bump vite from 8.1.0 to 8.1.3 in /webui (#328) ([24ad435](https://github.com/Feramance/Torrentarr/commit/24ad435fdc00306841a30a3a71f60cc1a40d444e))
- build(deps): Bump @mantine/core from 9.4.0 to 9.4.1 in /webui (#325) ([14f83eb](https://github.com/Feramance/Torrentarr/commit/14f83ebb4be9ee4b91b7f9d2b4ee1ce184036b6d))
- build(deps-dev): Bump @types/node from 26.0.1 to 26.1.0 in /webui (#323) ([b4cdb39](https://github.com/Feramance/Torrentarr/commit/b4cdb398423db67c2ebadf8f78428350935114a2))
- build(deps-dev): Bump tailwindcss from 4.3.1 to 4.3.2 in /webui (#321) ([f97d5b6](https://github.com/Feramance/Torrentarr/commit/f97d5b69e3d598369e0b63bc988f5c7ada4f2c08))

---

## v6.12.3 (2026-06-30)

### Bug Fixes
- [patch] fix(webui): resolve frontend lint and build failures (#319) ([f1a4901](https://github.com/Feramance/Torrentarr/commit/f1a49012bc36164561d626368ce2b0cf4fa16ed3))
- fix: preserve CHANGE_ME qBit placeholder sections on config save (#282) ([269fb98](https://github.com/Feramance/Torrentarr/commit/269fb988130b4f1b735a98d2062ec4aa27536c4c))
- fix: gate CF-unmet torrent deletion on HnrAllowsDeleteAsync (#278) ([908ec29](https://github.com/Feramance/Torrentarr/commit/908ec29299b0b118807de8ce9738ebf5ed4a7ed7))
- fix: v5 import readiness and tagless worker DB scoping (#231) ([4098f75](https://github.com/Feramance/Torrentarr/commit/4098f75e0b0ac042f0316832504bfa31e9d51cd4))
- fix: block config password bypass, api config wipe, and sync/tagless bugs (#258) ([75c2896](https://github.com/Feramance/Torrentarr/commit/75c289600b735ec860ab72dc3022e39b985e7b71))

### Documentation
- docs: add PR validation report for 9 open pull requests (#302) ([a88f5d2](https://github.com/Feramance/Torrentarr/commit/a88f5d2922130351cf730ca8089b59da12663304))
- docs: Cursor Automation spec for PR triage on open/push (#273) ([d1a1f03](https://github.com/Feramance/Torrentarr/commit/d1a1f03e79b7b12f7a2313893244db4a57d6a12f))
- docs: one-shot PR triage audit report for open pull requests (#272) ([8684eb0](https://github.com/Feramance/Torrentarr/commit/8684eb06126747309af7de7a22a9bca56a57e603))

### Maintenance
- fix(ci): Docker openapi copy and ConfigReloader test isolation (#318) ([3c2b2bf](https://github.com/Feramance/Torrentarr/commit/3c2b2bf86d8a99d18a2384969a228a8a740ea9a4))
- Bump Swashbuckle.AspNetCore from 7.2.0 to 10.2.3 (#315) ([ddf9669](https://github.com/Feramance/Torrentarr/commit/ddf96693dd6e2a49ada6b69e174637e99da08467))
- Bump Tomlyn from 0.17.0 to 2.9.0 (#316) ([23629ed](https://github.com/Feramance/Torrentarr/commit/23629ed2464dd0e744051a907543258b8b9c69e0))
- build(deps-dev): Bump @types/node from 25.9.3 to 26.0.1 in /webui (#309) ([61fdad3](https://github.com/Feramance/Torrentarr/commit/61fdad318fbca324222e88e13ee806a483ba31e4))
- Bump xunit.runner.visualstudio from 2.8.2 to 3.1.5 (#317) ([dc339e3](https://github.com/Feramance/Torrentarr/commit/dc339e3ce098d7b678102c42d2eaf596960c28fa))
- build(deps-dev): Bump typescript-eslint from 8.61.1 to 8.62.0 in /webui (#312) ([2590970](https://github.com/Feramance/Torrentarr/commit/25909706ff9846ceb066088fcf4f5dfa05c216d3))
- Bump Microsoft.NET.Test.Sdk from 18.4.0 to 18.7.0 (#314) ([1569cfa](https://github.com/Feramance/Torrentarr/commit/1569cfa9ffae61ee2f02ac5e8bb61ae603bb3e1a))
- build(deps-dev): Bump eslint from 10.5.0 to 10.6.0 in /webui (#311) ([27d21af](https://github.com/Feramance/Torrentarr/commit/27d21af5042a20e26344771ce88b68f9d9754921))
- Bump Microsoft.NET.Test.Sdk from 18.6.0 to 18.7.0 (#313) ([72939f2](https://github.com/Feramance/Torrentarr/commit/72939f25c5baebc364b39cfa7744430d19787614))
- build(deps): Bump @mantine/dates from 9.3.2 to 9.4.0 in /webui (#306) ([53aab21](https://github.com/Feramance/Torrentarr/commit/53aab21eca4bbecb542d66820b084a5dda631b4a))
- build(deps-dev): Bump autoprefixer from 10.5.0 to 10.5.2 in /webui (#310) ([4332927](https://github.com/Feramance/Torrentarr/commit/4332927154d08fe31a5ee4017f35f818975ca0e7))
- build(deps-dev): Bump vite from 8.0.16 to 8.1.0 in /webui (#308) ([7bfb033](https://github.com/Feramance/Torrentarr/commit/7bfb033f86d6ced9753285464c701a065214e038))
- build(deps): Bump @mantine/hooks from 9.3.2 to 9.4.0 in /webui (#307) ([5193602](https://github.com/Feramance/Torrentarr/commit/5193602cf6880e43f7ca87bb3186fd9ba9448ee6))
- build(deps): Bump @mantine/core from 9.3.2 to 9.4.0 in /webui (#305) ([366316e](https://github.com/Feramance/Torrentarr/commit/366316e88fa799922c827a743aaab2bd39097a50))
- build(deps): Bump actions/setup-dotnet from 5.3.0 to 5.4.0 (#304) ([2e966e7](https://github.com/Feramance/Torrentarr/commit/2e966e79a03f52a1c476909153453d2866a650ac))
- Delete .cursor directory ([8a0afc8](https://github.com/Feramance/Torrentarr/commit/8a0afc8afcd2ee64e2364225b369dd8095bfc708))
- fix(multi-qbit): scope IsReadyForImportAsync client lookup by instance (#303) ([8097c8a](https://github.com/Feramance/Torrentarr/commit/8097c8a2177d3b5408881d66ba6fbd0998e9ea0e))
- Close qBitrr parity gaps: category workers, MatchSubcategories, retries (#280) ([8578a8f](https://github.com/Feramance/Torrentarr/commit/8578a8fb35a4a656afe58afbc626f3c9b38d71c5))
- Bump Serilog.Sinks.File from 6.0.0 to 7.0.0 (#300) ([a3a6087](https://github.com/Feramance/Torrentarr/commit/a3a6087b8a383717ff933085c0725990cd667ca6))
- build(deps): bump actions/checkout from 6 to 7 (#285) ([d0b010e](https://github.com/Feramance/Torrentarr/commit/d0b010e1427e95815cf951b769406bfd7783b47e))
- build(deps): bump react-hook-form from 7.79.0 to 7.80.0 in /webui (#292) ([9094d6d](https://github.com/Feramance/Torrentarr/commit/9094d6d666e2afcd051580bd620c247e73f3d16e))
- Bump Microsoft.Extensions.Options and Microsoft.Extensions.Options.DataAnnotations (#296) ([83e2393](https://github.com/Feramance/Torrentarr/commit/83e239333e4dc3a42e1d76d9d844e11871df1505))
- fix(multi-qbit): scope Imported checks by qBit instance (#281) ([8ef62f7](https://github.com/Feramance/Torrentarr/commit/8ef62f7a90661f5396276334516ae1dcbc5b6b17))
- fix(security): block PasswordHash bypass via whole-WebUI section replace (#283) ([e3845a4](https://github.com/Feramance/Torrentarr/commit/e3845a4e915a519e35795db8c21789dab8e8f563))
- Bump Polly from 8.6.6 to 8.7.0 (#297) ([23679f3](https://github.com/Feramance/Torrentarr/commit/23679f311089390c176c1d6efb76940c7e6ce76d))
- Bump Serilog.Sinks.Console from 6.0.0 to 6.1.1 (#299) ([5f86469](https://github.com/Feramance/Torrentarr/commit/5f864691570ef57aedafd8a6850693fc38d4e467))
- Bump Xunit.SkippableFact from 1.4.13 to 1.5.61 (#301) ([a0f9a60](https://github.com/Feramance/Torrentarr/commit/a0f9a601ef385d628c5a937b7050a0302da5cd33))
- Bump Serilog from 4.3.0 to 4.3.1 (#298) ([d1e99f9](https://github.com/Feramance/Torrentarr/commit/d1e99f9a4a13653d52e8f94ae349ca0c13299c1e))
- build(deps): bump @mantine/dates from 9.3.1 to 9.3.2 in /webui (#288) ([8cab217](https://github.com/Feramance/Torrentarr/commit/8cab21774059151e6b5efe0971eb7815eaab8280))
- Bump Microsoft.Extensions.Http from 10.0.8 to 10.0.9 (#294) ([4c59d01](https://github.com/Feramance/Torrentarr/commit/4c59d0181a84f9d4e7d3c687afd3c0fd75eb4859))
- build(deps-dev): bump vitest from 4.1.8 to 4.1.9 in /webui (#291) ([6eb6682](https://github.com/Feramance/Torrentarr/commit/6eb6682a96659ae9362029dec50946e923168ce9))
- build(deps): bump @mantine/core from 9.1.1 to 9.3.2 in /webui (#290) ([140704c](https://github.com/Feramance/Torrentarr/commit/140704cae5efa9a8243ae3345ba2df879ce2e695))
- build(deps-dev): bump eslint-plugin-react-refresh in /webui (#289) ([bf8ff38](https://github.com/Feramance/Torrentarr/commit/bf8ff383f3039be47e162e0d508ad8577b0b698f))
- build(deps-dev): bump typescript-eslint from 8.61.0 to 8.61.1 in /webui (#287) ([066f4fa](https://github.com/Feramance/Torrentarr/commit/066f4fae4c3a5c19504d23fa1746edac64045c50))
- build(deps): bump @mantine/hooks from 9.3.1 to 9.3.2 in /webui (#286) ([34c8e02](https://github.com/Feramance/Torrentarr/commit/34c8e02170385f790fa1eacda2b4c35ca543d63c))
- build(deps-dev): bump undici (#284) ([e045e4b](https://github.com/Feramance/Torrentarr/commit/e045e4b4593622c75e558cefea466a7a2dec694c))
- Delete docs/audits directory ([3f2c16f](https://github.com/Feramance/Torrentarr/commit/3f2c16ff9269c3e3c8d0b35d816df73041c87e63))
- build(deps): bump react-hook-form from 7.76.1 to 7.79.0 in /webui (#239) ([d80525f](https://github.com/Feramance/Torrentarr/commit/d80525fa7497651e791a34fda7ae462e80a060d7))
- build(deps-dev): bump @tailwindcss/postcss from 4.3.0 to 4.3.1 in /webui (#238) ([db93ab1](https://github.com/Feramance/Torrentarr/commit/db93ab150b91e7e9e68b180260baa88c24496031))
- build(deps-dev): Bump @vitest/coverage-v8 from 4.1.8 to 4.1.9 in /webui (#234) ([75a91e0](https://github.com/Feramance/Torrentarr/commit/75a91e045518c0226835028f42d0504468a5aedb))
- fix(sync): guard Lidarr track wipe when albums API is empty (#243) ([1c4c60e](https://github.com/Feramance/Torrentarr/commit/1c4c60e3ffd908c30918aeede31e0dd4e6bc8a87))
- fix(security): block PasswordHash changes via config API (#271) ([c3a63f2](https://github.com/Feramance/Torrentarr/commit/c3a63f278e63aea92802da17f75cb3a55985ad8c))

---

## v6.12.2 (2026-06-08)

### Features
- qBitrr 5.12.3 feature parity: TorrentPolicyManager (tracker sort + free-space gating), UrlBase subpath support, catalog rollups, Lidarr artists/thumbnails, category path validation, auth bootstrap setup token, OpenAPI expansion with CI drift check
- Comprehensive test coverage for parity surfaces (692 .NET + 148 Vitest non-live tests)

### Bug Fixes
- HnR dead-tracker: remove bare `"not found"` keyword from `SeedingService` (#412)
- WebUI `configForDI` compile error; config/env alias precedence and migration fixes

### Documentation
- Parity docs under `docs/parity/` with GitHub blob links for MkDocs CI
- Align `AGENTS.md`, `CLAUDE.md`, and config docs with `ExpectedConfigVersion = 6.12.2`

## v6.1.0 (2026-03-13)

### Features
- feat(docs): add Swagger UI in running app and MkDocs - Enable Swagger in all environments (Host + WebUI) - Add Bearer token support in Swagger UI for /api/* endpoints - Document /swagger in docs/webui/api.md - Add mkdocs-swagger-ui-tag, docs/webui/swagger.md, docs/assets/openapi.json - Add ExportOpenApiSpecTests to regenerate spec when TORRENTARR_EXPORT_OPENAPI=1 - Document spec regeneration in release-process and contributing - ensure-global-json: use latestMinor so 10.0.200 SDK is accepted by pre-commit - Also includes: WebUI auth helpers, login/set-password rate limiters, and related tests/docs. ([8c9a261](https://github.com/Feramance/Torrentarr/commit/8c9a2612675f1d0b829c7e4c1f24759c3b4036e5))

### Bug Fixes
- Fix main branch nightly docker and hook order ([1f8494f](https://github.com/Feramance/Torrentarr/commit/1f8494f238481c954c09d2f916212ae2f1465046))
- fix: add System.Collections.Concurrent for ConcurrentDictionary in rate limiters ([dcee0d2](https://github.com/Feramance/Torrentarr/commit/dcee0d265d31322ce337d3a98f8f44035f9ffeab))
- fix: add OpenIdConnect package to WebUI project for PR build ([683b791](https://github.com/Feramance/Torrentarr/commit/683b791d9043099d47f82a8f48eca3968dd56e70))

### Documentation
- docs: add WebUI auth docs and fix MkDocs git-revision plugin warnings ([b32116b](https://github.com/Feramance/Torrentarr/commit/b32116b948784bd314c43058f644dfd5a1e17b54))

### Maintenance
- fix(webui): modals close only on Close/Done/Cancel/Confirm (qBitrr #332) ([950190c](https://github.com/Feramance/Torrentarr/commit/950190c242638113d95a0c04e2fc00a80b802359))
- fix(tests): RadarrView timeout + security pass on auth paths ([657df2c](https://github.com/Feramance/Torrentarr/commit/657df2cf6c5133fdea5f3202faaf6cff096c36ba))
- test: use AllowAutoRedirect=false for logout test to assert on 302 ([eec2154](https://github.com/Feramance/Torrentarr/commit/eec2154a3effa209e721ad990e59a0bb7b74022a))
- ci: add .NET and Node setup to pre-commit workflow for local hooks ([88efe9a](https://github.com/Feramance/Torrentarr/commit/88efe9a71088cadf208170007f31736ba1555aea))
- fix(tests): use API token for Swagger spec test so it passes when auth enabled ([3df9a0c](https://github.com/Feramance/Torrentarr/commit/3df9a0cab7d299c2c97ddc0b322948d99b81f1dc))
- ci: harden build workflow and add pre-commit compile checks ([d825b10](https://github.com/Feramance/Torrentarr/commit/d825b100eeab93108fd82c71de01e130b7d83c97))
- pre-commit fixes ([46fffe7](https://github.com/Feramance/Torrentarr/commit/46fffe77db3c2cba0f2f83c50cf44fec3b1b6887))
- Align config version fallbacks with expected schema ([b6e55d8](https://github.com/Feramance/Torrentarr/commit/b6e55d86952475b29206a5c5f2ea7a35b07b200b))
- Gate PR Docker publish on build job ([30179e0](https://github.com/Feramance/Torrentarr/commit/30179e04d202b0b873fa069644d8dac8f4ae62cd))
- Auth by default for new installs with welcome setup screen ([ac2366b](https://github.com/Feramance/Torrentarr/commit/ac2366b2a4a73e5330d11054d49ea296dca3c366))
- Require build job before nightly Docker publish ([7415324](https://github.com/Feramance/Torrentarr/commit/7415324efdb5a4d60f02881bb5bec0c342575207))
- Consolidate CI: single build workflow, remove pull_requests and nightly ([851297e](https://github.com/Feramance/Torrentarr/commit/851297ebf64f90f24d91bac624f5d80434cab080))
- Make csproj selection deterministic in ensure-global-json ([73ec79a](https://github.com/Feramance/Torrentarr/commit/73ec79a19f5d894e2a5b09d56ceab126c5f4d212))
- fix(security): override immutable to 5.1.5 (CVE-2026-29063, GHSA-wf6x-7x77-mvgw) ([a785265](https://github.com/Feramance/Torrentarr/commit/a7852658d16706bd1985fe0c0bfa2bea577d4759))
- fix(deps): align Microsoft.Extensions and EF Core to 10.0.3 for Dependabot merge ([f0a8e1e](https://github.com/Feramance/Torrentarr/commit/f0a8e1ef98484df6eb0b2db46c9bf6422f39caea))
- Update config, services, WebUI and ConfigView ([dc3e1ae](https://github.com/Feramance/Torrentarr/commit/dc3e1aecbec41935497079aacab794f356057071))
- chore(deps): bump @mantine/hooks from 8.3.15 to 8.3.16 in /webui (#38) ([e65f5ff](https://github.com/Feramance/Torrentarr/commit/e65f5fff4875fa31cdace962186220487b85de84))
- chore(deps-dev): bump eslint from 10.0.1 to 10.0.3 in /webui (#31) ([644db82](https://github.com/Feramance/Torrentarr/commit/644db825315fbd31212e6b14e30076daeced9344))
- chore(deps-dev): bump typescript-eslint from 8.56.0 to 8.56.1 in /webui (#37) ([486c793](https://github.com/Feramance/Torrentarr/commit/486c793b490a618a580d460021df971c1d0429ab))
- chore(deps-dev): bump @types/node from 25.3.0 to 25.3.5 in /webui (#36) ([0d544cb](https://github.com/Feramance/Torrentarr/commit/0d544cbb59a11be50e08f164859d376bf4967f2c))
- chore(deps): bump @mantine/core from 8.3.15 to 8.3.16 in /webui (#34) ([934391e](https://github.com/Feramance/Torrentarr/commit/934391e3e073fb25965194a051caec369c151a4b))
- fix(config): fix DurationInput empty-string clamping bug ([74f0a06](https://github.com/Feramance/Torrentarr/commit/74f0a061a5d4ebf46e35de237d3922121f8919b7))
- Bump Microsoft.EntityFrameworkCore.InMemory from 9.0.0 to 10.0.3 ([b98250e](https://github.com/Feramance/Torrentarr/commit/b98250e8889e45f4b8a9a3b7f7ee0b7da137fbd5))
- Bump Microsoft.EntityFrameworkCore.Design from 9.0.0 to 10.0.3 ([f50535d](https://github.com/Feramance/Torrentarr/commit/f50535d72ff1bea4972e5efd56b4ab8e0022669c))
- Bump Microsoft.EntityFrameworkCore from 9.0.0 to 10.0.3 ([aa94c95](https://github.com/Feramance/Torrentarr/commit/aa94c954c351107f1737f0e4e24a1a322f0c0d1f))
- Bump Microsoft.AspNetCore.SpaServices.Extensions from 9.0.0 to 10.0.3 ([ffec7c6](https://github.com/Feramance/Torrentarr/commit/ffec7c6261d122524d805abfd670dee7c4765a88))
- Bump Microsoft.AspNetCore.Mvc.NewtonsoftJson from 9.0.0 to 10.0.3 ([59877b4](https://github.com/Feramance/Torrentarr/commit/59877b4ce32c8cfa576cf85a0d52dc480a6c89fb))
- chore(deps): bump docker/setup-buildx-action from 3 to 4 ([ca1f970](https://github.com/Feramance/Torrentarr/commit/ca1f970425c0d8ce38d958147b616eb0c78952ea))
- chore(deps): bump docker/login-action from 3 to 4 ([bd07038](https://github.com/Feramance/Torrentarr/commit/bd070382905734432b0d62465867c03bb42fe932))
- chore(deps): bump docker/metadata-action from 5 to 6 ([297f406](https://github.com/Feramance/Torrentarr/commit/297f40642a13fcd56359e15d533c1d9a8068ac64))
- chore(deps-dev): bump autoprefixer from 10.4.24 to 10.4.27 in /webui (#29) ([922ba82](https://github.com/Feramance/Torrentarr/commit/922ba82fc64a4a9ff6d94af8c989d96214e2a5f1))
- chore(deps): bump actions/setup-dotnet from 5.1.0 to 5.2.0 ([8f602b7](https://github.com/Feramance/Torrentarr/commit/8f602b77c1530654d272898a2387fc6552e20f62))
- chore(deps): bump docker/build-push-action from 6 to 7 ([adaa153](https://github.com/Feramance/Torrentarr/commit/adaa1539d4036c8eed2441f8fc53c85845db2d8d))
- fix(config): fix config view data flow and add missing fields ([ca9b4da](https://github.com/Feramance/Torrentarr/commit/ca9b4daf6c89fdb2954262e260309b942bc86fbc))
- chore: update .gitignore and CLAUDE.md ([60d1041](https://github.com/Feramance/Torrentarr/commit/60d1041e37bfd19f8bc37fd6a09b016f60da20e9))
- Docs and Processes UI: formatting and content updates, process state and tests ([9e52a03](https://github.com/Feramance/Torrentarr/commit/9e52a033bea0b8e1625a54253220f693f60d83b7))
- Update Patreon funding name to 'Feramance' ([c97d400](https://github.com/Feramance/Torrentarr/commit/c97d4004ab1c3d76486461ac891c5b49e4c70791))
- fix(auth): TokenOnly mode must set AuthDisabled=false not true ([a8d409f](https://github.com/Feramance/Torrentarr/commit/a8d409f148600a475cecc7a6fe0395a19c624719))
- security(auth): harden login, set-password, and config endpoints ([db9814a](https://github.com/Feramance/Torrentarr/commit/db9814a09c0a1ee09fc6cd79e12a8e0dc6294fb1))
- feat(auth): comprehensive auth test coverage + improved auth settings modal ([fafb065](https://github.com/Feramance/Torrentarr/commit/fafb0657975256a0674a3107135043745a5efab8))
- Cursor review: OIDC challenge check Authority/ClientId; AuthGate fetch and store token when auth disabled ([7e628f2](https://github.com/Feramance/Torrentarr/commit/7e628f281457ca6db03ae090375f2a33b6a67f9b))
- Cursor review: login constant-time bcrypt to prevent username enum; WebUI OIDC registration and challenge endpoint ([9eebf46](https://github.com/Feramance/Torrentarr/commit/9eebf46ff963531dc979dbaaf440bc83a86532b0))
- Cursor review: set-password revert config on save failure; AuthGate store token on initial getToken success ([96902dc](https://github.com/Feramance/Torrentarr/commit/96902dc0c066d015ecc52eb9331ea565b8cba585))
- fix(webui): add Name claim to Bearer identity for consistency with Host ([4f440bd](https://github.com/Feramance/Torrentarr/commit/4f440bd5b54abcf532338e72cb0a63bdb5fee89b))
- Cursor bot: constant-time token compare via SHA-256, restrict OIDC public path to challenge ([3646f22](https://github.com/Feramance/Torrentarr/commit/3646f22a78bda65c568a306500f5bd62c464468f))

---

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
