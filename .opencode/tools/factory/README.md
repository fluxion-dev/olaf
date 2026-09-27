# Factory Tools

Persistent, repo-scoped helpers owned by the `olaf-factory` skill.
All tools are mutable — extend them as scenarios evolve, don't fork around them.
Scratch work goes in `scratch/` (repo-local, gitignored); promote here when reused 2+ times or across issues.

## Rules
- Bash or `python3` only, no secrets, no absolute local paths. Repo-root relative.
- Each tool: executable bit, `--help` output, `VERSION=` header, exit 0/1/2 matching factory contract where applicable.
- Register every tool in `TOOLS.md` with purpose + usage + token savings + version + status (`active`|`deprecated`).
- Prefer reusing or modifying a tool over re-emitting long `gh`/`dotnet` pipelines.
- Start new tools from `_template.sh`.

## Lifecycle (fluid)
1. **Create:** prototype in `scratch/`, promote on 2nd reuse. Bump `VERSION`, add `TOOLS.md` row.
2. **Modify:** extend in place for scenario drift (new flag, new output, hardened parsing). Additive only — existing invocations keep working. Bump minor `VERSION`, append changelog line in `TOOLS.md`, test `--help` + one real run.
3. **Deprecate → remove:** mark `deprecated` in `TOOLS.md` when superseded; keep file one full factory loop, then delete.

## Layout
- `gh-issue-queue.sh` — list OPEN issues as `number|title` (factory loop step 1).
- `dotnet-test-fast.sh` — Minimal `dotnet test` wrapper used by tester/QA gates.
- `_template.sh` — copy-paste starter for new tools (not executed directly).
- Add/modify via `factory-toolbuilder` workflow in the skill.
