#!/usr/bin/env bash
# DEPRECATED (issue #123): template engine + README template section deleted — nothing to drift-check. Kept one issue cycle per lifecycle; delete next cycle if still unused.
# template-docs-probe.sh — Template-docs drift probe (promoted v0.1.0 direct, no scratch).
# Mandatory per recurring-drift rule, 4th sighting #70-#73: the template model-table /
#   caps text was touched every issue in the run (enrichment keys, holders key, engine
#   section), so README-vs-engine drift gets a standing gate. Direct-promote carries the
#   same justification as catch-guard-probe (pre-qualified repetition, pure grep/python).
# Offline-safe: pure grep + python3, no dotnet, no network, no temp files.
#   D1 FAIL: README model-table keys != TemplateEngine ToScope dict keys (exact sets for
#      top/license/group scopes) or missing Ordinal sort pins (README "Ordinal" wording +
#      StringComparer.Ordinal in TemplateEngine.ToScope dicts + TemplateModelBuilder
#      GroupBy/OrderBy).
#   D2 FAIL: README template section lacks a cap literal (256 KiB / 1 MiB / 10_000) or the
#      engine consts drift (MaxTemplateBytes != 256*1024, MaxOutputBytes != 1024*1024,
#      MaxIterations != 10_000).
#   D3 FAIL: 3-way toolVersion mismatch — README `toolVersion` literal vs
#      TemplateModelBuilder.ToolVersionValue vs Olaf.Cli.csproj Version.
#   D4 FAIL: any committed *.scriban fixture (exactly 2) carries a non-4-tag construct
#      ({{else}}, `|` filters, unknown #// block, bad var path) or unclosed/mismatched block.
#   D5 FAIL: attribution example block carries real-looking package/holder rows
#      without a nearby synthetic|illustrative|shape-only marker; or a `…`
#      abbreviation without an abbreviates-note; or a TemplateModel.Groups vs
#      LicenseGrouper sort mention without the Unknown-last contrast note.
# Rules: repo-relative, idempotent (read-only), no secrets, exit 0/1/2.
VERSION="0.2.0"
set -euo pipefail

# ---- Canonical root resolution (factory depth: ../../.. per parser-coverage-probe.sh) ----
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

# ---- Canonical pass/fail ----
fail=0
pass() { echo "PASS: $*"; }
fail_msg() { echo "FAIL: $*"; fail=1; }

usage() {
  cat <<EOF
Usage: $(basename "$0") [--help] [--version]

Template-docs drift probe: README model-table keys == TemplateEngine ToScope
keys (D1, exact + Ordinal), README caps == engine consts (D2: 256 KiB / 1 MiB
/ 10_000), README toolVersion literal == Olaf.Cli.csproj Version (D3, 3-way),
both committed *.scriban fixtures parse under the 4-tag grammar (D4: no
{{else}}/filters), attribution example synthetic-labels pinned (D5:
synthetic|illustrative|shape-only + abbreviates-note + Unknown-last
contrast). Offline-safe: pure grep + python3, no dotnet, no network.

Exit codes: 0 all PASS, 1 assertion failure, 2 usage/environment error.

Example:
  $(basename "$0")
EOF
}

if [[ "${1:-}" == "--help" ]]; then usage; exit 0; fi
if [[ "${1:-}" == "--version" ]]; then echo "$(basename "$0") $VERSION"; exit 0; fi
if [[ -n "${1:-}" ]]; then echo "Unknown option: $1 (try --help)" >&2; exit 2; fi

ENGINE="$ROOT/src/Olaf.Formatters/TemplateEngine.cs"
BUILDER="$ROOT/src/Olaf.Formatters/TemplateModel.cs"
CSPROJ="$ROOT/src/Olaf.Cli/Olaf.Cli.csproj"
README="$ROOT/README.md"
TPLDIR="$ROOT/tests/Olaf.Tests/Fixtures/templates"
for f in "$ENGINE" "$BUILDER" "$CSPROJ" "$README"; do
  [[ -f "$f" ]] || { echo "Missing required file: $f" >&2; exit 2; }
done
[[ -d "$TPLDIR" ]] || { echo "Missing template fixture dir: $TPLDIR" >&2; exit 2; }
command -v python3 >/dev/null 2>&1 || { echo "Missing required command: python3" >&2; exit 2; }

echo "== template-docs-probe v$VERSION =="
echo "repo: $ROOT"

echo "-- D1: README model-table keys == TemplateEngine ToScope keys (exact, Ordinal) --"
set +e
D1OUT="$(python3 - "$ENGINE" "$BUILDER" "$README" <<'PY'
import re, sys
engine_path, builder_path, readme_path = sys.argv[1], sys.argv[2], sys.argv[3]
src = open(engine_path).read()
builder = open(builder_path).read()
readme = open(readme_path).read()
bad = []

# Engine side: keys per ToScope overload, sliced method-by-method.
starts = [(m.group(1), m.start()) for m in re.finditer(
    r'private static Dictionary<string, object\?> ToScope\((TemplateModel model|TemplateLicenseEntry l|TemplateGroup g)\)', src)]
if len(starts) != 3:
    print(f"MISMATCH: want 3 ToScope overloads, found {len(starts)}")
    sys.exit(1)
starts.sort(key=lambda t: t[1])
bounds = [s for _, s in starts] + [len(src)]
scope_of = {'TemplateModel model': 'top', 'TemplateLicenseEntry l': 'license', 'TemplateGroup g': 'group'}
eng = {}
for (param, s), e in zip(starts, bounds[1:]):
    eng[scope_of[param]] = set(re.findall(r'\["(\w+)"\]', src[s:e]))

# README side: model-reference section only.
m0 = readme.find('Model reference (exact field names')
m1 = readme.find('(`LicenseText` is NOT in the model')
if m0 < 0 or m1 < 0 or m1 <= m0:
    print("MISMATCH: README model-reference section anchors not found")
    sys.exit(1)
section = readme[m0:m1]
STOP = {'string', 'int', 'bool', 'if', 'each', 'else'}
scalars = {m.group(1) for m in re.finditer(r'^\|\s*`([A-Za-z]+)(\[\])?`\s*\|', section, re.M)}
lic_row = next((l for l in section.splitlines() if '`licenses[]`' in l), '')
lic = {t for t in re.findall(r'`([^`]+)`', lic_row)
       if re.fullmatch(r'[a-z][a-zA-Z]*', t) and t not in STOP}
grp_row = next((l for l in section.splitlines() if '`groups[]`' in l), '')
gm = re.search(r'`\{\s*([^`]*)\}`', grp_row)
grp = set(re.findall(r'([a-z][a-zA-Z]*)\s*:', gm.group(1))) if gm else set()
rd = {'top': scalars, 'license': lic, 'group': grp}

for scope in ('top', 'license', 'group'):
    if eng[scope] != rd[scope]:
        only_eng = sorted(eng[scope] - rd[scope])
        only_rd = sorted(rd[scope] - eng[scope])
        print(f"MISMATCH [{scope}]: engine-only={only_eng} readme-only={only_rd}")
        bad.append(scope)
    else:
        print(f"PASS: D1 [{scope}] exact ({len(eng[scope])} keys: {', '.join(sorted(eng[scope]))})")

# Ordinal pins: README wording + engine comparers.
rd_ord = section.count('Ordinal') + readme[m0:m0 + 2000].count('Ordinal')
eng_ord = src.count('StringComparer.Ordinal')
bld_ord = builder.count('StringComparer.Ordinal')
if 'Ordinal' not in section:
    print("MISMATCH: README model section lacks 'Ordinal' sort wording")
    bad.append('ordinal-readme')
else:
    print(f"PASS: D1 README carries Ordinal sort wording ({section.count('Ordinal')}x in model section)")
if eng_ord < 3:
    print(f"MISMATCH: TemplateEngine.cs has {eng_ord}x StringComparer.Ordinal (want >=3: 3 ToScope dicts)")
    bad.append('ordinal-engine')
else:
    print(f"PASS: D1 engine ToScope dicts Ordinal ({eng_ord}x)")
if bld_ord < 2:
    print(f"MISMATCH: TemplateModel.cs has {bld_ord}x StringComparer.Ordinal (want >=2: GroupBy+OrderBy)")
    bad.append('ordinal-builder')
else:
    print(f"PASS: D1 builder GroupBy/OrderBy Ordinal ({bld_ord}x)")

sys.exit(1 if bad else 0)
PY
)"
D1RC=$?
set -e
echo "$D1OUT"
if [[ "$D1RC" -eq 0 ]]; then
  pass "D1 model keys exact + Ordinal pinned"
else
  fail_msg "D1 model-keys drift (see MISMATCH lines above)"
fi

echo "-- D2: README caps == engine consts (256 KiB / 1 MiB / 10_000) --"
set +e
D2OUT="$(python3 - "$ENGINE" "$README" <<'PY'
import re, sys
engine_path, readme_path = sys.argv[1], sys.argv[2]
src = open(engine_path).read()
readme = open(readme_path).read()
bad = []
t0 = readme.find('### Custom attribution templates')
t1 = readme.find('## Exit codes')
tsection = readme[t0:t1] if 0 <= t0 < t1 else ''
for lit in ('256 KiB', '1 MiB', '10_000'):
    if lit in tsection:
        print(f"PASS: D2 README carries '{lit}'")
    else:
        print(f"MISMATCH: README template section lacks '{lit}'")
        bad.append(lit)
mt = re.search(r'MaxTemplateBytes\s*=\s*(\d+)\s*\*\s*(\d+)', src)
mo = re.search(r'MaxOutputBytes\s*=\s*(\d+)\s*\*\s*(\d+)', src)
mi = re.search(r'MaxIterations\s*=\s*([\d_]+)', src)
checks = [('MaxTemplateBytes', mt, 256 * 1024), ('MaxOutputBytes', mo, 1024 * 1024)]
for name, m, want in checks:
    if m and int(m.group(1)) * int(m.group(2)) == want:
        print(f"PASS: D2 engine {name} = {m.group(1)}*{m.group(2)} ({want} bytes)")
    else:
        print(f"MISMATCH: engine {name} != {want} bytes")
        bad.append(name)
if mi and int(mi.group(1).replace('_', '')) == 10_000:
    print("PASS: D2 engine MaxIterations = 10_000")
else:
    print("MISMATCH: engine MaxIterations != 10_000")
    bad.append('MaxIterations')
sys.exit(1 if bad else 0)
PY
)"
D2RC=$?
set -e
echo "$D2OUT"
if [[ "$D2RC" -eq 0 ]]; then
  pass "D2 caps pinned (256 KiB / 1 MiB / 10_000)"
else
  fail_msg "D2 caps drift (see MISMATCH lines above)"
fi

echo "-- D3: README toolVersion literal == Olaf.Cli.csproj Version (3-way) --"
set +e
D3OUT="$(python3 - "$BUILDER" "$CSPROJ" "$README" <<'PY'
import re, sys
builder_path, csproj_path, readme_path = sys.argv[1], sys.argv[2], sys.argv[3]
builder = open(builder_path).read()
csproj = open(csproj_path).read()
readme = open(readme_path).read()
mb = re.search(r'ToolVersionValue\s*=\s*"([^"]+)"', builder)
mc = re.search(r'<Version>([^<]+)</Version>', csproj)
m0 = readme.find('Model reference (exact field names')
m1 = readme.find('(`LicenseText` is NOT in the model')
section = readme[m0:m1] if 0 <= m0 < m1 else ''
mr = re.search(r'`toolVersion`[^`\n]*`?"([^"]+)"', section)
bv, cv, rv = mb.group(1) if mb else None, mc.group(1) if mc else None, mr.group(1) if mr else None
print(f"builder ToolVersionValue = {bv}")
print(f"csproj  Version          = {cv}")
print(f"README  toolVersion      = {rv}")
if bv and bv == cv == rv:
    print(f"PASS: D3 3-way toolVersion agreement ({bv})")
    sys.exit(0)
print("MISMATCH: toolVersion 3-way disagreement")
sys.exit(1)
PY
)"
D3RC=$?
set -e
echo "$D3OUT"
if [[ "$D3RC" -eq 0 ]]; then
  pass "D3 toolVersion 3-way agreement"
else
  fail_msg "D3 toolVersion drift (see values above)"
fi

echo "-- D4: committed *.scriban fixtures parse under the 4-tag grammar --"
set +e
D4OUT="$(python3 - "$TPLDIR" <<'PY'
import glob, os, re, sys
tpldir = sys.argv[1]
VAR = re.compile(r'^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$')
EACH_PATHS = {'licenses', 'groups', 'items'}
files = sorted(glob.glob(os.path.join(tpldir, '*.scriban')))
print(f"fixtures: {', '.join(os.path.basename(f) for f in files)}")
bad = []
if len(files) != 2:
    print(f"MISMATCH: want exactly 2 committed fixtures, found {len(files)}")
    sys.exit(1)
for path in files:
    name = os.path.basename(path)
    src = open(path).read()
    errs = []
    tags = []
    pos = 0
    while True:
        o = src.find('{{', pos)
        if o < 0:
            break
        c = src.find('}}', o + 2)
        if c < 0:
            errs.append(f"line {src.count(chr(10), 0, o) + 1}: unclosed '{{{{'")
            break
        tags.append((src[o + 2:c], src.count('\n', 0, o) + 1))
        pos = c + 2
    stack = []
    for raw, line in tags:
        tag = raw.strip()
        if tag.startswith('#each '):
            p = tag[6:].strip()
            if '|' in raw or p not in EACH_PATHS:
                errs.append(f"line {line}: bad #each path '{p}' (want one of {sorted(EACH_PATHS)}, no filters)")
            else:
                stack.append(('each', line))
        elif tag.startswith('#if '):
            p = tag[4:].strip()
            if '|' in raw or not VAR.fullmatch(p):
                errs.append(f"line {line}: bad #if path '{p}' (dotted ident, no filters)")
            else:
                stack.append(('if', line))
        elif tag in ('/each', '/if'):
            kind = tag[1:]
            if not stack or stack[-1][0] != kind:
                errs.append(f"line {line}: mismatched '{tag}'")
            else:
                stack.pop()
        elif tag == '':
            errs.append(f"line {line}: empty tag")
        elif tag == 'else' or tag.startswith('else ') or tag.startswith('#') or tag.startswith('/'):
            errs.append(f"line {line}: non-4-tag block '{{{tag}}}' (no {{{{else}}}}, no unknown blocks)")
        else:
            if '|' in raw:
                errs.append(f"line {line}: filter pipe in '{{{tag}}}' (no filters v1)")
            elif not VAR.fullmatch(tag):
                errs.append(f"line {line}: bad var path '{{{tag}}}'")
    for kind, line in stack:
        errs.append(f"line {line}: unclosed '{{#{kind}}}' block")
    if errs:
        for e in errs:
            print(f"MISMATCH [{name}]: {e}")
        bad.append(name)
    else:
        print(f"PASS: D4 [{name}] parses under 4-tag grammar ({len(tags)} tags)")
sys.exit(1 if bad else 0)
PY
)"
D4RC=$?
set -e
echo "$D4OUT"
if [[ "$D4RC" -eq 0 ]]; then
  pass "D4 both fixtures parse under 4-tag grammar"
else
  fail_msg "D4 fixture grammar violation (see MISMATCH lines above)"
fi

echo "-- D5: attribution example synthetic-labels pinned --"
set +e
D5OUT="$(python3 - "$README" <<'PY'
import re, sys
readme_path = sys.argv[1]
readme = open(readme_path, encoding='utf-8').read()
lines = readme.splitlines()
bad = []

# Collect fenced blocks with line numbers.
fences = []  # (start_idx, end_idx, content)
i = 0
while i < len(lines):
    if lines[i].strip().startswith('```'):
        s = i
        j = i + 1
        while j < len(lines) and not lines[j].strip().startswith('```'):
            j += 1
        if j >= len(lines):
            break
        fences.append((s, j, '\n'.join(lines[s + 1:j])))
        i = j + 1
    else:
        i += 1

PKG = re.compile(r'[A-Za-z0-9_.\-]+@\d+\.\d+')
MARK = re.compile(r'synthetic|illustrative|shape-only', re.I)
ELL = '\u2026'
ABBR = re.compile(r'abbreviat', re.I)

# D5a+D5b: only attribution example blocks (contain a rendered name@version row).
attr = [(s, e, c) for (s, e, c) in fences if PKG.search(c)]
if not attr:
    print("MISMATCH: no attribution example block with name@version found")
    bad.append('no-example')
else:
    print(f"attribution example blocks: {len(attr)}")
for (s, e, c) in attr:
    pre = '\n'.join(lines[max(0, s - 10):s])
    # D5a-1: intro marker nearby (10 lines before fence).
    if MARK.search(pre):
        print(f"PASS: D5a intro marker nearby fence L{s + 1}")
    else:
        print(f"MISMATCH: attribution example fence L{s + 1} with real-looking package rows lacks nearby synthetic|illustrative|shape-only marker (10 lines before)")
        bad.append(f'intro-{s + 1}')
    # D5a-2: every rendered package bullet row carries its own marker.
    for n, ln in enumerate(c.splitlines(), 1):
        if PKG.search(ln):
            if MARK.search(ln):
                print(f"PASS: D5a bullet marker L{s + 1 + n}: {ln.strip()[:80]}")
            else:
                print(f"MISMATCH: attribution bullet without synthetic|illustrative marker (fence L{s + 1}, row: {ln.strip()[:100]})")
                bad.append(f'bullet-{s + 1}-{n}')
    # D5b: `…` abbreviation requires an abbreviates-note nearby (same fence or intro).
    if ELL in c:
        if ABBR.search(c) or ABBR.search(pre):
            print(f"PASS: D5b `…` abbreviates-note present (fence L{s + 1})")
        else:
            print(f"MISMATCH: `…` abbreviation without abbreviates-note (fence L{s + 1})")
            bad.append(f'ellipsis-{s + 1}')
    else:
        print(f"PASS: D5b no `…` in fence L{s + 1} (nothing to label)")

# D5c: every TemplateModel.Groups + LicenseGrouper sort window needs Unknown-last contrast.
has_both = 'TemplateModel.Groups' in readme and 'LicenseGrouper' in readme
if not has_both:
    print("PASS: D5c no Groups-vs-Grouper sort mention (nothing to contrast)")
else:
    windows = []
    for k in range(len(lines)):
        w = '\n'.join(lines[k:k + 10])
        if 'TemplateModel.Groups' in w and 'LicenseGrouper' in w:
            windows.append(k)
    if not windows:
        print("MISMATCH: TemplateModel.Groups + LicenseGrouper mentioned but never together in a 10-line window (contrast note placement drift)")
        bad.append('contrast-placement')
    else:
        for k in windows:
            w = '\n'.join(lines[k:k + 10])
            if 'Unknown' in w and re.search(r'last', w, re.I):
                print(f"PASS: D5c Unknown-last contrast present (window L{k + 1}-L{k + 10})")
            else:
                print(f"MISMATCH: Groups-vs-Grouper sort mention without Unknown-last contrast (window L{k + 1}-L{k + 10})")
                bad.append(f'contrast-{k + 1}')

sys.exit(1 if bad else 0)
PY
)"
D5RC=$?
set -e
echo "$D5OUT"
if [[ "$D5RC" -eq 0 ]]; then
  pass "D5 attribution synthetic-labels pinned"
else
  fail_msg "D5 synthetic-label drift (see MISMATCH lines above)"
fi

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "TEMPLATE-DOCS-PROBE OK: D1-D5 all pass."
  exit 0
else
  echo "TEMPLATE-DOCS-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
