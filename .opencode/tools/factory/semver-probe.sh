#!/usr/bin/env bash
# semver-probe.sh — SemVer + Conventional Commits gate for olaf-factory.
# Asserts the factory's versioning contract so every issue ships a
# SemVer 2.0-compliant <Version> driven by a Conventional Commits type:
#   Arms:
#     S1 semver-valid — src/Olaf.Cli/Olaf.Cli.csproj <Version> parses as
#       stable SemVer MAJOR.MINOR.PATCH with optional +build (no -prerelease;
#       preview track retired; no leading zeros).
#     S2 pin-consistency — all version pins in README.md + site/index.html
#       (when present) resolve to a single stable SemVer value == csproj
#       <Version> (leading-v stripped).
#     C1 conventional-shape — every subject in a commit range (or a single
#       --title/--commit message) matches Conventional Commits:
#       type(scope)!?: subject  where type in
#       feat|fix|docs|chore|refactor|test|ci|build|perf|style|revert.
#       Scope is optional lowercase/dash/underscore/slash. Breaking is
#       `!` before the colon OR `BREAKING CHANGE:` in the body.
#     B1 bump-mapping — the csproj delta base->current matches the declared
#       --type under stable SemVer (0.x: breaking=>MINOR else MAJOR;
#       feat=>MINOR; fix/patch/docs/chore/...=>PATCH). Core moves with lower
#       reset (e.g. fix: 0.1.2 -> 0.1.3; feat: 0.1.3 -> 0.2.0). Prerelease
#       versions (hyphen) FAIL by design — the preview track is retired.
# Rules: repo-relative, idempotent, no secrets, exit 0/1/2.
VERSION="0.2.0"
set -euo pipefail

REPO_ROOT=""
BASE_VERSION=""
EXPECT_TYPE=""
CHECK_RANGE=""
CHECK_TITLE=""
CHECK_COMMIT=""
TIMEOUT_SECS=120

usage() {
  cat <<'EOF'
Usage: semver-probe.sh [options]

SemVer + Conventional Commits gate for olaf-factory (ASSERTS ONLY).

Options:
  --repo-root <dir>     Repo root (default: git top-level or script-relative fallback)
  --base-version <ver>  Base <Version> for B1 bump-mapping (default: skip B1)
  --type <type>         Declared conventional type for B1: feat|fix|breaking|patch|minor|major|docs|chore|refactor|test|ci|build|perf|style|revert
  --range <base..head>  Commit range for C1 (default: HEAD~1..HEAD when git present)
  --title <msg>         Single PR-title/commit-subject for C1 (repeatable)
  --commit <msg>        Alias for --title (repeatable)
  --timeout <secs>      Reserved (default: 120)
  --workdir <dir>       Work dir for logs (default: mktemp)
  --keep-temp           Keep work dir for debugging (default: remove)
  --help                Show this help and exit 0
  --version             Show version and exit 0

Examples:
  semver-probe.sh
  semver-probe.sh --base-version 0.1.2 --type feat
  semver-probe.sh --range main..HEAD
  semver-probe.sh --title "feat(parsers): add foo (closes #123)"

Exit codes: 0 PASS, 1 FAIL, 2 usage/environment error.
EOF
}

TITLES=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "$(basename "$0") $VERSION"; exit 0 ;;
    --repo-root) REPO_ROOT="${2:-}"; shift 2 ;;
    --repo-root=*) REPO_ROOT="${1#*=}"; shift ;;
    --base-version) BASE_VERSION="${2:-}"; shift 2 ;;
    --base-version=*) BASE_VERSION="${1#*=}"; shift ;;
    --type) EXPECT_TYPE="${2:-}"; shift 2 ;;
    --type=*) EXPECT_TYPE="${1#*=}"; shift ;;
    --range) CHECK_RANGE="${2:-}"; shift 2 ;;
    --range=*) CHECK_RANGE="${1#*=}"; shift ;;
    --title) TITLES+=("${2:-}"); shift 2 ;;
    --title=*) TITLES+=("${1#*=}"); shift ;;
    --commit) TITLES+=("${2:-}"); shift 2 ;;
    --commit=*) TITLES+=("${1#*=}"); shift ;;
    --timeout) TIMEOUT_SECS="${2:-}"; shift 2 ;;
    --timeout=*) TIMEOUT_SECS="${1#*=}"; shift ;;
    --workdir) WORKDIR="${2:-}"; shift 2 ;;
    --workdir=*) WORKDIR="${1#*=}"; shift ;;
    --keep-temp) KEEP_TEMP=1; shift ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done
# Normalize the --commit= alias form collected above (kept symmetric with --title=).
WORKDIR="${WORKDIR:-}"
KEEP_TEMP="${KEEP_TEMP:-0}"

if [[ -z "$REPO_ROOT" ]]; then
  if git rev-parse --show-toplevel >/dev/null 2>&1; then
    REPO_ROOT="$(git rev-parse --show-toplevel)"
  else
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    if [[ -f "$SCRIPT_DIR/../../../olaf.slnx" ]]; then
      REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
    elif [[ -f "$SCRIPT_DIR/../../../../olaf.slnx" ]]; then
      REPO_ROOT="$(cd "$SCRIPT_DIR/../../../.." && pwd)"
    else
      echo "Cannot locate repo root (no git top-level, no olaf.slnx fallback)." >&2
      exit 2
    fi
  fi
fi
if [[ ! -f "$REPO_ROOT/olaf.slnx" ]]; then
  echo "Repo root has no olaf.slnx: $REPO_ROOT" >&2
  exit 2
fi

CSPROJ="$REPO_ROOT/src/Olaf.Cli/Olaf.Cli.csproj"
README="$REPO_ROOT/README.md"
SITE="$REPO_ROOT/site/index.html"

fail=0
npass=0
pass() { echo "PASS: $*"; npass=$((npass + 1)); }
fail_msg() { echo "FAIL: $*"; fail=1; }
warn() { echo "WARN: $*"; }

if [[ -z "${WORKDIR:-}" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/semver-probe-XXXXXX")"
  MADE_TMP=1
else
  mkdir -p "$WORKDIR"
  MADE_TMP=0
fi
cleanup() {
  if (( ! KEEP_TEMP )) && (( MADE_TMP )) && [[ -d "$WORKDIR" ]]; then
    rm -rf "$WORKDIR"
  fi
}
trap cleanup EXIT

echo "== semver-probe v$VERSION =="
echo "repo: $REPO_ROOT"

# ---- S1: csproj <Version> is valid SemVer 2.0 ----
echo "-- S1 semver-valid --"
CSPROJ_VER="$(grep -oE '<Version>[^<]+</Version>' "$CSPROJ" | head -1 | sed -E 's|</?Version>||g' || true)"
if [[ -z "$CSPROJ_VER" ]]; then
  fail_msg "S1: cannot parse <Version> from $CSPROJ"
else
  echo "csproj <Version> = $CSPROJ_VER"
  if python3 - "$CSPROJ_VER" <<'PY' 2>/dev/null
import re, sys
v = sys.argv[1]
pat = r'^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(\+([0-9a-zA-Z-]+(\.[0-9a-zA-Z-]+)*))?$'
sys.exit(0 if re.match(pat, v) else 1)
PY
  then
    pass "S1: csproj $CSPROJ_VER is valid stable SemVer"
  else
    fail_msg "S1: csproj $CSPROJ_VER is NOT valid stable SemVer (want MAJOR.MINOR.PATCH[+build], no -prerelease, no leading zeros)"
  fi
fi

# ---- S2: pins resolve to single SemVer == csproj ----
# Pin scope is olaf product pins only (not every SemVer-like string in
# HTML): stable pins require leading v (`vX.Y.Z`) so bare dependency
# versions (1.1.9, bare 0.1.0) never count. Build-metadata placeholders
# (`0.1.3+...`) match on the v-prefixed URL pins only.
echo "-- S2 pin-consistency --"
if [[ -z "${CSPROJ_VER:-}" ]]; then
  fail_msg "S2: SKIP — no csproj version from S1"
else
  PIN_RE='v[0-9]+\.[0-9]+\.[0-9]+'
  PINS="$(grep -oE "$PIN_RE" "$README" || true)"
  SITE_PINS=""
  if [[ -f "$SITE" ]]; then
    SITE_PINS="$(grep -oE "$PIN_RE" "$SITE" || true)"
  else
    warn "S2: missing site/index.html — README-only pins"
  fi
  ALL_PINS="$(printf '%s\n%s\n' "$PINS" "$SITE_PINS" | grep -E '.+' || true)"
  if [[ -z "$ALL_PINS" ]]; then
    fail_msg "S2: no SemVer pins in README.md + site/index.html (csproj $CSPROJ_VER)"
  else
    UNIQUES="$(printf '%s\n' "$ALL_PINS" | sed -E 's/^v//' | sort -u | tr '\n' ' ')"
    NUNIQ="$(printf '%s\n' "$ALL_PINS" | sed -E 's/^v//' | sort -u | wc -l | tr -d ' ')"
    if [[ "$NUNIQ" -ne 1 ]]; then
      fail_msg "S2: pins resolve to $NUNIQ distinct versions ($UNIQUES), want exactly 1"
    elif [[ "${UNIQUES% }" != "$CSPROJ_VER" ]]; then
      fail_msg "S2: pin ${UNIQUES% } != csproj $CSPROJ_VER"
    else
      pass "S2: README+site pins resolve to single $CSPROJ_VER == csproj"
    fi
  fi
fi

# ---- C1: conventional-commit shape ----
echo "-- C1 conventional-shape --"
CONV_RE='^(feat|fix|docs|chore|refactor|test|ci|build|perf|style|revert)(\([a-z0-9/_-]+\))?(!)?: .+'
SUBJECTS_FILE="$WORKDIR/subjects.txt"
: > "$SUBJECTS_FILE"
if (( ${#TITLES[@]} )); then
  printf '%s\n' "${TITLES[@]}" > "$SUBJECTS_FILE"
elif [[ -n "$CHECK_RANGE" ]]; then
  if ! git -C "$REPO_ROOT" log --format='%s' "$CHECK_RANGE" > "$SUBJECTS_FILE" 2>/dev/null; then
    fail_msg "C1: git log failed for range $CHECK_RANGE"
  fi
else
  if git -C "$REPO_ROOT" rev-parse --verify HEAD >/dev/null 2>&1; then
    git -C "$REPO_ROOT" log --format='%s' 'HEAD~1..HEAD' > "$SUBJECTS_FILE" 2>/dev/null || : > "$SUBJECTS_FILE"
    if [[ ! -s "$SUBJECTS_FILE" ]]; then
      git -C "$REPO_ROOT" log --format='%s' -1 > "$SUBJECTS_FILE" 2>/dev/null || true
    fi
  else
    warn "C1: no git HEAD — SKIP range check (use --title/--range)"
  fi
fi
if [[ -f "$SUBJECTS_FILE" ]] && [[ -s "$SUBJECTS_FILE" ]]; then
  C1FAIL=0
  while IFS= read -r subj || [[ -n "$subj" ]]; do
    [[ -z "$subj" ]] && continue
    if printf '%s' "$subj" | grep -qE -- "$CONV_RE"; then
      pass "C1: conventional subject: $subj"
    else
      fail_msg "C1: non-conventional subject: $subj (want 'type(scope)!?: subject', type=feat|fix|docs|chore|refactor|test|ci|build|perf|style|revert)"
      C1FAIL=1
    fi
    if printf '%s' "$subj" | grep -qE '^Merge (pull request|branch) '; then
      fail_msg "C1: merge commit subject is not conventional: $subj (use squash or conventional PR title)"
      C1FAIL=1
    fi
  done < "$SUBJECTS_FILE"
  if (( ! C1FAIL )); then
    pass "C1: all subjects conventional"
  fi
else
  if (( ${#TITLES[@]} )) || [[ -n "$CHECK_RANGE" ]]; then
    fail_msg "C1: no subjects to check (empty range/titles)"
  else
    warn "C1: no subjects found — SKIP (not a FAIL on fresh clones)"
  fi
fi

# ---- B1: bump mapping base -> current matches --type ----
echo "-- B1 bump-mapping --"
if [[ -z "$BASE_VERSION" ]]; then
  warn "B1: no --base-version — SKIP (pass --base-version + --type to gate the bump)"
elif [[ -z "$EXPECT_TYPE" ]]; then
  fail_msg "B1: --base-version given without --type (pass --type feat|fix|breaking|...)"
elif [[ -z "${CSPROJ_VER:-}" ]]; then
  fail_msg "B1: SKIP — no csproj version from S1"
else
  case "$EXPECT_TYPE" in
    feat|fix|breaking|major|minor|patch|docs|chore|refactor|test|ci|build|perf|style|revert) ;;
    *) echo "Invalid --type '$EXPECT_TYPE'." >&2; exit 2 ;;
  esac
  if python3 - "$BASE_VERSION" "$CSPROJ_VER" "$EXPECT_TYPE" <<'PY' 2>"$WORKDIR/b1.err"
import re, sys
sem = r'^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-((0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(\.(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(\+([0-9a-zA-Z-]+(\.[0-9a-zA-Z-]+)*))?$'
base, cur, typ = sys.argv[1], sys.argv[2], sys.argv[3]
for label, v in (("base", base), ("current", cur)):
    if not re.match(sem, v):
        print(f"{label} version not SemVer: {v}", file=sys.stderr)
        sys.exit(2)
def core(v):
    m = re.match(r'^(\d+)\.(\d+)\.(\d+)', v)
    return tuple(int(m.group(i)) for i in (1, 2, 3))
def pre(v):
    m = re.match(r'^\d+\.\d+\.\d+-([0-9a-zA-Z.-]+?)(\+.*)?$', v)
    return m.group(1) if m else ""
b, c = core(base), core(cur)
bp, cp = pre(base), pre(cur)
if bp or cp:
    print(f"preview track retired: prerelease versions are not accepted (base {base}, current {cur}); use stable MAJOR.MINOR.PATCH", file=sys.stderr)
    sys.exit(1)
if (b, bp) == (c, cp):
    print(f"no bump: base {base} == current {cur}", file=sys.stderr)
    sys.exit(1)
# Expected core transition per type (0.x breaking rule: MINOR, not MAJOR).
want = None
if typ in ("breaking", "major"):
    want = "major" if b[0] >= 1 else "minor"
elif typ == "feat" or typ == "minor":
    want = "minor"
elif typ in ("fix", "patch", "docs", "chore", "refactor", "test", "ci", "build", "perf", "style", "revert"):
    want = "patch"
if want == "major":
    core_ok = c[0] == b[0] + 1 and c[1] == 0 and c[2] == 0
elif want == "minor":
    core_ok = c[0] == b[0] and c[1] == b[1] + 1 and c[2] == 0
elif want == "patch":
    core_ok = c[0] == b[0] and c[1] == b[1] and c[2] == b[2] + 1
else:
    core_ok = False
if not core_ok:
    print(f"bump mismatch: base {base} -> current {cur} is not a {want} bump for type {typ} (want major=+1.0.0 minor=+0.1.0 patch=+0.0.1 with lower resets)", file=sys.stderr)
    sys.exit(1)
print(f"{want} bump ok: {base} -> {cur} for type {typ}")
PY
  then
    B1MSG="$(python3 - "$BASE_VERSION" "$CSPROJ_VER" "$EXPECT_TYPE" <<'PY' 2>/dev/null
import sys
print(f"{sys.argv[1]} -> {sys.argv[2]} for type {sys.argv[3]}")
PY
)"
    pass "B1: $B1MSG"
  else
    B1ERR="$(cat "$WORKDIR/b1.err" 2>/dev/null || echo 'bump check failed')"
    fail_msg "B1: $B1ERR"
  fi
fi

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "SEMVER-PROBE OK: $npass PASS"
  exit 0
else
  echo "SEMVER-PROBE FAIL: $npass PASS, see FAIL lines above" >&2
  exit 1
fi
