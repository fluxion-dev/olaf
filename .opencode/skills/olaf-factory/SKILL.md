---
name: olaf-factory
description: Run the olaf software factory. Use when implementing GitHub issues end-to-end via planner, discovery, implement, test, QA, refactor, docs agents with PR delivery.
---

# Olaf Factory

Serial factory loop for this repo (`dotnet`, `olaf.slnx`, `src/Olaf.*/`, `tests/Olaf.Tests/`).
Source of truth for work: GitHub issues (`gh issue list/view/create`).

## Factory loop (do not skip)

1. **Pick work:** `gh issue list --limit 20 --json number,title,state,labels`
   - If zero OPEN issues: STOP and report "factory idle — no remaining issues".
   - Otherwise pick lowest-numbered OPEN MVP issue. Read it fully: `gh issue view <n> --comments`.
2. **Plan:** delegate to `factory-planner`. It must fan out to `factory-discovery` agents to build codebase context, then return a serial step plan (prep-refactor → implement → test → QA → cleanup-refactor → final-QA → docs → PR → merge). Do not start coding until plan exists.
3. **Execute serially, one phase at a time:**
   1. `factory-refactor` (prep): small safe cleanups only, keep tests green.
   2. `factory-implementer`: implement plan steps, no drive-by scope creep.
   3. `factory-tester`: add/update xUnit tests under `tests/Olaf.Tests/`, run `dotnet test`.
   4. `factory-qa`: adversarial review + `dotnet test` + manual `dotnet run --project src/Olaf.Cli -- --help` and a fixture scan. Reject back to implementer on failure.
   5. `factory-refactor` (cleanup): deduplicate, tighten names, no behavior change.
   6. Final QA (`factory-qa` again, full suite) then `factory-docs` (README + relevant docs only if behavior changed).
   7. Open PR with `gh pr create --fill`, link `Closes #<n>`, record QA sign-off + `Factory-Notes` in PR body.
   8. **Merge** (orchestrator executes directly, never delegate): merge the PR, then verify + close + clean up.
      - Gate on final-QA verdict: merge only on `MERGE: READY`. Never merge on `MERGE: BLOCKED` or `QA: FAIL`.
      - **Green base:** PR-branch suite exit 0 (verified in an isolated worktree) → `gh pr merge <pr> --merge --delete-branch`.
      - **Red base** (base commit itself has failing tests): merge allowed only with zero regressions — failed-test set on PR branch ⊆ failed-test set on base commit, passed count ≥ base, every new/scoped test green. List pre-existing failures (count + names + owning issue #s) in the PR body.
      - Verify: `gh pr view <pr> --json state,mergedAt` shows MERGED; check `gh issue view <n> --json state` and `gh issue close <n>` only if auto-close did not fire.
      - Clean up: `git worktree remove --force`, drop the local branch if present. Never `--force`-push, never merge another issue's PR.
4. **Next issue:** re-run `gh issue list`. Continue until none OPEN, then STOP.

## Tooling capability (mandatory)

Agents are authorized and expected to create, modify, reuse, and retire tooling.
All tools are mutable and must evolve with developing scenarios — never treat a
promoted tool as frozen:

- **Detect:** any `gh`/`dotnet`/parse/inspect pipeline repeated 3+ times, or costing 500+ tokens per re-emission, is a tool candidate. A tool that no longer fits its scenario (missing flag, wrong output shape, brittle parsing) is a modify candidate — same priority as a new tool.
- **Scratch first:** prototype in `/tmp/opencode/factory-tools/<name>.sh` (or `.py`). Keep it repo-relative, idempotent, no secrets.
- **Promote when reused 2+ times or across issues:** move to `.opencode/tools/factory/<name>.sh`, `chmod +x`, support `--help` (and `--version` when behavior matters), add `VERSION=` header, document in `.opencode/tools/factory/TOOLS.md` (purpose + usage + tokens saved + version + status).
- **Modify fluidly:** any agent may extend a promoted tool mid-issue when the scenario demands it (new flag, new output mode, new ecosystem). Rules: additive changes only (never break existing flags/output without a `VERSION` minor/major bump + `TOOLS.md` changelog line), test `--help` + one real run after every edit, keep the tool executable. Prefer editing the existing tool over forking a `v2` copy; fork only on breaking change, then mark old as `deprecated` in `TOOLS.md` for one issue cycle before removal.
- **Retire:** when a scenario dies (flag removed, workflow replaced), mark `deprecated` in `TOOLS.md`, leave the file for one full factory loop, then delete. Record all of this in the PR body under `Factory-Notes`.
- **Reuse mandate:** check `TOOLS.md` and `.opencode/tools/factory/README.md` before re-emitting a pipeline. Prefer `./.opencode/tools/factory/gh-issue-queue.sh` for issue queue and `./.opencode/tools/factory/dotnet-test-fast.sh` for test gates. Copy `.opencode/tools/factory/_template.sh` for new tools.
- **Build via `factory-toolbuilder`:** planner assigns tool builds AND tool modifications as plan steps; implementer/tester/QA may request one mid-phase. Record every new/modified/retired tool in the PR body under `Factory-Notes`.
- **Self-improve:** repo-general lessons go into the owning agent file; tool-specific lessons go into `TOOLS.md`.

## Rules for all agents

- .NET 10, `olaf.slnx`. Verify with execution: `dotnet test tests/Olaf.Tests/Olaf.Tests.csproj --verbosity minimal`. Never claim green without running it.
- Keep diffs minimal and scoped to the target issue. Git side effects (branch/commit/push/PR/merge) are authorized only inside the factory loop's PR → merge steps, scoped to the target issue's files; otherwise do not commit unless user explicitly asks.
- Token discipline: read files with Glob/Grep first, full Read only on demand. Reuse discovery output and factory tools instead of re-exploring.
- **Self-improvement:** if you repeat a lookup/conversion 3+ times, prototype a script in `/tmp/opencode/factory-tools/` (e.g. fixture dumper, SDK log parser), promote to `.opencode/tools/factory/` when reused, register in `TOOLS.md`, and note it in the PR body under `Factory-Notes`. Update your agent file with the lesson only when it is repo-general (no secrets, no local paths).
- **Bug reporting:** any out-of-scope bug found during work → `gh issue create --title "bug: ..." --body "repro, expected, actual, files"` immediately, then continue current issue. Never silently fix out-of-scope bugs.
- Exit codes are contract: `0` success, `1` `--strict` violation, `2` usage/IO.

## Context pointers

- CLI entry: `src/Olaf.Cli/Program.cs`
- Core contracts: `src/Olaf.Core/Dependency.cs`, `ResolvedLicense.cs`, `ScanResult.cs`, `IEcosystemParser.cs`, `ILicenseFormatter.cs`, `ILicenseResolver.cs`
- Parsers: `src/Olaf.Parsers/ParserRegistry.cs`, `NpmParser.cs`, `NuGetParser.cs`, `PipParser.cs`
- Resolvers: `src/Olaf.Resolvers/CachingLicenseResolver.cs`, `Npm|NuGet|PyPI|ClearlyDefinedFallbackResolver.cs`, `SpdxMapper.cs`
- Formatters: `src/Olaf.Formatters/FormatterRegistry.cs`, `Json|Yaml|Xml|HtmlFormatter.cs`
- Tests: `tests/Olaf.Tests/Cli/CliTests.cs`, `Formatters/FormatterTests.cs`, `Parsers/*Tests.cs`, `Resolvers/ResolverTests.cs`, `Fixtures/`
