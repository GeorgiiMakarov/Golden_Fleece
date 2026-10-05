#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Probe matrix for validate_xr_layer.py.

Run:  python3 tools/test_xr_layer.py          (exit 0 only if every probe passes)

The baseline is examples/sample_character_pack_xr.json, built from the real reference pack
(target_surfaces, assets[].asset_id). Each rule probe mutates it so that exactly one thing is
wrong and asserts the exact set of rules that fire plus the exit code. Further groups cover the
legacy v1 transition, the patched base schema, waivers, SARIF, determinism and fail-closed
behavior.
"""
from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
_spec = importlib.util.spec_from_file_location("vxl", HERE / "validate_xr_layer.py")
vxl = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(vxl)

BASE = json.loads((ROOT / "examples" / "sample_character_pack_xr.json").read_text(encoding="utf-8"))
REAL = json.loads((ROOT / "examples" / "sample_character_pack.json").read_text(encoding="utf-8"))
BASE_SCHEMA = json.loads((ROOT / "schemas" / "character-pack.schema.json").read_text(encoding="utf-8"))
POLICY = json.loads((ROOT / "policy" / "xr-gate-policy.v1.json").read_text(encoding="utf-8"))
NOW = "2026-10-03"
TMP = Path(tempfile.mkdtemp(prefix="xrgate-"))
PASSED = 0
FAILED = 0

CARD = "aru_xr_card"
IDLE, GREET, LISTEN, SPEAK, BYE, WALK = ("aru_xr_%s" % s for s in ("idle", "greeting", "listening", "speaking", "farewell", "walking"))


def h(s):
    return hashlib.sha256(s.encode()).hexdigest()


# ------------------------------------------------------------------ helpers

def get(node, path):
    for k in path.split("/"):
        node = node[int(k)] if isinstance(node, list) else node[k]
    return node


def setp(p, path, value):
    head, _, last = path.rpartition("/")
    node = get(p, head) if head else p
    if isinstance(node, list):
        node[int(last)] = value
    else:
        node[last] = value


def delp(p, path):
    head, _, last = path.rpartition("/")
    node = get(p, head) if head else p
    if isinstance(node, list):
        del node[int(last)]
    else:
        del node[last]


def asset(p, asset_id):
    return [a for a in p["assets"] if a["asset_id"] == asset_id][0]


def new_asset(p, asset_id, kind, size=50000):
    p["assets"].append({"asset_id": asset_id, "bytes": size, "kind": kind, "sha256": h(asset_id),
                        "uri": "pack://assets/%s.bin" % asset_id})


def state(p, sid):
    return [s for s in p["states"] if s["id"] == sid][0]


def write(name, obj):
    path = TMP / name
    path.write_text(obj if isinstance(obj, str) else json.dumps(obj, ensure_ascii=False), encoding="utf-8")
    return path


def check(pack, waivers=None, now=NOW, policy=None):
    pp = write("pack.json", pack)
    wp = write("waivers.json", waivers) if waivers is not None else None
    kw = {"policy_path": write("policy.json", policy)} if policy is not None else {}
    return vxl.evaluate(pp, waivers_path=wp, now_date=vxl.parse_date(now), **kw)


def live(rep):
    return set(f.rule for f in rep.findings if f.waiver is None)


def probe(name, fn):
    global PASSED, FAILED
    try:
        fn()
        PASSED += 1
        print("PASS  " + name)
    except AssertionError as e:
        FAILED += 1
        print("FAIL  %s  :: %s" % (name, e))
    except Exception as e:  # a crash inside a probe is a failure too
        FAILED += 1
        print("ERROR %s  :: %s: %s" % (name, type(e).__name__, e))


def rule_probe(name, mutate, expected, code):
    def fn():
        p = copy.deepcopy(BASE)
        mutate(p)
        rep = check(p)
        got = live(rep)
        assert got == set(expected), "rules %s, expected %s" % (sorted(got), sorted(expected))
        assert rep.code == code, "exit %d, expected %d" % (rep.code, code)
    probe(name, fn)


def waiver(rule, fp, **kw):
    w = {"rule": rule, "fingerprint": fp, "pack_version": "1.1.0",
         "reason": "Accepted for the pilot, tracked in ticket 42", "approver": "Platform reviewer",
         "approver_org": "platform-demo", "expires": "2026-11-01"}
    w.update(kw)
    return w


def fps(rep, rule):
    return [f.fp for f in rep.findings if f.rule == rule and f.waiver is None]


def schema_errors(pack, schema):
    errs = []
    vxl.check_schema(pack, schema, schema, "", errs)
    return errs


def original_base_schema():
    """The repository schema as it was before addendum A1: patched schema minus the three additions."""
    s = copy.deepcopy(BASE_SCHEMA)
    del s["properties"]["xr_layer"]
    del s["properties"]["provenance"]
    s["properties"]["assets"]["items"]["properties"]["kind"]["enum"].remove("xr_clip")
    return s


# ------------------------------------------------------------------ baseline, structure, accessors

def t_baseline():
    rep = check(copy.deepcopy(BASE))
    assert rep.findings == [], "baseline must be clean, got %s" % [(f.rule, f.pointer) for f in rep.findings]
    assert rep.code == 0


probe("baseline: the example built from the real pack is clean (exit 0)", t_baseline)


def t_dict_accessors():
    p = copy.deepcopy(BASE)
    p["assets"] = dict((a["asset_id"], a) for a in p["assets"])
    p["states"] = dict((s["id"], s) for s in p["states"])
    p["target_surfaces"] = dict((s, {}) for s in p["target_surfaces"])
    rep = check(p)
    assert rep.findings == [], [(f.rule, f.pointer) for f in rep.findings]


probe("tolerant accessors accept assets, states and target_surfaces as objects", t_dict_accessors)


def t_id_fallbacks():
    p = copy.deepcopy(BASE)
    for a in p["assets"]:
        a["id"] = a.pop("asset_id")
    p["surfaces"] = p.pop("target_surfaces")
    rep = check(p)
    assert rep.findings == [], [(f.rule, f.pointer) for f in rep.findings]


probe("fallback keys: assets[].id and surfaces are still understood", t_id_fallbacks)

rule_probe("XR000: states empty -> base structure not recognized",
           lambda p: p.__setitem__("states", []), {"XR000"}, 2)
rule_probe("XR001: xr_glasses claimed (target_surfaces) without xr_layer, provenance present",
           lambda p: delp(p, "xr_layer"), {"XR001"}, 2)


def m_surfaces_key(p):
    p["surfaces"] = p.pop("target_surfaces")
    del p["xr_layer"]


rule_probe("XR001: the claim is also found under the fallback key 'surfaces'", m_surfaces_key, {"XR001"}, 2)
rule_probe("PV001: xr_layer without provenance is an error",
           lambda p: delp(p, "provenance"), {"PV001"}, 2)

# ------------------------------------------------------------------ legacy v1 transition


def t_legacy_real_pack():
    rep = check(copy.deepcopy(REAL))
    sev = dict((f.rule, f.severity) for f in rep.findings)
    assert sev == {"XR001": "warning", "PV001": "warning"}, sev
    assert rep.code == 1, "exit %d" % rep.code


probe("legacy: the real v1 pack passes with a mark (XR001 and PV001 as warnings, exit 1)", t_legacy_real_pack)


def t_legacy_no_xr_claim():
    p = copy.deepcopy(REAL)
    p["target_surfaces"] = ["facial_display"]
    rep = check(p)
    assert dict((f.rule, f.severity) for f in rep.findings) == {"PV001": "warning"} and rep.code == 1


probe("legacy: original_design without provenance on a facial-only pack is PV001 warning", t_legacy_no_xr_claim)


def t_legacy_strict_policy():
    pol = copy.deepcopy(POLICY)
    pol["legacy_severity"] = {}
    rep = check(copy.deepcopy(REAL), policy=pol)
    assert live(rep) == {"XR001", "PV001"} and rep.code == 2, (sorted(live(rep)), rep.code)


probe("legacy: removing legacy_severity from the policy ends the transition (exit 2)", t_legacy_strict_policy)


def t_legacy_ends_with_any_a1_block():
    p = copy.deepcopy(REAL)
    p["provenance"] = copy.deepcopy(BASE["provenance"])
    rep = check(p)
    assert dict((f.rule, f.severity) for f in rep.findings if f.rule == "XR001") == {"XR001": "error"}


probe("legacy: a pack that carries any A1 block is no longer legacy (XR001 becomes an error)", t_legacy_ends_with_any_a1_block)


def t_bad_legacy_policy():
    pol = copy.deepcopy(POLICY)
    pol["legacy_severity"] = {"XR008": "warning"}
    r = subprocess.run([sys.executable, str(HERE / "validate_xr_layer.py"), str(write("p.json", BASE)),
                        "--policy", str(write("pol.json", pol)), "--now", NOW], capture_output=True, text=True)
    assert r.returncode == 3, r.returncode


probe("legacy: legacy_severity may only name XR001 and PV001 (otherwise exit 3)", t_bad_legacy_policy)

# ------------------------------------------------------------------ base schema (patched) and the real pack


def t_real_pack_base():
    assert schema_errors(REAL, BASE_SCHEMA) == []
    assert schema_errors(REAL, original_base_schema()) == []


probe("base schema: the real v1 pack is valid before and after the patch", t_real_pack_base)


def t_example_base():
    errs = schema_errors(BASE, BASE_SCHEMA)
    assert errs == [], errs[:3]


probe("base schema: the A1 example is valid against the patched schema", t_example_base)


def t_patch_needed():
    msgs = [m for _, m in schema_errors(BASE, original_base_schema())]
    assert any("unexpected property 'xr_layer'" in m for m in msgs), msgs[:4]
    assert any("unexpected property 'provenance'" in m for m in msgs), msgs[:4]
    assert any("xr_clip" not in m and "must be one of" in m for m in msgs), "asset kind enum error expected"


probe("base schema: without the patch the example is rejected (closed root, kind enum)", t_patch_needed)


def t_patch_additive():
    assert set(BASE_SCHEMA["properties"]) - set(original_base_schema()["properties"]) == {"xr_layer", "provenance"}
    assert BASE_SCHEMA["additionalProperties"] is False, "the root must stay closed"
    assert "xr_layer" not in BASE_SCHEMA["required"] and "provenance" not in BASE_SCHEMA["required"], "must stay optional"


probe("base schema: the patch only adds optional properties and keeps the root closed", t_patch_additive)


def t_engine_covers_base():
    vxl.audit_schema(BASE_SCHEMA)
    vxl.audit_schema(json.loads((ROOT / "schemas" / "xr-layer.addendum.schema.json").read_text(encoding="utf-8")))


probe("engine: every keyword of both schemas is implemented", t_engine_covers_base)


def t_engine_units():
    assert schema_errors("abcd", {"type": "string", "maxLength": 3})
    assert not schema_errors("abc", {"type": "string", "maxLength": 3})
    names = {"type": "object", "propertyNames": {"enum": ["kk", "ru"]}, "additionalProperties": {"type": "array"}}
    assert not schema_errors({"kk": [], "ru": []}, names)
    assert schema_errors({"en": []}, names)


probe("engine: maxLength and propertyNames behave", t_engine_units)


def t_unknown_keyword_fail_closed():
    bad = copy.deepcopy(json.loads((ROOT / "schemas" / "xr-layer.addendum.schema.json").read_text(encoding="utf-8")))
    bad["$defs"]["xr_layer"]["oneOf"] = []
    r = subprocess.run([sys.executable, str(HERE / "validate_xr_layer.py"), str(write("p2.json", BASE)),
                        "--schema", str(write("bad-schema.json", bad)), "--now", NOW], capture_output=True, text=True)
    assert r.returncode == 3 and "unsupported keyword" in r.stderr, (r.returncode, r.stderr[:120])


probe("fail-closed: a schema keyword the engine does not implement is exit 3, not a silent pass", t_unknown_keyword_fail_closed)

# ------------------------------------------------------------------ schema level (XR002)

rule_probe("XR002: unexpected property", lambda p: setp(p, "xr_layer/foo", 1), {"XR002"}, 2)
rule_probe("XR002: enum violation (prediction)", lambda p: setp(p, "xr_layer/registration/prediction", "kalman"), {"XR002"}, 2)
rule_probe("XR002: boolean is not an integer", lambda p: setp(p, "xr_layer/audit/retention_days", True), {"XR002"}, 2)
rule_probe("XR002: bad timestamp", lambda p: setp(p, "provenance/terms_evidence/captured_at", "yesterday"), {"XR002"}, 2)
rule_probe("XR002: bad sha256", lambda p: setp(p, "provenance/terms_evidence/evidence_sha256", "abc"), {"XR002"}, 2)
rule_probe("XR002: T0 missing", lambda p: delp(p, "xr_layer/states/idle/tiers/T0"), {"XR002"}, 2)
rule_probe("XR002: duplicate device class",
           lambda p: setp(p, "xr_layer/device_classes", ["wired_xr_glasses", "wired_xr_glasses"]), {"XR002"}, 2)
rule_probe("XR002: number out of range (speed)", lambda p: setp(p, "xr_layer/registration/max_speed_mps", 9), {"XR002"}, 2)

# ------------------------------------------------------------------ XR rules

rule_probe("XR003: state without xr_layer entry", lambda p: delp(p, "xr_layer/states/speaking"), {"XR003"}, 2)


def m_unknown_state(p):
    p["xr_layer"]["states"]["ghost"] = copy.deepcopy(p["xr_layer"]["states"]["idle"])


rule_probe("XR003: xr_layer describes an unknown state", m_unknown_state, {"XR003"}, 2)


def m_glb_with_video(p):
    p["xr_layer"]["states"]["idle"]["tiers"]["T2"]["render"] = "glb"


rule_probe("XR005 + XR026: glb render pointing at a video asset", m_glb_with_video, {"XR005", "XR026"}, 2)


def m_no_media(p):
    new_asset(p, "ghost_asset", "image")
    p["xr_layer"]["states"]["idle"]["tiers"]["T1"]["asset"] = "ghost_asset"


rule_probe("XR005: asset without media descriptor", m_no_media, {"XR005"}, 2)


def m_orphan_media(p):
    p["xr_layer"]["media"]["orphan"] = {"kind": "image", "width": 64, "height": 64, "watermark_free": True}


rule_probe("XR005 + PV002: media descriptor without an asset", m_orphan_media, {"XR005", "PV002"}, 2)
rule_probe("XR005: manifest kind does not match the media kind",
           lambda p: asset(p, IDLE).__setitem__("kind", "expression_clip"), {"XR005"}, 2)
rule_probe("XR006: safe_state is not a pack state", lambda p: setp(p, "xr_layer/safe_state", "ghost"), {"XR006"}, 2)


def m_safe_t1(p):
    p["xr_layer"]["states"]["safe_idle"]["tiers"]["T1"] = {"asset": CARD}


rule_probe("XR006: safe_state carries T1", m_safe_t1, {"XR006"}, 2)
rule_probe("XR007: precise tolerance with telemetry-only registration",
           lambda p: setp(p, "xr_layer/registration/accepted_modes", ["telemetry"]), {"XR007"}, 2)
rule_probe("XR007 + XR008: precise tolerance on a state that may move",
           lambda p: setp(p, "xr_layer/states/greeting/motion", "any"), {"XR007", "XR008"}, 2)
rule_probe("XR008: error budget over tolerance (speed 2.0 m/s)",
           lambda p: setp(p, "xr_layer/registration/max_speed_mps", 2.0), {"XR008"}, 2)
rule_probe("XR008: little headroom is a warning (speed 1.4 m/s)",
           lambda p: setp(p, "xr_layer/registration/max_speed_mps", 1.4), {"XR008"}, 1)
rule_probe("XR009: stationary state without moving_fallback",
           lambda p: delp(p, "xr_layer/states/greeting/moving_fallback"), {"XR009"}, 2)
rule_probe("XR009: moving_fallback is itself stationary",
           lambda p: setp(p, "xr_layer/states/greeting/moving_fallback", "listening"), {"XR009"}, 2)
rule_probe("XR009: moving_fallback points to itself",
           lambda p: setp(p, "xr_layer/states/greeting/moving_fallback", "greeting"), {"XR009"}, 2)
rule_probe("XR010: hold_ms over the limit", lambda p: setp(p, "xr_layer/registration/loss_policy/hold_ms", 1500), {"XR010"}, 2)
rule_probe("XR010: hold_ms above the advisory is a warning",
           lambda p: setp(p, "xr_layer/registration/loss_policy/hold_ms", 600), {"XR010"}, 1)
rule_probe("XR011: flash rate over 3 Hz", lambda p: setp(p, "xr_layer/safety/max_flash_hz", 5), {"XR011"}, 2)
rule_probe("XR011: e-stop response too slow", lambda p: setp(p, "xr_layer/safety/estop_response_ms", 400), {"XR011"}, 2)
rule_probe("XR012: character bounds inside a keep-out volume",
           lambda p: setp(p, "xr_layer/states/greeting/tiers/T2/attach/offset_m/2", 0.0), {"XR012"}, 2)
rule_probe("XR012: bounds with min >= max",
           lambda p: setp(p, "xr_layer/states/idle/tiers/T2/bounds_m/min", [0.5, 0.5, 0.5]), {"XR012"}, 2)
rule_probe("XR013: blank AI disclosure label", lambda p: setp(p, "xr_layer/disclosure/labels/kk", "  "), {"XR013"}, 2)
rule_probe("XR014: caption angle below guideline", lambda p: setp(p, "xr_layer/safety/min_caption_angle_deg", 0.4), {"XR014"}, 1)
rule_probe("XR015: audit retention above policy", lambda p: setp(p, "xr_layer/audit/retention_days", 120), {"XR015"}, 1)
rule_probe("XR023: FOV coverage above heuristic", lambda p: setp(p, "xr_layer/safety/max_fov_coverage_pct", 40), {"XR023"}, 1)
rule_probe("XR024: tiers declared but only an audio device class listed",
           lambda p: setp(p, "xr_layer/device_classes", ["audio_ai_glasses"]), {"XR024"}, 1)
rule_probe("XR020: media carries a watermark",
           lambda p: setp(p, "xr_layer/media/%s/watermark_free" % CARD, False), {"XR020"}, 2)
rule_probe("XR020: fps not allowed", lambda p: setp(p, "xr_layer/media/%s/fps" % IDLE, 60), {"XR020"}, 2)
rule_probe("XR020: video declares audio", lambda p: setp(p, "xr_layer/media/%s/has_audio" % IDLE, True), {"XR020"}, 2)
rule_probe("XR020: loop outside the clip", lambda p: setp(p, "xr_layer/media/%s/loop/out_ms" % IDLE, 5000), {"XR020"}, 2)


def m_big_frame(p):
    m = p["xr_layer"]["media"][IDLE]
    m["width"], m["height"] = 4096, 2048


rule_probe("XR020: frame too large", m_big_frame, {"XR020"}, 2)
rule_probe("XR020: packed_lr needs an even width", lambda p: setp(p, "xr_layer/media/%s/width" % IDLE, 1535), {"XR020"}, 2)
rule_probe("XR020: video without alpha_mode", lambda p: delp(p, "xr_layer/media/%s/alpha_mode" % IDLE), {"XR020"}, 2)
rule_probe("XR021: XR media over the byte budget",
           lambda p: asset(p, IDLE).__setitem__("bytes", 50000000), {"XR021"}, 2)
rule_probe("XR021: missing bytes is a warning", lambda p: asset(p, CARD).pop("bytes"), {"XR021"}, 1)
rule_probe("XR022: viseme_lite without viseme assets",
           lambda p: setp(p, "xr_layer/states/speaking/tiers/T2/speaking_driver", "viseme_lite"), {"XR022"}, 2)
rule_probe("XR022: viseme assets while driver is not viseme_lite",
           lambda p: setp(p, "xr_layer/states/idle/tiers/T2/viseme_assets", [CARD]), {"XR022"}, 2)


def t_viseme_ok():
    p = copy.deepcopy(BASE)
    for i in range(3):
        mid = "aru_mouth_%d" % i
        new_asset(p, mid, "image")
        p["xr_layer"]["media"][mid] = {"kind": "image", "width": 256, "height": 256, "watermark_free": True}
        p["provenance"]["generation"]["jobs"][0]["produces_assets"].append(mid)
    t2 = p["xr_layer"]["states"]["speaking"]["tiers"]["T2"]
    t2["speaking_driver"] = "viseme_lite"
    t2["viseme_assets"] = ["aru_mouth_0", "aru_mouth_1", "aru_mouth_2"]
    rep = check(p)
    assert rep.findings == [], [(f.rule, f.pointer, f.message) for f in rep.findings]


probe("viseme_lite with three mouth sprites passes", t_viseme_ok)


def m_misaligned_lines(p):
    state(p, "greeting")["voice_lines"]["kk"].append("Қосымша жол")


rule_probe("XR025: voice_lines arrays differ in length across locales", m_misaligned_lines, {"XR025"}, 2)


def glb_variant(p):
    new_media = {"kind": "glb", "triangles": 20000, "texture_max_px": 1024, "watermark_free": True}
    p["xr_layer"]["media"]["aru_xr_bundle"] = new_media
    asset(p, "aru_xr_bundle")["bytes"] = 3000000  # the real sample keeps placeholder size 0, which XR021 flags
    p["provenance"]["hand_made_assets"] = ["aru_xr_bundle"]
    t2 = p["xr_layer"]["states"]["speaking"]["tiers"]["T2"]
    t2["render"], t2["asset"] = "glb", "aru_xr_bundle"


def t_glb_ok():
    p = copy.deepcopy(BASE)
    glb_variant(p)
    rep = check(p)
    assert rep.findings == [], [(f.rule, f.pointer, f.message) for f in rep.findings]


probe("glb path: T2 glb equal to the v1 xr.bundle_asset passes", t_glb_ok)


def m_glb_mismatch(p):
    glb_variant(p)
    state(p, "speaking")["xr"]["bundle_asset"] = "aru_idle_clip"


rule_probe("XR026: T2 glb differs from the v1 xr.bundle_asset of the state", m_glb_mismatch, {"XR026"}, 2)

# ------------------------------------------------------------------ PV rules


def m_uncovered(p):
    p["provenance"]["generation"]["jobs"][0]["produces_assets"].remove(WALK)


rule_probe("PV002: XR asset not covered by provenance", m_uncovered, {"PV002"}, 2)


def m_two_jobs(p):
    j = copy.deepcopy(p["provenance"]["generation"]["jobs"][0])
    j["job_id"] = "demo-job-002"
    j["produces_assets"] = [CARD]
    p["provenance"]["generation"]["jobs"].append(j)


rule_probe("PV002: asset claimed by two jobs", m_two_jobs, {"PV002"}, 2)
rule_probe("PV003: commercial use not confirmed", lambda p: setp(p, "provenance/terms_evidence/commercial_use", False), {"PV003"}, 2)
rule_probe("PV003: outputs not watermark-free", lambda p: setp(p, "provenance/terms_evidence/watermark_free", False), {"PV003"}, 2)
rule_probe("PV003: free plan", lambda p: setp(p, "provenance/terms_evidence/plan_kind", "free"), {"PV003"}, 2)
rule_probe("PV003: terms captured far from generation is a warning",
           lambda p: setp(p, "provenance/terms_evidence/captured_at", "2026-07-01T00:00:00Z"), {"PV003"}, 1)
rule_probe("PV004: similarity screening failed", lambda p: setp(p, "provenance/similarity_screening/result", "fail"), {"PV004"}, 2)


def m_manual_rejected(p):
    p["provenance"]["similarity_screening"]["result"] = "manual_review"
    p["provenance"]["human_review"]["decision"] = "rejected"


rule_probe("PV004 + PV005: manual review without approval", m_manual_rejected, {"PV004", "PV005"}, 2)


def t_manual_approved():
    p = copy.deepcopy(BASE)
    p["provenance"]["similarity_screening"]["result"] = "manual_review"
    rep = check(p)
    assert rep.findings == [] and rep.code == 0


probe("manual_review with an approval passes", t_manual_approved)
rule_probe("PV005: human review rejected", lambda p: setp(p, "provenance/human_review/decision", "rejected"), {"PV005"}, 2)
rule_probe("PV006: no machine-readable marking (KZ) is an error",
           lambda p: setp(p, "provenance/content_credentials/machine_readable", False), {"PV006"}, 2)


def m_marking_eu(p):
    p["xr_layer"]["jurisdictions"] = ["DE"]
    p["provenance"]["content_credentials"]["machine_readable"] = False


rule_probe("PV006: outside KZ the same gap is a warning", m_marking_eu, {"PV006"}, 1)


def m_ref_unknown(p):
    p["provenance"]["generation"]["jobs"][0]["reference_inputs"] = [{"sha256": h("r"), "rights": "unknown"}]


rule_probe("PV007: reference input with unknown rights", m_ref_unknown, {"PV007"}, 2)


def m_ref_licensed(p):
    p["provenance"]["generation"]["jobs"][0]["reference_inputs"] = [{"sha256": h("r"), "rights": "licensed"}]


rule_probe("PV007: licensed reference without evidence", m_ref_licensed, {"PV007"}, 2)
rule_probe("PV008: platform may train on prompts (default plan)",
           lambda p: setp(p, "provenance/terms_evidence/training_exposure", "default_may_train"), {"PV008"}, 1)
rule_probe("PV009: timestamp in the future",
           lambda p: setp(p, "provenance/terms_evidence/captured_at", "2026-12-01T00:00:00Z"), {"PV009", "PV003"}, 2)
rule_probe("PV009: review precedes screening",
           lambda p: setp(p, "provenance/human_review/reviewed_at", "2026-10-01T10:00:00Z"), {"PV009"}, 2)
rule_probe("PV010: platform other without a name", lambda p: setp(p, "provenance/generation/platform", "other"), {"PV010"}, 2)
rule_probe("PV011: no human authorship evidence", lambda p: delp(p, "provenance/human_authorship"), {"PV011"}, 1)
rule_probe("PV012: job without prompt hash",
           lambda p: delp(p, "provenance/generation/jobs/0/prompt_sha256"), {"PV012"}, 1)

# ------------------------------------------------------------------ waivers


def _warn_pack():
    p = copy.deepcopy(BASE)
    p["provenance"]["terms_evidence"]["training_exposure"] = "default_may_train"
    return p


def t_waiver_ok():
    rep0 = check(_warn_pack())
    fp = fps(rep0, "PV008")[0]
    rep = check(_warn_pack(), [waiver("PV008", fp)])
    assert rep.code == 0, "exit %d" % rep.code
    assert [f for f in rep.findings if f.rule == "PV008" and f.waiver is not None], "finding not marked waived"


probe("waiver: valid waiver suppresses a warning (exit 0)", t_waiver_ok)


def t_waiver_cases():
    fp = fps(check(_warn_pack()), "PV008")[0]
    cases = [
        ("expired", waiver("PV008", fp, expires="2026-10-01")),
        ("approver in the creator organisation", waiver("PV008", fp, approver_org="distributor-demo")),
        ("wrong pack_version", waiver("PV008", fp, pack_version="9.9.9")),
        ("reason too short", waiver("PV008", fp, reason="ok")),
        ("horizon over 90 days", waiver("PV008", fp, expires="2027-06-01")),
    ]
    for label, w in cases:
        rep = check(_warn_pack(), [w])
        assert live(rep) == {"PV008", "WV001"}, "%s: rules %s" % (label, sorted(live(rep)))
        assert rep.code == 1, "%s: exit %d" % (label, rep.code)


probe("waiver: expired / creator-approved / wrong version / short reason / long horizon are rejected", t_waiver_cases)


def t_waiver_nonwaivable():
    p = copy.deepcopy(BASE)
    p["provenance"]["human_review"]["decision"] = "rejected"
    fp = fps(check(p), "PV005")[0]
    rep = check(p, [waiver("PV005", fp)])
    assert live(rep) == {"PV005", "WV001"} and rep.code == 2


probe("waiver: a non-waivable rule stays blocking", t_waiver_nonwaivable)


def t_waiver_stale():
    rep = check(copy.deepcopy(BASE), [waiver("PV008", "0123456789abcdef")])
    assert live(rep) == {"WV001"} and rep.code == 1


probe("waiver: a stale waiver is reported (warning)", t_waiver_stale)


def t_waiver_malformed():
    w = waiver("PV008", "0123456789abcdef")
    del w["approver"]
    rep = check(copy.deepcopy(BASE), [w])
    assert live(rep) == {"WV001"} and rep.code == 1


probe("waiver: a malformed waiver is reported", t_waiver_malformed)


def t_waiver_error_rule():
    p = copy.deepcopy(BASE)
    p["xr_layer"]["registration"]["max_speed_mps"] = 2.0
    rep0 = check(p)
    ids = fps(rep0, "XR008")
    assert len(ids) == 2, "expected two XR008 findings, got %d" % len(ids)
    one = check(p, [waiver("XR008", ids[0])])
    assert one.code == 2, "one of two errors waived must still block, exit %d" % one.code
    both = check(p, [waiver("XR008", i) for i in ids])
    assert both.code == 0, "exit %d" % both.code


probe("waiver: a waivable error needs one waiver per finding", t_waiver_error_rule)

# ------------------------------------------------------------------ SARIF, determinism, consistency


def t_sarif():
    p = _warn_pack()
    fp = fps(check(p), "PV008")[0]
    rep = check(p, [waiver("PV008", fp)])
    s = vxl.to_sarif(rep)
    run = s["runs"][0]
    assert s["version"] == "2.1.0" and len(run["results"]) == len(rep.findings)
    sup = [r for r in run["results"] if r.get("suppressions")]
    assert len(sup) == 1 and sup[0]["suppressions"][0]["kind"] == "external"
    assert all(r["partialFingerprints"]["charpack/v1"] for r in run["results"])
    assert run["invocations"][0]["executionSuccessful"] is True
    for k in ("validator_sha256", "schema_sha256", "policy_sha256", "input_sha256", "policy_version"):
        assert run["properties"].get(k), "missing digest %s" % k
    assert not run["artifacts"][0]["location"]["uri"].startswith("/"), "absolute path leaked into SARIF"


probe("SARIF: structure, suppression, fingerprints, digests, relative path", t_sarif)


def t_deterministic():
    a = json.dumps(vxl.to_sarif(check(_warn_pack())), sort_keys=True)
    b = json.dumps(vxl.to_sarif(check(_warn_pack())), sort_keys=True)
    assert a == b


probe("determinism: two runs give byte-identical SARIF", t_deterministic)


def t_policy_matches_rules():
    assert set(POLICY["waivable"]) == set(vxl.RULES), "policy.waivable and RULES differ: %s" % (
        sorted(set(POLICY["waivable"]) ^ set(vxl.RULES)))


probe("consistency: policy.waivable covers exactly the rule catalogue", t_policy_matches_rules)


def t_schema_refs():
    text = (ROOT / "schemas" / "xr-layer.addendum.schema.json").read_text(encoding="utf-8")
    schema = json.loads(text)
    refs = set(re.findall(r'"\$ref":\s*"([^"]+)"', text))
    assert refs
    for r in refs:
        vxl._resolve_ref(schema, r)


probe("consistency: every $ref in the schema resolves", t_schema_refs)


def t_spec_lists_rules():
    spec = ROOT / "docs" / "character-pack-spec-v1.md"
    if not spec.exists():
        return
    ids = set(re.findall(r"\b((?:XR|PV|WV)\d{3})\b", spec.read_text(encoding="utf-8")))
    missing = sorted(set(vxl.RULES) - ids)
    assert not missing, "spec does not mention rules: %s" % missing


probe("consistency: the spec mentions every rule id", t_spec_lists_rules)

# ------------------------------------------------------------------ fail-closed and CLI


def cli(*args):
    return subprocess.run([sys.executable, str(HERE / "validate_xr_layer.py")] + list(args),
                          cwd=str(ROOT), capture_output=True, text=True)


def t_cli_codes():
    ex = "examples/sample_character_pack_xr.json"
    assert cli(ex, "--now", NOW).returncode == 0
    bad = copy.deepcopy(BASE)
    bad["xr_layer"]["registration"]["max_speed_mps"] = 2.0
    assert cli(str(write("bad.json", bad)), "--now", NOW).returncode == 2
    assert cli(str(write("warn.json", _warn_pack())), "--now", NOW).returncode == 1
    assert cli("examples/sample_character_pack.json", "--now", NOW).returncode == 1, "real legacy pack"


probe("CLI: exit codes 0 / 1 / 2, and 1 for the real legacy pack", t_cli_codes)


def t_infra_cases():
    ex = "examples/sample_character_pack_xr.json"
    assert cli(str(write("broken.json", "{not json")), "--now", NOW).returncode == 3, "invalid JSON"
    assert cli(str(write("dup.json", '{"a": 1, "a": 2}')), "--now", NOW).returncode == 3, "duplicate keys"
    assert cli(str(write("nan.json", '{"a": NaN}')), "--now", NOW).returncode == 3, "NaN constant"
    assert cli(str(TMP / "missing.json"), "--now", NOW).returncode == 3, "missing pack"
    assert cli(ex, "--schema", str(TMP / "none.json"), "--now", NOW).returncode == 3, "missing schema"
    assert cli(ex, "--policy", str(TMP / "none.json"), "--now", NOW).returncode == 3, "missing policy"
    assert cli(ex, "--now", "03.10.2026").returncode == 3, "bad --now"
    pol = copy.deepcopy(POLICY)
    del pol["limits"]["max_flash_hz"]
    assert cli(ex, "--policy", str(write("pol.json", pol)), "--now", NOW).returncode == 3, "policy without a limit"
    assert cli(ex, "--waivers", str(write("w.json", '"nope"')), "--now", NOW).returncode == 3, "waivers wrong shape"


probe("fail-closed: unreadable or malformed input, schema, policy, waivers -> exit 3", t_infra_cases)


def t_infra_sarif():
    out = TMP / "infra.sarif"
    r = cli(str(write("broken2.json", "{not json")), "--sarif", str(out), "--now", NOW)
    assert r.returncode == 3
    s = json.loads(out.read_text(encoding="utf-8"))
    inv = s["runs"][0]["invocations"][0]
    assert inv["executionSuccessful"] is False and inv["toolExecutionNotifications"], "infra failure must be visible in SARIF"


probe("fail-closed: an infrastructure failure is recorded in SARIF as unsuccessful", t_infra_sarif)


def t_internal_error():
    saved = vxl.XR_RULES

    def boom(c, out):
        raise RuntimeError("simulated bug")

    vxl.XR_RULES = (boom,)
    try:
        code = vxl.main([str(write("pack.json", copy.deepcopy(BASE))), "--now", NOW])
    finally:
        vxl.XR_RULES = saved
    assert code == 3, "a crashing rule must give exit 3, got %d" % code


probe("fail-closed: a crashing rule is exit 3, never a pass", t_internal_error)


def t_list_rules():
    r = cli("--list-rules")
    assert r.returncode == 0
    for rid in vxl.RULES:
        assert rid in r.stdout, rid


probe("CLI: --list-rules prints the whole catalogue", t_list_rules)

# ------------------------------------------------------------------ summary

total = PASSED + FAILED
print("\n%d/%d probes passed" % (PASSED, total))
sys.exit(0 if FAILED == 0 else 1)
