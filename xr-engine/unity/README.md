# Lightweight XR Content Engine — Phase 1 Skeleton (Unity/OpenXR)

Phase 1 of LIGHTWEIGHT XR CONTENT ENGINE (Android XR Edition), rev3: base Unity/OpenXR project, in-house gesture classifiers from raw hand joints, JSON-manifest parser, pre-baked layer switcher, head-locked placement. No video binding yet — debugging against a test scene.

## Requirements

- Unity 6000.x (Unity 6)
- Packages (Package Manager → + → by name):
  - `com.unity.xr.openxr` — OpenXR Plugin
  - `com.unity.xr.hands` — XR Hands (raw 26-joint poses)
  - `com.unity.cloud.gltfast` — glTFast (runtime GLB loading)
  - `com.unity.inputsystem` — Input System (keyboard test hooks)
- OpenXR Feature Groups: enable Hand Tracking (and Hand Tracking Aim on Quest).

## Project layout

```
Assets/_XRLayers/
  Scripts/
    Core/XREngine.cs               bootstrap: manifest -> stack -> switcher -> gestures
    Manifest/LayerManifest.cs      JSON schema (mirrors VAST/VMAP trigger pattern)
    Manifest/ManifestParser.cs     parse + gesture-contract version check
    Layers/LayerStack.cs           prefetch all GLBs up front, LRU eviction (~10)
    Layers/LayerSwitcher.cs        swipe cycles layers, hold dismisses; <100ms budget
    Layers/LayerStateMachine.cs    Appear -> Idle -> React -> Dismissed (pre-baked)
    Gestures/GestureDictionary.cs  versioned v1 contract + tunables
    Gestures/HandJointReader.cs    raw joint polling (XR Hands subsystem)
    Gestures/SwipeClassifier.cs    directional swipe, velocity window + debounce
    Gestures/PalmHoldClassifier.cs open-palm hold ~0.8s
    Gestures/GestureEventBus.cs    single funnel: classifiers in, switcher consumes
    Placement/HeadLockedPlacement.cs  v1 placement: view-space offset, no screen CV
    Debug/TestSceneBootstrap.cs    code-built test scene + keyboard gesture stand-ins
  Data/example_manifest_v1.json
  GESTURE_DICTIONARY_v1.md
```

## Quick start (no headset needed)

1. Open this folder as a Unity project (Unity 6000.x).
2. Install the packages above; enable Hand Tracking in OpenXR settings.
3. Create an empty scene, add `TestSceneBootstrap` to any GameObject.
4. Paste the contents of `Data/example_manifest_v1.json` into its `demoManifestJson` field.
5. Press Play. Layer 1 appears head-locked. Press `1` = swipe right (next layer), `2` = swipe left, `3` = palm hold (dismiss). Replace the example `bundleUrls` with real GLBs to see art instead of the procedural stand-in.

## What Phase 1 deliberately does NOT include

- Video/media sync (Phase 2: MediaProjection + AudioPlaybackCapture adapters plug into `XREngine.ShowLayer`).
- Screen-anchored placement via CV (roadmap R&D; v1 is head-locked per rev3 §4).
- Partner cue endpoint (Phase 3, gated on BD).
- Baked skeletal clips — `LayerStateMachine.bakedAnimator` is the hook; assign an Animator with Appear/Idle/React triggers and the procedural tweens step aside.

## Notes

- `ManifestParser` refuses to run on a gesture-dictionary version mismatch. The dictionary is a contract, not a suggestion.
- `LayerSwitcher` stopwatches every switch and warns past the 100 ms budget.
- Prefetch budget is 10 layers; beyond that LRU evicts oldest.
