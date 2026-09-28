# olaf

License scanner: scans `npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan|apk|dpkg|rpm` projects, resolves licenses, writes a report to stdout or a file.

Supported ecosystems: `npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan|apk|dpkg|rpm` (`pypi` alias for `pip`). Supported formats: `json|yaml|xml|html|txt|md|cyclonedx-json|cyclonedx|cyclonedx-xml|spdx-json` (`markdown` alias for `md`, `cyclonedx` alias for `cyclonedx-json`; `cyclonedx-xml` has no alias, `spdx-json` has no alias, `cyclonedx` stays JSON).

Parser coverage: `npm` handles `package.json|package-lock.json|pnpm-lock.yaml|yarn.lock|bun.lock` (`bun.lockb` binary yields empty; any lock beats manifest, all locks merge deduped, lock entries transitive); `pip` handles `Pipfile.lock|requirements.txt|pyproject.toml|poetry.lock|uv.lock|environment.yml|environment.yaml` (preference `Pipfile.lock` authoritative-first (empty lock falls through to poetry/uv tier), then `poetry.lock`/`uv.lock` merged > `requirements.txt` > `pyproject.toml` > `environment.yml|environment.yaml`; `Pipfile.lock` entries `IsTransitive=false`; `hashes[]` validated but parsed-but-deferred — not stored on the report; conda entries reported as `pip`); `go` handles `go.mod|go.sum` (2 lines per module in `go.sum` deduped to one dep; `// indirect` + present in `go.sum` → transitive, `// indirect` + absent → direct fallback, `go.mod`-only dir keeps legacy `// indirect` → transitive, `go.sum`-only dir yields all-transitive deps; `h1:` hashes syntactically validated but parsed-but-deferred — not stored on the 9-field report, SBOM enrichment follow-up). `apk` handles `installed` (`lib/apk/db/installed`: blank-line-separated stanzas with `P:`/`V:` fields; versions verbatim including `-r0`; `L:` (declared license) + `A:` (arch) validated-but-deferred — presence never breaks parsing, values not stored, deferred to issue #70; entries `IsTransitive=false`; malformed yields empty, never throws; downstream resolves `Unknown` with `license-unknown: unsupported ecosystem.`); `dpkg` handles `status` (`var/lib/dpkg/status`: blank-line-separated stanzas with `Package:`/`Version:` fields; versions verbatim including epoch; `Architecture:` validated-but-deferred — same terms, deferred to issue #70; entries `IsTransitive=false`; malformed yields empty, never throws; downstream resolves `Unknown` with `license-unknown: unsupported ecosystem.`); `rpm` handles `Packages` text dumps (`var/lib/rpm/Packages` or `usr/lib/sysimage/rpm/Packages`: one `name-ver-rel.arch` NVRA line per package, `rpm -qa` default output; arch stripped after the last `.`, version is `ver-rel` joined verbatim; binary (BerkeleyDB) input yields empty, never throws; entries `IsTransitive=false`; downstream resolves `Unknown` with `license-unknown: unsupported ecosystem.`); `container` scans image tarballs (`*.tar|*.tar.gz|*.tgz` with top-level `manifest.json`/`index.json` markers) and exploded OCI/docker-save layout dirs (`manifest.json|index.json|oci-layout` plus layer blobs) and reports the image's packages as `apk`/`dpkg`/`rpm` entries (`IsTransitive=false`; layers apply bottom→top with OCI whiteouts — basename prefix `.wh.`, `.wh..wh..opq` clears the directory — and topmost layer wins for the same package; absolute/`..` entries and symlinks/hardlinks are skipped; RPM `Packages` text dumps are routed to the `rpm` parser while BINARY rpm bytes still yield empty (binary format deferred to issue #70); uncompressed bytes are capped by `--max-image-mb`, default `1024`; corrupt/truncated tarballs and non-image tars fail with exit `2`, never silent empty).

## Tool install

```bash
dotnet pack src/Olaf.Cli -c Release
dotnet tool install --global --add-source ./src/Olaf.Cli/bin/Release olaf --version 0.1.0-preview.1
olaf --help
```

Version is pinned: `--version 0.1.0-preview.1` is required — unpinned install fails for prerelease versions. Tests: 674 passing (`dotnet test`).

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
# CycloneDX SBOM (spec 1.5; `cyclonedx` alias works too)
dotnet run --project src/Olaf.Cli -- --input package.json --format cyclonedx-json
# CycloneDX SBOM as XML (spec 1.5, same data as JSON; `cyclonedx` stays JSON)
dotnet run --project src/Olaf.Cli -- --input package.json --format cyclonedx-xml
# SPDX SBOM (spec 2.3; `spdx-json` has no alias)
dotnet run --project src/Olaf.Cli -- --input package.json --format spdx-json
# direct-only filter (report direct dependencies only; counts recompute on the filtered set)
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/go --format json --direct-only
# allow-list gate
dotnet run --project src/Olaf.Cli -- --input package.json --strict --allow MIT,Apache-2.0
```

Flags: `--input <file|dir>`, `--format json|yaml|xml|html|txt|md|cyclonedx-json|cyclonedx|cyclonedx-xml|spdx-json` (default `json`; `markdown` alias for `md`, `cyclonedx` alias for `cyclonedx-json`; `cyclonedx-xml` has no alias, `spdx-json` has no alias), `--out <file>` (default stdout), `--force`, `--strict`, `--allow <csv>`, `--deny <csv>`, `--direct-only`, `--include-transitive`, `--ecosystem npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan|apk|dpkg|rpm` (`pypi` alias for `pip`), `--max-image-mb <n>` (container-image cap in MB of uncompressed bytes handled, default `1024`; must be `> 0`, missing/invalid → exit `2`), `--verbose`, `--quiet`, `--help`, `--version` (built-in).
`--out` parent directories are auto-created; `--out` fails if the file exists unless `--force` is given.
`--allow` is a comma-separated SPDX allow-list (fail licenses not in the list); `--deny` is a comma-separated SPDX deny-list (fail licenses in the list). `--allow`/`--deny` without `--strict` warns on stderr but still enforces the policy gate.
Transitive filter: neither flag (default) reports all dependencies; `--direct-only` reports direct dependencies only (`direct == true`); `--include-transitive` explicitly reports all (same result as neither, documents intent). `--direct-only` + `--include-transitive` together is a usage conflict (stderr + exit `2`). The filter runs post-scan/pre-format so `summary` counts recompute on the filtered set, and the `--strict`/`--allow`/`--deny` gates see the FILTERED set.

`--help` excerpt (via `dotnet run --project src/Olaf.Cli -- --help`, exit `0`):

```text
--format <format>        Output format: json|yaml|xml|html|txt|md|cyclonedx-json|cyclonedx|cyclonedx-xml|spdx-json (default: json) [default: json]
--ecosystem <ecosystem>  Limit scan to ecosystem: npm|nuget|pip|go|cargo|maven|gradle|composer|bundler|swift|cocoapods|vcpkg|conan|apk|dpkg|rpm (pypi alias for pip)
--max-image-mb <max-image-mb>  Cap container-image scan at N megabytes uncompressed handled (default: 1024; must be > 0)
--strict                 Fail on unresolved or unknown licenses
--direct-only            Report direct dependencies only (exclude transitive; strict/allow/deny gates see the filtered set)
--include-transitive     Explicitly include transitive dependencies (same as default: report all)
```

## Reliability

Resolution runs with bounded-8 concurrency and retry-once on transient HTTP failures (timeout/transport/408/429/5xx). Unresolved packages return `Unknown` with reason tokens (`offline-cache-miss`, `not-found`, `resolver-error`, …). Cancellation (`OperationCanceledException`) is always rethrown, never swallowed into `Unknown`.

## Report contract

Every `licenses` entry has the same 9 fields (rows sorted by ecosystem, name, version; `direct` is always LAST):

| Field | Meaning |
|-------|---------|
| `ecosystem` | `npm`, `nuget`, `pip`, `go`, `cargo`, `maven`, `gradle`, `composer`, `bundler`, `swift`, `cocoapods`, `vcpkg`, `conan`, `apk`, `dpkg`, or `rpm` (container-image scans report their packages as `apk`/`dpkg`/`rpm` entries) |
| `name` | Package name |
| `version` | Version spec from the manifest |
| `spdx` | SPDX id, or null when unresolved |
| `licenseText` | License text, or null when unavailable |
| `sourceUrl` | Provenance URL, or null when unavailable |
| `status` | `Resolved` or `Unknown` |
| `reason` | Why unresolved (null when resolved) |
| `direct` | `true` when direct (`!IsTransitive`), `false` when transitive — always last |

### Transitive semantics (`direct` / `IsTransitive`)

`direct` surfaces `Dependency.Direct` (`!IsTransitive`). Per-ecosystem rules (unchanged by #66 except dedup):

| Ecosystem | Direct (`direct: true`) | Transitive (`direct: false`) | Notes |
|-----------|-------------------------|------------------------------|-------|
| `npm` | `package.json` manifest entries | `package-lock.json` / `pnpm-lock.yaml` / `yarn.lock` / `bun.lock` lock entries (all) | Lock-all-transitive is a heuristic (direct deps re-listed in a lock still surface as transitive); any lock beats manifest, all locks merge deduped |
| `pip` | `Pipfile.lock`, `requirements.txt`, `pyproject.toml`, `environment.yml`/`environment.yaml` entries | `poetry.lock` / `uv.lock` (TOML `[[package]]`) entries | `Pipfile.lock` stays `IsTransitive=false` (matches #63 pinned tests); conda entries reported as `pip` |
| `go` | `go.mod` entries without `// indirect`, or `// indirect` + absent from `go.sum` (direct fallback) | `// indirect` + present in `go.sum` → transitive; `go.mod`-only dir keeps legacy `// indirect` → transitive; `go.sum`-only dir yields all-transitive deps | AND-table: `IsTransitive = manifest-indirect && in-go.sum`; 2 `go.sum` lines per module deduped to one dep |
| `cargo` | `Cargo.toml` manifest entries | `Cargo.lock` entries (all) | Lock-all-transitive heuristic, same direct-conflation caveat as npm |
| `nuget` | manifest / `packages.lock.json` direct entries | lock entries resolved `!direct` | `packages.lock.json` carries its own direct marker |
| `composer` | `composer.json` manifest entries | `composer.lock` entries (all) | — |
| `bundler` | `Gemfile` / `*.gemspec` manifest entries | `Gemfile.lock` `PATH`/`GEM` remote entries (all) | — |
| `swift` | `Package.swift` manifest entries | `Package.resolved` entries (all) | — |
| `cocoapods` | `Podfile` manifest entries | `Podfile.lock` entries (all) | — |
| `conan` | `conanfile.txt` / `conanfile.py` manifest entries | `conan.lock` entries (all) | — |
| `maven` | all entries | — (false-only) | `pom.xml` has no transitive marker; everything reports `direct: true` |
| `gradle` | all entries | — (false-only) | `build.gradle` / lockfile entries all `direct: true` |
| `vcpkg` | all entries | — (false-only) | `vcpkg.json` entries all `direct: true` |
| `apk` / `dpkg` / `rpm` / `container` | all entries | — (false-only) | OS DB rows and image-layer packages report `direct: true` |

Dedup (registry): same `(ecosystem, name, version)` triple from manifest + lock (or overlapping locks) collapses to one row with **direct-wins** — the `IsTransitive: false` survivor is kept, so a direct listing beats a transitive duplicate. Sort stays `ecosystem → name → version`.

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

- JSON: `{"summary":{"total":…,"resolved":…,"unknown":…},"licenses":[…]}` with 9 keys per entry (`direct` last).
- YAML: `summary:` with `total`/`resolved`/`unknown` plus a `licenses:` list with the same 9 keys (`direct: true|false` last).
- XML: `<report><summary total="…" resolved="…" unknown="…"/><licenses><license>` with the 9 fields as child elements (`<direct>` last).
- HTML: `<p>Total: … · Resolved: … · Unknown: …</p>` plus a 9-column table (Ecosystem, Name, Version, SPDX, License, Source, Status, Reason, Direct; the SPDX cell falls back to status when `spdx` is null).
- TXT: `Third-Party Attribution` header with `Total: …, Resolved: …, Unknown: …` plus one `name@version (ecosystem) direct=<true|false>` block per package (SPDX falls back to `Unknown`, plus source/status/reason lines).
- MD (`markdown` alias): `# Third-Party Attribution` header with `Total: …, Resolved: …, Unknown: …`, a 9-column markdown table (`| Direct |` last), plus one `## name@version (ecosystem)` section per package carrying all 9 fields (`- Direct: true|false` last).
- CycloneDX JSON (`cyclonedx-json`, `cyclonedx` alias): CycloneDX 1.5 SBOM — see `### CycloneDX JSON export` below.
- CycloneDX XML (`cyclonedx-xml`, no alias): same CycloneDX 1.5 data as JSON, XML-encoded — see `### CycloneDX XML export` below.
- SPDX JSON (`spdx-json`, no alias): SPDX 2.3 SBOM — see `### SPDX JSON export` below.

Empty scan: `total`/`resolved`/`unknown` are all `0`; JSON/YAML emit an empty `licenses` list, XML emits `<licenses />`, HTML emits an empty `<tbody>`, CycloneDX JSON emits an empty `components` array (envelope + `olaf:*` zero counts still present), CycloneDX XML emits `<components />` (envelope + `olaf:*` zero counts still present), SPDX JSON emits empty `packages` with a single self-`DESCRIBES` relationship (`SPDXRef-DOCUMENT` → `SPDXRef-DOCUMENT`), `documentDescribes: ["SPDXRef-DOCUMENT"]`, and the `olaf:total=0/resolved=0/unknown=0` counts comment still present.

### CycloneDX JSON export

`--format cyclonedx-json` (`cyclonedx` alias) emits a CycloneDX 1.5 SBOM (`{"bomFormat":"CycloneDX","specVersion":"1.5","version":1,…}`):

- `serialNumber` is `urn:uuid:<guid>`, fresh on every run; `metadata.timestamp` is an ISO-8601 UTC timestamp (never golden-match either in tests).
- `metadata.tools` is `[{vendor: olaf, name: olaf, version: <assembly>}]`; `metadata.component` is the constant `{type: application, name: olaf-scan}` (the formatter only sees the `ScanResult`, so the input path is unavailable at that layer).
- `metadata.properties` carries the report counts (`olaf:total` / `olaf:resolved` / `olaf:unknown`, always equal to the `ScanResult` counts, so `--direct-only` filtering is reflected).
- `components[]` is sorted `ecosystem → name → version`, one entry per license row with `type: library`:
  - `scope` is `required` when direct, `optional` when transitive.
  - `bom-ref` mirrors the CLI key `{ecosystem}:{name}@{version}`; duplicate triples get `-2`, `-3`, … suffixes so every ref is unique.
  - `licenses`: single-token SPDX → `{"license":{"id":"…"}}`; multi-word SPDX → `{"license":{"name":"…"}}`; `Unknown` → empty `[]` plus `properties` entries `olaf:status`, `olaf:reason`, and `olaf:sourceUrl` (only when a source URL exists).
  - `purl`: `npm` → `pkg:npm/…` (scoped `@scope/name` encodes `@` as `%40`), `pip`/`pypi` → `pkg:pypi/…`, `go` → `pkg:golang/…`, `maven`/`gradle` → `pkg:maven/<group>/<artifact>…` (split on the first `:`; a bare name without `:` falls back to `pkg:maven/<name>…`), everything else → `pkg:generic/…` (never throws). `maven`/`gradle` entries with `group:artifact` coordinates also carry a separate `group` field.

### CycloneDX XML export

`--format cyclonedx-xml` (no alias; `cyclonedx` stays JSON) emits the same CycloneDX 1.5 data as JSON, XML-encoded. Both formatters share one mapper (`CycloneDxComponentMapper`: sort, `bom-ref` dedup, purl, license rule, counts), so JSON↔XML field parity holds per component:

- The spec version rides in the namespace — `<bom xmlns="http://cyclonedx.org/schema/bom/1.5" serialNumber="urn:uuid:…" version="1">` with NO `specVersion` attribute. Child order is pinned: `metadata` then `components`; `metadata` children are `timestamp`, `tools`, `component`, `properties`.
- `scope` is an ELEMENT, always emitted (`required` when direct, `optional` when transitive) — never an attribute. Component child order is pinned: `group?`, `name`, `version`, `scope`, `licenses?`, `purl`, `properties?` (`purl` is unconditional; `group` only on `maven`/`gradle` `group:artifact` coordinates).
- Properties carry values as element text (`<property name="olaf:total">3</property>`), not attributes — same `olaf:total` / `olaf:resolved` / `olaf:unknown` counts (filtered-set aware) plus per-component `olaf:status`, `olaf:reason`, `olaf:sourceUrl` (only when a source URL exists) on `Unknown` rows.
- Escaping is owned by `XElement`/`XmlWriter` — values are never pre-encoded (double-escape ban), so `&<>"'` round-trip through a parse-back. Invalid XML control chars (e.g. `\u0001`) are stripped, never thrown.

### SPDX JSON export

`--format spdx-json` (no alias; `spdx` is rejected with exit `2`) emits an SPDX 2.3 SBOM (`{"spdxVersion":"SPDX-2.3","dataLicense":"CC0-1.0","SPDXID":"SPDXRef-DOCUMENT","name":"olaf-scan",…}`):

- `spdxVersion` is `SPDX-2.3`, `dataLicense` is `CC0-1.0`, `SPDXID` is the pinned literal `SPDXRef-DOCUMENT`, `name` is the constant `olaf-scan`. `documentNamespace` is `https://olaf.example/sbom/<uuid>`, fresh on every run; `creationInfo.created` is an ISO-8601 UTC timestamp and `creationInfo.creators` is `["Tool: olaf <assembly>"]` (never golden-match namespace/created in tests).
- `packages[]` is sorted `ecosystem → name → version`, one entry per license row with child order pinned: `SPDXID`, `name`, `versionInfo`, `supplier`, `downloadLocation`, `filesAnalyzed`, `licenseConcluded`, `licenseDeclared`, `copyrightText`, `externalRefs`.
- `SPDXID` is positional (`SPDXRef-Package-1..N` in sort order): the `bom-ref` charset (`eco:name@ver`) is illegal for SPDXID, and positional IDs avoid sanitize collisions.
- `licenseConcluded`: single-token SPDX ids pass through, SPDX expressions with `AND`/`OR`/`WITH` (parens allowed) pass through; everything else — `Unknown`/`NONE`/`NOASSERTION`/null/empty/whitespace or multi-word text (e.g. `My Custom License`) — collapses to `NOASSERTION` (never `NONE`, never `""`, never an `Unknown` string leak). `licenseDeclared` always mirrors `licenseConcluded` (the resolver raw string no longer exists at the formatter layer, so no model change).
- `copyrightText`, `supplier`, and `downloadLocation` are the literal `NOASSERTION` on every package (pre-issue #72: no copyright scraping; `SourceUrl` is never copied — a registry page is not a download URI). Copyright scraping is owned by issue #72.
- `externalRefs` is unconditional: every package (even `Unknown`) carries one `PACKAGE-MANAGER`/`purl` entry (same purl rule as CycloneDX; never throws).
- `filesAnalyzed` is always `false`.
- Relationships are flat-list honest (no tree is inferred): every package gets BOTH `DESCRIBES` and `CONTAINS` from `SPDXRef-DOCUMENT` (`relationships` count is `2 × packages`, referentially closed); `documentDescribes` lists every package `SPDXID`. An empty scan carries a single self-`DESCRIBES` (`SPDXRef-DOCUMENT` → `SPDXRef-DOCUMENT`) with `documentDescribes: ["SPDXRef-DOCUMENT"]`.
- Counts ride in `comment` (`olaf:total=<n>/resolved=<n>/unknown=<n>`, always equal to the `ScanResult` counts, so `--direct-only` filtering is reflected).
- SPDX 2.3 only: no 3.0 profile/context fields are emitted. A future SPDX-3.0 formatter ships as a separate format, not an extension — this output stays valid 2.3 input for converters (v3.0-ready in that sense, not by emitting 3.0 fields).

### Enrichment (component PURL + hashes + supplier + download)

`ResolvedLicense` carries an optional 7th `Enrichment` record — `Enrichment(Purl, Hashes, Supplier, DownloadUrl)` (default `null`, so unenriched output is byte-stable). `Hashes` entries are `algo:value` strings (e.g. `sha512:…`).

NO-NEW-HTTP rule: enrichment is harvested ONLY from the response body the resolver already fetched for license resolution. No resolver issues a new `GET` for enrichment (the pre-existing license-text fetch stays license-text-only). Absent fields stay `null` (never `""` or fabricated values); when hashes, supplier, and download URL are ALL absent the whole `Enrichment` is `null`, and the purl is only emitted alongside real enrichment data.

Per-resolver availability (payload → fields):

| Resolver | Already-fetched payload | Hashes | Supplier | DownloadUrl |
|----------|-------------------------|--------|----------|-------------|
| `npm` | versioned registry JSON | `dist.integrity` (`algo-base64` → `algo:value`) | `author` then `maintainers[]` (name/email) | `dist.tarball` |
| `nuget` | registration / catalog JSON (catalog doc only when already fetched for license resolution) | `packageHash` + `packageHashAlgorithm` (default `sha512`) | `authors` | `packageContent` |
| `pip` | PyPI JSON API | `urls[].digests{algo: value}` | `info.author` | `urls[]` file URL |
| `cargo` | crates.io JSON | `version.checksum` → `sha256:` | — (null) | `version.dl_path` (absolutized against `https://crates.io`) |
| `composer` | packagist JSON | `dist.shasum` → `sha1:` | `authors[]` (name/email) | `dist.url` |
| `maven` | fetched POM XML | — (null; POM carries no hashes) | `organization.name`, else `developers/developer/name` | project `<url>` (license `<url>` elements are never read) |
| `bundler` | rubygems JSON (PARTIAL) | — (null; versioned sha lives on an unfetched endpoint) | `authors` | `gem_uri` |
| `go` | proxy `.info` / license-file path (PARTIAL) | — (null; `.ziphash` endpoint unfetched) | — (null) | constructed `https://proxy.golang.org/<module>/@v/<version>.zip` (never fetched) |
| `swift` / `cocoapods` / `vcpkg` / `conan` | current endpoints (NULL group) | — | — | — (endpoints lack enrichment data → `Enrichment` null) |
| ClearlyDefined fallback | definitions JSON | `files[]` first `sha256`/`sha` (incl. nested `hashes.sha256`) → `sha256:` | `parties[]` (name, else url, else email; top-level or `licensed.parties`) | — (null) |

Deferred parser hashes: `go.sum` `h1:` hashes and npm-lock `integrity` values are validated-but-deferred — parsed, never stored (`Hashes` stays `null` for them).

PURL mapping (`PurlBuilder.Build` in `Olaf.Core` — the single home shared by resolvers and formatters; `CycloneDxPurl.Build` delegates to it, never forks; never throws, null/empty inputs degrade to the generic fallback):

| Ecosystem input | PURL type | Notes |
|-----------------|-----------|-------|
| `npm` | `pkg:npm/…` | scoped `@scope/name` encodes `@` as `%40`, keeps `/` |
| `pip` / `pypi` (alias) | `pkg:pypi/…` | — |
| `go` / `golang` (alias) | `pkg:golang/…` | — |
| `maven` / `gradle` | `pkg:maven/<group>/<artifact>…` | `group:artifact` split on the first `:`; bare name without `:` falls back to `pkg:maven/<name>…` |
| `nuget` | `pkg:nuget/…` | — |
| `cargo` | `pkg:cargo/…` | — |
| `bundler` / `gem` (alias) | `pkg:gem/…` | — |
| `composer` | `pkg:composer/…` | — |
| `swift` | `pkg:swift/…` | — |
| `cocoapods` | `pkg:cocoapods/…` | — |
| `vcpkg` | `pkg:vcpkg/…` | — |
| `conan` | `pkg:conan/…` | — |
| `apk` / `dpkg` / `rpm` | `pkg:generic/…` | explicit: distro packages have no dedicated purl type here |
| unmapped / null | `pkg:generic/…` | fallback, never throws |

Qualifiers: `PurlBuilder.Build` accepts optional `qualifiers` (emitted as `?k=v&…` when non-empty, e.g. `pkg:npm/express@4.18.2?arch=x64`); null/empty means no suffix. All formatters pass `null` for now.

SBOM consumption (omit-null everywhere — unenriched SBOM output is stable):

- CycloneDX JSON: enriched purl is preferred (else the computed purl); `supplier` → `supplier: {name}`; `Hashes` → `hashes[]` (`{alg, content}`, alg names `SHA-512` style, unknown algos pass through uppercased); `DownloadUrl` → `externalReferences: [{type: distribution, url}]`. Optional order after `group`: `supplier`, `hashes`, `externalReferences`. Unparseable hash entries (no colon, blank halves) are dropped, never emitted, never throw.
- CycloneDX XML: mirrors JSON — `supplier`, `hashes` (`<hash alg="…">`), `externalReferences` (`<reference type="distribution">`) after `<purl>`, before `<properties>`.
- SPDX JSON: `supplier` → `supplier` (`Person: <name>` when enriched, else literal `NOASSERTION`); `DownloadUrl` → `downloadLocation` (else `NOASSERTION`); `Hashes` → `checksums[]` (`{algorithm, checksumValue}`, algorithm names `SHA256` style, unknown algos pass through uppercased without hyphens), omitted entirely when absent. `copyrightText` stays `NOASSERTION` (no copyright scraping).

Legacy structured formats: JSON/YAML emit optional `purl` / `supplier` / `downloadUrl` / `hashes` keys AFTER `direct`, omitted-when-null (unenriched rows keep the 9-field shape).

Fixed-shape human formats consciously omit enrichment: `xml` (fixed-shape report), `html`/`md` (fixed-column tables), `txt` (fixed-line attribution) — SBOM (`cyclonedx-json`/`cyclonedx-xml`/`spdx-json`) and structured (`json`/`yaml`) formats carry enrichment.

Null-tolerance + provenance rules: blank suppliers/URLs normalize to `null`; non-`http(s)` download URLs are rejected to `null`; blank hash entries are dropped. `SourceUrl` (a registry page) is NEVER copied into `DownloadUrl` (a download URI) — when no harvestable download URL exists, `DownloadUrl` stays `null` even though `SourceUrl` is set.

```bash
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/npm/package.json --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/npm --format txt
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/npm --format md
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/npm --format cyclonedx-json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/npm --format cyclonedx-xml
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/npm --format spdx-json
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
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/apk --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/dpkg --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/apk --ecosystem apk --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/dpkg --ecosystem dpkg --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/rpm --format json
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/rpm --ecosystem rpm --format json
# container image (docker-save/OCI tarball or exploded layout dir; apk/dpkg/rpm DBs routed per layer, binary rpm yields empty)
dotnet run --project src/Olaf.Cli -- --input image.tar --format json
dotnet run --project src/Olaf.Cli -- --input ./oci-dir --format json
dotnet run --project src/Olaf.Cli -- --input image.tar --format json --max-image-mb 2048
```

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success (including non-`--strict` runs with `Unknown` licenses; `--help`/`--version` also `0`) |
| `1` | `--strict` found unresolved/`Unknown` licenses, or `--allow`/`--deny` policy-gate offenders |
| `2` | Usage/IO error: missing `--input`, input not found, unsupported `--format`/`--ecosystem`, `--out` exists without `--force`, invalid/missing `--max-image-mb`, corrupt/truncated or non-image container tarball, scan/write failure, conflicting `--direct-only` + `--include-transitive` |

## Output contract

- Report goes to stdout when `--out` is omitted (machine-parseable; e.g. stdout is pure JSON with `--format json`).
- When `--out <file>` is given the report goes to the file and stdout stays empty.
- Errors, the `--strict` notice, and `--verbose` logs go to stderr; `--version` prints to stdout.
