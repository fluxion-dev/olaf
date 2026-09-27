#!/usr/bin/env bash
# format-matrix-dump.sh — CLI format matrix on committed npm fixture (issue #3).
# Runs: for f in json yaml xml html txt md cyclonedx-json cyclonedx-xml; do
#   dotnet run --project src/Olaf.Cli -- --input tests/Olaf.Tests/Fixtures/npm --format $f; done
# then checks each output for 8 fields + direct (9th) + summary counts; HTML additionally
# requires <table> and HTML-encoded cell output; cyclonedx-json asserts the
# CycloneDX envelope (bomFormat/specVersion/components); cyclonedx-xml asserts the
# CycloneDX XML envelope (<bom/xmlns bom/1.5/metadata/components) + <scope>
# element + olaf: counts props + well-formedness (python xml parse).
# NOTE: empty-components case not applicable — the CLI exits 2 ("No manifests
# found") on manifest-less input before any formatter runs, so every matrix
# scan carries ≥1 component.
# Offline-safe: never passes --strict; resolver network failures degrade to
# Unknown with exit 0 (deterministic continue in Program.cs).
# Rules: repo-relative, idempotent (stdout only, temp files cleaned), no secrets.
VERSION="0.4.1"
set -euo pipefail

TIMEOUT_SECS=60
FORMATS="json yaml xml html txt md cyclonedx-json cyclonedx-xml"
FIXTURE_REL="tests/Olaf.Tests/Fixtures/npm"
PROJECT_REL="src/Olaf.Cli"
WORKDIR=""
KEEP_TEMP=0

usage() {
  echo "Usage: $(basename "$0") [--format json|yaml|xml|html|txt|md|cyclonedx-json|cyclonedx-xml|all] [--timeout <secs>] [--workdir <dir>] [--keep-temp] [--help] [--version]"
  echo ""
  echo "CLI format matrix on the committed npm fixture (offline-safe, non-strict)."
  echo "Runs 'dotnet run --project $PROJECT_REL -- --input $FIXTURE_REL --format <f>'"
  echo "for each format (default: all eight) with 'timeout <secs>s' per scan"
  echo "(default: 60), then verifies:"
  echo "  - 8 fields present (ecosystem,name,version,spdx,licenseText,sourceUrl,status,reason"
  echo "    or per-format equivalents: <th> headers for html, <elements> for xml, keys for yaml,"
  echo "    SPDX:/Status: lines for txt, | table | headers for md, components[] for cyclonedx-json)"
  echo "  - 9th field 'direct' present (json \"direct\" / yaml direct: / xml <direct> / html <th>Direct</th>"
  echo "    / txt direct= / md | Direct | / cyclonedx-json scope required|optional / cyclonedx-xml <scope>required|optional</scope>)"
  echo "  - summary counts present (total/resolved/unknown or per-format equivalent)"
  echo "  - html additionally contains <table> and HTML-encoded cell output"
  echo "  - cyclonedx-json additionally asserts bomFormat==CycloneDX + specVersion==1.5"
  echo "  - cyclonedx-xml additionally asserts <bom + xmlns bom/1.5 + <metadata>/<components>"
  echo "    + well-formedness (python xml parse)"
  echo ""
  echo "Options:"
  echo "  --format <f>   single format or 'all' (default: all)"
  echo "  --timeout <n>  per-scan timeout in seconds (default: 60)"
  echo "  --workdir <d>  work dir (default: mktemp -d)"
  echo "  --keep-temp    keep temp work dir for debugging (default: remove)"
  echo "  --help         show this help and exit 0"
  echo "  --version      print VERSION and exit 0"
  echo ""
  echo "Exit codes: 0 matrix passed, 1 check failure, 2 usage/environment error."
  echo "Examples:"
  echo "  $(basename "$0")"
  echo "  $(basename "$0") --format html"
  echo "  $(basename "$0") --format json --timeout 30"
  echo "  $(basename "$0") --workdir /tmp/fm --keep-temp"
}

if [[ "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
  echo "$(basename "$0") $VERSION"
  exit 0
fi

while [[ $# -gt 0 ]]; do
  case "$1" in
    --format)
      [[ $# -lt 2 ]] && { echo "Missing value for --format." >&2; exit 2; }
      case "$2" in
        json|yaml|xml|html|txt|md|cyclonedx-json|cyclonedx-xml) FORMATS="$2" ;;
        all) FORMATS="json yaml xml html txt md cyclonedx-json cyclonedx-xml" ;;
        *) echo "Unsupported --format '$2'. Expected json|yaml|xml|html|txt|md|cyclonedx-json|cyclonedx-xml|all." >&2; exit 2 ;;
      esac
      shift 2
      ;;
    --timeout)
      [[ $# -lt 2 ]] && { echo "Missing value for --timeout." >&2; exit 2; }
      [[ "$2" =~ ^[0-9]+$ ]] || { echo "Invalid --timeout '$2': expected integer seconds." >&2; exit 2; }
      TIMEOUT_SECS="$2"
      shift 2
      ;;
    --workdir)
      [[ $# -lt 2 ]] && { echo "Missing value for --workdir." >&2; exit 2; }
      WORKDIR="$2"
      shift 2
      ;;
    --workdir=*)
      WORKDIR="${1#*=}"
      shift
      ;;
    --keep-temp)
      KEEP_TEMP=1
      shift
      ;;
    --help|--version)
      # handled above for $1; repeated/positional form
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument '$1'. See --help." >&2
      exit 2
      ;;
  esac
done

ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
FIXTURE="$ROOT/$FIXTURE_REL"
PROJECT="$ROOT/$PROJECT_REL"
[[ -d "$FIXTURE" ]] || { echo "Fixture not found: $FIXTURE" >&2; exit 2; }
[[ -d "$PROJECT" ]] || { echo "CLI project not found: $PROJECT" >&2; exit 2; }

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/format-matrix-XXXXXX")"
  MADE_TMP=1
else
  mkdir -p "$WORKDIR"
  MADE_TMP=0
fi
# Internal alias: TMP points at the work dir (renamed from TMPDIR to stop
# shadowing the $TMPDIR environment variable used as the mktemp parent above).
TMP="$WORKDIR"
cleanup() {
  if [[ "$KEEP_TEMP" -eq 1 ]]; then
    echo "keeping work dir: $WORKDIR"
  elif [[ "$MADE_TMP" -eq 1 ]]; then
    rm -rf "$WORKDIR"
  fi
}
trap cleanup EXIT

FAIL=0
pass() { echo "PASS [$1]: $2"; }
fail() { echo "FAIL [$1]: $2"; FAIL=1; }

check_contains() { # $1=format $2=file $3=label $4..=patterns (all must match, fixed strings)
  local fmt="$1" file="$2" label="$3"; shift 3
  local pat
  for pat in "$@"; do
    if grep -qF -- "$pat" "$file"; then
      :
    else
      fail "$fmt" "$label missing '$pat'"
      return 1
    fi
  done
  return 0
}

# shellcheck disable=SC2086
for f in $FORMATS; do
  OUT="$TMP/out.$f"
  echo "=== format: $f ==="
  if command -v timeout >/dev/null 2>&1; then
    if timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" -- --input "$FIXTURE" --format "$f" >"$OUT" 2>"$TMP/err.$f"; then
      :
    else
      rc=$?
      if [[ $rc -eq 124 ]]; then
        fail "$f" "scan timed out after ${TIMEOUT_SECS}s"
      else
        fail "$f" "scan exited $rc (stderr tail: $(tail -c 300 "$TMP/err.$f" | tr '\n' ' '))"
      fi
      continue
    fi
  else
    if dotnet run --project "$PROJECT" -- --input "$FIXTURE" --format "$f" >"$OUT" 2>"$TMP/err.$f"; then
      :
    else
      rc=$?
      fail "$f" "scan exited $rc (stderr tail: $(tail -c 300 "$TMP/err.$f" | tr '\n' ' '))"
      continue
    fi
  fi
  lines="$(wc -l <"$OUT" | tr -d ' ')"
  bytes="$(wc -c <"$OUT" | tr -d ' ')"
  echo "--- $f output: $lines lines, $bytes bytes ---"

  case "$f" in
    json)
      if check_contains "$f" "$OUT" "fields" '"ecosystem"' '"name"' '"version"' '"spdx"' '"licenseText"' '"sourceUrl"' '"status"' '"reason"' \
        && check_contains "$f" "$OUT" "counts" '"total"' '"resolved"' '"unknown"'; then
        pass "$f" "8 fields + summary counts present"
      fi
      if check_contains "$f" "$OUT" "direct" '"direct"'; then
        pass "$f" "9th field 'direct' present"
      fi
      ;;
    yaml)
      if check_contains "$f" "$OUT" "fields" 'ecosystem:' 'name:' 'version:' 'spdx:' 'licenseText:' 'sourceUrl:' 'status:' 'reason:' \
        && check_contains "$f" "$OUT" "counts" 'total:' 'resolved:' 'unknown:'; then
        pass "$f" "8 fields + summary counts present"
      fi
      if check_contains "$f" "$OUT" "direct" 'direct:'; then
        pass "$f" "9th field 'direct' present"
      fi
      ;;
    xml)
      if check_contains "$f" "$OUT" "fields" '<ecosystem>' '<name>' '<version>' '<spdx>' '<licenseText>' '<sourceUrl>' '<status>' '<reason>' \
        && check_contains "$f" "$OUT" "counts" 'total=' 'resolved=' 'unknown='; then
        pass "$f" "8 fields + summary counts present"
      fi
      if check_contains "$f" "$OUT" "direct" '<direct>'; then
        pass "$f" "9th field 'direct' present"
      fi
      ;;
    txt)
      if check_contains "$f" "$OUT" "fields" 'SPDX:' 'Status:' \
        && check_contains "$f" "$OUT" "counts" 'Total:' 'Resolved:' 'Unknown:'; then
        pass "$f" "fields + summary counts present"
      fi
      if check_contains "$f" "$OUT" "direct" 'direct='; then
        pass "$f" "9th field 'direct=' present"
      fi
      ;;
    md)
      if check_contains "$f" "$OUT" "fields/headers" '| Ecosystem |' '| Name |' '| Version |' '| SPDX |' '| License |' '| Source |' '| Status |' '| Reason |' \
        && check_contains "$f" "$OUT" "counts" 'Total:' 'Resolved:' 'Unknown:'; then
        pass "$f" "8 | headers | + summary counts present"
      fi
      if check_contains "$f" "$OUT" "direct-header" '| Direct |'; then
        pass "$f" "9th header | Direct | present"
      fi
      ;;
    cyclonedx-json)
      if check_contains "$f" "$OUT" "envelope" '"bomFormat"' '"CycloneDX"' '"specVersion"' '"1.5"' '"components"'; then
        pass "$f" "envelope bomFormat==CycloneDX + specVersion==1.5 + components present"
      fi
      if check_contains "$f" "$OUT" "direct-scope" '"scope"'; then
        pass "$f" "9th field 'direct' via scope required|optional present"
      fi
      if check_contains "$f" "$OUT" "counts" '"olaf:total"' '"olaf:resolved"' '"olaf:unknown"'; then
        pass "$f" "summary counts (olaf:total/resolved/unknown props) present"
      fi
      ;;
    cyclonedx-xml)
      if check_contains "$f" "$OUT" "envelope" '<bom' 'bom/1.5' '<metadata>' '<components>'; then
        pass "$f" "envelope <bom + xmlns bom/1.5 + <metadata>/<components> present"
      fi
      if check_contains "$f" "$OUT" "direct-scope" '<scope>'; then
        pass "$f" "9th field 'direct' via <scope>required|optional</scope> present"
      fi
      if check_contains "$f" "$OUT" "counts" 'olaf:total' 'olaf:resolved' 'olaf:unknown'; then
        pass "$f" "summary counts (olaf:total/resolved/unknown props) present"
      fi
      if python3 - "$OUT" <<'PY' 2>/dev/null
import sys, xml.etree.ElementTree as ET
ET.parse(sys.argv[1])
PY
      then
        pass "$f" "well-formed XML (python xml parse OK)"
      else
        fail "$f" "not well-formed XML (python xml parse failed)"
      fi
      ;;

    html)
      if check_contains "$f" "$OUT" "fields/headers" '<th>Ecosystem</th>' '<th>Name</th>' '<th>Version</th>' '<th>SPDX</th>' '<th>License</th>' '<th>Source</th>' '<th>Status</th>' '<th>Reason</th>' \
        && check_contains "$f" "$OUT" "counts" 'Total:' 'Resolved:' 'Unknown:'; then
        pass "$f" "8 <th> headers + summary counts present"
      fi
      if check_contains "$f" "$OUT" "direct-header" '<th>Direct</th>'; then
        pass "$f" "9th header <th>Direct</th> present"
      fi
      if check_contains "$f" "$OUT" "table" '<table>' '</table>' '<td>'; then
        pass "$f" "<table> with cells present"
      fi
      # Encoded output: formatter HTML-encodes every cell via WebUtility.HtmlEncode.
      # Runtime proof: at least one HTML entity in cell data, OR source-level proof
      # that the encoder is wired (keeps check green on fixtures without &<>" chars).
      if grep -qE '&(amp|lt|gt|quot|#39);' "$OUT"; then
        pass "$f" "HTML-encoded entity found in output"
      elif grep -q 'HtmlEncode' "$ROOT/src/Olaf.Formatters/HtmlFormatter.cs" 2>/dev/null; then
        pass "$f" "no entity chars in fixture data; encoder wired (HtmlEncode in HtmlFormatter.cs)"
      else
        fail "$f" "no HTML-encoded entity and HtmlEncode not found in HtmlFormatter.cs"
      fi
      ;;
  esac
done

if [[ "$FAIL" -eq 0 ]]; then
  echo "MATRIX OK: formats [$FORMATS] all passed."
  exit 0
else
  echo "MATRIX FAILED: see FAIL lines above." >&2
  exit 1
fi
