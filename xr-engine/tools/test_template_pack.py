#!/usr/bin/env python3
"""Negative-probe matrix for ad template packs.

Every probe must be REJECTED by tools/validate_template_pack.py.
The sample pack must be ACCEPTED. Mirrors tools/test_character_pack.py style.
"""
import copy
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from validate_template_pack import validate

REPO_ROOT = Path(__file__).resolve().parent.parent
SAMPLE = json.loads((REPO_ROOT / "examples" / "sample_template_pack.json").read_text())

passed = total = 0


def probe(name, pack, expect_ok):
    global passed, total
    total += 1
    errors = validate(pack)
    ok = (not errors) == expect_ok
    print(f"[{'OK' if ok else 'FAIL'}] {name}"
          + ("" if ok else f" -- errors={errors[:2]}"))
    passed += ok


def mutate(fn):
    p = copy.deepcopy(SAMPLE)
    fn(p)
    return p


probe("sample pack accepted", SAMPLE, True)
probe("P1: no fallback -> reject",
      mutate(lambda p: p.pop("fallback")), False)
probe("dwell 500 < 1200 floor -> reject",
      mutate(lambda p: p["verification"].__setitem__("min_dwell_ms", 500)), False)
probe("level 1 + character_anchor slot -> reject (level/slots)",
      mutate(lambda p: p["slots"].__setitem__(0, {
          "slot_id": "hero_face", "kind": "character_anchor",
          "character_role": "hero",
          "timeline": {"start_ms": 1000, "end_ms": 5000},
          "placement_mode": "head_locked",
          "binding_rules": {"keep_aspect_ratio": True, "max_scale": 1.5,
                            "allow_recolor": False, "allow_morph": False}})), False)
probe("bad placement_mode -> reject",
      mutate(lambda p: p["slots"][0].__setitem__("placement_mode", "orbit")), False)
probe("tampered manifest_sha256 -> reject",
      mutate(lambda p: p.__setitem__("manifest_sha256", "0" * 64)), False)
probe("empty slots -> reject",
      mutate(lambda p: p.__setitem__("slots", [])), False)
probe("bad creative sha256 -> reject",
      mutate(lambda p: p["creative"].__setitem__("sha256", "zzz")), False)
probe("max_scale 5.0 -> reject",
      mutate(lambda p: p["slots"][0]["binding_rules"].__setitem__("max_scale", 5.0)), False)

print(f"\n{passed}/{total} probes green")
sys.exit(0 if passed == total else 1)
