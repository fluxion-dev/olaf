# olaf

License scanner: scans `npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan` projects, resolves licenses, writes a report to stdout or a file.

Supported ecosystems: `npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan` (`pypi` alias for `pip`). Supported formats: `json|yaml|xml|html|txt|md` (`markdown` alias for `md`).

Parser coverage: `npm` handles `package.json|package-lock.json|pnpm-lock.yaml|yarn.lock|bun.lock` (`bun.lockb` binary yields empty; any lock beats manifest, all locks merge deduped, lock entries transitive); `pip` handles `requirements.txt|pyproject.toml|poetry.lock|uv.lock|environment.yml|environment.yaml` (preference lock>`requirements.txt`>`pyproject.toml`>`environment.yml`; conda entries reported as `pip`).

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

Flags: `--input <file|dir>`, `--format json|yaml|xml|html|txt|md` (default `json`; `markdown` alias for `md`), `--out <file>` (default stdout), `--force`, `--strict`, `--allow <csv>`, `--deny <csv>`, `--ecosystem npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan` (`pypi` alias for `pip`), `--verbose`, `--quiet`, `--help`, `--version` (built-in).
`--out` parent directories are auto-created; `--out` fails if the file exists unless `--force` is given.
`--allow` is a comma-separated SPDX allow-list (fail licenses not in the list); `--deny` is a comma-separated SPDX deny-list (fail licenses in the list). `--allow`/`--deny` without `--strict` warns on stderr but still enforces the policy gate.

`--help` excerpt (via `dotnet run --project src/Olaf.Cli -- --help`, exit `0`):

```text
--format <format>        Output format: json|yaml|xml|html|txt|md (default: json) [default: json]
--ecosystem <ecosystem>  Limit scan to ecosystem: npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan (pypi alias for pip)
--strict                 Fail on unresolved or unknown licenses
```

## Reliability

Resolution runs with bounded-8 concurrency and retry-once on transient HTTP failures (timeout/transport/408/429/5xx). Unresolved packages return `Unknown` with reason tokens (`offline-cache-miss`, `not-found`, `resolver-error`, …). Cancellation (`OperationCanceledException`) is always rethrown, never swallowed into `Unknown`.

## Report contract

Every `licenses` entry has the same 8 fields (rows sorted by ecosystem, name, version):

| Field | Meaning |
|---|---|
| `ecosystem` | `npm`, `nuget`, `pip`, `go`, `cargo`, `maven`, `gradle`, `composer`, `bundler`, `swift`, `cocoapods`, `vcpkg`, or `conan` |
| `name` | Package name |
| `version` | Version spec from the manifest |
| `spdx` | SPDX id, or null when unresolved |
| `licenseText` | License text, or null when unavailable |
| `sourceUrl` | Provenance URL, or null when unavailable |
| `status` | `Resolved` or `Unknown` |
| `reason` | Why unresolved (null when resolved) |

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
```

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success (including non-`--strict` runs with `Unknown` licenses; `--help`/`--version` also `0`) |
| `1` | `--strict` found unresolved/`Unknown` licenses, or `--allow`/`--deny` policy-gate offenders |
| `2` | Usage/IO error: missing `--input`, input not found, unsupported `--format`/`--ecosystem`, `--out` exists without `--force`, scan/write failure |

## Output contract

- Report goes to stdout when `--out` is omitted (machine-parseable; e.g. stdout is pure JSON with `--format json`).
- When `--out <file>` is given the report goes to the file and stdout stays empty.
- Errors, the `--strict` notice, and `--verbose` logs go to stderr; `--version` prints to stdout.
