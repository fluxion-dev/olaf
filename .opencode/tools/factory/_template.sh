#!/usr/bin/env bash
# Template for factory tools. Copy to <name>.sh and fill in.
# Rules: repo-relative, idempotent, no secrets, executable, --help required.
# /tmp-mktemp permitted exception: ephemeral mktemp probe dirs under
# ${TMPDIR:-/tmp} with trap-cleanup are permitted (see KEEP_TEMP fragment
# below); persistent files/plans/retros/prototypes/worktrees MUST be
# repo-relative — NEVER write those outside the repo.
# --workdir standard: every probe accepts [--workdir <dir>] [--keep-temp]
# wired to WORKDIR/KEEP_TEMP below so runs are reproducible and debuggable.
# usage() heredoc idiom (#149 harvest, 2nd backtick-expansion sighting):
# prefer quoted <<'EOF' + hardcoded name; if unquoted, escape every $/`
# in the body or --help executes it (docs-drift 0.3.0 + pages-verify 0.3.0).
VERSION="0.2.12"
set -euo pipefail

# ---- Canonical root resolution (copy-paste; do not hardcode paths) ----
# Priority: git top-level -> script-dir fallback -> fail-closed.
# NOTE: factory tools live at .opencode/tools/factory/ so repo root is
#   ../../.. from the script dir. Scratch prototypes live one level deeper
#   (.opencode/tools/factory/scratch/) so use ../../../.. there instead.
#   Verify against parser-coverage-probe.sh:11 which uses ../../...
ROOT=""
if git rev-parse --show-toplevel >/dev/null 2>&1; then
  ROOT="$(git rev-parse --show-toplevel)"
else
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  if [[ -f "$SCRIPT_DIR/../../../olaf.slnx" ]]; then
    ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
  else
    echo "Cannot locate repo root (no git top-level, no olaf.slnx fallback)." >&2
    exit 2
  fi
fi

# ---- Canonical pass/fail (copy-paste) ----
fail=0
pass() { echo "PASS: $*"; }
fail_msg() { echo "FAIL: $*"; fail=1; }

# ---- Canonical temp-dir with KEEP_TEMP (copy-paste; adapt prefix) ----
# Wire --workdir/--keep-temp flags to these; mktemp under ${TMPDIR:-/tmp}.
WORKDIR=""
KEEP_TEMP=0
# if [[ -z "$WORKDIR" ]]; then
#   WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/<name>-XXXXXX")"
#   MADE_TMP=1
# else
#   mkdir -p "$WORKDIR"
#   MADE_TMP=0
# fi
# cleanup() {
#   if (( ! KEEP_TEMP )) && (( MADE_TMP )) && [[ -d "$WORKDIR" ]]; then
#     rm -rf "$WORKDIR"
#   fi
# }
# trap cleanup EXIT

# ---- Canonical dep extractors (copy-paste; python3, no jq dependency) ----
# dep_count <json>: print licenses/dependencies/resolved count ("?" on bad JSON).
# dep_field <json> <name> <field>: print <field> for package <name>
#   (case-insensitive; unwraps {"dependency":{...}} envelope; empty if absent;
#   booleans print as true/false; string values lowercased only for
#   field=="direct" so version passthrough stays verbatim).
# Thin wrappers (define live alongside): dep_ver() { dep_field "$1" "$2" "version"; }
#   dep_direct() { dep_field "$1" "$2" "direct"; }
# Canonical source promoted to template 0.2.2 after 3rd/4th family use
#   (pip-lock-probe.sh dep_ver/dep_count, transitive-surface-probe.sh
#   dep_direct/dep_count — dep_count 2nd exact copy; behavior identical).
#   Back-ported: both probes now carry this exact block live.
# dep_count() {
#   python3 - "$1" <<'PY' 2>/dev/null
# import json, sys
# try:
#     with open(sys.argv[1]) as f:
#         data = json.load(f)
# except Exception:
#     print("?")
#     sys.exit(0)
# items = data.get("licenses", data.get("dependencies", data.get("resolved", [])))
# print(len(items) if isinstance(items, list) else "?")
# PY
# }
# dep_field() {
#   python3 - "$1" "$2" "$3" <<'PY' 2>/dev/null
# import json, sys
# path, want, field = sys.argv[1], sys.argv[2].lower(), sys.argv[3]
# try:
#     with open(path) as f:
#         data = json.load(f)
# except Exception:
#     sys.exit(0)
# items = data.get("licenses", data.get("dependencies", data.get("resolved", [])))
# if isinstance(items, list):
#     for it in items:
#         if not isinstance(it, dict):
#             continue
#         dep = it.get("dependency", it)
#         name = str(dep.get("name", it.get("name", "")))
#         if name.lower() == want:
#             v = dep.get(field, it.get(field, ""))
#             if v is True:
#                 print("true")
#             elif v is False:
#                 print("false")
#             elif isinstance(v, str):
#                 print(v.lower() if field == "direct" else v)
#             else:
#                 print(str(v) if v != "" else "")
#             break
# PY
# }
# dep_ver() { dep_field "$1" "$2" "version"; }
# dep_direct() { dep_field "$1" "$2" "direct"; }

# ---- Canonical R1 format-matrix regression (copy-paste; then call) ----
# Requires (define live alongside): run_scan <tag> <inputdir> <format>
#   [extra...] writing $WORKDIR/$tag.stdout and setting RC; FIXTURE input dir;
#   WORKDIR; pass()/fail_msg(). Marker map mirrors format-matrix-dump.sh
#   9th-field `direct` asserts (json/yaml/xml/html subset; txt/md/cdx arms
#   live in the matrix tool, not in probes).
# Canonical source promoted to template 0.2.3 after 4th family use
#   (cyclonedx-probe.sh + cyclonedx-xml-probe.sh + spdx-probe.sh 4-format
#   loops, enrichment-probe.sh narrowed 2-format loop — restored to 4-format
#   on back-port since `direct` spans all 9 matrix formats).
#   Back-ported: all four probes now call this exact function live.
# r1_matrix_regression() {
#   # r1_matrix_regression [formats...]; default: json yaml xml html.
#   local formats=("$@")
#   (( ${#formats[@]} )) || formats=(json yaml xml html)
#   local R1FAIL=0 f pat
#   echo "-- R1 format-matrix regression --"
#   for f in "${formats[@]}"; do
#     run_scan "r1-$f" "$FIXTURE" "$f"
#     if [[ "$RC" -ne 0 ]]; then
#       fail_msg "R1/$f scan exited $RC"; R1FAIL=1; continue
#     fi
#     case "$f" in
#       json) pat='"direct"' ;;
#       yaml) pat='direct:' ;;
#       xml) pat='<direct>' ;;
#       html) pat='<th>Direct</th>' ;;
#       *) fail_msg "R1/$f: no marker for format"; R1FAIL=1; continue ;;
#     esac
#     if grep -qF -- "$pat" "$WORKDIR/r1-$f.stdout"; then
#       pass "R1/$f carries $pat"
#     else
#       fail_msg "R1/$f missing $pat"; R1FAIL=1
#     fi
#   done
#   if (( ! R1FAIL )); then pass "R1 MATRIX OK: ${formats[*]} untouched"; fi
# }
# # call site (replaces the inline for-f loop):  r1_matrix_regression
# # narrowed call site (documents deliberate narrowing):  r1_matrix_regression json yaml

# ---- Canonical scriban_inline (copy-paste skeleton; then adapt) ----
# Inline .scriban fixtures for error/negative arms: committed *.scriban files
#   are NEVER edited inline — write temp copies under $WORKDIR with printf and
#   explicit \n (echo mangles backslashes; heredocs risk {{ }} review
#   confusion). Single-line shape probes need no trailing newline; multiline
#   blocks need \n per line so engine line numbers stay meaningful.
# Canonical source promoted to template 0.2.6 after 3rd family use
#   (template-probe.sh T1 holder-passthrough + T3 bad-syntax + T4
#   unknown-field printf arms — identical printf->run_template shape).
# printf '{{#each licenses}}{{name}}|{{copyright}};\n{{/each}}' > "$WORKDIR/holder.scriban"
# printf 'header\n{{#each licenses}}\nno-close\n' > "$WORKDIR/bad.scriban"
# printf 'A-{{nosuchfield}}-B' > "$WORKDIR/unk.scriban"
# # call site (replaces ad-hoc echo/heredoc fixture synthesis):
# #   run_template "t1-holder" "$FIXTURE" "$WORKDIR/holder.scriban"

# ---- Canonical air-gap grep (copy-paste; then call) ----
# Requires (define live alongside): air_gap_grep <file> [extra-pattern...]
#   writing nothing; exit 0 = clean (air-gap holds), 1 = HTTP surface found,
#   2 = missing file. Strips //-comment lines before matching so
#   doc-only mentions (e.g. "// no HttpClient here") never trip the arm.
#   Default vocab: HttpClient|GetAsync|System\.Net|HttpRequest|GetByteArray|SendAsync.
#   Extra args are OR-ed into the vocab (BRE-escaped by caller if needed).
# Canonical source promoted to template 0.2.4 after 2nd family use
#   (license-text-probe.sh L3 air-gap arm — hand-rolled grep -v + grep -q
#   pair refactored to call this function live; default vocab widened with
#   GetByteArray|SendAsync for forward coverage, behavior identical on old hits).
#   Back-ported: license-text-probe.sh now calls this exact function live.
# air_gap_grep() {
#   local file="$1"; shift
#   [[ -f "$file" ]] || return 2
#   local vocab='HttpClient|GetAsync|System\.Net|HttpRequest|GetByteArray|SendAsync'
#   local p
#   for p in "$@"; do vocab="$vocab|$p"; done
#   if grep -v '^[[:space:]]*//' "$file" | grep -qE -- "$vocab"; then
#     return 1
#   fi
#   return 0
# }
# # call site (replaces the inline grep-v | grep-q pair):
# #   if air_gap_grep "$DB"; then pass "air-gap safe (pure in-memory, zero HTTP)"; else fail_msg "HTTP surface found (air-gap broken)"; fi
# # widened call site (documents deliberate widening):
# #   if air_gap_grep "$DB" 'MyCustomSender'; then pass "..."; else fail_msg "..."; fi

# ---- Canonical py_anchor_assert (copy-paste skeleton; then adapt) ----
# Python anchor assert with dual-anchor + set +e guard + try/except split.
# Pattern: a src method moved into a wrapper (e.g. TryFetchLicenseTextAsync
#   -> TryFetchLicenseTextWithProvenanceAsync) so the assert prefers the new
#   anchor with fallback to the old name; the `set +e ... RC=$? ... set -e`
#   wrapper keeps `set -euo pipefail` from silently aborting on a python
#   nonzero (missing anchor / failed assert), and the try/except around
#   str.split turns a missing anchor into sys.exit(1) -> FAIL line via the
#   RC check instead of a traceback abort.
# Canonical source promoted to template 0.2.5 after 2nd family use
#   (license-text-probe.sh L2 chain assert — #72 REJECT-FIX: split on the
#   old anchor silently aborted after #72 moved the chain into the
#   WithProvenanceAsync wrapper; `return (null, firstFailure` prefix-match
#   accepts both 2-tuple and 3-tuple provenance returns).
#   Back-ported: license-text-probe.sh L2 now carries the canonical header
#   comment live (behavior identical, comment-only diff).
# set +e
# python3 - "$FILE" <<'PY' 2>/dev/null
# import sys
# src = open(sys.argv[1]).read()
# anchor = "NewAnchorName" if "NewAnchorName" in src else "OldAnchorName"
# try:
#     body = src.split(anchor, 1)[1]
# except (IndexError, ValueError):
#     print(f"anchor split failed: {anchor}", file=sys.stderr)
#     sys.exit(1)
# # ... asserts on body follow (index order, vocab pins) ...
# print("anchor order ok")
# PY
# ANCHOR_RC=$?
# set -e
# if [[ "$ANCHOR_RC" -eq 0 ]]; then pass "<label>: anchor order ok"; else fail_msg "<label>: anchor/pinning wrong"; fi

# ---- Canonical mkphantom fixture (copy-paste; then call) ----
# mkphantom <dir>: synth phantom package.json carrying the never-resolvable
#   dep this-package-definitely-does-not-exist-olaf-xyz@9.9.9 (404 online /
#   transport-error offline, both Unknown with a pinned unresolved-prefix
#   reason). Requires PHANTOM_PKG/PHANTOM_VER vars (older probes use
#   PKG_NAME/PKG_VERSION — adapt the two variable refs) + pass()/fail_msg()
#   only when the optional python guard is kept (keep it when the probe
#   already depends on python3; drop it for guard-free minimal back-ports —
#   the printf output is static valid JSON either way).
#   Frozen-date idiom for synth rules files (documented here, NOT in the
#   function — mkphantom stays phantom-carrier only): `expires: 2099-01-01`
#   = bounded-live exception, `expires: 2000-01-01` = expired
#   ("(exception expired:"), absent `expires:` = perpetual. Frozen literals
#   keep expiry arms deterministic (never `date +%Y` computed dates).
#   Root "name" field is cosmetic (scanner keys on dependencies only):
#   back-ports keep their historical root names (olaf-reliability-probe,
#   olaf-policy-gate-probe, olaf-ux-strict-probe, olaf-policy-fixture).
# Canonical source promoted to template 0.2.7 after 4th family use
#   (policy-file-probe.sh live mkphantom + resolve-reliability-probe.sh +
#   policy-gate-probe.sh + cli-ux-probe.sh step-2 inline heredocs — all the
#   same package.json shape).
#   Back-ported: all four probes now call mkphantom live (three heredocs
#   replaced by calls, policy-file header cites canonical; behavior
#   identical, probe versions unchanged).
# mkphantom() {
#   local d="$1"
#   mkdir -p "$d"
#   printf '{\n  "name": "olaf-phantom-fixture",\n  "version": "1.0.0",\n  "dependencies": {\n    "%s": "%s"\n  }\n}\n' "$PHANTOM_PKG" "$PHANTOM_VER" > "$d/package.json"
#   # optional guard (keep only when probe already depends on python3):
#   # if command -v python3 >/dev/null 2>&1; then
#   #   if ! python3 -c "import json,sys;json.load(open(sys.argv[1]))" "$d/package.json" 2>/dev/null; then
#   #     fail_msg "synth fixture invalid JSON: $d/package.json"
#   #   fi
#   # fi
# }
# # call site (replaces the inline cat-heredoc package.json block):
# #   PHANTOM="$WORKDIR/phantom"; mkphantom "$PHANTOM"

# ---- Canonical cdx_normalize (copy-paste; then call) ----
# cdx_normalize <file>: print CycloneDX JSON with per-run fields neutralized
#   so generate-vs---input outputs byte-compare. jq path deletes
#   .serialNumber + .metadata.timestamp (mirrors unit NormalizedCycloneDx in
#   tests/Olaf.Tests/Cli/GenerateCliTests.cs, which pins both to NORMALIZED
#   before Assert.Equal); sed fallback rewrites both values to NORMALIZED
#   when jq is absent (CycloneDxFormatter emits single-line JSON so the
#   line-oriented substitution holds; keep the g flag — envelope may repeat
#   keys in nested components). Either path neutralizes per-run variance;
#   byte-compare is valid as long as both sides use the same function.
# Canonical source promoted to template 0.2.8 after 3rd family use
#   (unit NormalizedCycloneDx + cli-ux-probe.sh generate-cdx-parity arm +
#   future SBOM diffs — CycloneDxFormatter serialNumber Guid.NewGuid() +
#   metadata timestamp DateTime.UtcNow make raw byte-compare flaky).
#   Back-ported: cli-ux-probe.sh now carries this exact function live.
# cdx_normalize() {
#   local f="$1"
#   if command -v jq >/dev/null 2>&1; then
#     jq 'del(.serialNumber, .metadata.timestamp)' "$f"
#   else
#     sed -e 's/"serialNumber"[[:space:]]*:[[:space:]]*"[^"]*"/"serialNumber":"NORMALIZED"/g' \
#         -e 's/"timestamp"[[:space:]]*:[[:space:]]*"[^"]*"/"timestamp":"NORMALIZED"/g' "$f"
#   fi
# }
# # call site (replaces raw cmp of cyclonedx-json outputs):
# #   cdx_normalize "$WORKDIR/generate-cdx.stdout" > "$WORKDIR/generate-cdx.normalized"
# #   cdx_normalize "$WORKDIR/parity-cdx.stdout" > "$WORKDIR/parity-cdx.normalized"
# #   if cmp -s "$WORKDIR/generate-cdx.normalized" "$WORKDIR/parity-cdx.normalized"; then
# #     pass "generate-cdx-parity: normalized byte-identical"
# #   else
# #     fail_msg "generate-cdx-parity: normalized mismatch"
# #   fi

# ---- Canonical e2e_trait_gate (copy-paste; then adapt) ----
# e2e Trait gate: every live-network Fact in *E2ETests.cs must carry
#   [Trait("Category","E2E")] so CI can filter with Category!=E2E.
#   Count attribute-anchored lines ONLY — bare grep -c Trait overcounts by ~1
#   per file via /// doc-comment mentions ("so CI can filter with
#   Category!=E2E"). RC=1 on zero matches must be handled explicitly
#   (|| [ $? -eq 1 ] / set +e; RC=$? pattern), and [Fact]/[Theory] counted
#   distinctly (Theory expands at runtime; text count != case count).
# Canonical source promoted to template 0.2.9 after 3rd family use
#   (maven/npm/nuget/pypi/go/cargo base + #48 swift-cocoapods + #49 vcpkg-conan
#   E2E files — Trait doc-comment overcount hit in #48/#49 QA slots).
#   Back-port: vcpkg-conan-e2e-probe.sh E2 gate already carries the anchored
#   form live (fixed pre-promote from naive grep -c).
# # E2E_TRAIT_RE='^[[:space:]]*\[Trait("Category", *"E2E")\]'
# # e2e_trait_count="$(grep -rc --include='*.cs' -e "$E2E_TRAIT_RE" tests/Olaf.Tests/Resolvers/ || [ $? -eq 1 ])"
# # NOTE: BRE literal parens are BARE ( \( \) are grouping operators and can
# #   never match literal parens — fixed 0.2.10 after xml-report probe copier
# #   caught the escaped form matching zero lines).
# # call site (replaces bare grep -c Trait census):
# #   n="$(grep -c -e "$E2E_TRAIT_RE" "$f" || [ $? -eq 1 ]); echo \"trait-attrs=$n facts=$(grep -c '\[Fact\]' "$f" || [ $? -eq 1 ])\""

# ---- Canonical run_gate + test-count extraction (copy-paste; then call) ----
# run_gate <logfile> -- <cmd...>: redirect-to-file gate with RC capture.
#   `set +e` MUST precede the command (`set +e; <cmd>; RC=$?` — the reverse
#   order captures set's own exit); the function always returns 0 so bare
#   calls stay safe under `set -euo pipefail`, and RC carries the verdict.
#   Requires RC defined live alongside (RC=0). Never `cmd | tee/head` +
#   `${PIPESTATUS[0]}` (unreliable without a bash -c pipefail wrapper).
# Count extraction is ALWAYS `grep -E 'Passed!|Failed!' <log> | tail -1`
#   (last summary wins; `|| true` keeps empty logs from aborting) — never
#   `grep "Total tests"` (build-plan lines also mention totals; only the
#   Passed!/Failed! summary line carries the authoritative counts).
# Canonical source promoted to template 0.2.11 after 2nd family use
#   (test-count-probe.sh live gate + LAST extraction; cyclonedx-family
#   run_scan RC shape — same set+e/capture/set-e skeleton).
# run_gate() {
#   local log="$1"; shift
#   if [[ "${1:-}" == "--" ]]; then shift; fi
#   set +e
#   "$@" >"$log" 2>&1
#   RC=$?
#   set -e
# }
# # call site (replaces `if ! <cmd> >log 2>&1; then :; fi` + hand grep):
# #   run_gate "$WORKDIR/t.stdout" -- dotnet test "$CSPROJ" --verbosity minimal
# #   LAST="$(grep -E 'Passed!|Failed!' "$WORKDIR/t.stdout" | tail -1 || true)"
# #   [[ -z "$LAST" ]] && { fail_msg "no Passed!/Failed! summary (build error?)"; return; }

if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: $(basename "$0") [options]"
  echo "TODO: describe purpose, options, examples."
  exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
  echo "$(basename "$0") $VERSION"
  exit 0
fi
echo "TODO: implement" >&2
exit 2
