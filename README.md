# olaf

License scanner: `olaf generate <DIR>` scans a project directory, resolves licenses, writes a report to stdout or a file.

Supported ecosystems (13): `npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan` (`pypi` alias for `pip`). Supported formats: `json|yaml|xml|md|cyclonedx-json|cyclonedx-xml|spdx-json` (7, zero aliases).

## Tool install

No .NET install required: each `v*` tag builds self-contained single-file
binaries (`.github/workflows/release.yml`, matrix `linux-x64|osx-arm64|win-x64`)
and attaches the three bundles to that tag's GitHub Release:

| RID | Binary | Size (publish from source) |
|-----|--------|----------------------------|
| `linux-x64` | `Olaf.Cli` | 75,006,505 bytes (~71.5 MiB) |
| `osx-arm64` | `Olaf.Cli` | 81,467,641 bytes (~77.7 MiB) |
| `win-x64` | `Olaf.Cli.exe` | 74,966,246 bytes (~71.5 MiB) |

```bash
chmod +x ./Olaf.Cli
./Olaf.Cli --version
```

No tagged release yet (or your platform is missing)? Publish from source
(requires the .NET 10 SDK; `<rid>` is one of the three RIDs above):

```bash
dotnet publish src/Olaf.Cli -c Release -p:PublishRID=<rid>
src/Olaf.Cli/bin/Release/net10.0/<rid>/publish/Olaf.Cli --version
```

(`Olaf.Cli.exe` on `win-x64`. The `linux-x64` apphost runs with no `dotnet`
on `PATH`; `win-x64`/`osx-arm64` bundles are existence+size verified here,
runtime covered by CI.)

Alternative — .NET tool (needs a .NET runtime to run):

```bash
dotnet pack src/Olaf.Cli -c Release
dotnet tool install --global --add-source ./src/Olaf.Cli/bin/Release olaf --version 0.1.0-preview.1
olaf --help
```

Version is pinned: `--version 0.1.0-preview.1` is required — unpinned install fails for prerelease versions. Tests: 664 passing (`dotnet test`).

## Usage

Quickstart (both exit `0`):

```bash
dotnet run --project src/Olaf.Cli -- generate tests/Olaf.Tests/Fixtures/npm --format json
dotnet run --project src/Olaf.Cli -- generate . --format json --offline
```

`generate [PATH]` is the only command. `PATH` defaults to `.` (current directory); `--format` defaults to `json`; the report goes to stdout unless `--out` is given.

Flags: exactly these five plus `PATH` (`--format`, `--out`, `--force`, `--strict`, `--offline` — matches live `generate --help` verbatim):

```text
--format <format>  Output format: json|yaml|xml|md|cyclonedx-json|cyclonedx-xml|spdx-json (default: json) [default: json]
--out <out>        Output file path (default: stdout; parent directories are created)
--force            Overwrite output file if it exists
--strict           Fail (exit 1) on unknown licenses
--offline          Resolve licenses from the embedded offline DB only (no network; unknown licenses stay Unknown)
```

There are no other flags: `--input`, `--template`, `--ecosystem`, `--cache-dir`, `--no-cache`, `--refresh-cache`, `--cache-ttl-days`, `--allow`, `--deny`, `--rules`, `--direct-only`, `--include-transitive`, `--group-by-license`, `--max-image-mb`, `--verbose`, and `--quiet` were removed (unknown option → exit `2`).

File output (exit `0`, stdout stays empty; fails if the file exists unless `--force`):

```bash
dotnet run --project src/Olaf.Cli -- generate tests/Olaf.Tests/Fixtures/npm --format json --offline --out /tmp/olaf-report.json --force
```

Empty directory: `generate <empty-dir>` emits a valid empty report and exits `0`:

```bash
mkdir -p /tmp/olaf-empty
dotnet run --project src/Olaf.Cli -- generate /tmp/olaf-empty --format json --offline
# {"summary":{"total":0,"resolved":0,"unknown":0},"licenses":[]}
```

## Formats

| Format | Output |
|--------|--------|
| `json` | `{"summary":{"total":…,"resolved":…,"unknown":…},"licenses":[…]}`; 9 base keys (`ecosystem`, `name`, `version`, `spdx`, `licenseText`, `sourceUrl`, `status`, `reason`, `direct` last) (+ optional `purl|supplier|downloadUrl|hashes` enrichment keys after `direct`, omitted when null), sorted `ecosystem → name → version` |
| `yaml` | Same shape as `json`, YAML-encoded |
| `xml` | Same 9 base fields as `json`, XML-encoded (`<report><summary …/><licenses>…`) (enrichment omitted) |
| `md` | Human-readable attribution report (header counts + per-package sections) |
| `cyclonedx-json` | CycloneDX 1.5 SBOM |
| `cyclonedx-xml` | Same CycloneDX 1.5 data as JSON, XML-encoded |
| `spdx-json` | SPDX 2.3 SBOM |

One example per format (all exit `0`):

```bash
dotnet run --project src/Olaf.Cli -- generate tests/Olaf.Tests/Fixtures/npm --format json --offline
dotnet run --project src/Olaf.Cli -- generate tests/Olaf.Tests/Fixtures/npm --format yaml --offline
dotnet run --project src/Olaf.Cli -- generate tests/Olaf.Tests/Fixtures/npm --format xml --offline
dotnet run --project src/Olaf.Cli -- generate tests/Olaf.Tests/Fixtures/npm --format md --offline
dotnet run --project src/Olaf.Cli -- generate tests/Olaf.Tests/Fixtures/npm --format cyclonedx-json --offline
dotnet run --project src/Olaf.Cli -- generate tests/Olaf.Tests/Fixtures/npm --format cyclonedx-xml --offline
dotnet run --project src/Olaf.Cli -- generate tests/Olaf.Tests/Fixtures/npm --format spdx-json --offline
```

Anything else (e.g. `--format bogus`, `txt`, `html`, `markdown`, `cyclonedx`) exits `2`:

```bash
dotnet run --project src/Olaf.Cli -- generate tests/Olaf.Tests/Fixtures/npm --format bogus --offline
# Unsupported format 'bogus'. Supported: json|yaml|xml|md|cyclonedx-json|cyclonedx-xml|spdx-json.
```

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success — including runs with `Unknown` licenses (without `--strict`), empty-directory empty reports, `--help`/`--version` |
| `1` | `--strict` found `Unknown` licenses (report is still written first) |
| `2` | Usage/IO error: path not found, single-file input, unsupported `--format`, unknown option, `--out` exists without `--force`, scan/write failure |

Strict gate (exit `1` on any `Unknown` row):

```bash
dotnet run --project src/Olaf.Cli -- generate tests/Olaf.Tests/Fixtures/npm --format json --offline --strict
```

## Offline / air-gap (`--offline`)

`--offline` resolves licenses from the embedded SPDX DB only — zero HTTP. A miss stays `Unknown` (exit `0`, or exit `1` with `--strict`). Reason strings vary with network, so never golden-match them.

```bash
sha256sum -c tools/spdx-db.sha256
```

The license-resolution cache is internal and always on (in-memory + on-disk, no flags). A stale on-disk cache starts empty with a stderr warning — never an error.

## Help

```bash
dotnet run --project src/Olaf.Cli -- generate --help
```

Full command shape: `olaf generate <DIR> [--format <fmt>] [--out <file>] [--force] [--strict] [--offline]`.
