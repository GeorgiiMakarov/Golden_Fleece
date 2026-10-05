# Benchmarks

Measured 2026-09-24 on a Linux sandbox (Ubuntu 24.04, CPython 3.x). These are
order-of-magnitude numbers for the headless / synthetic path — not on-device
guarantees. On-device latency comes from the gesture-dataset milestone (rev3 §6).

## Gesture pipeline (synthetic 26-joint stream, 945 frames)

| Metric | Measured | Budget |
|---|---|---|
| Max classify time per frame (Python reference, `tools/check_pipeline.py`) | 0.03 ms | 100 ms |
| Max classify time per frame (C# emulator, `dotnet run`, incl. first-frame JIT) | 3.7–5.8 ms | 100 ms |
| Max switch dispatch (C# emulator) | 0.23 ms | 100 ms |
| Reference pipeline, end-to-end (945 frames) | 0.09 s | — |
| Peak RSS, reference pipeline process | 18 MB | — |

The C# headless testbench (`unity/LayerSwitcherEmulator`, `dotnet run`) enforces
the same 100 ms budget per frame in CI; its self-reported numbers are printed
in the CI log on every push. First compiled and run 2026-09-24: ALL PASS
(945 frames, SwipeRight@2344ms / SwipeLeft@4344ms / PalmHold@6800ms, 0 false
positives).

## Character Pack validator

| Metric | Measured |
|---|---|
| `validate_character_pack.py` on the reference pack «Ару» (CLI end-to-end, incl. interpreter startup) | ~170 ms/pack |
| Probe matrix `test_character_pack.py` (15 positive/negative probes) | 2.6 s |
| Peak RSS, validator process | 25 MB |

## Merkle audit batch (Defense-Dossier, pure stdlib, synthetic leaves)

| Leaves | Anchor (build tree) | Proof + verify (1 leaf) |
|---|---|---|
| 100 | 0.3 ms | 0.02 ms |
| 1,000 | 2.4 ms | 0.02 ms |
| 10,000 | 25 ms | 0.04 ms |

Anchor cost grows ~linearly; proof size and verify time grow logarithmically.

## What is NOT measured here

- **Sidecar footprint** (`<5% CPU / <200 MB RAM` in `docs/integration-contract-sidecar.md`)
  is a contractual integration limit, not a measured number — no target
  hardware yet.
- **Unity compile / on-device frame time** — pending milestone 1 hardware.
- **Production signatures** (ЭЦП НУЦ РК, RFC 3161 TSA) are not in the measured
  path; the demo uses `mock_ecp_sign` / `mock_rfc3161_timestamp` with a
  documented seam for the real ones.
