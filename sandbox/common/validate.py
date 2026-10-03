#!/usr/bin/env python3
"""Validate an `olaf generate --format json` report.

Checks that the report is a *proper licence file*:
  - valid JSON object with `summary` + `licenses`
  - summary.total == len(licenses) and total >= --min-packages
  - every license row has the 9 base keys
    (ecosystem, name, version, spdx, licenseText, sourceUrl, status, reason, direct)
  - at least one row matches --eco (repos may contain several ecosystems)
  - at least one row is resolved (status != Unknown / spdx != Unknown);
    with --offline some rows may stay Unknown, but zero resolved means
    the report is useless.

Exit 0 on success, 1 on failure. Human-readable PASS/FAIL line on stdout.
Stdlib only so it runs on every sandbox image.
"""

import argparse
import json
import sys

BASE_KEYS = (
    "ecosystem",
    "name",
    "version",
    "spdx",
    "licenseText",
    "sourceUrl",
    "status",
    "reason",
    "direct",
)


def fail(msg: str) -> int:
    print(f"FAIL: {msg}")
    return 1


def main() -> int:
    ap = argparse.ArgumentParser(description="Validate olaf json report.")
    ap.add_argument("report", help="Path to olaf --format json report.")
    ap.add_argument("--eco", required=True, help="Expected ecosystem (e.g. npm).")
    ap.add_argument("--min-packages", type=int, default=1)
    ap.add_argument(
        "--allow-unresolved",
        action="store_true",
        help="Skip the >=1-resolved gate (for --offline runs where misses stay Unknown).",
    )
    args = ap.parse_args()

    try:
        with open(args.report, encoding="utf-8") as fh:
            data = json.load(fh)
    except FileNotFoundError:
        return fail(f"report not found: {args.report}")
    except json.JSONDecodeError as ex:
        return fail(f"report is not valid JSON: {ex}")

    if not isinstance(data, dict):
        return fail("top-level JSON is not an object")
    if "summary" not in data or "licenses" not in data:
        return fail("missing 'summary' or 'licenses' keys")
    summary = data["summary"]
    licenses = data["licenses"]
    if not isinstance(summary, dict) or not isinstance(licenses, list):
        return fail("'summary' must be an object and 'licenses' a list")

    total = summary.get("total")
    if total != len(licenses):
        return fail(f"summary.total ({total}) != len(licenses) ({len(licenses)})")
    if total < args.min_packages:
        return fail(f"total {total} < min-packages {args.min_packages}")

    for key in ("total", "resolved", "unknown"):
        if key not in summary:
            return fail(f"summary missing '{key}'")

    for i, row in enumerate(licenses):
        if not isinstance(row, dict):
            return fail(f"licenses[{i}] is not an object")
        missing = [k for k in BASE_KEYS if k not in row]
        if missing:
            return fail(f"licenses[{i}] missing keys: {', '.join(missing)}")
        if not str(row.get("name", "")).strip():
            return fail(f"licenses[{i}] has empty name")

    eco_rows = [
        r
        for r in licenses
        if str(r.get("ecosystem", "")).lower() == args.eco.lower()
    ]
    if not eco_rows:
        found = sorted({str(r.get("ecosystem", "?")) for r in licenses})
        return fail(f"no rows with ecosystem '{args.eco}' (found: {found})")

    resolved = [
        r
        for r in licenses
        if str(r.get("status", "")).lower() != "unknown"
        or str(r.get("spdx", "")).lower() not in ("", "unknown", "none")
    ]
    if not resolved and total > 0 and not args.allow_unresolved:
        return fail("zero resolved licenses (all Unknown) — report is useless")

    print(
        f"PASS: eco={args.eco} total={total} "
        f"resolved={summary.get('resolved')} unknown={summary.get('unknown')} "
        f"eco_rows={len(eco_rows)}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
