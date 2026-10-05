#!/usr/bin/env python3
"""Probe matrix for the reference composer (tools/reference_composer.py).

NOW is pinned inside the sample receipt window (2026-10-02), between
ConsentGranted (10:00) and ConsentRevoked (12:30) — deterministic demo time.
"""
import copy
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from reference_composer import compose, decide

REPO_ROOT = Path(__file__).resolve().parent.parent
TPL = json.loads((REPO_ROOT / "examples" / "sample_template_pack.json").read_text())
PROFILE = json.loads((REPO_ROOT / "examples" / "sample_profile.json").read_text())
CSR = json.loads((REPO_ROOT / "examples" / "sample_consent_receipt.json").read_text())

NOW = "2026-10-02T11:00:00+06:00"   # inside window, after grant, before revoke
LATE = "2026-10-03T00:00:01+06:00"  # past window_not_after

passed = total = 0


def probe(name, cond, detail=""):
    global passed, total
    total += 1
    print(f"[{'OK' if cond else 'FAIL'}] {name}" + (f" — {detail}" if detail else ""))
    passed += cond


# 1. valid consent -> personalized, tokens filled, hash, TTL
m = compose(TPL, PROFILE, CSR, now=NOW)
slots = {s["slot_id"]: s for s in m["filled_slots"]}
probe("valid consent -> personalized manifest",
      m["mode"] == "personalized" and m["personalized"]
      and m["consent_receipt_id"] == "csr_demo_0001"
      and slots["cup_name"]["value"] == PROFILE["display_name"]
      and slots["city_line"]["value"] == "Алматы"
      and len(m["instance_hash"]) == 64
      and m["expires_at"] > m["composed_at"]
      and m["media_retention"] == "hash_only",
      f"instance={m['instance_id']}")

# 2. no consent -> fallback only (P2)
m2 = compose(TPL, PROFILE, None, now=NOW)
probe("no consent -> fallback only",
      m2["mode"] == "fallback" and not m2["personalized"]
      and m2.get("fallback_served") and m2["reason"] == "no_consent")

# 3. expired consent -> fallback
mode3, det3 = decide(TPL, CSR, now=LATE)
probe("expired consent -> fallback",
      mode3 == "fallback" and det3["reason"] == "consent_expired")

# 4. classes insufficient -> fallback
csr4 = copy.deepcopy(CSR)
csr4["classes"] = {"profile_token": False, "likeness_binding": False}
mode4, det4 = decide(TPL, csr4, now=NOW)
probe("classes insufficient -> fallback",
      mode4 == "fallback" and det4["reason"] == "classes_insufficient")

# 5. revoked receipt -> fallback
mode5, det5 = decide(TPL, CSR, now=NOW, revoked=["csr_demo_0001"])
probe("revoked receipt -> fallback",
      mode5 == "fallback" and det5["reason"] == "consent_revoked")

# 6. tampered template -> compose refuses (fail closed)
bad = copy.deepcopy(TPL)
bad.pop("fallback")
try:
    compose(bad, PROFILE, CSR, now=NOW)
    probe("tampered template -> compose refuses", False)
except ValueError as e:
    probe("tampered template -> compose refuses", True, str(e)[:60])

print(f"\n{passed}/{total} probes green")
sys.exit(0 if passed == total else 1)
