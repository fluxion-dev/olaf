---
name: olaf-factory
description: Run the olaf software factory. Use when implementing GitHub issues end-to-end via planner, discovery, implement, test, QA, refactor, docs agents with PR delivery.
---

# Olaf Factory

## Contract

- Execute Glob in parallel.
- Execute Grep in parallel.
- Execute Read in parallel.
- Execute gh issue view in parallel.
- Execute gh pr view in parallel.
- Execute git diff in parallel.
- Execute git status in parallel.
- Execute git log in parallel.
- Execute dotnet build in parallel.
- Execute dotnet test in parallel.
- Prohibit file edits during reads.
- Prohibit git commit during reads.
- Prohibit git push during reads.
- Prohibit git branch operations during reads.
- Prohibit gh issue create during reads.
- Prohibit gh pr create during reads.
- Execute file edits serially.
- Execute git commit serially.
- Execute git push serially.
- Execute git branch operations serially.
- Execute gh issue operations serially.
- Execute gh pr operations serially.
- Idle all non-scheduled writers during writes.

## Wave 0

- List OPEN issues.
- Select lowest-numbered OPEN MVP issue.
- View issue comments.
- Create worktree under .opencode/worktrees/olaf-<n>.
- Create issue branch.
- Stop on zero OPEN issues.
- Report factory idle on zero OPEN issues.

## Wave 1

- Fan out factory-planner.
- Fan out factory-discovery per area.
- Fan out baseline readers.
- Run factory-toolbuilder in analyze-only mode.
- Run factory-qa in pre-flight mode.
- Wait for all readers.
- Read last 3 retro files.
- Synthesize context packet.
- Write .opencode/plans/issue-<n>.md.
- Pin base SHA in plan.
- Declare RELEASE-TYPE in plan.
- Declare SemVer bump in plan.
- Declare PR title in plan.
- Start code only after plan exists.

## Wave 2

- Run one writer at a time.
- Read plan before each write slot.
- Restrict edits to plan-named files.
- Run factory-implementer first.
- Direct implementer to build simplest solution.
- Defer generalization during implementation.
- Run factory-tester second.
- Direct tester to cover new behavior.
- Run factory-qa third.
- Direct QA to gate simplest solution.
- Run factory-refactor fourth conditionally.
- Direct refactorer to generalize only on approved QA.
- Eliminate duplication during refactor.
- Align refactor to existing architecture.
- Reduce cognitive complexity during refactor.
- Preserve observable behavior during refactor.
- Re-run factory-tester after refactor.
- Re-run factory-qa after refactor.
- Run factory-toolbuilder on TOOL-REQUEST only.
- Run factory-docs on behavior change only.
- Apply release bump last.
- Preserve exit codes 0/1/2.
- Keep diffs scoped to issue.
- File out-of-scope bugs via gh issue create.
- Continue current scope after filing.

## Wave 3

- Freeze writes.
- Run factory-qa perspectives in parallel.
- Run smoke readers in parallel.
- Run docs check in parallel.
- Require all PASS.
- Re-queue single writer on FAIL.
- Re-run full verify block after fix.
- Prohibit patches during verification.
- Emit MERGE READY only on green.
- Emit MERGE BLOCKED on regression.

## Wave 4

- Create PR with conventional title.
- Link Closes #<n> in body.
- Include QA sign-off in body.
- Include Factory-Notes in body.
- Include plan-accuracy token in body.
- Include RELEASE-BUMP receipt in body.
- Merge on READY only.
- Block merge on FAIL.
- Delete branch on merge.
- Tag v<Version> after merge.
- Push tag after merge.
- Verify release probes.
- Verify pages probes on site change.
- Verify PR merged state.
- Close issue on auto-close miss.
- Remove worktree.
- Proceed to Wave 5.

## Wave 5

- Harvest FRICTION packets in parallel.
- Deduplicate packets.
- Rank by frequency.
- Schedule serial rectify slots.
- Run factory-toolbuilder first.
- Patch owning agent files second.
- File deferred items as friction issues.
- Write .opencode/plans/retro-<n>.md.
- Verify promoted tools.
- Unblock next Wave 0 after retro lands.
- Block next pick until retro lands.

## Rules

- Operate in repo root.
- Use dotnet with olaf.slnx.
- Run dotnet test.
- Claim green only after execution.
- Keep diffs minimal.
- Commit only in write windows.
- Prefer Glob/Grep before Read.
- Reuse context packet.
- Emit TOOL-REQUEST on 3rd repetition.
- End every handoff with FRICTION.
- Store plans under .opencode/plans/.
- Store prototypes under scratch/.
- Store worktrees under .opencode/worktrees/.
- Treat csproj Version as truth.
- Pin README version to csproj.
- Pin site version to csproj.
- Apply SemVer core bump per issue.
- Apply Conventional Commits.
- Apply conventional title to PRs.
- Forbid merge commits.
- Treat src/Olaf.Cli/Program.cs as CLI entry.
- Treat src/Olaf.Core/Dependency.cs as model truth.
- Treat src/Olaf.Core/ResolvedLicense.cs as model truth.
- Treat src/Olaf.Core/ScanResult.cs as model truth.
- Treat src/Olaf.Core/IEcosystemParser.cs as parser contract.
- Treat src/Olaf.Core/ILicenseFormatter.cs as formatter contract.
- Treat src/Olaf.Core/ILicenseResolver.cs as resolver contract.
- Treat src/Olaf.Parsers/ParserRegistry.cs as parser registry.
- Treat src/Olaf.Resolvers/CachingLicenseResolver.cs as resolver cache.
- Treat src/Olaf.Formatters/FormatterRegistry.cs as formatter registry.
- Treat tests/Olaf.Tests/ as test root.
