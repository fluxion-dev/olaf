#!/usr/bin/env bash
# tools/update-spdx-db.sh — maintainer-only SPDX DB refresh (issue #77).
# MAINTAINER-NETWORK: this script fetches from the network. CI and the test
# suite NEVER execute it; suites prove zero-network offline behavior with
# throwing-handler tests instead.
#
# Pipeline (deterministic, byte-identical across runs):
#   license-list-data tag ${SPDX_VERSION} tarball
#     -> python3 assembly (stdlib json only): metadata-first key order
#        (id, name, osi, fsf, deprecated, text), ids in LC_ALL=C byte order
#        (verified with `LC_ALL=C sort -c`), text = text/<id>.txt where
#        present else ""
#     -> src/Olaf.Resolvers/Data/spdx-licenses.json
#     -> tools/spdx-db.sha256 (sha256sum of the json)
#
# Size gate (D3): DB must stay <5MB. If the full-text build exceeds 5MB the
# script FAILS (exit 1): ship the metadata-all + text-for-35-seed-ids-only
# variant instead (texts resolve at runtime via SpdxLicenseTexts) and file a
# follow-up gh issue for the full-text deferral — record its number in the
# runbook note. Current seed (35 texts) is ~6KB, far under the gate.
set -euo pipefail

SPDX_VERSION="${SPDX_VERSION:-v3.29.0}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_JSON="$REPO_ROOT/src/Olaf.Resolvers/Data/spdx-licenses.json"
OUT_HASH="$REPO_ROOT/tools/spdx-db.sha256"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

export LC_ALL=C

TARBALL_URL="https://github.com/spdx/license-list-data/archive/refs/tags/${SPDX_VERSION}.tar.gz"
curl -fsSL "$TARBALL_URL" -o "$WORK_DIR/license-list-data.tar.gz"
tar -xzf "$WORK_DIR/license-list-data.tar.gz" -C "$WORK_DIR"

SRC_DIR="$(echo "$WORK_DIR"/license-list-data-*/)"
python3 - "$SRC_DIR" "$OUT_JSON" <<'EOF'
import json, os, sys

src_dir, out_json = sys.argv[1], sys.argv[2]
json_dir = os.path.join(src_dir, "json")
text_dir = os.path.join(src_dir, "text")

entries = []
for fname in sorted(os.listdir(json_dir)):
    if not fname.endswith(".json"):
        continue
    with open(os.path.join(json_dir, fname), encoding="utf-8") as f:
        meta = json.load(f)
    lid = meta.get("licenseId", "")
    if not lid:
        continue
    text_path = os.path.join(text_dir, lid + ".txt")
    text = ""
    if os.path.isfile(text_path):
        with open(text_path, encoding="utf-8") as f:
            text = f.read()
    # Metadata-first key order (NOT alphabetical): id, name, osi, fsf, deprecated, text.
    entries.append({
        "id": lid,
        "name": meta.get("name", ""),
        "osi": bool(meta.get("isOsiApproved", False)),
        "fsf": bool(meta.get("isFsfLibre", False)),
        "deprecated": bool(meta.get("isDeprecatedLicenseId", False)),
        "text": text,
    })

entries.sort(key=lambda e: e["id"].encode("utf-8"))
with open(out_json, "w", encoding="utf-8") as f:
    json.dump(entries, f, indent=2, ensure_ascii=False)
    f.write("\n")
print(f"spdx-db: wrote {len(entries)} entries to {out_json}")
EOF

# Load-bearing order check: ids must already be in LC_ALL=C byte order.
jq -r '.[].id' "$OUT_JSON" | LC_ALL=C sort -c
jq -S . "$OUT_JSON" > /dev/null

sha256sum "$OUT_JSON" | awk '{print $1 "  src/Olaf.Resolvers/Data/spdx-licenses.json"}' > "$OUT_HASH"

SIZE_BYTES="$(wc -c < "$OUT_JSON" | tr -d ' ')"
echo "spdx-db: $OUT_JSON ($SIZE_BYTES bytes) sha256: $(cut -d' ' -f1 "$OUT_HASH")"
if [ "$SIZE_BYTES" -ge 5242880 ]; then
  echo "spdx-db: SIZE GATE TRIPPED (>= 5MB) — ship the seed variant and file a follow-up issue." >&2
  exit 1
fi
