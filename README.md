# olaf

License scanner scaffold (greenfield, no feature logic yet).

```bash
dotnet run --project src/Olaf.Cli -- --help
```

## Reliability

Resolution runs with bounded-8 concurrency and retry-once on transient HTTP failures (timeout/transport/408/429/5xx). Unresolved packages return `Unknown` with reason tokens (`offline-cache-miss`, `not-found`, `resolver-error`, …). Cancellation (`OperationCanceledException`) is always rethrown, never swallowed into `Unknown`.

## Report contract

Every `licenses` entry has the same 8 fields (rows sorted by ecosystem, name, version):

| Field | Meaning |
|---|---|
| `ecosystem` | `npm`, `nuget`, or `pip` |
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

Empty scan: `total`/`resolved`/`unknown` are all `0`; JSON/YAML emit an empty `licenses` list, XML emits `<licenses />`, HTML emits an empty `<tbody>`.

```bash
dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/npm/package.json --format json
```

Flags: `--format json|yaml|xml|html` (default `json`), `--ecosystem npm|nuget|pip`. Exit codes: `0` success, `1` `--strict` found unknown licenses, `2` usage/IO error (missing input, bad flag, unwritable `--out`).
