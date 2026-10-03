# olaf sandbox matrix

Docker sandboxes — one per supported packaging system (13) — each able to
`git clone` a real repo and prove `olaf generate` emits a proper licence file.

## Layout

| Path | Purpose |
|---|---|
| `sandbox/repos.json` | Single source of truth: smoke repo + manifest + `minPackages` per ecosystem |
| `sandbox/common/verify.sh` | Verification framework (clone → `olaf generate` → validate → all-format sweep) |
| `sandbox/common/validate.py` | Stdlib-only JSON report checker (proper-licence-file gate) |
| `sandbox/docker/<eco>.Dockerfile` | 13 sandbox images, each with its toolchain + `git` + `olaf` + the framework |
| `sandbox/run-all.sh` | Host runner: `docker build` + `docker run` every sandbox, PASS/FAIL table |
| `sandbox/smoke-local.sh` | Same verification without docker (clone + `dotnet`-built `olaf`) |

## Supported envs (13) and smoke repos

All repos were hand-checked via the GitHub contents API (2026-10-03).
Each contains the manifest `olaf` parses, with ≥1 dependency so the
`total > 0, resolved > 0` gate is meaningful.

| Eco | Repo | Manifest |
|---|---|---|
| `npm` | `expressjs/express` | `package.json` |
| `nuget` | `xunit/xunit` | `src/**/*.csproj`, `packages.lock.json` |
| `pip` | `psf/requests` | `pyproject.toml` |
| `go` | `spf13/cobra` | `go.mod` (4 requires) |
| `cargo` | `BurntSushi/ripgrep` | `Cargo.toml` + `Cargo.lock` |
| `maven` | `apache/commons-lang` | `pom.xml` |
| `gradle` | `mockito/mockito` | `build.gradle.kts` + `libs.versions.toml` |
| `composer` | `Seldaek/monolog` | `composer.json` |
| `bundler` | `jekyll/jekyll` | `Gemfile` + `jekyll.gemspec` |
| `swift` | `vapor/vapor` | `Package.swift` (~24 `.package` deps) |
| `cocoapods` | `artsy/eidolon` | `Podfile` + `Podfile.lock` |
| `vcpkg` | `microsoft/terminal` | `vcpkg.json` |
| `conan` | `conan-io/examples` | nested `conanfile.txt` (`[requires]`) |

Rejected lookalikes (documented in `repos.json`): `monolog/monolog`
(wrong owner), `apple/swift-argument-parser` (zero `.package` deps),
`Alamofire/Alamofire` (only `.podspec`, which `olaf` does not parse),
`catchorg/Catch2` (leaf `conanfile.py` recipe, no `requires`).

All 13 ecos assert `≥1 resolved`, including `conan` (resolved via the
`ConanLicenseResolver` primary off `conan-center-index` recipe data;
stale pins like `poco/1.9.4` resolve through the latest-folder fallback).

## What "proper licence file" means

`validate.py` gates every run (online resolution by default):

1. valid JSON with `summary` + `licenses`
2. `summary.total == len(licenses)` and `total >= minPackages` (1)
3. every row has the 9 base keys (`ecosystem…direct`)
4. ≥1 row matches the expected ecosystem (repos may mix ecosystems)
5. ≥1 resolved row (`status`/`spdx` not `Unknown`)

Online resolution is the default because a *proper* licence file means
actually resolved licences. `--offline` (hermetic/air-gap) relaxes gate 5 —
misses stay `Unknown` by design — while gates 1–4 still apply:

```bash
sandbox/smoke-local.sh --eco go --offline   # hermetic, resolved gate relaxed
```

`verify.sh --formats all` additionally runs the other six formats
(`yaml|xml|md|cyclonedx-json|cyclonedx-xml|spdx-json`) and asserts exit `0`
plus non-empty output.

## Each sandbox has `git` + `olaf`

Every `Dockerfile` installs `git curl ca-certificates jq python3`
(on top of its toolchain base), downloads the pinned `olaf-linux-x64`
release binary (`OLAF_VERSION`, default `v0.1.3-preview.1`) to
`/usr/local/bin/olaf`, and bakes in `verify.sh`/`validate.py`/`repos.json`.
The container `CMD` clones its `repos.json` URL (`git clone --depth 1`)
and runs the full verification — online resolution plus all 7 formats —
no host tooling needed beyond docker.

Toolchain bases: `node:22` (npm), `dotnet/sdk:10.0` (nuget),
`python:3.13` (pip), `golang:1.24` (go), `rust:1` (cargo),
`maven:3-temurin-21` (maven), `gradle:8-jdk21` (gradle),
`php:8.3` + composer (composer), `ruby:3.4` (bundler),
`swift:6.1` (swift), `ruby:3.4` + `cocoapods` gem (cocoapods),
`ubuntu:24.04` + cmake/ninja (vcpkg), `python:3.13` + `conan` (conan).

## Run it

```bash
# Full docker matrix (builds 13 images, runs 13 containers)
sandbox/run-all.sh

# One ecosystem only
sandbox/run-all.sh --eco npm

# Same checks without docker (uses dotnet-built olaf, clones to /tmp/opencode/olaf-sandbox)
sandbox/smoke-local.sh
sandbox/smoke-local.sh --eco pip --formats all

# Inside a built sandbox, re-run manually
docker run --rm -it olaf-sandbox-npm
```

`run-all.sh` / `smoke-local.sh` exit `0` iff every selected ecosystem
prints `PASS`; both end with `results: PASS=n FAIL=m`.
