#!/usr/bin/env python3
"""Validate an ad template pack: JSON Schema + platform invariants.

Checks:
  schema ............ pack validates against schemas/template-pack.schema.json
  P1 ................ fallback present (no fallback -> pack rejected)
  level/slots ....... level 1 allows only name_token/text_token;
                      character_anchor requires level 2
  manifest_sha256 ... recomputed over canonical JSON (sort_keys, compact
                      separators, utf-8, manifest_sha256 field excluded)
                      and compared to the declared value

Canonical JSON definition (also in docs/ad-template-pack-v1.md):
    json.dumps(obj, sort_keys=True, separators=(",", ":"),
               ensure_ascii=False).encode("utf-8")

Usage:
    python3 tools/validate_template_pack.py examples/sample_template_pack.json
"""
import hashlib
import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCHEMA = json.loads((REPO_ROOT / "schemas" / "template-pack.schema.json").read_text())

try:
    import jsonschema
except ImportError:
    sys.exit("FAIL: jsonschema not installed (pip install jsonschema)")


def canonical(obj: dict) -> bytes:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=False).encode("utf-8")


def manifest_hash(pack: dict) -> str:
    body = {k: v for k, v in pack.items() if k != "manifest_sha256"}
    return hashlib.sha256(canonical(body)).hexdigest()


TEXT_KINDS = {"name_token", "text_token"}


def validate(pack: dict) -> list:
    errors = []
    for e in jsonschema.Draft7Validator(SCHEMA).iter_errors(pack):
        errors.append(f"schema: {e.message[:160]}")
    # P1: no fallback -> reject
    if "fallback" not in pack:
        errors.append("P1: pack has no fallback -> rejected (fallback is mandatory)")
    # level vs slots consistency
    level = pack.get("personalization_level")
    kinds = {s.get("kind") for s in pack.get("slots", []) if isinstance(s, dict)}
    if level == 1 and not kinds <= TEXT_KINDS:
        errors.append(
            f"level/slots: personalization_level=1 allows only {sorted(TEXT_KINDS)}, "
            f"got {sorted(kinds)}")
    # manifest hash
    declared = pack.get("manifest_sha256")
    if declared != manifest_hash(pack):
        errors.append("manifest_sha256 mismatch: declared value does not match "
                      "canonical recomputation")
    return errors


def main() -> int:
    if len(sys.argv) != 2:
        print(f"usage: {sys.argv[0]} <template-pack.json>")
        return 2
    path = Path(sys.argv[1])
    try:
        pack = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as e:
        print(f"FAIL: cannot parse {path}: {e}")
        return 1
    errors = validate(pack)
    if errors:
        print(f"FAIL: {path.name} invalid ({len(errors)} problem(s)):")
        for e in errors:
            print(f"  - {e}")
        return 1
    print(f"OK: {path.name} valid "
          f"(schema + P1 + level/slots + manifest_sha256)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
