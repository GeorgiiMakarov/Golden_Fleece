#!/usr/bin/env python3
"""E2E stack check: character pack -> XR events -> decision core -> Defense-Dossier.

Scenario: ONE billable robot interaction.
  genie.visitor_detected -> pack 'pack_aru_guide' resolves state 'greeting'
  -> trusted edge emits PresetServed / ImpressionValidated / PresetClosed
  -> events validated against schemas/xr-event.schema.json (ENGINE repo)
  -> projected to UpdateMetric per docs/domain-profiles/xr-adtech-profile.md
  -> decision-intelligence-core, in-process (in-memory adapters = same
     application code that runs against Kafka/Redis in production)
  -> Defense-Dossier Merkle anchoring of the billing records

Glue code (pack trigger resolution, XR event construction, billing rule)
is THIS script. Repo code does: schema validation, evidence/idempotency
rules, scoring, Merkle anchoring.

Usage (from the ENGINE repo root):
    python3 tools/e2e_stack_check.py [--dic PATH] [--dd PATH]

Defaults assume sibling checkouts:
    ../decision-intelligence-core   (or $DIC_PATH)
    ../Defense-Dossier              (or $DD_PATH)
Pack and xr-event.schema.json are read from THIS repo.
"""
import argparse
import asyncio
import hashlib
import importlib.util
import json
import os
import sys
import time
import uuid
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

_ap = argparse.ArgumentParser()
_ap.add_argument("--dic", default=os.environ.get(
    "DIC_PATH", str(REPO_ROOT.parent / "decision-intelligence-core")),
    help="decision-intelligence-core checkout")
_ap.add_argument("--dd", default=os.environ.get(
    "DD_PATH", str(REPO_ROOT.parent / "Defense-Dossier")),
    help="Defense-Dossier checkout")
_args = _ap.parse_args()

DIC = Path(_args.dic)
DD_DEMO = Path(_args.dd) / "defense_dossier_demo.py"
for p, label in ((DIC, "decision-intelligence-core"), (DD_DEMO, "defense_dossier_demo.py")):
    if not p.exists():
        sys.exit(f"FAIL: {label} not found at {p} (use --dic/--dd or env)")

import jsonschema

sys.path.insert(0, str(DIC))
from application.command_handler import CommandHandler, UpdateMetricCommand
from application.merkle import MerkleBatchBuilder
from application.merkle_anchor_service import MerkleAnchorService
from application.scoreboard_projector import ScoreboardProjector
from infrastructure.ed25519_signer import Ed25519Signer
from infrastructure.memory_adapters import (
    MemoryAuditTrail, MemoryEventBus, MemoryProjectionStore, MemoryTsa,
)

# Defense-Dossier demo machinery (import by path; __main__ guard keeps it safe)
_spec = importlib.util.spec_from_file_location("dd", str(DD_DEMO))
dd = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(dd)

PACK = json.loads((REPO_ROOT / "examples" / "sample_character_pack.json").read_text())
XR_SCHEMA = json.loads((REPO_ROOT / "schemas" / "xr-event.schema.json").read_text())
XR_VALIDATOR = jsonschema.Draft202012Validator(XR_SCHEMA)

DWELL_FLOOR_MS = 1200   # profile section 5
V_MAX_MPS = 1.2         # profile section 5 (placeholder, documented)

ok_count = 0
total_count = 0


def check(name, cond, detail=""):
    global ok_count, total_count
    total_count += 1
    print(f"[{'OK' if cond else 'FAIL'}] {name}" + (f" — {detail}" if detail else ""))
    if cond:
        ok_count += 1
    return cond


def make_event(event_type, session_id, preset_id, evidence_refs, **extra):
    return {
        "event_id": str(uuid.uuid4()),
        "event_type": event_type,
        "schema_version": "1.0",
        "occurred_at_phone_ms": int(time.time() * 1000),
        "session_id": session_id,
        "preset_id": preset_id,
        "evidence_refs": evidence_refs,
        **extra,
    }


def project(event, decision_id):
    """xr-adtech-profile section 3: XR event -> UpdateMetric."""
    metric = {"PresetServed": "xr.preset_served",
              "ImpressionValidated": "xr.impression_validated",
              "PresetClosed": "xr.preset_closed"}[event["event_type"]]
    value = {"PresetServed": "SERVED", "ImpressionValidated": "VALIDATED",
             "PresetClosed": "TIMELINE_END"}[event["event_type"]]
    ctx = {"domain_profile": "xr-adtech-v1",
           "session_id": event["session_id"], "preset_id": event["preset_id"]}
    if event["event_type"] == "ImpressionValidated":
        ctx["dwell_ms"] = event["dwell_duration_ms"]
    return UpdateMetricCommand(
        decision_id=decision_id, metric_name=metric, value=value,
        idempotency_key=event["event_id"], evidence_refs=event["evidence_refs"],
        proposed_by="qazbot-edge",
        runtime_context=ctx,
    )


async def main():
    # ---- 1. pack trigger resolution (glue) ----
    trigger = "genie.visitor_detected"
    state = next(s for s in PACK["states"] if s["trigger"] == trigger)
    check("pack resolves genie.visitor_detected -> 'greeting'",
          state["id"] == "greeting", f"surfaces={PACK['target_surfaces']}")
    print(f"     voice (ru): {state['voice_lines']['ru'][0]}")

    session_id = f"qaz-{uuid.uuid4().hex[:8]}"
    preset_id = "preset_aru_greeting"
    decision_id = "qazbot-pilot-001"

    events = [
        make_event("PresetServed", session_id, preset_id,
                   ["trigger:genie.visitor_detected", "pack:pack_aru_guide:1.1.0"],
                   trigger="schedule", manifest_version="1.0",
                   started_at_phone_ms=int(time.time() * 1000),
                   planned_duration_ms=state["timing"]["duration_ms"]),
        make_event("ImpressionValidated", session_id, preset_id,
                   ["dwell:preset_aru_greeting:8200ms", "stability:imu_v_max:0.4mps"],
                   dwell_duration_ms=8200, head_stable=True, timeline_coverage_pct=95.0),
        make_event("PresetClosed", session_id, preset_id,
                   [f"session:{session_id}:closed"],
                   reason="timeline_end", watched_duration_ms=8200, completed=True),
    ]

    # ---- 2. schema validation (ENGINE repo contract) ----
    all_valid = True
    for e in events:
        errs = list(XR_VALIDATOR.iter_errors(e))
        all_valid &= not errs
    check("3 XR events validate against xr-event.schema.json v1", all_valid)

    # sub-floor dwell is a VALID event now (policy, not wire contract, decides billing)
    sub = make_event("ImpressionValidated", session_id, preset_id, ["dwell:x:800ms"],
                     dwell_duration_ms=800, head_stable=True, timeline_coverage_pct=20.0)
    check("schema accepts sub-floor dwell 800ms (recorded, billing decides)",
          not list(XR_VALIDATOR.iter_errors(sub)))
    zero = make_event("ImpressionValidated", session_id, preset_id, ["dwell:x:0ms"],
                      dwell_duration_ms=0, head_stable=True, timeline_coverage_pct=0.0)
    check("schema still rejects dwell 0ms (technical minimum 1)",
          bool(list(XR_VALIDATOR.iter_errors(zero))))
    bad2 = make_event("ImpressionValidated", session_id, preset_id, [],
                      dwell_duration_ms=8200, head_stable=True, timeline_coverage_pct=95.0)
    check("schema rejects empty evidence_refs (I3 boundary)",
          bool(list(XR_VALIDATOR.iter_errors(bad2))))

    # ---- 3. decision core (in-process, in-memory adapters) ----
    audit, bus, store = MemoryAuditTrail(), MemoryEventBus(), MemoryProjectionStore()
    builder = MerkleBatchBuilder(max_leaves=100, max_seconds=9999)
    handler = CommandHandler(audit_trail=audit, event_bus=bus, merkle_builder=builder,
                             signer=Ed25519Signer(key_id="test"), tsa=MemoryTsa())
    anchor = MerkleAnchorService(builder=builder, audit_trail=audit,
                                 projection_store=store,
                                 signer=Ed25519Signer(key_id="test"), tsa=MemoryTsa())
    projector = ScoreboardProjector(audit_trail=audit, projection_store=store)

    results = []
    for e in events:
        r = await handler.handle_update_metric(project(e, decision_id))
        results.append((e["event_type"], r.status.value, r.leaf_hash))
    check("core ACCEPTED all 3 metrics",
          all(s == "ACCEPTED" for _, s, _ in results),
          ", ".join(f"{t}={s}" for t, s, _ in results))

    # I6: redeliver the impression -> must be ignored, no second leaf
    dup = await handler.handle_update_metric(project(events[1], decision_id))
    check("I6: duplicate event_id -> DUPLICATE_IGNORED",
          dup.status.value == "DUPLICATE_IGNORED" and dup.leaf_hash is None)

    # I3 at the core boundary: empty evidence straight to the handler
    cmd = project(events[1], decision_id)
    cmd = UpdateMetricCommand(decision_id=cmd.decision_id, metric_name=cmd.metric_name,
                              value=cmd.value, idempotency_key=str(uuid.uuid4()),
                              evidence_refs=[], proposed_by=cmd.proposed_by,
                              runtime_context=cmd.runtime_context)
    rej = await handler.handle_update_metric(cmd)
    check("I3: metric without evidence -> REJECTED_MISSING_EVIDENCE",
          rej.status.value == "REJECTED_MISSING_EVIDENCE")

    # billing rule (xr-adtech-profile section 5 — policy level, not wire contract)
    def is_billable(dwell_ms, imu_v_max_mps):
        return dwell_ms >= DWELL_FLOOR_MS and imu_v_max_mps <= V_MAX_MPS

    check("billing rule: dwell 8200>=1200, imu 0.4<=1.2 -> BILLABLE",
          is_billable(8200, 0.4))
    check("billing rule: dwell 800<1200 -> recorded but NOT billable",
          not is_billable(800, 0.4))

    # ---- 4. Merkle anchor + scoreboard (core) ----
    root = await anchor.force_close()
    check("Merkle batch anchored, root present", bool(root), f"root={str(root)[:16]}...")
    sb = await projector.rebuild(decision_id,
                                 required_metrics=["xr.preset_served",
                                                   "xr.impression_validated",
                                                   "xr.preset_closed"])
    check("scoreboard rebuilt with 3 metrics",
          all(m in sb.metrics for m in ("xr.preset_served",
                                        "xr.impression_validated",
                                        "xr.preset_closed")),
          f"verdict={sb.policy_verdict}, trust={sb.trust_score}")

    # ---- 5. Defense-Dossier: anchor billing records, prove, tamper ----
    leaves = []
    for et, status, leaf_hash in results:
        leaf = dd.Leaf(submitter_id="qazbot-fleet", role="fleet_operator", period=1,
                       content={"event_type": et, "status": status,
                                "core_leaf": leaf_hash, "dwell_ms": 8200,
                                "billable": True, "pack": "pack_aru_guide:1.1.0"},
                       claimed_date="2026-09-23")
        leaf.signature = dd.mock_ecp_sign(leaf.submitter_id, leaf.content_hash())
        leaves.append(leaf)
    hashes = [l.leaf_hash() for l in leaves]
    levels = dd.build_merkle_tree(hashes)
    dd_root = levels[-1][0]
    imp_idx = 1
    proof = dd.merkle_proof(levels, imp_idx)
    check("Defense-Dossier: Merkle proof for impression leaf verifies",
          dd.verify_merkle_proof(hashes[imp_idx], proof, dd_root))
    tampered = dd.Leaf(submitter_id=leaves[imp_idx].submitter_id,
                       role=leaves[imp_idx].role, period=1,
                       content={**leaves[imp_idx].content, "dwell_ms": 100,
                                "billable": False},
                       claimed_date="2026-09-23",
                       ingestion_ts=leaves[imp_idx].ingestion_ts,
                       signature=leaves[imp_idx].signature)
    check("Defense-Dossier: tampered leaf (dwell 8200->100) rejected",
          not dd.verify_merkle_proof(tampered.leaf_hash(), proof, dd_root))

    # ---- 6. Ad template pack: consent -> personalized / fallback path ----
    # Decision + composition come from the reference composer
    # (tools/reference_composer.py) — single source of truth, no glue drift.
    sys.path.insert(0, str(REPO_ROOT / "tools"))
    from validate_template_pack import validate as validate_tpl
    from reference_composer import compose as compose_ad
    TPL = json.loads((REPO_ROOT / "examples" / "sample_template_pack.json").read_text())
    PROFILE = json.loads((REPO_ROOT / "examples" / "sample_profile.json").read_text())
    CSR_SCHEMA = json.loads((REPO_ROOT / "schemas" / "consent-receipt.schema.json").read_text())
    EVT_SCHEMA = json.loads((REPO_ROOT / "schemas" / "consent-event.schema.json").read_text())
    csr_v = jsonschema.Draft7Validator(CSR_SCHEMA)
    evt_v = jsonschema.Draft7Validator(EVT_SCHEMA)
    tpl_errors = validate_tpl(TPL)
    check("ad template pack validates via validate_template_pack.validate "
          "(schema + P1 + level/slots + manifest_sha256)",
          not tpl_errors, "; ".join(tpl_errors[:2]))
    CSR = json.loads((REPO_ROOT / "examples" / "sample_consent_receipt.json").read_text())
    GRANT = json.loads((REPO_ROOT / "examples" / "sample_consent_granted.json").read_text())
    check("consent receipt + ConsentGranted validate",
          not list(csr_v.iter_errors(CSR)) and not list(evt_v.iter_errors(GRANT)))

    # Deterministic demo time: inside the sample receipt window,
    # after ConsentGranted (10:00), before ConsentRevoked (12:30).
    AD_NOW = "2026-10-02T11:00:00+06:00"

    man_fb = compose_ad(TPL, PROFILE, None, now=AD_NOW)
    ad_fallback = make_event("ImpressionValidated", session_id, "preset_ad_fallback_01",
                             ["pack:tpl_demo_welcome_01:1.0.0", "served:fallback",
                              "personalized:false"],
                             dwell_duration_ms=2000, head_stable=True,
                             timeline_coverage_pct=90.0)
    check("ad path: no consent -> fallback only, personalized=false, event valid",
          man_fb["mode"] == "fallback" and not man_fb["personalized"]
          and man_fb.get("fallback_served")
          and not list(XR_VALIDATOR.iter_errors(ad_fallback)))

    man = compose_ad(TPL, PROFILE, CSR, now=AD_NOW)
    filled = {s["slot_id"]: s["value"] for s in man["filled_slots"]}
    check("ad path: valid consent -> personalized manifest, slots filled, hash + receipt id",
          man["mode"] == "personalized" and man["personalized"]
          and len(man["instance_hash"]) == 64
          and man["consent_receipt_id"] == CSR["receipt_id"]
          and filled.get("cup_name") == PROFILE["display_name"])

    print(f"\n{ok_count}/{total_count} checks green")
    return ok_count == total_count


if __name__ == "__main__":
    sys.exit(0 if asyncio.run(main()) else 1)
