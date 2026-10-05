#!/usr/bin/env python3
"""Reference pipeline check for the synthetic gesture stream.

Mirrors unity/LayerSwitcherEmulator/GestureMath.cs + LayerSwitcherModel.cs 1:1
(same constants, same state machines). Used to validate the scenario
design locally; the C# emulator is the artifact studios run with dotnet.
"""
import json
import math
import os
import sys
import time
from collections import deque

# --- tuning: must match GestureDictionary.cs (v1) ---------------------------
SWIPE_MIN_DIST = 0.22
SWIPE_MAX_DUR = 0.60
SWIPE_MAX_VERT = 0.15
SWIPE_DEBOUNCE = 0.50
SWIPE_WINDOW = 0.35
HOLD_DUR = 0.80
HOLD_DRIFT = 0.05
SWITCH_BUDGET_MS = 100.0

REPO = os.path.join(os.path.dirname(__file__), "..")
STREAM = os.path.join(REPO, "unity", "LayerSwitcherEmulator", "data",
                      "sample_gesture_stream.json")


class SwipeDetector:
    def __init__(self):
        self.trail = deque()
        self.last_fire = -10.0

    def update(self, palm, t, tracked):
        if not tracked:
            self.trail.clear()
            return None
        self.trail.append((palm, t))
        while self.trail and t - self.trail[0][1] > SWIPE_WINDOW:
            self.trail.popleft()
        if len(self.trail) < 2:
            return None
        oldest = self.trail[0]
        dt = t - oldest[1]
        if dt <= 0.001 or dt > SWIPE_MAX_DUR:
            return None
        if t - self.last_fire < SWIPE_DEBOUNCE:
            return None
        dx = palm[0] - oldest[0][0]
        dy = abs(palm[1] - oldest[0][1])
        if abs(dx) >= SWIPE_MIN_DIST and dy <= SWIPE_MAX_VERT:
            self.last_fire = t
            self.trail.clear()
            return "SwipeRight" if dx > 0 else "SwipeLeft"
        return None


class PalmHoldDetector:
    def __init__(self):
        self.anchor = None
        self.still_since = -1.0
        self.fired = False

    def update(self, palm, t, tracked):
        if not tracked:
            self.still_since, self.fired = -1.0, False
            return None
        if self.still_since < 0:
            self.still_since, self.anchor, self.fired = t, palm, False
        dist = math.dist(palm, self.anchor)
        if dist > HOLD_DRIFT:
            self.still_since, self.anchor, self.fired = t, palm, False
            return None
        if not self.fired and t - self.still_since >= HOLD_DUR:
            self.fired = True
            return "PalmHold"
        return None


class SwitcherModel:
    """Headless LayerSwitcher semantics: index only, timed dispatch."""

    def __init__(self, count):
        self.count = count
        self.current = -1
        self.max_switch_ms = 0.0

    def on_gesture(self, g):
        t0 = time.perf_counter()
        if g == "SwipeRight":
            self.current = self.current + 1
            if self.current >= self.count:
                self.current = -1
        elif g == "SwipeLeft":
            self.current = max(0, self.current - 1)
        elif g == "PalmHold":
            self.current = -1
        ms = (time.perf_counter() - t0) * 1000.0
        self.max_switch_ms = max(self.max_switch_ms, ms)
        return self.current, ms


def main():
    stream = json.load(open(STREAM))
    frames = stream["frames"]
    swipe, hold = SwipeDetector(), PalmHoldDetector()
    switcher = SwitcherModel(3)
    detections = []  # (t_ms, gesture)
    max_classify_ms = 0.0

    for f in frames:
        t = f["t_ms"] / 1000.0
        palm = tuple(f["joints"][0])
        t0 = time.perf_counter()
        g1 = swipe.update(palm, t, f["tracked"])
        g2 = hold.update(palm, t, f["tracked"])
        max_classify_ms = max(max_classify_ms,
                              (time.perf_counter() - t0) * 1000.0)
        for g in (g1, g2):
            if g:
                layer, ms = switcher.on_gesture(g)
                detections.append((f["t_ms"], g))

    # segment checks: contiguous runs with expected != None
    segments, cur = [], None
    for f in frames:
        exp = f["expectedGesture"]
        if exp != "None" and (cur is None or cur["label"] != f["label"]):
            if cur:
                segments.append(cur)
            cur = {"label": f["label"], "expected": exp,
                   "start": f["t_ms"], "end": f["t_ms"]}
        elif exp != "None" and cur is not None:
            cur["end"] = f["t_ms"]
        else:
            if cur:
                segments.append(cur)
                cur = None
    if cur:
        segments.append(cur)

    ok, fails = True, []
    print(f"frames: {len(frames)}, detections: "
          + ", ".join(f"{g}@{t}ms" for t, g in detections))
    for s in segments:
        hits = [d for d in detections
                if d[1] == s["expected"]
                and s["start"] - 50 <= d[0] <= s["end"] + 300]
        status = "PASS" if len(hits) == 1 else "FAIL"
        if status == "FAIL":
            ok = False
            fails.append(s["label"])
        print(f"[{status}] {s['label']}: expected 1x {s['expected']}, "
              f"got {len(hits)}")
    # false positives: detections outside every expected window (+300ms grace)
    fps = [d for d in detections if not any(
        s["start"] - 50 <= d[0] <= s["end"] + 300 for s in segments)]
    status = "PASS" if not fps else "FAIL"
    if fps:
        ok, fails = False, fails + ["false_positives"]
    print(f"[{status}] false positives: {len(fps)}")
    print(f"max classify/frame: {max_classify_ms:.3f} ms, "
          f"max switch dispatch: {switcher.max_switch_ms:.3f} ms "
          f"(budget {SWITCH_BUDGET_MS:.0f} ms) -> "
          f"{'PASS' if switcher.max_switch_ms < SWITCH_BUDGET_MS else 'FAIL'}")
    print("RESULT:", "ALL PASS" if ok else f"FAILURES: {fails}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
