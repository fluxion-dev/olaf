#!/usr/bin/env bash
# fqn-lint: fully-qualified-name lint for src/*.cs (pre-qualified: 15-line
# HttpStatusCode cluster fixed in rectify + #68/#69 FQN findings; same
# direct-promote justification as catch-guard-probe).
# Check 1 FAIL (exit 1): any single file with >2 non-using, non-comment code
#   lines carrying `System.<X>` tokens (a `using` pays for itself at 3+ uses).
# Check 2 FAIL (exit 1): any single FQN (full `System(\.Seg)+` token,
#   `(?<![A-Za-z_])` lookbehind so `OperatingSystem.*` never counts)
#   appearing in code lines across >=3 distinct files (shared vocabulary
#   belongs in usings).
# Allowlist by construction: `using System...;` directives (incl.
#   global/static/alias forms), `//` comments (a token only counts when it
#   precedes any `//` on the line), and files with <=2 hit lines never FAIL.
# Check 3 FAIL (exit 1): any promoted factory tool (*.sh, scratch excluded)
#   reading `PIPESTATUS` without a pipefail wrapper (`set -euo pipefail` /
#   `set -o pipefail` anywhere in the file). Exit-code hygiene per
#   factory-qa: assert via redirect-to-file + RC capture (see _template.sh
#   run_gate), never `cmd | tee/head` + `${PIPESTATUS[0]}`.
VERSION="0.1.2"
set -euo pipefail

# ---- Canonical root resolution (copy-paste; do not hardcode paths) ----
# (from _template.sh 0.2.3; factory tools live at .opencode/tools/factory/ so
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
note() { echo "NOTE: $*"; }

if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: $(basename "$0") [--help] [--version]"
  echo "FQN lint for src/*.cs (*.cs only, obj/bin excluded): FAILs per file"
  echo "with >2 non-using, non-comment code lines carrying \`System.<X>\`"
  echo "tokens (check 1), and per FQN token appearing in code lines across"
  echo ">=3 distinct files (check 2). \`using System...;\` directives"
  echo "(global/static/alias) and \`//\` comments are allowlisted by"
  echo "construction; files with <=2 hit lines never FAIL. Check 3 lints"
  echo "promoted factory tools (*.sh) for bare \`PIPESTATUS\` use without a"
  echo "pipefail wrapper (redirect-to-file + RC instead)."
  echo "Exit 0 = OK; 1 = FQN violation; 2 = usage/IO error."
  echo "Example: ./.opencode/tools/factory/fqn-lint.sh"
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
command -v python3 >/dev/null 2>&1 || { echo "python3 required" >&2; exit 2; }

# Structured candidate counts from python (thresholds enforced in bash so the
# spec reads directly): FILECOUNT <rel> <hit-lines>; FQNFILES <token> <nfiles>
#   <rel,rel,...>. Comment heuristic: code is the text before the first `//`
#   not preceded by `:` (keeps https:// URLs inside strings intact).
SCAN="$(SRC="$SRC" python3 <<'PY'
import os, re
src = os.environ["SRC"]
tok = re.compile(r"(?<![A-Za-z_])System(?:\.[A-Za-z_][A-Za-z0-9_]*)+")
using_re = re.compile(r"^\s*using\s+(global\s+)?(static\s+)?System[_.A-Za-z0-9]*\s*;\s*$")
alias_re = re.compile(r"^\s*using\s+[A-Za-z_][A-Za-z0-9_]*\s*=")
comment_re = re.compile(r"(?<!:)//")
file_hits = {}
fqn_files = {}
for dirpath, dirnames, filenames in os.walk(src):
    dirnames[:] = [d for d in dirnames if d not in ("obj", "bin")]
    for fn in sorted(filenames):
        if not fn.endswith(".cs"):
            continue
        p = os.path.join(dirpath, fn)
        rel = os.path.relpath(p, src)
        try:
            # utf-8-sig strips a BOM so a BOM-led `using System...;` still
            # matches the using-directive allowlist (e.g. Olaf.Cli/Program.cs).
            with open(p, encoding="utf-8-sig", errors="replace") as f:
                lines = f.readlines()
        except OSError:
            continue
        n = 0
        for ln, line in enumerate(lines, 1):
            code = comment_re.split(line.rstrip("\n"), maxsplit=1)[0]
            if not tok.search(code):
                continue
            if using_re.match(code) or alias_re.match(code):
                continue
            n += 1
            for t in set(tok.findall(code)):
                fqn_files.setdefault(t, set()).add(rel)
        if n:
            file_hits[rel] = n
for rel in sorted(file_hits):
    print(f"FILECOUNT {rel} {file_hits[rel]}")
for t in sorted(fqn_files):
    files = sorted(fqn_files[t])
    print(f"FQNFILES {t} {len(files)} {','.join(files)}")
PY
)"

echo "--- Check 1 (FAIL): >2 FQN code-hit lines in one file ---"
C1=0
while IFS= read -r line; do
  [[ -z "$line" ]] && continue
  if [[ "$line" == FILECOUNT* ]]; then
    set -- $line
    rel="$2"; n="$3"
    if (( n > 2 )); then
      fail_msg "$rel carries $n FQN code-hit lines (>2) — add a using"
      C1=1
    else
      note "$rel carries $n FQN code-hit line(s) (<=2 allowlisted)"
    fi
  fi
done <<<"$SCAN"
(( C1 )) || pass "no file exceeds 2 FQN code-hit lines"

echo "--- Check 2 (FAIL): same FQN in >=3 files repo-wide ---"
C2=0
while IFS= read -r line; do
  [[ -z "$line" ]] && continue
  if [[ "$line" == FQNFILES* ]]; then
    token="${line%% *}"; rest="${line#* }"
    token="${rest%% *}"; rest="${rest#* }"
    nfiles="${rest%% *}"; files="${rest#* }"
    if (( nfiles >= 3 )); then
      fail_msg "$token shared across $nfiles files ($files) — hoist to usings"
      C2=1
    fi
  fi
done <<<"$SCAN"
(( C2 )) || pass "no FQN spans >=3 files"

echo "--- Check 3 (FAIL): PIPESTATUS without pipefail wrapper (factory *.sh) ---"
FACTORY="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
C3=0
while IFS= read -r sh; do
  [[ -z "$sh" ]] && continue
  rel="${sh#"$ROOT"/}"
  if grep -q 'PIPESTATUS' "$sh"; then
    if grep -q 'set -o pipefail\|set -euo pipefail' "$sh"; then
      note "$rel uses PIPESTATUS under a pipefail wrapper (allowed)"
    else
      fail_msg "$rel uses PIPESTATUS without pipefail wrapper — use redirect-to-file + RC (see _template.sh run_gate)"
      C3=1
    fi
  fi
done < <(find "$FACTORY" -maxdepth 1 -name '*.sh' -type f | LC_ALL=C sort)
(( C3 )) || pass "no bare PIPESTATUS use in factory tools"

echo "---"
if (( fail )); then
  echo "FQN-LINT FAIL"
  exit 1
fi
echo "FQN-LINT OK"
exit 0
