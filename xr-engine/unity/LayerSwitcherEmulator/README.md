# LayerSwitcher Emulator

Headless test bench for the Phase 1 gesture pipeline. Replays a synthetic
26-joint hand stream (JSON) through the **same gesture math as the Unity
runtime** and scores the result against the 100 ms switch budget.

No Unity, no headset, no dataset needed — the studio runs this on day one.

## Run

Requires the .NET 8 SDK:

```bash
cd unity/LayerSwitcherEmulator
dotnet run -- data/sample_manifest_demo.json data/sample_gesture_stream.json
```

Exit code `0` = all checks PASS, `1` = any FAIL.

## What it does

1. Loads a layer manifest (`schemas/xr-layer-manifest.schema.json` contract).
   Refuses to run when `gestureDictionaryVersion` ≠ runtime `v1`.
2. Replays frames in timestamp order:
   - video-clock spawns (`spawnAtMs`) drive `LayerSwitcherModel.ShowLayer`
     (Phase 2 behavior; Phase 1 uses the manual test hook);
   - palm positions (joint 0) feed `SwipeDetector` + `PalmHoldDetector`;
   - detections dispatch into `LayerSwitcherModel.OnGesture` (timed).
3. Scores the run:
   - each labeled segment (`swipe_right`, `palm_hold`, …) must produce
     **exactly one** expected detection inside its window (+300 ms grace);
   - negative segments (`slow_swipe`, `short_swipe`, `idle`) must produce **zero**;
   - max per-frame classify time and max switch dispatch are reported
     against the 100 ms budget.

Sample output:

```
[  1000 ms] clock -> spawn layer_01 (visible layer 0)
[  2344 ms] SwipeRight -> visible layer 1 (dispatch 0.229 ms)
...
[PASS] swipe_right: expected 1x SwipeRight, got 1
[PASS] false positives: 0
max classify/frame: 3.664 ms, max switch dispatch: 0.229 ms (budget 100 ms) -> PASS
RESULT: ALL PASS
```
(measured 2026-09-24, dotnet 8.0.425, Linux sandbox; classify max includes first-frame JIT)

## Files

| File | Purpose |
|---|---|
| `GestureMath.cs` | Pure-C# port of `SwipeClassifier` / `PalmHoldClassifier` (1:1 state machines, same v1 tuning). The Unity MonoBehaviours should delegate to this — refactor pending first Unity compile. |
| `LayerSwitcherModel.cs` | Headless `LayerSwitcher` index semantics + timed dispatch. |
| `Dto.cs` | Manifest + stream DTOs, no UnityEngine dependency. |
| `Program.cs` | Replay loop + PASS/FAIL report. |
| `data/sample_manifest_demo.json` | 3-layer demo manifest (spawns at 1 s / 4.5 s / 7.5 s). |
| `data/sample_gesture_stream.json` | 945 synthetic frames @90 Hz (seed 7). Regenerate: `python3 tools/generate_gesture_stream.py`. Reference check: `python3 tools/check_pipeline.py`. |

## Honest limits

- First compiled and run 2026-09-24 (dotnet 8.0.425, Linux): ALL PASS. The first
  run caught two real bugs — `Emulator.Main` was private (CS0122) and the
  `t_ms` wire field never mapped to `TMs` (camelCase policy expected `tMs`,
  so every timestamp deserialized as 0 and nothing was detected). Both fixed;
  the sample output above is now the actual output.
- Dispatch timings on a desktop prove the pipeline is O(1) per frame, not that
  a phone hits the budget — the on-device number comes from the gesture dataset
  milestone (rev3 §6).
- Synthetic data is authored in head space (identity head rotation); on device,
  `SwipeClassifier` transforms deltas via the camera.
