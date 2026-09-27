---
name: olaf-factory
description: Run the olaf software factory. Use when implementing GitHub issues end-to-end via planner, discovery, implement, test, QA, refactor, docs agents with PR delivery.
---

# Olaf Factory — parallel-read / serial-write

Factory loop for this repo (`dotnet`, `olaf.slnx`, `src/Olaf.*/`, `tests/Olaf.Tests/`).
Source of truth: GitHub issues (`gh issue list/view/create`).

## Read vs write contract (applies to orchestrator + all agents)

- **READ (massively parallel, unlimited fan-out):** `Glob`/`Grep`/`Read`, `gh issue list/view`, `gh pr view`, `TOOLS.md`/`README.md`/plan reads, `git diff/status/log`, `dotnet build/test/run` verification runs, probe `--help` + dry runs, QA diff review. Readers NEVER edit files, NEVER `git commit/push/branch`, NEVER `gh issue create/close/pr create/merge`, NEVER create/promote/modify tools.
- **WRITE (strictly serial, one writer at a time, one worktree/branch):** any file edit (`src/`, `tests/`, docs, `.opencode/plans/`, `.opencode/tools/factory/`, agent files), any `gh issue create/close`, `gh pr create/merge`, `git commit/push/branch/worktree add/remove`. Only the scheduled writer runs; all other agents are idle or limited to reads that do not touch the writer's worktree.
- **Build artifacts (`bin/`, `obj/`) are write-noise, not source writes:** parallel `dotnet test` runs must use isolated worktrees or `-p:BaseIntermediateOutputPath` per reader if they overlap a live writer; default is to run read-verify waves on a pinned SHA with the writer idle.

## Factory loop (waves — do not skip, do not reorder)

### Wave 0 — Pick (orchestrator, single read)
1. `./.opencode/tools/factory/gh-issue-queue.sh` (or `gh issue list --limit 20 --json number,title,state,labels`).
2. Zero OPEN → STOP, report "factory idle — no remaining issues".
3. Else pick lowest-numbered OPEN MVP issue. `gh issue view <n> --comments`. Create/find worktree + branch for `<n>` now so all later waves share one path.

### Wave 1 — Read fan-out (orchestrator fans out, all parallel, no writes)
Launch in ONE parallel block (background subagents where possible):
- `factory-planner` → fans out further to `factory-discovery` × areas (parsers, resolvers, formatters, CLI, tests). Each returns files + `path:line` contracts + fixtures + gaps.
- Baseline readers (can be planner sub-fan-out or sibling agents): `TOOLS.md` + `README.md` reuse check, base-suite `dotnet test` result on pinned SHA, risk scan (network tests, alias collisions).
- `factory-toolbuilder` in **analyze-only** mode: report reuse/modify/create candidate, do NOT write scratch yet.
- `factory-qa` in **pre-flight read** mode (optional): scope risks on base, no verdict.

Merge rule: orchestrator waits for ALL readers, synthesizes into the serial plan, then `factory-planner` performs the ONE serial write of this wave: `.opencode/plans/issue-<n>.md` (implement → test → cleanup → docs → verify → PR → merge + retro, each step with done-criteria + verification command). Planner MUST `Read` the last 3 `retro-<n>.md` files first and cite reused tools + applied lessons in the plan. No coding starts until the plan file exists. Pin `base SHA` in the plan for Wave 3 comparison.

### Wave 2 — Serial write chain (one agent at a time, in order, exclusive worktree)
Run strictly in sequence. Each writer: pull latest plan, apply ONLY its scoped edits, run narrowest check, hand off. No parallel writers, no background writers, no re-discovery reads beyond `Grep/Read` of files the plan already names.
1. `factory-implementer` (write): numbered plan steps only, minimal diff, preserve exit codes 0/1/2. Narrow `dotnet build` / single-filter test per edit. May request a tool but does NOT promote it — drops prototype in `scratch/` and leaves a `TOOL-REQUEST` note for step 4.
2. `factory-tester` (write, tests-only lock): add/update xUnit under `tests/Olaf.Tests/` (no live network, `Fixtures/` + temp dirs). Filtered runs during iteration, full suite before sign-off. Sends impl failures back with `file:line` + repro (re-queue implementer serially, never parallel).
3. `factory-refactor` (cleanup, write): the ONLY refactoring pass — dedup/tighten only after implementation + tests are green, no behavior change. Green check required.
4. `factory-toolbuilder` (write, if requested): promote/modify/retire in `.opencode/tools/factory/` + `TOOLS.md` row. Additive-only, `VERSION` bump, `--help` + one real run verified.
5. `factory-docs` (write, docs-only lock): touch ONLY docs affected by shipped behavior (`README.md`, `--help` text, formatter notes). Every example re-executed.

Any writer that finds an out-of-scope bug: `gh issue create` (serial write, allowed inside writer's window) then continue current scope. Never silently fix out-of-scope bugs, never widen scope.

### Wave 3 — Read verify fan-out (all parallel, writer idle, pinned SHA)
Freeze writes. Run on the writer-finished SHA in ONE parallel block:
- `factory-qa` × perspectives (scope/adversarial, exit-code contract, formatter validity, no-network) — each returns `QA: PASS` / `QA: FAIL (reasons + file:line)`.
- Smoke readers: `dotnet run --project src/Olaf.Cli -- --help` + fixture scan (`tests/Olaf.Tests/Fixtures/npm --format json`), `./.opencode/tools/factory/dotnet-test-fast.sh`.
- `factory-docs` in **check-only** mode: examples accurate, no invented flags.

Merge rule: ALL must be `PASS`. Any `FAIL` → reject serially back into Wave 2 (single writer fix, then re-run full Wave 3 — never patch during Wave 3). Final-QA adds merge-readiness on pinned SHA: `MERGE: READY (green)` if suite exit 0; `MERGE: READY (no-regressions; <k> pre-existing ⊆ base)` if failures ⊆ base failures with passed ≥ base and scoped tests green; else `MERGE: BLOCKED`.

### Wave 4 — Serial publish (orchestrator executes directly, never delegate)
Gate on final-QA `MERGE: READY`. Never merge on `BLOCKED`/`FAIL`.
1. `gh pr create --fill`, link `Closes #<n>`, PR body carries QA sign-off + `Factory-Notes` (tools, lessons).
2. Green base → `gh pr merge <pr> --merge --delete-branch`. Red base → merge only with zero regressions (rule above), pre-existing failures listed with counts + names + owning issue #s.
3. Verify `gh pr view <pr> --json state,mergedAt` = MERGED; `gh issue view <n> --json state`, `gh issue close <n>` only if auto-close missed.
4. `git worktree remove --force`, drop local branch if present. Never `--force`-push, never merge another issue's PR.
5. Proceed to Wave 5. Never jump directly to Wave 0 — retrospective is blocking.

### Wave 5 — Retrospective (blocking; parallel harvest → serial rectify)
Runs after EVERY merged issue (and after an aborted issue), before the next Wave 0. Orchestrator blocks next pick until the retro file lands.
1. **5a Harvest (parallel reads, one block):** fan out ALL agents that touched the issue (planner, implementer, tester, QA, refactor, docs, toolbuilder) in `report-only` mode. Each returns a `FRICTION` packet: repeats (cmd + count), est. tokens wasted, wall-time slowdowns, confusing contracts/handoffs, flaky tests, tool misfits. Readers only — no patches yet. Reuse the issue's plan + `Factory-Notes` + `TOOLS.md` as input.
2. **5b Rectify (serial writes, one agent at a time, on main):** orchestrator dedups packets into a ranked list (token-savings × frequency first), then schedules serial slots:
   - `factory-toolbuilder` first: build/promote/modify/retire tools for every qualifying repeat (thresholds below). Each: `scratch/` → promoted tool + `chmod +x` + `--help` + `VERSION=` + `TOOLS.md` row/changelog + `--help` + real-run verification.
   - Owning agent(s) second: patch repo-general lessons directly into their agent file (no secrets, no local paths); tool-specific lessons → `TOOLS.md`; process lessons → propose SKILL.md patch for orchestrator to apply.
   - Deferred items MUST get a `gh issue create` with `friction:` prefix and are cited in the retro file — never silently dropped.
3. **5c Record + verify (serial write):** orchestrator writes `.opencode/plans/retro-<n>.md` (what slowed us, what we changed: tool/agent/skill patches with versions, measured savings, deferred issues with #s). Verify: every promoted tool passes `--help` + one real run; every agent-file patch is concise and green-neutral. Only then unblock Wave 0 for the next issue. Wave 1 of the next issue MUST `Read` the last 3 retro files as first-class context.

## Self-improvement system (mandatory, not optional)
Goal: every issue makes the factory faster, cheaper (tokens + wall-time), and less flaky — for implementation AND execution.
- **Thresholds (trigger = must act):** same pipeline/search/command 3+ times in one issue OR reused across 2+ issues OR one emission costs ~500+ tokens → mandatory `TOOL-REQUEST`. Same confusion/handoff-miss 2+ times → mandatory agent-file patch proposal. Tool misfit (missing flag, wrong shape, brittle parse) → modify-in-place request, same priority as new tool. Stale tool (unused 3+ issues) → retire candidate.
- **Tool lifecycle (reads parallel, writes serial):**
  - Parallel (read): checking `TOOLS.md`/`README.md`, running any promoted tool for verification.
  - Serial (Wave 2 step 4 or Wave 5b): prototype in `scratch/`, promote to `.opencode/tools/factory/<name>.sh` (`chmod +x`, `--help`, `VERSION=`), modify in place (additive-only + bump + changelog line), deprecate → remove after one loop. Record every change under PR `Factory-Notes` AND the retro file.
- **Reuse mandate:** check registry before re-emitting any `gh`/`dotnet`/parse pipeline. Build via `factory-toolbuilder`; planner assigns tool builds as Wave 2 steps, retro assigns batch tooling as Wave 5b steps — never Wave 1/3 writes.
- **Efficiency ledger:** `.opencode/plans/retro-<n>.md` files are the ledger (per-issue savings + cumulative notes). Orchestrator reads the last 3 in every Wave 1.
- **No free work:** repeat work without a matching `TOOL-REQUEST`, agent patch, or deferred `friction:` issue is a Wave 5 FAIL — orchestrator sends it back.

## Rules for all agents
- .NET 10, `olaf.slnx`. Verify by execution: `dotnet test tests/Olaf.Tests/Olaf.Tests.csproj --verbosity minimal`. Never claim green without running it.
- Keep diffs minimal and scoped. Git side effects authorized only inside owning write window (Wave 2), publish (Wave 4), and rectify (Wave 5b); otherwise no commits unless user explicitly asks.
- Token discipline: `Glob`/`Grep` first, full `Read` on demand. Wave 1 context packet is authoritative — downstream agents reuse it instead of re-exploring. Repeat lookup 3+ times → `TOOL-REQUEST`, not ad-hoc re-scan.
- **Working directory:** all factory working files in repo working directory (plans in `.opencode/plans/issue-<n>.md`, retros in `.opencode/plans/retro-<n>.md`, prototypes in `.opencode/tools/factory/scratch/`, all gitignored). Sole exception: ephemeral `/tmp` verification worktrees, removed immediately after use.
- **Self-improvement (every agent, every issue):** each handoff ends with a `FRICTION` section (repeats + counts, est. tokens wasted, slowdowns, misfits, patch/tool proposal or `no-friction`). Threshold hits MUST emit `TOOL-REQUEST` or agent-patch proposal. Wave 5 harvests these, rectifies serially, records in `retro-<n>.md`. Next issue's Wave 1 reads the last 3 retros. Repeat work with no improvement artifact = defect.
- Exit codes are contract: `0` success, `1` `--strict` violation, `2` usage/IO.

## Context pointers
- CLI entry: `src/Olaf.Cli/Program.cs`
- Core contracts: `src/Olaf.Core/Dependency.cs`, `ResolvedLicense.cs`, `ScanResult.cs`, `IEcosystemParser.cs`, `ILicenseFormatter.cs`, `ILicenseResolver.cs`
- Parsers: `src/Olaf.Parsers/ParserRegistry.cs`, `NpmParser.cs`, `NuGetParser.cs`, `PipParser.cs`
- Resolvers: `src/Olaf.Resolvers/CachingLicenseResolver.cs`, `Npm|NuGet|PyPI|ClearlyDefinedFallbackResolver.cs`, `SpdxMapper.cs`
- Formatters: `src/Olaf.Formatters/FormatterRegistry.cs`, `Json|Yaml|Xml|HtmlFormatter.cs`
- Tests: `tests/Olaf.Tests/Cli/CliTests.cs`, `Formatters/FormatterTests.cs`, `Parsers/*Tests.cs`, `Resolvers/ResolverTests.cs`, `Fixtures/`
