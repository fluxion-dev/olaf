# Factory Tool Registry

| Tool | Version | Status | Purpose | Usage | Saves |
| ---- | ------- | ------ | ------- | ----- | ----- |
| `gh-issue-queue.sh` | 0.2.0 | active | Factory loop step 1: OPEN issue queue | `./.opencode/tools/factory/gh-issue-queue.sh [--json]` | Replaces repeated `gh issue list` flag lookup |
| `dotnet-test-fast.sh` | 0.2.0 | active | Fast test gate for tester/QA | `./.opencode/tools/factory/dotnet-test-fast.sh [--filter <expr>]` | Replaces repeated `dotnet test --verbosity minimal` plumbing |
| `scan-fixture-dump.sh` | 0.1.0 | active | Nested fixture scan dump (npm root + deep pip) for issue #1 | `./.opencode/tools/factory/scan-fixture-dump.sh [--workdir <dir>]` | Replaces re-emitting nested fixture build + full/pypi scan pipeline (~40 tokens/run) |
| `resolve-reliability-probe.sh` | 0.1.1 | active | Reliability probe for issue #2: phantom package → Unknown (non-strict 0, strict 1) | `./.opencode/tools/factory/resolve-reliability-probe.sh [--timeout <secs>]` | Replaces re-emitting strict-fixture build + scan pipeline (~500 tokens/run) |
| `format-matrix-dump.sh` | 0.1.0 | active | Format matrix for issue #3: one fixture x 4 formats, 8-field + counts check | `./.opencode/tools/factory/format-matrix-dump.sh [--format <f>]` | Replaces re-emitting 4x dotnet-run + grep pipeline (~60-80 tokens/run) |
| `cli-ux-probe.sh` | 0.1.0 | active | CLI UX probe for issue #4: --help/--version, exit-code matrix (missing-input/bad-format/bad-ecosystem/out-exists→2, nested-out→0, strict→1), stdout-vs-stderr split | `./.opencode/tools/factory/cli-ux-probe.sh [--timeout <secs>] [--fixture <dir>]` | Replaces re-emitting ~10x dotnet-run exit-code + split pipeline (~120 tokens/run) |
| `policy-gate-probe.sh` | 0.1.0 | active | Policy gate probe for issue #5: strict-phantom fixture (non-strict→0, strict→1+stderr, --allow present→0 / --deny→1+offender / --allow mismatch→1, SKIP when flags absent) | `./.opencode/tools/factory/policy-gate-probe.sh [--timeout <secs>] [--keep-temp]` | Replaces re-emitting strict-fixture + allow/deny gate pipeline (~80 tokens/run) |
| `pip-lock-probe.sh` | 0.1.0 | active | Pipfile.lock probe for issue #6: canonical lock (stripped versions, VCS/local→*, count 7, default-wins), malformed→0/0, dir-preference lock-only 7, IsTransitive=false static check | `./.opencode/tools/factory/pip-lock-probe.sh [--timeout <secs>] [--workdir <dir>] [--keep-temp]` | Replaces re-emitting synth-lock + 3x dotnet-run + rival-manifest pipeline (~150 tokens/run) |
| `parser-coverage-probe.sh` | 0.1.0 | active | README parser-coverage drift probe: CanHandle sets + Supported consts + tier comments + --help vs README L5/L7 (FAIL on missing/order/notes, WARN on abbreviation drift) | `./.opencode/tools/factory/parser-coverage-probe.sh` | Replaces re-emitting grep-CanHandle + const + README-diff pipeline (~120 tokens/run) |
| `image-scan-probe.sh` | 0.1.0 | active | Container-image scan probe for issue #64: synth docker-save tar + OCI layout dir, counts/exit codes (C1-C7: valid→0, cap/corrupt→2, empty→0/0) | `./.opencode/tools/factory/image-scan-probe.sh [--timeout <secs>] [--workdir <dir>] [--keep-temp]` | Replaces re-emitting synth-tar + 7x dotnet-run + count/exit pipeline (~150 tokens/run) |
| `catch-guard-probe.sh` | 0.1.0 | active | CS0160 catch guard: FAIL on catch(File/DirectoryNotFound), WARN on when-FileNotFound w/o `// allowlist:`, FAIL per catch(IOException) w/o covering comment | `./.opencode/tools/factory/catch-guard-probe.sh` | Replaces re-emitting catch-grep + comment-window pipeline (~60 tokens/run) |
| `test-count-probe.sh` | 0.1.0 | active | README Tests:N docs gate: live `dotnet test` count vs documented (FAIL on mismatch, never edits docs) | `./.opencode/tools/factory/test-count-probe.sh` | Replaces re-emitting README-grep + full-test-count pipeline (~40 tokens/run) |

Add/modify rows whenever `factory-toolbuilder` promotes or evolves a script.
Changelog: 0.2.0 — added `VERSION` headers + fluid modify/deprecate lifecycle.
Changelog: 0.1.0 — promote `resolve-reliability-probe.sh` (issue #2, reused 4x: toolbuilder/tester/QA/final-QA).
Changelog: 0.1.0 — promote `format-matrix-dump.sh` (issue #3, reused 4x: toolbuilder/tester/QA/final-QA).
Changelog: 0.1.0 — promote `cli-ux-probe.sh` (issue #4; prototype run: 12/14 PASS, correctly flags nested-out→2-want-0 defect).
Changelog: 0.1.0 — promote `policy-gate-probe.sh` (issue #5; prototype run: (a)+(b) PASS, (c)-(e) SKIP — allow/deny not in --help yet).
Changelog: 0.1.0 — promote `pip-lock-probe.sh` (issue #6; full run: 19/19 PASS + PIP-LOCK-PROBE OK).
Changelog: 0.1.0 — promote `parser-coverage-probe.sh` (README L7 stale watch; first run: exit 0, all filenames/tiers/notes PASS + expected abbreviation WARN).
Changelog: 0.1.0 — promote `image-scan-probe.sh` (issue #64; first run: exit 0, 8/8 PASS; fixed scratch→factory ROOT depth 4-ups→3-ups).
Changelog: 0.2.0 — modify `_template.sh` (add canonical root-resolution + pass()/fail_msg() + KEEP_TEMP/mktemp/trap-cleanup fragments).
Changelog: 0.1.1 — modify `resolve-reliability-probe.sh` (replace hardcoded /home/brad/personal-repos/olaf fallback with script-dir fallback, additive no flag change).
Changelog: 0.1.0 — promote `catch-guard-probe.sh` (catch blocks touched #62-64; first run: exit 1 — check 1 PASS no re-introduction, 1 WARN at ContainerImageParser.cs:388 no allowlist, 40 FAIL bare catch(IOException) w/o covering comment + 6 PASS).
Changelog: 0.1.0 — promote `test-count-probe.sh` (README Tests:N stale 2+ issues; first run: exit 1 — README 327 vs live 551 passed; probe FAILs only, docs fix left to docs agent).
