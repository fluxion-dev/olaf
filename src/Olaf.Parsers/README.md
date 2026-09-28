# Olaf.Parsers

Ecosystem parsers (`IEcosystemParser` implementations).

Transitive surfacing (implemented, issue #66): every parser sets `Dependency.IsTransitive`
(manifest entries direct, lock entries transitive per ecosystem — see the root README
"Transitive semantics" table; `maven`/`gradle`/`vcpkg`
entries are false-only), surfaced on the report as the `direct` bool (`Direct => !IsTransitive`,
always last). Same-triple dedup in `ParserRegistry` is direct-wins.

Implemented:

- Transitive dependencies via lockfiles (`package-lock.json`, `packages.lock.json`, `poetry.lock`, …).
- Recursive directory scan.
