#!/usr/bin/env python3
"""Test matrix for validate_character_pack.py.

Positive probes must PASS, negative probes must FAIL. Every probe runs the
validator with an explicit --schema so results do not depend on path layout.

Exit 0 = all probes behave as expected, 1 = otherwise.
Usage: python3 test_character_pack.py
"""
import copy
import hashlib
import json
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).parent
VALIDATOR = HERE / "validate_character_pack.py"
SCHEMA = HERE.parent / "schemas" / "character-pack.schema.json"
SAMPLE = HERE.parent / "examples" / "sample_character_pack.json"

BASE = json.loads(SAMPLE.read_text(encoding="utf-8"))


def run(pack):
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False,
                                     encoding="utf-8") as f:
        json.dump(pack, f)
        tmp = f.name
    try:
        r = subprocess.run(
            [sys.executable, str(VALIDATOR), tmp,
             "--schema", str(SCHEMA)],
            capture_output=True, text=True, timeout=60)
        return r.returncode == 0, (r.stdout + r.stderr).strip().splitlines()[-1]
    finally:
        Path(tmp).unlink()


def mutate(fn):
    p = copy.deepcopy(BASE)
    fn(p)
    return p


def set_original_false(p):
    p["character"]["original_design"] = False


def drop_spec_version(p):
    del p["spec_version"]


def bad_trigger(p):
    p["states"][1]["trigger"] = "visitor_detected"  # missing genie. prefix


def dup_state_ids(p):
    p["states"][1]["id"] = "idle"


def bad_fallback(p):
    p["fallback_state"] = "nope"


def unknown_asset_ref(p):
    p["states"][0]["facial"]["expression_clip"] = "ghost_clip"


def undeclared_locale(p):
    p["states"][1]["voice_lines"]["de"] = ["Hallo!"]


def missing_xr_block(p):
    del p["states"][0]["xr"]


def file_ok(p):
    data = b"real asset bytes"
    with tempfile.NamedTemporaryFile("wb", suffix=".bin", delete=False) as f:
        f.write(data)
        tmp = f.name
    p["assets"][0]["uri"] = f"file://{tmp}"
    p["assets"][0]["sha256"] = hashlib.sha256(data).hexdigest()
    p["assets"][0]["bytes"] = len(data)
    p["_tmpfile"] = tmp  # cleaned by caller


def file_bad_hash(p):
    data = b"real asset bytes"
    with tempfile.NamedTemporaryFile("wb", suffix=".bin", delete=False) as f:
        f.write(data)
        tmp = f.name
    p["assets"][0]["uri"] = f"file://{tmp}"
    p["assets"][0]["sha256"] = "0" * 64
    p["assets"][0]["bytes"] = len(data)
    p["_tmpfile"] = tmp


def file_missing(p):
    p["assets"][0]["uri"] = "file:///tmp/definitely_not_here_qazbot.bin"
    p["assets"][0]["sha256"] = "0" * 64


def tier_l2_ok(p):
    p["delivery_tier"] = "L2"  # BASE already targets both surfaces


def tier_l2_no_facial(p):
    p["delivery_tier"] = "L2"
    p["target_surfaces"] = ["xr_glasses"]


def bad_tier(p):
    p["delivery_tier"] = "L3"


PROBES = [
    ("valid sample pack", lambda: BASE, True),
    ("original_design=false rejected", lambda: mutate(set_original_false), False),
    ("missing spec_version rejected", lambda: mutate(drop_spec_version), False),
    ("bad trigger name rejected", lambda: mutate(bad_trigger), False),
    ("duplicate state ids rejected", lambda: mutate(dup_state_ids), False),
    ("unknown fallback_state rejected", lambda: mutate(bad_fallback), False),
    ("unknown asset ref rejected", lambda: mutate(unknown_asset_ref), False),
    ("undeclared locale rejected", lambda: mutate(undeclared_locale), False),
    ("missing xr block rejected", lambda: mutate(missing_xr_block), False),
    ("file:// with correct sha256 passes", lambda: mutate(file_ok), True),
    ("file:// with wrong sha256 rejected", lambda: mutate(file_bad_hash), False),
    ("file:// missing file rejected", lambda: mutate(file_missing), False),
    ("L2 pack covering both surfaces passes", lambda: mutate(tier_l2_ok), True),
    ("L2 without facial_display rejected", lambda: mutate(tier_l2_no_facial), False),
    ("unknown delivery_tier rejected", lambda: mutate(bad_tier), False),
]

failures = []
for name, make, expect_pass in PROBES:
    pack = make()
    tmpfile = pack.pop("_tmpfile", None)
    try:
        ok, last = run(pack)
    finally:
        if tmpfile:
            Path(tmpfile).unlink(missing_ok=True)
    status = "ok" if ok == expect_pass else "WRONG"
    print(f"[{status}] expect={'PASS' if expect_pass else 'FAIL'} "
          f"got={'PASS' if ok else 'FAIL'}  {name}")
    if ok != expect_pass:
        failures.append((name, last))

if failures:
    print(f"\n{len(failures)} probe(s) misbehaved:")
    for name, last in failures:
        print(f"  - {name}: {last}")
    sys.exit(1)
print(f"\n{len(PROBES)}/{len(PROBES)} probes behave as expected")
