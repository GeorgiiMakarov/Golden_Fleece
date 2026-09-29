#!/usr/bin/env python3
"""CI gate: compare a fresh canonical bake against frozen reference metrics.

Usage:
    python3 simulate.py sl 0 0 240 jacobi 50 static
    python3 verify_reference.py [path/to/checks.json]

Compares every frame of the new checks.json with
reference/canonical-sl-checks.json. Fails on any relative deviation
above TOL.
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REF = os.path.join(HERE, "reference", "canonical-sl-checks.json")
NEW = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "output", "checks.json")

KEYS = ["max_abs_div_after", "ke", "max_vel", "dye_mass", "mean_abs_vort"]
TOL = 1e-6  # pure numpy/scipy pipeline: deterministic, tight tolerance is safe


def main():
    ref = json.load(open(REF))
    new = json.load(open(NEW))
    assert len(ref) == len(new), f"frame count {len(new)} != reference {len(ref)}"
    worst = 0.0
    for fr, fn in zip(ref, new):
        assert fr["frame"] == fn["frame"] and fr["step"] == fn["step"], \
            f"frame/step mismatch: {fr['frame']}/{fr['step']} vs {fn['frame']}/{fn['step']}"
        for k in KEYS:
            a, b = fr[k], fn[k]
            rel = abs(a - b) / max(abs(a), 1e-300)
            worst = max(worst, rel)
            assert rel <= TOL, \
                f"frame {fr['frame']} {k}: ref {a!r} vs new {b!r} (rel {rel:.2e} > {TOL:.0e})"
    print(f"OK: {len(new)} frames match reference, worst relative deviation {worst:.2e}")


if __name__ == "__main__":
    main()
