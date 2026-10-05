#!/usr/bin/env python3
"""Validate a QAZBOT character pack against character-pack.schema.json.

Checks:
  1. JSON Schema conformance (draft 2020-12).
  2. Cross-field rules the schema cannot express:
     - state ids are unique
     - fallback_state references an existing state
     - facial.expression_clip / xr.bundle_asset reference listed asset_ids
     - voice_lines locales are a subset of the pack locales
     - every state covers every target surface
     - delivery_tier L2 packs must also cover facial_display
       (L2 builds on L1, never replaces it)
  3. file:// asset URIs must exist on disk (pack:// URIs are resolved at
     distribution time and are skipped here).

Exit 0 = PASS, 1 = FAIL. Usage:
    python3 validate_character_pack.py <pack.json> [--schema <schema.json>]
"""
import argparse
import hashlib
import json
import sys
import urllib.parse
from pathlib import Path

try:
    import jsonschema
    from jsonschema import Draft202012Validator
except ImportError:
    sys.exit("FAIL: jsonschema package is required (pip install jsonschema)")


def fail(msg):
    print(f"FAIL: {msg}")
    sys.exit(1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pack", help="character pack JSON file")
    ap.add_argument("--schema", default=None, help="schema file (default: ../schemas/character-pack.schema.json)")
    args = ap.parse_args()

    pack_path = Path(args.pack)
    schema_path = Path(args.schema) if args.schema else pack_path.parent.parent / "schemas" / "character-pack.schema.json"

    try:
        pack = json.loads(pack_path.read_text(encoding="utf-8"))
    except Exception as e:
        fail(f"pack is not valid JSON: {e}")
    try:
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
    except Exception as e:
        fail(f"cannot read schema: {e}")

    errors = sorted(Draft202012Validator(schema).iter_errors(pack), key=lambda e: list(e.path))
    if errors:
        for e in errors[:10]:
            loc = ".".join(str(p) for p in e.path) or "<root>"
            print(f"  schema: {loc}: {e.message}")
        fail(f"{len(errors)} schema error(s)")

    # --- cross-field rules ---
    states = pack["states"]
    ids = [s["id"] for s in states]
    if len(set(ids)) != len(ids):
        fail("duplicate state ids")

    if pack["fallback_state"] not in ids:
        fail(f"fallback_state '{pack['fallback_state']}' does not match any state id")

    asset_ids = {a["asset_id"] for a in pack["assets"]}
    for s in states:
        facial = s.get("facial") or {}
        if facial.get("expression_clip") and facial["expression_clip"] not in asset_ids:
            fail(f"state '{s['id']}': expression_clip references unknown asset")
        xr = s.get("xr") or {}
        if xr.get("bundle_asset") and xr["bundle_asset"] not in asset_ids:
            fail(f"state '{s['id']}': bundle_asset references unknown asset")

    pack_locales = set(pack["locales"])
    for s in states:
        extra = set((s.get("voice_lines") or {})) - pack_locales
        if extra:
            fail(f"state '{s['id']}': voice_lines use undeclared locales {sorted(extra)}")

    for surface in pack["target_surfaces"]:
        key = "facial" if surface == "facial_display" else "xr"
        missing = [s["id"] for s in states if not s.get(key)]
        if missing:
            fail(f"surface '{surface}': states missing '{key}' block: {missing}")

    if pack["delivery_tier"] == "L2" and "facial_display" not in pack["target_surfaces"]:
        fail("delivery_tier 'L2' must also cover 'facial_display' "
             "(L2 builds on L1, never replaces it)")

    # --- file:// assets: existence + integrity (sha256 + bytes) ---
    for a in pack["assets"]:
        u = urllib.parse.urlparse(a["uri"])
        if u.scheme == "file":
            p = Path(urllib.parse.unquote(u.path))
            if not p.is_file():
                fail(f"asset '{a['asset_id']}': file not found: {u.path}")
            raw = p.read_bytes()
            digest = hashlib.sha256(raw).hexdigest()
            if digest != a["sha256"]:
                fail(f"asset '{a['asset_id']}': sha256 mismatch "
                     f"(manifest {a['sha256'][:12]}..., file {digest[:12]}...)")
            if a.get("bytes") and len(raw) != a["bytes"]:
                fail(f"asset '{a['asset_id']}': bytes mismatch "
                     f"(manifest {a['bytes']}, file {len(raw)})")
    # pack:// URIs resolve inside the distributed pack and are skipped here;
    # they are checked at packaging/distribution time, not by this validator.

    print(f"PASS: {pack['pack_id']} v{pack['pack_version']} "
          f"[{pack['delivery_tier']}] "
          f"({len(states)} states, {len(pack['assets'])} assets, "
          f"surfaces={','.join(pack['target_surfaces'])})")


if __name__ == "__main__":
    main()
