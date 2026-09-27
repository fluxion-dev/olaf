# olaf

License scanner: scans `npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan|apk|dpkg` projects, resolves licenses, writes a report to stdout or a file.

Supported ecosystems: `npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan|apk|dpkg` (`pypi` alias for `pip`). Supported formats: `json|yaml|xml|html|txt|md` (`markdown` alias for `md`).

Parser coverage: `npm` handles `package.json|package-lock.json|pnpm-lock.yaml|yarn.lock|bun.lock` (`bun.lockb` binary yields empty; any lock beats manifest, all locks merge deduped, lock entries transitive); `pip` handles `Pipfile.lock|requirements.txt|pyproject.toml|poetry.lock|uv.lock|environment.yml|environment.yaml` (preference `Pipfile.lock` authoritative-first (empty lock falls through to poetry/uv tier), then `poetry.lock`/`uv.lock` merged > `requirements.txt` > `pyproject.toml` > `environment.yml|environment.yaml`; `Pipfile.lock` entries `IsTransitive=false`; `hashes[]` validated but parsed-but-deferred — not stored on the report; conda entries reported as `pip`); `go` handles `go.mod|go.sum` (2 lines per module in `go.sum` deduped to one dep; `// indirect` + present in `go.sum` → transitive, `// indirect` + absent → direct fallback, `go.mod`-only dir keeps legacy `// indirect` → transitive, `go.sum`-only dir yields all-transitive deps; `h1:` hashes syntactically validated but parsed-but-deferred — not stored on the 8-field report, SBOM enrichment follow-up). `apk` handles `installed` (`lib/apk/db/installed`: blank-line-separated stanzas with `P:`/`V:` fields; entries `IsTransitive=false`; malformed yields empty, never throws); `dpkg` handles `status` (`var/lib/dpkg/status`: blank-line-separated stanzas with `Package:`/`Version:` fields; entries `IsTransitive=false`; malformed yields empty, never throws); `container` scans image tarballs (`*.tar|*.tar.gz|*.tgz` with top-level `manifest.json`/`index.json` markers) and exploded OCI/docker-save layout dirs (`manifest.json|index.json|oci-layout` plus layer blobs) and reports the image's packages as `apk`/`dpkg` entries (`IsTransitive=false`; layers apply bottom→top with OCI whiteouts — basename prefix `.wh.`, `.wh..wh..opq` clears the directory — and topmost layer wins for the same package; absolute/`..` entries and symlinks/hardlinks are skipped; RPM `Packages` DB bytes are ignored, deferred to issue #65; uncompressed bytes are capped by `--max-image-mb`, default `1024`; corrupt/truncated tarballs and non-image tars fail with exit `2`, never silent empty).

## Tool install

```bash
dotnet pack src/Olaf.Cli -c Release
dotnet tool install --global --add-source ./src/Olaf.Cli/bin/Release olaf --version 0.1.0-preview.1
olaf --help
```

Version is pinned: `--version 0.1.0-preview.1` is required — unpinned install fails for prerelease versions. Tests: 327 passing (`dotnet test`).

## Usage

```bash
# stdout (default json)
dotnet run --project src/Olaf.Cli -- --input package.json
# file (parent dirs auto-created; fails if exists unless --force)
dotnet run --project src/Olaf.Cli -- --input ./src --out report.json --format yaml
# strict gate (exit 1 on Unknown)
dotnet run --project src/Olaf.Cli -- --input package.json --strict
# attribution report (human-readable text / markdown)
dotnet run --project src/Olaf.Cli -- --input package.json --format txt
dotnet run --project src/Olaf.Cli -- --input package.json --format md
# allow-list gate
dotnet run --project src/Olaf.Cli -- --input package.json --strict --allow MIT,Apache-2.0
```

Flags: `--input <file|dir>`, `--format json|yaml|xml|html|txt|md` (default `json`; `markdown` alias for `md`), `--out <file>` (default stdout), `--force`, `--strict`, `--allow <csv>`, `--deny <csv>`, `--ecosystem npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan|apk|dpkg` (`pypi` alias for `pip`), `--max-image-mb <n>` (container-image cap in MB of uncompressed bytes handled, default `1024`; must be `> 0`, missing/invalid → exit `2`), `--verbose`, `--quiet`, `--help`, `--version` (built-in).
`--out` parent directories are auto-created; `--out` fails if the file exists unless `--force` is given.
`--allow` is a comma-separated SPDX allow-list (fail licenses not in the list); `--deny` is a comma-separated SPDX deny-list (fail licenses in the list). `--allow`/`--deny` without `--strict` warns on stderr but still enforces the policy gate.

`--help` excerpt (via `dotnet run --project src/Olaf.Cli -- --help`, exit `0`):

```text
--format <format>        Output format: json|yaml|xml|html|txt|md (default: json) [default: json]
--ecosystem <ecosystem>  Limit scan to ecosystem: npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan|apk|dpkg (pypi alias for pip)
--max-image-mb <max-image-mb>  Cap container-image scan at N megabytes uncompressed handled (default: 1024; must be > 0)
--strict                 Fail on unresolved or unknown licenses
```

## Reliability

Resolution runs with bounded-8 concurrency and retry-once on transient HTTP failures (timeout/transport/408/429/5xx). Unresolved packages return `Unknown` with reason tokens (`offline-cache-miss`, `not-found`, `resolver-error`, …). Cancellation (`OperationCanceledException`) is always rethrown, never swallowed into `Unknown`.

## Report contract

Every `licenses` entry has the same 8 fields (rows sorted by ecosystem, name, version):

| Field | Meaning |
|-------|---------|
| `ecosystem` | `npm`, `nuget`, `pip`, `go`, `cargo`, `maven`, `gradle`, `composer`, `bundler`, `swift`, `cocoapods`, `vcpkg`, `conan`, `apk`, or `dpkg` (container-image scans report their packages as `apk`/`dpkg` entries) |
| `name` | Package name |
| `version` | Version spec from the manifest |
| `spdx` | SPDX id, or null when unresolved |
| `licenseText` | License text, or null when unavailable |
| `sourceUrl` | Provenance URL, or null when unavailable |
| `status` | `Resolved` or `Unknown` |
| `reason` | Why unresolved (null when resolved) |

## License Coverage

This project provides SPDX license mapping for the following licenses:

| License | SPDX ID | Description |
|---------|----------|-------------|
| MIT | MIT | MIT License |
| Apache License 2.0 | Apache-2.0 | Apache License Version 2.0 |
| Apache License 1.1 | Apache-1.1 | Apache License Version 1.1 |
| ISC | ISC | ISC License |
| BSD 2-Clause | BSD-2-Clause | BSD 2-Clause License |
| BSD 3-Clause | BSD-3-Clause | BSD 3-Clause License |
| BSD 4-Clause | BSD-4-Clause | Original BSD License |
| GNU General Public License v1.0 | GPL-1.0-only | GNU General Public License Version 1.0 |
| GNU General Public License v2.0 | GPL-2.0-only | GNU General Public License Version 2.0 |
| GNU General Public License v3.0 | GPL-3.0-only | GNU General Public License Version 3.0 |
| GNU Lesser General Public License v2.0 | LGPL-2.0-only | GNU Lesser General Public License Version 2.0 |
| GNU Lesser General Public License v2.1 | LGPL-2.1-only | GNU Lesser General Public License Version 2.1 |
| GNU Lesser General Public License v3.0 | LGPL-3.0-only | GNU Lesser General Public License Version 3.0 |
| GNU Affero General Public License v1.0 | AGPL-1.0-only | GNU AFFERO General Public License Version 1.0 |
| GNU Affero General Public License v3.0 | AGPL-3.0-only | GNU AFFERO General Public License Version 3.0 |
| Mozilla Public License v1.0 | MPL-1.0 | Mozilla Public License Version 1.0 |
| Mozilla Public License v1.1 | MPL-1.1 | Mozilla Public License Version 1.1 |
| Mozilla Public License v2.0 | MPL-2.0 | Mozilla Public License Version 2.0 |
| CDDL License v1.0 | CDDL-1.0 | Common Development and Distribution License |
| Eclipse Public License v1.0 | EPL-1.0 | Eclipse Public License v1.0 |
| Unlicense | Unlicense | Unlicense |
| CC0 1.0 Universal | CC0-1.0 | CC0 1.0 Universal |
| Artistic License 2.0 | Artistic-2.0 | Artistic License 2.0 |
| Attribution Assurance License | AAL | Attribution Assurance License |

The SPDX IDs are available through `SpdxMapper.Normalize()` and `SpdxLicenseTexts.GetText()` for license text retrieval.

Summary shape per format:

- JSON: `{"summary":{"total":…,"resolved":…,"unknown":…},"licenses":[…]}`.
- YAML: `summary:` with `total`/`resolved`/`unknown` plus a `licenses:` list with the same 8 keys.
- XML: `<report><summary total="…" resolved="…" unknown="…"/><licenses><license>` with the 8 fields as child elements.
- HTML: `<p>Total: … · Resolved: … · Unknown: …</p>` plus an 8-column table (Ecosystem, Name, Version, SPDX, License, Source, Status, Reason; the SPDX cell falls back to status when `spdx` is null).
- TXT: `Third-Party Attribution` header with `Total: …, Resolved: …, Unknown: …` plus one `name@version (ecosystem)` block per package (SPDX falls back to `Unknown`, plus source/status/reason lines).
- MD (`markdown` alias): `# Third-Party Attribution` header with `Total: …, Resolved: …, Unknown: …`, an 8-column markdown table, plus one `## name@version (ecosystem)` section per package carrying all 8 fields.

Empty scan: `total`/`resolved`/`unknown` are all `0`; JSON/YAML emit an empty `licenses` list, XML emits `<licenses />`, HTML emits an empty `<tbody>`.

```bash
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/npm/package.json --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/npm --format txt
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/npm --format md
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/go --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/cargo --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/go --ecosystem go --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/cargo --ecosystem cargo --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/maven --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/gradle --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/maven --ecosystem maven --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/gradle --ecosystem gradle --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/composer --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/bundler --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/composer --ecosystem composer --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/bundler --ecosystem bundler --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/swift --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/cocoapods --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/swift --ecosystem swift --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/cocoapods --ecosystem cocoapods --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/vcpkg --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/conan --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/vcpkg --ecosystem vcpkg --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/conan --ecosystem conan --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/pip --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/pip --ecosystem pip --format json
# container image (docker-save/OCI tarball or exploded layout dir; RPM deferred to #65)
dotnet run --project src/Olaf.Cli -- --input image.tar --format json
dotnet run --project src/Olaf.Cli -- --input ./oci-dir --format json
dotnet run --project src/Olaf.Cli -- --input image.tar --format json --max-image-mb 2048
```

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success (including non-`--strict` runs with `Unknown` licenses; `--help`/`--version` also `0`) |
| `1` | `--strict` found unresolved/`Unknown` licenses, or `--allow`/`--deny` policy-gate offenders |
| `2` | Usage/IO error: missing `--input`, input not found, unsupported `--format`/`--ecosystem`, `--out` exists without `--force`, invalid/missing `--max-image-mb`, corrupt/truncated or non-image container tarball, scan/write failure |

## Output contract

- Report goes to stdout when `--out` is omitted (machine-parseable; e.g. stdout is pure JSON with `--format json`).
- When `--out <file>` is given the report goes to the file and stdout stays empty.
- Errors, the `--strict` notice, and `--verbose` logs go to stderr; `--version` prints to stdout.
