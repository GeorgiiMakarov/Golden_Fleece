#!/usr/bin/env python3
"""Reference composer for Ad Template Packs — the "open integrator" artifact.

Scope (deliberately thin):
  * decides personalized vs fallback (P2: device consent is authoritative);
  * level 1: fills name_token / text_token slots from a profile;
  * level 2: emits the character-composition CONTRACT only
    (rendering happens on-device; this tool never renders);
  * emits a composition manifest with instance_hash; media itself is never
    written (media_retention=hash_only).

Fails closed: invalid template, missing token value, or no valid consent
-> no personalized instance (fallback only).

Usage:
    python3 tools/reference_composer.py --template examples/sample_template_pack.json \\
        --profile examples/sample_profile.json \\
        --consent examples/sample_consent_receipt.json \\
        [--now 2026-10-02T11:00:00+06:00] [--revoked csr_demo_0001]

The manifest is printed to stdout as JSON.
"""
import argparse
import hashlib
import json
import sys
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

try:
    import jsonschema
except ImportError:
    sys.exit("FAIL: jsonschema not installed (pip install jsonschema)")

sys.path.insert(0, str(REPO_ROOT / "tools"))
from validate_template_pack import validate as validate_tpl


def canonical(obj: dict) -> bytes:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=False).encode("utf-8")


def parse_time(s):
    if s is None:
        return datetime.now(timezone.utc)
    if isinstance(s, datetime):
        dt = s
    else:
        dt = datetime.fromisoformat(s)
    return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)


def decide(template: dict, consent: dict | None, now=None, revoked=()) -> tuple:
    """P2 decision. Returns (mode, detail); mode is 'personalized'|'fallback'."""
    required = set(template.get("consent", {}).get("classes_required", []))
    if consent is None:
        return "fallback", {"reason": "no_consent", "personalized": False}
    schema = json.loads((REPO_ROOT / "schemas" / "consent-receipt.schema.json").read_text())
    errs = list(jsonschema.Draft7Validator(schema).iter_errors(consent))
    if errs:
        return "fallback", {"reason": "receipt_invalid", "personalized": False,
                             "errors": [e.message[:120] for e in errs[:2]]}
    if consent.get("template_id") != template.get("template_id"):
        return "fallback", {"reason": "template_mismatch", "personalized": False}
    if consent.get("receipt_id") in set(revoked or ()):
        return "fallback", {"reason": "consent_revoked", "personalized": False}
    t = parse_time(now)
    w = consent.get("window", {})
    if not (parse_time(w["not_before"]) <= t <= parse_time(w["not_after"])):
        return "fallback", {"reason": "consent_expired", "personalized": False}
    granted = {k for k, v in consent.get("classes", {}).items() if v}
    if not required <= granted:
        return "fallback", {"reason": "classes_insufficient", "personalized": False}
    return "personalized", {"personalized": True,
                            "consent_receipt_id": consent["receipt_id"]}


def compose(template: dict, profile: dict, consent: dict | None,
            now=None, revoked=()) -> dict:
    """Build a composition manifest. Raises ValueError on invalid input."""
    errs = validate_tpl(template)
    if errs:
        raise ValueError("template invalid: " + "; ".join(errs[:3]))
    mode, det = decide(template, consent, now=now, revoked=revoked)
    t = parse_time(now)
    ttl = template.get("compose", {}).get("instance_ttl_min", 60)
    manifest = {
        "instance_id": f"adinst_{uuid.uuid4().hex[:12]}",
        "template_id": template["template_id"],
        "template_version": template.get("template_version"),
        "mode": mode,
        "personalized": det["personalized"],
        "reason": det.get("reason"),
        "consent_receipt_id": det.get("consent_receipt_id"),
        "composed_at": t.isoformat(),
        "expires_at": (t + timedelta(minutes=ttl)).isoformat(),
        "media_retention": "hash_only",
        "filled_slots": [],
    }
    if mode == "personalized":
        for s in template.get("slots", []):
            kind, sid = s["kind"], s["slot_id"]
            if kind == "name_token":
                val = profile.get("display_name")
            elif kind == "text_token":
                val = (profile.get("tokens") or {}).get(sid)
            elif kind == "character_anchor":
                val = None  # device-side rendering; contract only
            else:
                raise ValueError(f"unknown slot kind {kind!r}")
            if kind in ("name_token", "text_token") and not val:
                raise ValueError(f"no profile value for slot {sid!r} (fail closed)")
            manifest["filled_slots"].append(
                {"slot_id": sid, "kind": kind, "value": val})
        if template.get("personalization_level") == 2:
            manifest["character_composition"] = {
                "status": "device_side",
                "note": "rendering is performed on-device; "
                        "reference composer emits contract only",
            }
    else:
        manifest["fallback_served"] = True
    body = {k: v for k, v in manifest.items() if k != "instance_hash"}
    manifest["instance_hash"] = hashlib.sha256(canonical(body)).hexdigest()
    return manifest


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--template", required=True)
    ap.add_argument("--profile", required=True)
    ap.add_argument("--consent", default=None)
    ap.add_argument("--now", default=None,
                    help="ISO datetime with offset; defaults to current UTC")
    ap.add_argument("--revoked", default="",
                    help="comma-separated revoked receipt ids")
    a = ap.parse_args()
    try:
        template = json.loads(Path(a.template).read_text(encoding="utf-8"))
        profile = json.loads(Path(a.profile).read_text(encoding="utf-8"))
        consent = (json.loads(Path(a.consent).read_text(encoding="utf-8"))
                   if a.consent else None)
        revoked = [r for r in a.revoked.split(",") if r]
        manifest = compose(template, profile, consent, now=a.now, revoked=revoked)
    except (OSError, json.JSONDecodeError, ValueError) as e:
        print(f"FAIL: {e}", file=sys.stderr)
        return 1
    print(json.dumps(manifest, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
