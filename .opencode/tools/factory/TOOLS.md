# Factory Tool Registry

| Tool | Version | Status | Purpose | Usage | Saves |
| ---- | ------- | ------ | ------- | ----- | ----- |
| `gh-issue-queue.sh` | 0.2.0 | active | Factory loop step 1: OPEN issue queue | `./.opencode/tools/factory/gh-issue-queue.sh [--json]` | Replaces repeated `gh issue list` flag lookup |
| `dotnet-test-fast.sh` | 0.2.0 | active | Fast test gate for tester/QA | `./.opencode/tools/factory/dotnet-test-fast.sh [--filter <expr>]` | Replaces repeated `dotnet test --verbosity minimal` plumbing |
| `scan-fixture-dump.sh` | 0.1.0 | active | Nested fixture scan dump (npm root + deep pip) for issue #1 | `./.opencode/tools/factory/scan-fixture-dump.sh [--workdir <dir>]` | Replaces re-emitting nested fixture build + full/pypi scan pipeline (~40 tokens/run) |
| `resolve-reliability-probe.sh` | 0.1.0 | active | Reliability probe for issue #2: phantom package → Unknown (non-strict 0, strict 1) | `./.opencode/tools/factory/resolve-reliability-probe.sh [--timeout <secs>]` | Replaces re-emitting strict-fixture build + scan pipeline (~500 tokens/run) |

Add/modify rows whenever `factory-toolbuilder` promotes or evolves a script.
Changelog: 0.2.0 — added `VERSION` headers + fluid modify/deprecate lifecycle.
Changelog: 0.1.0 — promote `resolve-reliability-probe.sh` (issue #2, reused 4x: toolbuilder/tester/QA/final-QA).
