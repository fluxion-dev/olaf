#!/usr/bin/env bash
# copyright-probe.sh — Copyright-holder probe for issue #72 (promoted v0.1.1, 2 uses ≥ bar: prototype + smoke PASS).
# v0.1.1: B1/B2/B3 source-grep asserts accept EITHER the inline
#   `CopyrightHolders is { Length: > 0 }` / `string.Join("; ", holders)` form OR the
#   `CopyrightHoldersFormat.Join/HasHolders` helper form (Wave 5b refactor repointed
#   Txt/Md/Html + Spdx/Cdx/CdxXml call sites to the helper; behavior identical).
# Model: license-text-probe.sh sections + shared offline-safe style; stubbed/offline-safe.
# C1 regex vectors: year-anchored HolderPattern (Copyright (c)/©/(c)/
#   Copyright © + year/range/list + holder) + RightsReserved tail trim +
#   IsDenied template negatives (FSF/Apache/AAL/Gnomovision/Yoyodyne/bare
#   contributors/placeholders) + no-copyright null + air-gap (pure sync,
#   zero HttpClient); behavioral proof = CopyrightScraperTests.
# C2 metadata fallback: ResolveHolders scraped-text-first + FromAuthorFallback
#   (Name <mail> -> Name, bare mail -> null) + AttachHolders (supplier never
#   rewritten) + resolver wiring via provenance overload (isPerPackage gate —
#   DB-subset texts never scraped); behavioral proof = CopyrightIntegrationTests.
# C3 guards: MaxHolders=5 + MaxHolderLength=200 hard-cut (no ellipsis) +
#   case-folded dedup + CleanHolders trim/dedup/null.
# B1 attribution: holders-only Copyright line in txt/md/html, omit-when-empty.
# B2 SBOM: SPDX copyrightText (joined holders else literal NOASSERTION) +
#   CDX JSON/XML evidence.copyright array-of-text, omit-when-empty.
# B3 holder-shape: S1 json/yaml/xml omit-shape (grep-empty) + S2 spdx-mandatory
#   vs cdx-omit + S3 single-home caps (5/200 scraper-only, zero formatter
#   re-truncation) + S4 positional arity (Enrichment/CdxComponent trailing
#   holder slot + pass-through) + S5 GPL-template null (FSF/Apache/AAL deny).
# R1 matrix regression: canonical r1_matrix_regression (json/yaml/xml/html).
# Rules: repo-relative, idempotent (mktemp cleaned), no secrets, exit 0/1/2.
VERSION="0.1.1"
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

# ---- Canonical temp-dir with KEEP_TEMP ----
WORKDIR=""
KEEP_TEMP=0
TIMEOUT_SECS=300
PROJECT_REL="src/Olaf.Cli"
TESTPROJ_REL="tests/Olaf.Tests/Olaf.Tests.csproj"
FIXTURE_REL="tests/Olaf.Tests/Fixtures/npm"
SCRAPER_REL="src/Olaf.Resolvers/CopyrightScraper.cs"
FETCHER_REL="src/Olaf.Resolvers/LicenseTextFetcher.cs"
HELPERS_REL="src/Olaf.Resolvers/EnrichmentHelpers.cs"

usage() {
  cat <<EOF
Usage: $(basename "$0") [--timeout <secs>] [--workdir <dir>] [--keep-temp] [--help] [--version]

Copyright-holder probe (issue #72): regex vectors (C1), metadata fallback (C2),
guards (C3), attribution render (B1), SBOM fields (B2), holder shape (B3),
matrix regression (R1).
Stubbed/offline-safe: inline-string unit tests only, no live network.

Options:
  --timeout <n>   per-command timeout in seconds (default: 300)
  --workdir <dir> work dir (default: mktemp -d under \${TMPDIR:-/tmp})
  --keep-temp     keep temp work dir for debugging (default: remove)
  --help          show this help and exit 0
  --version       print VERSION and exit 0

Exit codes: 0 all PASS, 1 assertion failure, 2 usage/environment error.

Examples:
  $(basename "$0")
  $(basename "$0") --timeout 120 --keep-temp
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "$(basename "$0") $VERSION"; exit 0 ;;
    --timeout) TIMEOUT_SECS="${2:-}"; shift 2 ;;
    --timeout=*) TIMEOUT_SECS="${1#*=}"; shift ;;
    --workdir) WORKDIR="${2:-}"; shift 2 ;;
    --workdir=*) WORKDIR="${1#*=}"; shift ;;
    --keep-temp) KEEP_TEMP=1; shift ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if ! [[ "$TIMEOUT_SECS" =~ ^[0-9]+$ ]]; then
  echo "Invalid --timeout '$TIMEOUT_SECS': must be a positive integer." >&2
  exit 2
fi

PROJECT="$ROOT/$PROJECT_REL"
TESTPROJ="$ROOT/$TESTPROJ_REL"
FIXTURE="$ROOT/$FIXTURE_REL"
SCRAPER="$ROOT/$SCRAPER_REL"
FETCHER="$ROOT/$FETCHER_REL"
HELPERS="$ROOT/$HELPERS_REL"
[[ -d "$PROJECT" ]] || { echo "CLI project not found: $PROJECT" >&2; exit 2; }
[[ -f "$TESTPROJ" ]] || { echo "Test project not found: $TESTPROJ" >&2; exit 2; }
[[ -d "$FIXTURE" ]] || { echo "Fixture not found: $FIXTURE" >&2; exit 2; }
[[ -f "$SCRAPER" ]] || { echo "Scraper not found: $SCRAPER" >&2; exit 2; }
[[ -f "$FETCHER" ]] || { echo "Fetcher not found: $FETCHER" >&2; exit 2; }
[[ -f "$HELPERS" ]] || { echo "Helpers not found: $HELPERS" >&2; exit 2; }
[[ -f "$ROOT/olaf.slnx" ]] || { echo "Repo root has no olaf.slnx: $ROOT" >&2; exit 2; }
for cmd in dotnet timeout; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/copyright-probe-XXXXXX")"
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

# ---- Canonical dep extractors (from _template.sh 0.2.2; behavior identical) ----
dep_count() {
  python3 - "$1" <<'PY' 2>/dev/null
import json, sys
try:
    with open(sys.argv[1]) as f:
        data = json.load(f)
except Exception:
    print("?")
    sys.exit(0)
items = data.get("licenses", data.get("dependencies", data.get("resolved", [])))
print(len(items) if isinstance(items, list) else "?")
PY
}
dep_field() {
  python3 - "$1" "$2" "$3" <<'PY' 2>/dev/null
import json, sys
path, want, field = sys.argv[1], sys.argv[2].lower(), sys.argv[3]
try:
    with open(path) as f:
        data = json.load(f)
except Exception:
    sys.exit(0)
items = data.get("licenses", data.get("dependencies", data.get("resolved", [])))
if isinstance(items, list):
    for it in items:
        if not isinstance(it, dict):
            continue
        dep = it.get("dependency", it)
        name = str(dep.get("name", it.get("name", "")))
        if name.lower() == want:
            v = dep.get(field, it.get(field, ""))
            if v is True:
                print("true")
            elif v is False:
                print("false")
            elif isinstance(v, str):
                print(v.lower() if field == "direct" else v)
            else:
                print(str(v) if v != "" else "")
            break
PY
}
dep_ver() { dep_field "$1" "$2" "version"; }
dep_direct() { dep_field "$1" "$2" "direct"; }

# ---- Canonical air-gap grep (from _template.sh 0.2.4; behavior identical) ----
air_gap_grep() {
  local file="$1"; shift
  [[ -f "$file" ]] || return 2
  local vocab='HttpClient|GetAsync|System\.Net|HttpRequest|GetByteArray|SendAsync'
  local p
  for p in "$@"; do vocab="$vocab|$p"; done
  if grep -v '^[[:space:]]*//' "$file" | grep -qE -- "$vocab"; then
    return 1
  fi
  return 0
}

RC=0
run_scan() {
  # run_scan <tag> <inputdir> <format> [extra args...]; stdout-><tag>.stdout
  local tag="$1" input="$2" fmt="$3"; shift 3
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-launch-profile -- --input "$input" --format "$fmt" "$@" >"$WORKDIR/$tag.stdout" 2>"$WORKDIR/$tag.stderr"
  RC=$?
  set -e
  if [[ "$RC" -eq 124 ]]; then
    fail_msg "$tag timed out after ${TIMEOUT_SECS}s"
  fi
}
run_t() {
  # run_t <tag> <filter>; dotnet test -> <tag>.stdout (Passed!/Failed! summary)
  local tag="$1" filter="$2"
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet test "$TESTPROJ" --filter "$filter" >"$WORKDIR/$tag.stdout" 2>"$WORKDIR/$tag.stderr"
  RC=$?
  set -e
  if [[ "$RC" -eq 124 ]]; then
    fail_msg "$tag timed out after ${TIMEOUT_SECS}s"
  fi
}
t_ok() {
  # t_ok <tag> <label>: exit 0 + Passed! summary (no Failed!).
  local tag="$1" label="$2" sum
  sum="$(grep -h "Passed!\|Failed!" "$WORKDIR/$tag.stdout" 2>/dev/null | tail -n 1)"
  if [[ "$RC" -eq 0 ]] && grep -q "Passed!" "$WORKDIR/$tag.stdout" 2>/dev/null \
      && ! grep -q "Failed!" "$WORKDIR/$tag.stdout" 2>/dev/null; then
    pass "$label: ${sum:-exit 0 + Passed!}"
  else
    fail_msg "$label: RC=$RC summary=[${sum:-none}] (see $tag.stdout)"
  fi
}

echo "== copyright-probe v$VERSION =="
echo "repo: $ROOT"
echo "work: $WORKDIR"

echo "-- step 0: build CLI --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
pass "build: dotnet build OK"

# ---- C1: regex vectors (year-anchored positives + template negatives) ----
echo "-- C1 regex vectors --"
grep -q 'HolderPattern' "$SCRAPER" && grep -q '(?<years>' "$SCRAPER" \
  && grep -q '(?<holder>' "$SCRAPER" && grep -q '\\d{4}' "$SCRAPER" \
  && pass "C1 src: HolderPattern year-anchored (marker + years + holder + 4-digit year)" \
  || fail_msg "C1 src: HolderPattern year anchor missing"
grep -q 'RegexOptions.IgnoreCase' "$SCRAPER" \
  && pass "C1 src: case-insensitive (Copyright/COPYRIGHT/(C) all anchor)" \
  || fail_msg "C1 src: IgnoreCase missing"
grep -q 'RightsReservedTail' "$SCRAPER" && grep -qi 'all rights reserved' "$SCRAPER" \
  && pass "C1 src: 'All rights reserved' tail trimmed" \
  || fail_msg "C1 src: RightsReserved tail missing"
for pat in 'free software foundation' 'apache software foundation' 'aal-authors' 'attribution assurance' 'gnomovision' 'yoyodyne'; do
  if grep -qi "$pat" "$SCRAPER"; then pass "C1 src: template deny '$pat'"; else fail_msg "C1 src: template deny '$pat' missing"; fi
done
if grep -q 'Equals("contributors"' "$SCRAPER" && grep -q 'the contributors' "$SCRAPER"; then
  pass "C1 src: bare-contributors deny"
else
  fail_msg "C1 src: bare-contributors deny missing"
fi
if grep -q "Contains('<')" "$SCRAPER" && grep -qi 'xxxx' "$SCRAPER"; then
  pass "C1 src: placeholder deny (brackets + XXXX)"
else
  fail_msg "C1 src: placeholder deny missing"
fi
grep -q 'return holders.Count == 0 ? null' "$SCRAPER" \
  && pass "C1 src: no-copyright/empty yields null (omit-null downstream)" \
  || fail_msg "C1 src: null-on-empty missing"
if air_gap_grep "$SCRAPER"; then
  pass "C1 src: air-gap safe (pure sync scraper, zero HTTP)"
else
  fail_msg "C1 src: HTTP surface in scraper (air-gap broken)"
fi
run_t "c1-scraper" "FullyQualifiedName~CopyrightScraperTests"
t_ok "c1-scraper" "C1 behavior: CopyrightScraperTests ((c)/©/bare/multi/negatives, offline)"

# ---- C2: metadata fallback (scraped-first + author-only-when-zero) ----
echo "-- C2 metadata fallback --"
grep -q 'ResolveHolders' "$SCRAPER" && grep -q 'Extract(perPackageText)' "$SCRAPER" \
  && grep -q 'FromAuthorFallback' "$SCRAPER" \
  && pass "C2 src: scraped-text-first, author fallback only when zero" \
  || fail_msg "C2 src: ResolveHolders precedence wrong"
if grep -q "IndexOf('<')" "$SCRAPER" && grep -q "Contains('@')" "$SCRAPER"; then
  pass "C2 src: Name <mail> -> Name; bare mail -> null (never promoted)"
else
  fail_msg "C2 src: email fallback rule missing"
fi
grep -q 'enrichment with { CopyrightHolders = holders }' "$SCRAPER" \
  && pass "C2 src: AttachHolders extends enrichment (record-with)" \
  || fail_msg "C2 src: AttachHolders with-expression missing"
if sed -n '/AttachHolders/,/^    }/p' "$SCRAPER" | grep -q 'Supplier'; then
  fail_msg "C2 src: AttachHolders rewrites Supplier (must stay publisher)"
else
  pass "C2 src: Supplier never rewritten (may differ from Holders)"
fi
for r in NpmLicenseResolver PyPILicenseResolver NuGetLicenseResolver CargoLicenseResolver; do
  if grep -q 'AttachHolders' "$ROOT/src/Olaf.Resolvers/$r.cs" \
      && grep -q 'isPerPackage ? text : null' "$ROOT/src/Olaf.Resolvers/$r.cs"; then
    pass "C2 wire: $r per-package-only attach"
  else
    fail_msg "C2 wire: $r attach/provenance gate missing"
  fi
done
grep -q 'IsPerPackage' "$FETCHER" && grep -q 'return (dbText, null, false)' "$FETCHER" \
  && pass "C2 src: provenance overload — DB-subset texts never scraped" \
  || fail_msg "C2 src: provenance/DB-false gate missing"
run_t "c2-integ" "FullyQualifiedName~CopyrightIntegrationTests"
t_ok "c2-integ" "C2 behavior: CopyrightIntegrationTests (stub-only, no network)"

# ---- C3: guards (max 5, deduped, length-capped) ----
echo "-- C3 guards --"
grep -q 'MaxHolders = 5' "$SCRAPER" \
  && pass "C3 src: MaxHolders = 5" \
  || fail_msg "C3 src: MaxHolders cap missing"
grep -q 'MaxHolderLength = 200' "$SCRAPER" \
  && pass "C3 src: MaxHolderLength = 200" \
  || fail_msg "C3 src: MaxHolderLength cap missing"
grep -q 'holders.Count >= MaxHolders' "$SCRAPER" \
  && pass "C3 src: per-package loop stops at cap" \
  || fail_msg "C3 src: cap enforcement missing"
grep -q 'StringComparer.OrdinalIgnoreCase' "$SCRAPER" && grep -q 'seen.Add(holder)' "$SCRAPER" \
  && pass "C3 src: case-folded dedup" \
  || fail_msg "C3 src: dedup missing"
grep -q 'Substring(0, MaxHolderLength)' "$SCRAPER" \
  && pass "C3 src: hard-cut at 200 chars" \
  || fail_msg "C3 src: hard-cut missing"
if grep -q '\.\.\."' "$SCRAPER" || grep -q '…' "$SCRAPER"; then
  fail_msg "C3 src: ellipsis truncation present (must be byte-stable hard-cut)"
else
  pass "C3 src: no ellipsis (byte-stable)"
fi
grep -q 'CleanHolders' "$HELPERS" && grep -q 'seen.Add(trimmed)' "$HELPERS" \
  && pass "C3 src: EnrichmentHelpers.CleanHolders trim/dedup/null" \
  || fail_msg "C3 src: CleanHolders missing"

# ---- B1: attribution render (txt/md/html holders-only, omit-when-empty) ----
echo "-- B1 attribution render --"
grep -q '  Copyright: ' "$ROOT/src/Olaf.Formatters/TxtFormatter.cs" \
  && pass "B1 src: txt '  Copyright: ' line" \
  || fail_msg "B1 src: txt Copyright line missing"
grep -q '\- Copyright: ' "$ROOT/src/Olaf.Formatters/MarkdownFormatter.cs" \
  && pass "B1 src: md '- Copyright: ' bullet" \
  || fail_msg "B1 src: md Copyright bullet missing"
grep -q '<p>Copyright: ' "$ROOT/src/Olaf.Formatters/HtmlFormatter.cs" \
  && grep -q 'HtmlEncode' "$ROOT/src/Olaf.Formatters/HtmlFormatter.cs" \
  && pass "B1 src: html '<p>Copyright: ' (HtmlEncoded)" \
  || fail_msg "B1 src: html Copyright paragraph missing"
for f in TxtFormatter MarkdownFormatter HtmlFormatter; do
  if grep -q 'CopyrightHolders is { Length: > 0 }' "$ROOT/src/Olaf.Formatters/$f.cs" \
      || grep -q 'CopyrightHoldersFormat.Join' "$ROOT/src/Olaf.Formatters/$f.cs" \
      || grep -q 'CopyrightHoldersFormat.HasHolders' "$ROOT/src/Olaf.Formatters/$f.cs"; then
    pass "B1 src: $f omit-when-empty"
  else
    fail_msg "B1 src: $f omit guard missing"
  fi
done
run_t "b1-attr" "FullyQualifiedName~CopyrightAttributionTests"
t_ok "b1-attr" "B1 behavior: CopyrightAttributionTests (holders-only, no enrichment leak)"

# ---- B2: SBOM (copyrightText else NOASSERTION / evidence.copyright) ----
echo "-- B2 SBOM --"
grep -q 'copyrightText' "$ROOT/src/Olaf.Formatters/SpdxJsonFormatter.cs" \
  && grep -q 'NOASSERTION' "$ROOT/src/Olaf.Formatters/SpdxJsonFormatter.cs" \
  && { grep -q 'string.Join("; ", holders)' "$ROOT/src/Olaf.Formatters/SpdxJsonFormatter.cs" \
      || grep -q 'CopyrightHoldersFormat.Join' "$ROOT/src/Olaf.Formatters/SpdxJsonFormatter.cs"; } \
  && pass "B2 src: spdx copyrightText = joined holders else NOASSERTION" \
  || fail_msg "B2 src: spdx copyrightText rule missing"
grep -q '"evidence"' "$ROOT/src/Olaf.Formatters/CycloneDxFormatter.cs" \
  && grep -q '"copyright"' "$ROOT/src/Olaf.Formatters/CycloneDxFormatter.cs" \
  && grep -q '"text"' "$ROOT/src/Olaf.Formatters/CycloneDxFormatter.cs" \
  && pass "B2 src: cdx-json evidence.copyright[] array-of-{text}" \
  || fail_msg "B2 src: cdx-json evidence missing"
grep -q '"evidence"' "$ROOT/src/Olaf.Formatters/CycloneDxXmlFormatter.cs" \
  && grep -q '"copyright"' "$ROOT/src/Olaf.Formatters/CycloneDxXmlFormatter.cs" \
  && grep -q '"text"' "$ROOT/src/Olaf.Formatters/CycloneDxXmlFormatter.cs" \
  && grep -q 'Sanitize(h)' "$ROOT/src/Olaf.Formatters/CycloneDxXmlFormatter.cs" \
  && pass "B2 src: cdx-xml evidence/copyright/text (Sanitized)" \
  || fail_msg "B2 src: cdx-xml evidence missing"
for f in SpdxJsonFormatter CycloneDxFormatter CycloneDxXmlFormatter; do
  if grep -q 'CopyrightHolders is { Length: > 0 }' "$ROOT/src/Olaf.Formatters/$f.cs" \
      || grep -q 'CopyrightHoldersFormat.Join' "$ROOT/src/Olaf.Formatters/$f.cs" \
      || grep -q 'CopyrightHoldersFormat.HasHolders' "$ROOT/src/Olaf.Formatters/$f.cs"; then
    pass "B2 src: $f omit/NOASSERTION-when-empty"
  else
    fail_msg "B2 src: $f empty guard missing"
  fi
done
run_scan "b2-json" "$FIXTURE" "json"
if [[ "$RC" -eq 0 ]]; then
  N="$(dep_count "$WORKDIR/b2-json.stdout")"
  echo "fixture dep count: $N"
  pass "B2 scan: fixture json renders (count=$N)"
else
  fail_msg "B2 scan: fixture json exited $RC"
fi
run_t "b2-sbom" "FullyQualifiedName~CopyrightSbomTests"
t_ok "b2-sbom" "B2 behavior: CopyrightSbomTests (offline, no network)"

# ---- B3: holder shape (omit-shape / mandatory-vs-omit / caps home / arity / GPL-template null) ----
echo "-- B3 holder shape --"
# S1 omit-shape: generic machine formats carry no holder shape at all.
if grep -qi 'copyright\|holder' "$ROOT/src/Olaf.Formatters/JsonFormatter.cs" \
    || grep -qi 'copyright\|holder' "$ROOT/src/Olaf.Formatters/YamlFormatter.cs" \
    || grep -qi 'copyright\|holder' "$ROOT/src/Olaf.Formatters/XmlFormatter.cs"; then
  fail_msg "B3/S1: holder shape leaked into json/yaml/xml (must omit entirely)"
else
  pass "B3/S1: json/yaml/xml carry no holder shape (omit entirely)"
fi
# S2 mandatory-vs-omit: spdx copyrightText always present, cdx evidence guarded.
grep -q '\["copyrightText"\]' "$ROOT/src/Olaf.Formatters/SpdxJsonFormatter.cs" \
  && grep -q 'NOASSERTION' "$ROOT/src/Olaf.Formatters/SpdxJsonFormatter.cs" \
  && pass "B3/S2: spdx copyrightText mandatory (joined holders else NOASSERTION)" \
  || fail_msg "B3/S2: spdx copyrightText mandatory rule missing"
for f in CycloneDxFormatter CycloneDxXmlFormatter; do
  if grep -q 'CopyrightHolders is { Length: > 0 }' "$ROOT/src/Olaf.Formatters/$f.cs" \
      || grep -q 'CopyrightHoldersFormat.Join' "$ROOT/src/Olaf.Formatters/$f.cs" \
      || grep -q 'CopyrightHoldersFormat.HasHolders' "$ROOT/src/Olaf.Formatters/$f.cs"; then
    pass "B3/S2: $f omit-when-empty (evidence only when holders)"
  else
    fail_msg "B3/S2: $f omit guard missing"
  fi
done
# S3 single-home caps: 5/200 defined once (scraper), formatters never re-truncate.
if [[ "$(grep -rl 'MaxHolders = 5\|MaxHolderLength = 200' "$ROOT/src" --include='*.cs' | grep -v '/obj/\|/bin/' | wc -l)" -eq 1 ]] \
    && grep -q 'MaxHolders = 5' "$SCRAPER" && grep -q 'MaxHolderLength = 200' "$SCRAPER"; then
  pass "B3/S3: caps single-homed in CopyrightScraper (5 / 200)"
else
  fail_msg "B3/S3: caps duplicated or missing single home"
fi
if grep -rn 'Substring(0, MaxHolderLength)' "$ROOT/src/Olaf.Formatters" --include='*.cs' 2>/dev/null | grep -q .; then
  fail_msg "B3/S3: formatter re-truncates holders (caps live in scraper only)"
else
  pass "B3/S3: formatters do zero holder re-truncation"
fi
# S4 positional arity: trailing holder slot + mapper pass-through.
grep -q 'string\[\]? CopyrightHolders = null)' "$ROOT/src/Olaf.Core/Enrichment.cs" \
  && pass "B3/S4: Enrichment trailing slot CopyrightHolders = null" \
  || fail_msg "B3/S4: Enrichment trailing holder slot missing"
grep -q 'string\[\]? CopyrightHolders = null)' "$ROOT/src/Olaf.Formatters/CycloneDxComponentMapper.cs" \
  && grep -q 'license.Enrichment?.CopyrightHolders' "$ROOT/src/Olaf.Formatters/CycloneDxComponentMapper.cs" \
  && pass "B3/S4: CycloneDxComponent trailing slot + pass-through" \
  || fail_msg "B3/S4: mapper trailing slot/pass-through missing"
# S5 GPL-template null: FSF/Apache/AAL template texts yield null holders.
grep -q 'Should_DenyTemplateHolders_When_FsfText' "$ROOT/tests/Olaf.Tests/Resolvers/CopyrightScraperTests.cs" \
  && pass "B3/S5: GPL-template (FSF 1989/1991) deny test pinned" \
  || fail_msg "B3/S5: GPL-template deny test missing"
run_t "b3-gpl" "FullyQualifiedName~CopyrightScraperTests.Should_DenyTemplateHolders"
t_ok "b3-gpl" "B3/S5 behavior: GPL/Apache/AAL template texts yield null"

# ---- Canonical R1 format-matrix regression (from _template.sh 0.2.3) ----
r1_matrix_regression() {
  # r1_matrix_regression [formats...]; default: json yaml xml html.
  local formats=("$@")
  (( ${#formats[@]} )) || formats=(json yaml xml html)
  local R1FAIL=0 f pat
  echo "-- R1 format-matrix regression --"
  for f in "${formats[@]}"; do
    run_scan "r1-$f" "$FIXTURE" "$f"
    if [[ "$RC" -ne 0 ]]; then
      fail_msg "R1/$f scan exited $RC"; R1FAIL=1; continue
    fi
    case "$f" in
      json) pat='"direct"' ;;
      yaml) pat='direct:' ;;
      xml) pat='<direct>' ;;
      html) pat='<th>Direct</th>' ;;
      *) fail_msg "R1/$f: no marker for format"; R1FAIL=1; continue ;;
    esac
    if grep -qF -- "$pat" "$WORKDIR/r1-$f.stdout"; then
      pass "R1/$f carries $pat"
    else
      fail_msg "R1/$f missing $pat"; R1FAIL=1
    fi
  done
  if (( ! R1FAIL )); then pass "R1 MATRIX OK: ${formats[*]} untouched"; fi
}

# ---- R1 format-matrix regression: json/yaml/xml/html direct markers untouched ----
r1_matrix_regression

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "COPYRIGHT-PROBE OK: C1-C3 + B1-B3 + R1 all pass."
  exit 0
else
  echo "COPYRIGHT-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
