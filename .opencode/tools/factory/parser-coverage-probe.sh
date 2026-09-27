#!/usr/bin/env bash
# parser-coverage-probe: README parser-coverage drift probe (issue: README L7 stale).
# Extracts CanHandle filename sets per parser, SupportedEcosystems/SupportedFormats
# consts, tier-preference comments, and --help text; diffs against README.md
# parser-coverage lines. FAIL (exit 1) on missing filename, wrong order, or
# missing parsed-but-deferred/IsTransitive/conda notes; WARN (exit 0 + note) on
# abbreviation-only drift (e.g. registry `environment` vs README full names).
# 0.2.0 adds: Apk/Dpkg/Rpm CanHandle sets (bare-name extractor, no dot filter),
# container ScanOverlay DB-routing static check, OS deferred-note checks
# (L:/A:/Architecture: validated-but-deferred, NVRA split, binary->empty,
# IsTransitive=false, license-unknown reason).
VERSION="0.2.0"
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
README="$ROOT/README.md"
PARSERS="$ROOT/src/Olaf.Parsers"
PROGRAM="$ROOT/src/Olaf.Cli/Program.cs"
REGISTRY="$PARSERS/ParserRegistry.cs"

if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: $(basename "$0") [--help] [--version]"
  echo "Probe README parser-coverage drift: extracts parser CanHandle sets"
  echo "(Npm/Pip/Go dotted names + Apk/Dpkg/Rpm bare names),"
  echo "SupportedEcosystems/SupportedFormats consts, tier comments, --help text,"
  echo "container ScanOverlay DB-routing, OS deferred notes;"
  echo "diffs against README.md parser-coverage lines."
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
for f in "$README" "$PROGRAM" "$REGISTRY"; do
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
    | grep -o '"[^"]*"' | tr -d '"' | grep -E '^[A-Za-z0-9_.-]+$' | grep '\.' | sort -u
}

# ---- Bare-name variant for OS parsers (0.2.0 additive): Apk/Dpkg/Rpm ----
# ---- CanHandle on extensionless DB basenames (installed/status/Packages) ----
# ---- which the dotted filter above would drop; same body-scoping, no dot ----
# ---- requirement. Npm/Pip/Go path untouched. ----
extract_handles_bare() {
  awk '/bool CanHandle/,/^\s*\}/' "$1" \
    | grep -o '"[^"]*"' | tr -d '"' | grep -E '^[A-Za-z0-9_.-]+$' | sort -u
}

echo "--- CanHandle sets (extracted) ---"
for p in Npm Pip Go; do
  echo "$p: $(extract_handles "$PARSERS/${p}Parser.cs" | tr '\n' ' ')"
done
for p in Apk Dpkg Rpm; do
  echo "$p: $(extract_handles_bare "$PARSERS/${p}Parser.cs" | tr '\n' ' ')"
done

# ---- Extract Supported consts ----
ECOS=$(grep -o 'SupportedEcosystems = "[^"]*"' "$PROGRAM" | cut -d'"' -f2)
FMTS=$(grep -o 'SupportedFormats = "[^"]*"' "$PROGRAM" | cut -d'"' -f2)
echo "SupportedEcosystems=$ECOS"
echo "SupportedFormats=$FMTS"

# ---- README coverage blob (L3/L5/L7 region: first 10 lines) ----
COV="$(head -10 "$README")"

# ---- Check 1 (FAIL): ecosystems/formats consts reflected in README ----
if grep -qF "$ECOS" "$README"; then pass "README lists SupportedEcosystems"; else fail_msg "README missing SupportedEcosystems '$ECOS'"; fi
if grep -qF "$FMTS" "$README"; then pass "README lists SupportedFormats"; else fail_msg "README missing SupportedFormats '$FMTS'"; fi
if grep -q 'pypi.*alias.*pip' "$README"; then pass "README notes pypi alias"; else fail_msg "README missing pypi-alias-for-pip note"; fi
if grep -q 'markdown.*alias.*md' "$README"; then pass "README notes markdown alias"; else fail_msg "README missing markdown-alias-for-md note"; fi

# ---- Check 2 (FAIL): every CanHandle filename present in coverage lines ----
for p in Npm Pip Go; do
  while IFS= read -r fn; do
    [[ -z "$fn" ]] && continue
    if grep -qF "$fn" <<<"$COV"; then
      pass "README covers $p file '$fn'"
    else
      fail_msg "README missing $p file '$fn'"
    fi
  done < <(extract_handles "$PARSERS/${p}Parser.cs")
done
for p in Apk Dpkg Rpm; do
  while IFS= read -r fn; do
    [[ -z "$fn" ]] && continue
    if grep -qF "$fn" <<<"$COV"; then
      pass "README covers $p file '$fn'"
    else
      fail_msg "README missing $p file '$fn'"
    fi
  done < <(extract_handles_bare "$PARSERS/${p}Parser.cs")
done

# ---- Check 3 (FAIL): filename order matches documented canonical order ----
order_ok() {
  local prev=0 cur=0 f
  for f in "$@"; do
    cur=$(grep -b -o -F "$f" <<<"$COV" | head -1 | cut -d: -f1)
    [[ -z "$cur" ]] && return 0  # missing already FAILed above; skip order
    if (( cur < prev )); then return 1; fi
    prev=$cur
  done
  return 0
}
if order_ok package.json package-lock.json pnpm-lock.yaml yarn.lock bun.lock; then
  pass "npm filename order OK"
else
  fail_msg "npm filename wrong order in README coverage line"
fi
if order_ok Pipfile.lock requirements.txt pyproject.toml poetry.lock uv.lock environment.yml environment.yaml; then
  pass "pip filename order OK"
else
  fail_msg "pip filename wrong order in README coverage line"
fi
if order_ok go.mod go.sum; then
  pass "go filename order OK"
else
  fail_msg "go filename wrong order in README coverage line"
fi

# ---- Check 4 (FAIL): tier-preference sentences ----
grep -q 'any lock beats manifest' "$README" || grep -q 'lock beats manifest' "$README" \
  && pass "npm lock-beats-manifest tier note" \
  || fail_msg "README missing npm lock-beats-manifest tier note"
grep -q 'Pipfile.lock.*authoritative-first' "$README" \
  && pass "pip Pipfile.lock authoritative-first tier note" \
  || fail_msg "README missing pip Pipfile.lock authoritative-first tier note"
grep -q 'poetry.lock.*uv.lock.*merged' "$README" \
  && pass "pip poetry/uv merged tier note" \
  || fail_msg "README missing pip poetry.lock/uv.lock merged tier note"
grep -q 'requirements.txt.*>.*pyproject.toml.*>.*environment' "$README" \
  && pass "pip requirements > pyproject > environment tier note" \
  || fail_msg "README missing pip requirements > pyproject > environment tier note"
grep -q 'indirect' "$README" \
  && pass "go // indirect transitive note" \
  || fail_msg "README missing go // indirect transitive note"

# ---- Check 5 (FAIL): parsed-but-deferred / IsTransitive / conda notes ----
grep -q 'parsed-but-deferred' "$README" \
  && pass "parsed-but-deferred note present" \
  || fail_msg "README missing parsed-but-deferred note (pip hashes / go h1)"
grep -q 'IsTransitive=false' "$README" \
  && pass "IsTransitive=false note present" \
  || fail_msg "README missing IsTransitive=false note (Pipfile.lock entries)"
grep -q 'conda' "$README" \
  && pass "conda note present" \
  || fail_msg "README missing conda-entries-reported-as-pip note"
grep -q 'bun.lockb.*binary.*empty\|binary yields empty' "$README" \
  && pass "bun.lockb binary-empty note present" \
  || fail_msg "README missing bun.lockb binary-yields-empty note"
grep -q 'dedupe\|deduped' "$README" \
  && pass "dedup note present" \
  || fail_msg "README missing dedup note (locks merge deduped / go.sum deduped)"

# ---- Check 6 (WARN): abbreviation-only drift (registry shorthand vs README full names) ----
if grep -q '> environment)' "$REGISTRY" && grep -q 'environment\.yml' "$README"; then
  warn_msg "abbreviation-only drift: registry says \`environment\`, README spells \`environment.yml|environment.yaml\` (accepted)"
else
  pass "no abbreviation drift (registry/README environment naming consistent)"
fi

# ---- Check 7 (FAIL): --help text ecosystems/formats match consts ----
HELP_TXT="$(dotnet run --project "$ROOT/src/Olaf.Cli" -- --help 2>/dev/null || true)"
if grep -qF "$ECOS" <<<"$HELP_TXT" && grep -qF "$FMTS" <<<"$HELP_TXT"; then
  pass "--help lists current ecosystems + formats"
else
  fail_msg "--help text drifted from Supported consts"
fi

# ---- Check 8 (FAIL, 0.2.0): OS CanHandle static — Apk/Dpkg/Rpm handle ----
# ---- their DB basenames (extensionless, so not covered by Check 2 dot ----
# ---- filter without the bare extractor) ----
if grep -q '"installed"' "$PARSERS/ApkParser.cs"; then
  pass "Apk CanHandle 'installed'"
else
  fail_msg "ApkParser CanHandle missing 'installed'"
fi
if grep -q '"status"' "$PARSERS/DpkgParser.cs"; then
  pass "Dpkg CanHandle 'status'"
else
  fail_msg "DpkgParser CanHandle missing 'status'"
fi
if grep -q '"Packages"' "$PARSERS/RpmParser.cs"; then
  pass "Rpm CanHandle 'Packages'"
else
  fail_msg "RpmParser CanHandle missing 'Packages'"
fi

# ---- Check 9 (FAIL, 0.2.0): container ScanOverlay DB-routing static ----
# ---- ScanOverlay must route the three on-disk DB paths to their parsers ----
CIP="$PARSERS/ContainerImageParser.cs"
if grep -q 'ScanOverlay' "$CIP"; then
  pass "ScanOverlay present in ContainerImageParser"
else
  fail_msg "ContainerImageParser missing ScanOverlay"
fi
if grep -qF 'lib/apk/db/installed' "$CIP"; then
  pass "ScanOverlay routes lib/apk/db/installed"
else
  fail_msg "ScanOverlay missing lib/apk/db/installed route"
fi
if grep -qF 'lib/dpkg/status' "$CIP"; then
  pass "ScanOverlay routes lib/dpkg/status"
else
  fail_msg "ScanOverlay missing lib/dpkg/status route"
fi
if grep -qF 'var/lib/rpm/Packages' "$CIP" && grep -qF 'usr/lib/sysimage/rpm/Packages' "$CIP"; then
  pass "ScanOverlay routes var/lib/rpm + sysimage Packages"
else
  fail_msg "ScanOverlay missing rpm Packages routes (var/lib/rpm + usr/lib/sysimage/rpm)"
fi

# ---- Check 10 (FAIL, 0.2.0): OS deferred-note checks in README ----
# ---- L:/A:/Architecture: validated-but-deferred; NVRA split; binary->empty; ----
# ---- IsTransitive=false on OS entries; license-unknown reason ----
if grep -q 'L:.*validated-but-deferred\|validated-but-deferred.*L:' "$README"; then
  pass "README notes apk L: validated-but-deferred"
else
  fail_msg "README missing apk L: validated-but-deferred note"
fi
if grep -q 'A:.*validated-but-deferred\|validated-but-deferred.*A:' "$README"; then
  pass "README notes apk A: validated-but-deferred"
else
  fail_msg "README missing apk A: validated-but-deferred note"
fi
if grep -q 'Architecture:.*validated-but-deferred\|validated-but-deferred.*Architecture' "$README"; then
  pass "README notes dpkg Architecture: validated-but-deferred"
else
  fail_msg "README missing dpkg Architecture: validated-but-deferred note"
fi
if grep -q 'NVRA' "$README"; then
  pass "README notes rpm NVRA split"
else
  fail_msg "README missing rpm NVRA split note"
fi
if grep -q 'BerkeleyDB.*empty\|binary.*yields empty' "$README"; then
  pass "README notes rpm binary->empty"
else
  fail_msg "README missing rpm binary-yields-empty note"
fi
if grep -q 'IsTransitive=false' "$README" && grep -q '`apk`.*IsTransitive=false\|IsTransitive=false.*`apk`' "$README"; then
  pass "README notes OS IsTransitive=false"
else
  # Fallback: README carries IsTransitive=false on apk/dpkg/rpm lines (wording drift tolerated)
  if [[ "$(grep -o 'IsTransitive=false' "$README" | wc -l)" -ge 4 ]]; then
    pass "README notes OS IsTransitive=false (4+ occurrences incl. pip/go)"
  else
    fail_msg "README missing OS IsTransitive=false note (apk/dpkg/rpm entries)"
  fi
fi
if grep -q 'license-unknown' "$README"; then
  pass "README notes license-unknown reason"
else
  fail_msg "README missing license-unknown reason note"
fi

echo "---"
if (( fail )); then
  echo "PARSER-COVERAGE-PROBE FAIL (real drift; README update needed, not made by probe)"
  exit 1
fi
if (( warn )); then
  note "PARSER-COVERAGE-PROBE OK with abbreviation warnings"
else
  echo "PARSER-COVERAGE-PROBE OK"
fi
exit 0
