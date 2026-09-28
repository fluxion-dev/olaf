#!/usr/bin/env bash
# parser-coverage-probe: README parser-coverage drift probe (issue #123 collapse).
# Extracts Npm/Pip/Go CanHandle filename sets, the 13-ecosystem source list,
# the 7-token SupportedFormats const, tier-preference pins, and generate
# --help text; diffs the documented surface (README.md L5) against live
# source. FAIL (exit 1) on missing ecosystem/format token, wrong canonical
# order, or missing tier/IsTransitive/conda/dedup source pins; WARN (exit 0
# + note) on abbreviation-only drift.
# 0.3.0 narrows to the post-#123 tree: Apk/Dpkg/Rpm bare-name extractor +
# OS CanHandle/ScanOverlay/deferred arms (old Checks 8/9/10) deleted with
# the parsers; `markdown alias md` Check-1 arm deleted (txt/html gone, zero
# aliases); SupportedEcosystems re-anchored to the 13 ParserRegistry
# ecosystems (no apk/dpkg/rpm/container) and SupportedFormats to the
# FormatterRegistry 7-token const (was: Program.cs consts); tier +
# IsTransitive/conda/dedup checks re-anchored from README sentences (gone
# in the collapse) to source-comment pins. Probe-only, never edits README/src.
VERSION="0.3.0"
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
README="$ROOT/README.md"
PARSERS="$ROOT/src/Olaf.Parsers"
PROJECT="$ROOT/src/Olaf.Cli"
REGISTRY="$PARSERS/ParserRegistry.cs"
FORMATREG="$ROOT/src/Olaf.Formatters/FormatterRegistry.cs"

if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: $(basename "$0") [--help] [--version]"
  echo "Probe README parser-coverage drift: extracts Npm/Pip/Go CanHandle sets,"
  echo "the 13-ecosystem source list, the FormatterRegistry 7-token"
  echo "SupportedFormats const, tier-preference source pins, and generate"
  echo "--help text; diffs against README.md L5 (ecosystems + formats)."
  echo "Exit 0 = OK (or WARN-only abbreviation drift); 1 = real drift found; 2 = usage/IO error."
  echo "Example: ./.opencode/tools/factory/parser-coverage-probe.sh"
  exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
  echo "$(basename "$0") $VERSION"
  exit 0
fi
if [[ -n "${1:-}" ]]; then
  echo "Unknown option: $1 (try --help)" >&2
  exit 2
fi
for f in "$README" "$REGISTRY" "$FORMATREG"; do
  [[ -f "$f" ]] || { echo "Missing required file: $f" >&2; exit 2; }
done

fail=0
warn=0
note() { echo "NOTE: $*"; }
pass() { echo "PASS: $*"; }
fail_msg() { echo "FAIL: $*"; fail=1; }
warn_msg() { echo "WARN: $*"; warn=1; }

# ---- Extract CanHandle filename literals per parser (quoted filename-shaped
# ---- strings inside the CanHandle body only; excludes regexes/versions/paths) ----
extract_handles() {
  awk '/bool CanHandle/,/^\s*\}/' "$1" \
    | grep -o '"[^"]*"' | tr -d '"' | grep -E '^[A-Za-z0-9_.-]+$' | grep '\.' | LC_ALL=C sort -u
}

echo "--- CanHandle sets (extracted) ---"
for p in Npm Pip Go; do
  echo "$p: $(extract_handles "$PARSERS/${p}Parser.cs" | tr '\n' ' ')"
done

# ---- Extract live values (post-#123 anchors) ----
# Ecosystems: the 13 `Ecosystem =>` values across src (ParserRegistry wires
# all 13; generate scans all — no --help surface, README L5 is the doc).
# Formats: FormatterRegistry.SupportedFormats const (aliased by
# ScanRunner.SupportedFormats; surfaced in generate --help + README L5).
SRC_ECOS="$(grep -ho 'Ecosystem => "[^"]*"' "$PARSERS"/*.cs | cut -d'"' -f2 | sort -u || true)"
ECO_N="$(wc -l <<<"$SRC_ECOS" | tr -d ' ')"
FMTS=$(grep -o 'SupportedFormats = "[^"]*"' "$FORMATREG" | head -1 | cut -d'"' -f2)
echo "SourceEcosystems($ECO_N): $(tr '\n' ' ' <<<"$SRC_ECOS")"
echo "SupportedFormats=$FMTS"
ECO_LINE="$(grep -m1 'Supported ecosystems' "$README" || true)"
ECO_SEG="$(grep -o '`[^`]*`' <<<"$ECO_LINE" | head -1 || true)"

# ---- Check 1 (FAIL): ecosystems/formats consts reflected in README L5 ----
if [[ "$ECO_N" -eq 13 ]]; then pass "source carries 13 ecosystems"; else fail_msg "source ecosystem count $ECO_N != 13"; fi
while IFS= read -r eco; do
  [[ -z "$eco" ]] && continue
  if grep -qF "$eco" <<<"$ECO_SEG"; then
    pass "README lists ecosystem '$eco'"
  else
    fail_msg "README missing ecosystem '$eco'"
  fi
done <<<"$SRC_ECOS"
# No OS/container stragglers in the documented set (apk/dpkg/rpm parsers +
# container-image parser deleted in #123).
if grep -qE 'apk|dpkg|rpm|container' <<<"$ECO_SEG"; then
  fail_msg "README ecosystem segment still mentions apk/dpkg/rpm/container"
else
  pass "README ecosystem segment free of apk/dpkg/rpm/container"
fi
if grep -qF "$FMTS" "$README"; then pass "README lists SupportedFormats"; else fail_msg "README missing SupportedFormats '$FMTS'"; fi
if [[ "$(awk -F'|' '{print NF}' <<<"$FMTS")" -eq 7 ]]; then
  pass "SupportedFormats carries 7 tokens (zero aliases)"
else
  fail_msg "SupportedFormats token count != 7: '$FMTS'"
fi
if grep -q 'pypi.*alias.*pip' "$README"; then pass "README notes pypi alias"; else fail_msg "README missing pypi-alias-for-pip note"; fi
# (markdown-alias-for-md arm deleted 0.3.0: txt/html gone, zero aliases.)

# ---- Check 2 (FAIL): Npm/Pip/Go CanHandle sets match canonical pins ----
# Post-#123 README documents ecosystems, not filenames, so the check pins
# the src sets directly: any add/remove FAILs until the pin is updated here.
check_set() {
  local p="$1"; shift
  local want_sorted got
  want_sorted="$(printf '%s\n' "$@" | LC_ALL=C sort -u)"
  got="$(extract_handles "$PARSERS/${p}Parser.cs")"
  if [[ "$got" == "$want_sorted" ]]; then
    pass "$p CanHandle set matches canonical pin ($(tr '\n' ' ' <<<"$got" | sed 's/ $//'))"
  else
    fail_msg "$p CanHandle drift: got [$(tr '\n' ' ' <<<"$got")] want [$(tr '\n' ' ' <<<"$want_sorted")]"
  fi
}
check_set Npm package.json package-lock.json pnpm-lock.yaml yarn.lock bun.lock bun.lockb
check_set Pip requirements.txt pyproject.toml poetry.lock uv.lock environment.yml environment.yaml Pipfile.lock
check_set Go go.mod go.sum

# ---- Check 3 (FAIL): CanHandle literal order matches canonical src order ----
order_ok_src() {
  local file="$1"; shift
  local prev=0 cur=0 f
  for f in "$@"; do
    cur=$(grep -b -o -m1 -F "\"$f\"" "$file" | head -1 | cut -d: -f1)
    [[ -z "$cur" ]] && return 0  # missing already FAILed above; skip order
    if (( cur < prev )); then return 1; fi
    prev=$cur
  done
  return 0
}
if order_ok_src "$PARSERS/NpmParser.cs" package.json package-lock.json pnpm-lock.yaml yarn.lock bun.lock bun.lockb; then
  pass "npm CanHandle order OK"
else
  fail_msg "npm CanHandle wrong order in NpmParser.cs"
fi
if order_ok_src "$PARSERS/PipParser.cs" requirements.txt pyproject.toml poetry.lock uv.lock environment.yml environment.yaml Pipfile.lock; then
  pass "pip CanHandle order OK"
else
  fail_msg "pip CanHandle wrong order in PipParser.cs"
fi
if order_ok_src "$PARSERS/GoParser.cs" go.mod go.sum; then
  pass "go CanHandle order OK"
else
  fail_msg "go CanHandle wrong order in GoParser.cs"
fi

# ---- Check 4 (FAIL): tier-preference pins in source comments ----
grep -q 'Lock > manifest' "$PARSERS/NpmParser.cs" \
  && pass "npm lock-beats-manifest tier pin" \
  || fail_msg "NpmParser missing lock-beats-manifest tier comment"
grep -q 'Pipfile.lock.*authoritative' "$PARSERS/PipParser.cs" \
  && pass "pip Pipfile.lock authoritative-first tier pin" \
  || fail_msg "PipParser missing Pipfile.lock authoritative-first tier comment"
grep -q 'poetry/uv merged' "$REGISTRY" \
  && pass "pip poetry/uv merged tier pin" \
  || fail_msg "ParserRegistry missing pip poetry/uv merged tier comment"
grep -q 'requirements > pyproject > environment' "$REGISTRY" \
  && pass "pip requirements > pyproject > environment tier pin" \
  || fail_msg "ParserRegistry missing pip requirements > pyproject > environment tier comment"
grep -q '// indirect' "$PARSERS/GoParser.cs" \
  && pass "go // indirect transitive pin" \
  || fail_msg "GoParser missing go // indirect transitive pin"

# ---- Check 5 (FAIL): parsed-but-deferred / IsTransitive / conda / bun / dedup pins ----
grep -q 'parsed-but-deferred' "$PARSERS/GoParser.cs" \
  && pass "parsed-but-deferred pin present (go h1 validated-not-stored)" \
  || fail_msg "GoParser missing parsed-but-deferred pin (go h1)"
grep -q 'deferred' "$PARSERS/PipParser.cs" \
  && pass "deferred pin present (pip hashes validated-not-stored)" \
  || fail_msg "PipParser missing deferred pin (pip hashes)"
grep -q 'IsTransitive: false' "$PARSERS/PipParser.cs" \
  && pass "IsTransitive:false pin present (Pipfile.lock entries)" \
  || fail_msg "PipParser missing IsTransitive:false pin (Pipfile.lock entries)"
grep -q 'SplitCondaSpec' "$PARSERS/PipParser.cs" \
  && pass "conda pin present (environment.yml reported-as-pip)" \
  || fail_msg "PipParser missing conda pin (SplitCondaSpec)"
grep -q 'bun.lockb is binary' "$PARSERS/NpmParser.cs" \
  && pass "bun.lockb binary-empty pin present" \
  || fail_msg "NpmParser missing bun.lockb binary-yields-empty pin"
grep -qi 'dedup' "$REGISTRY" \
  && pass "dedup pin present (locks merge deduped / DeduplicateAndSort)" \
  || fail_msg "ParserRegistry missing dedup pin (DeduplicateAndSort)"

# ---- Check 6 (WARN): abbreviation-only drift (registry shorthand vs README full names) ----
if grep -q '> environment)' "$REGISTRY" && grep -q 'environment\.yml' "$README"; then
  warn_msg "abbreviation-only drift: registry says \`environment\`, README spells \`environment.yml|environment.yaml\` (accepted)"
else
  pass "no abbreviation drift (registry/README environment naming consistent)"
fi

# ---- Check 7 (FAIL): generate --help lists current formats ----
# (Ecosystems have no --help surface post-#123: generate scans all 13;
# README L5 in Check 1 is the ecosystem doc.)
HELP_TXT="$(dotnet run --project "$PROJECT" -- generate --help 2>/dev/null || true)"
if grep -qF "$FMTS" <<<"$HELP_TXT"; then
  pass "generate --help lists current formats"
else
  fail_msg "generate --help drifted from SupportedFormats const"
fi

echo "---"
if (( fail )); then
  echo "PARSER-COVERAGE-PROBE FAIL (real drift; README/src update needed, not made by probe)"
  exit 1
fi
if (( warn )); then
  note "PARSER-COVERAGE-PROBE OK with abbreviation warnings"
else
  echo "PARSER-COVERAGE-PROBE OK"
fi
exit 0
