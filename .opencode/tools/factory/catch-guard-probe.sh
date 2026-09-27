#!/usr/bin/env bash
# catch-guard-probe: CS0160 catch-guard for src/ (pre-qualified: catch blocks touched 3/3 issues #62-64).
# Check 1 FAIL (exit 1): `catch (FileNotFound...` / `catch (DirectoryNotFound...`
#   in src/*.cs — CS0160 re-introduction (both derive from IOException; never
#   catch them separately).
# Check 2 WARN (exit 0): `catch ... when ... FileNotFound` without a trailing
#   `// allowlist:` comment on the same line.
# Check 3 FAIL (exit 1): every `catch (IOException)` in src/Olaf.Parsers +
#   src/Olaf.Cli (*.cs, obj/bin excluded) not followed within 3 lines by a
#   CS0160 covering comment (grep for 0160|inheritance|never catch).
#   `when`-guarded catches are exempt from check 3 — audited by check 2 instead.
VERSION="0.1.0"
set -euo pipefail

# ---- Canonical root resolution (copy-paste; do not hardcode paths) ----
# (from _template.sh 0.2.0; factory tools live at .opencode/tools/factory/ so
# repo root is ../../.. from the script dir.)
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
warn=0
warn_msg() { echo "WARN: $*"; warn=1; }
note() { echo "NOTE: $*"; }

if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: $(basename "$0") [--help] [--version]"
  echo "CS0160 catch-guard for src/: FAILs on direct catch of FileNotFound-"
  echo "or DirectoryNotFoundException (CS0160 re-introduction); WARNs on"
  echo "\`catch ... when ... FileNotFound\` guards lacking a trailing"
  echo "\`// allowlist:\` comment; FAILs per \`catch (IOException)\` in"
  echo "src/Olaf.Parsers + src/Olaf.Cli not followed within 3 lines by a"
  echo "CS0160 covering comment (0160|inheritance|never catch)."
  echo "Exit 0 = OK (or WARN-only); 1 = guard violation; 2 = usage/IO error."
  echo "Example: ./.opencode/tools/factory/catch-guard-probe.sh"
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

SRC="$ROOT/src"
[[ -d "$SRC" ]] || { echo "Missing source dir: $SRC" >&2; exit 2; }
GREP_BASE=(grep -rn -E --include='*.cs' --exclude-dir=obj --exclude-dir=bin)

echo "--- Check 1 (FAIL): direct catch of File/DirectoryNotFound (CS0160) ---"
PAT1='catch\s*\(\s*[^)]*(FileNotFound|DirectoryNotFound)'
C1="$("${GREP_BASE[@]}" "$PAT1" "$SRC" || true)"
if [[ -z "$C1" ]]; then
  pass "no direct catch of File/DirectoryNotFound in src/"
else
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    fail_msg "CS0160 re-introduction: $line"
  done <<<"$C1"
fi

echo "--- Check 2 (WARN): when-guarded FileNotFound catches need // allowlist: ---"
PAT2='catch.*when.*FileNotFound'
C2="$("${GREP_BASE[@]}" "$PAT2" "$SRC" || true)"
if [[ -z "$C2" ]]; then
  pass "no when-guarded FileNotFound catches in src/"
else
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    if [[ "$line" == *"allowlist:"* ]]; then
      pass "allowlisted when-guard: $line"
    else
      warn_msg "when-guarded FileNotFound catch without // allowlist: comment: $line"
    fi
  done <<<"$C2"
fi

echo "--- Check 3 (FAIL): catch (IOException) sites need CS0160 covering comment ---"
PAT3='catch\s*\(\s*IOException'
C3="$("${GREP_BASE[@]}" "$PAT3" "$ROOT/src/Olaf.Parsers" "$ROOT/src/Olaf.Cli" || true)"
if [[ -z "$C3" ]]; then
  pass "no catch (IOException) sites found (unexpected — check scope)"
else
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    loc="${line%%:*}"
    rest="${line#*:}"
    lineno="${rest%%:*}"
    text="${rest#*:}"
    if [[ "$text" == *"when"* ]]; then
      note "when-guarded, allowlist-audited (exempt from comment check): $line"
      continue
    fi
    window="$(sed -n "$((lineno + 1)),$((lineno + 3))p" "$loc")"
    if grep -qE '0160|inheritance|never catch' <<<"$window"; then
      pass "covering comment present: $loc:$lineno"
    else
      fail_msg "catch (IOException) without CS0160 covering comment within 3 lines: $loc:$lineno"
    fi
  done <<<"$C3"
fi

echo "---"
if (( fail )); then
  echo "CATCH-GUARD-PROBE FAIL"
  exit 1
fi
if (( warn )); then
  echo "CATCH-GUARD-PROBE OK with allowlist warnings"
else
  echo "CATCH-GUARD-PROBE OK"
fi
exit 0
