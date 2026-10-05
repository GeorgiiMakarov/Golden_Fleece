#!/usr/bin/env python3
"""Deterministic synthetic 26-joint hand stream for the LayerSwitcher emulator.

Generates unity/LayerSwitcherEmulator/data/sample_gesture_stream.json:
  frames of { t_ms, tracked, label, expectedGesture, joints[26][3] }.
Joint 0 = palm (XRHandJointID.Palm); the v1 classifiers only read the palm.

Scenarios (seeded, reproducible):
  idle      - natural hand wander (resets the hold detector, must NOT swipe)
  swipe_r/l - 0.30 m horizontal swipe in 0.40 s  -> exactly one SwipeRight/Left
  hold      - deliberate stillness 1.0 s        -> exactly one PalmHold
  slow      - 0.30 m swipe in 0.90 s (> 0.60)   -> must NOT fire (negative)
  short     - 0.10 m swipe (< 0.22 m)           -> must NOT fire (negative)
"""
import json
import math
import random
import os

FPS = 90
DT_MS = 1000.0 / FPS
SEED = 7
OUT = os.path.join(os.path.dirname(__file__), "..", "unity", "LayerSwitcherEmulator",
                   "data", "sample_gesture_stream.json")


def smoothstep(u):
    u = max(0.0, min(1.0, u))
    return u * u * (3.0 - 2.0 * u)


def hand_joints(px, py, pz):
    """26 joints: palm + 5 fingers x 5 joints (simple fan, v1 only needs palm)."""
    j = [[px, py, pz]]
    for f in range(5):
        ang = (f - 2) * 0.35
        dx, dy = math.sin(ang), math.cos(ang) * 0.9
        for k in range(1, 6):
            r = 0.02 + 0.016 * k
            j.append([px + dx * r, py + dy * r, pz - 0.01 * k])
    assert len(j) == 26
    return j


def main():
    rng = random.Random(SEED)
    frames = []

    def emit(t_ms, label, expected, x, y, z, noise):
        j = hand_joints(x + rng.gauss(0, noise),
                        y + rng.gauss(0, noise),
                        z + rng.gauss(0, noise))
        frames.append({"t_ms": int(t_ms), "tracked": True, "label": label,
                       "expectedGesture": expected, "joints": j})

    # (start_ms, end_ms, label, expected, kind, params)
    plan = [
        (0, 2000, "idle_1", "None", "idle", {}),
        (2000, 2400, "swipe_right", "SwipeRight", "swipe",
         {"x0": -0.15, "x1": 0.15}),
        (2400, 4000, "idle_2", "None", "idle", {}),
        (4000, 4400, "swipe_left", "SwipeLeft", "swipe",
         {"x0": 0.15, "x1": -0.15}),
        (4400, 6000, "idle_3", "None", "idle", {}),
        (6000, 7000, "palm_hold", "PalmHold", "hold", {}),
        (7000, 8000, "idle_4", "None", "idle", {}),
        (8000, 8900, "slow_swipe", "None", "swipe",
         {"x0": -0.15, "x1": 0.15}),          # too slow -> negative
        (8900, 9500, "idle_5", "None", "idle", {}),
        (9500, 9900, "short_swipe", "None", "swipe",
         {"x0": -0.05, "x1": 0.05}),          # too short -> negative
        (9900, 10500, "idle_6", "None", "idle", {}),
    ]

    bx, by, bz = 0.0, 0.10, 0.60  # base palm pos, metres (head space)
    for start, end, label, expected, kind, p in plan:
        n = int(round((end - start) / DT_MS))
        for i in range(n):
            t_ms = start + i * DT_MS
            u = i / max(1, n - 1)
            if kind == "idle":
                t = t_ms / 1000.0
                x = bx + 0.06 * math.sin(2 * math.pi * t / 2.1)
                y = by + 0.05 * math.sin(2 * math.pi * t / 1.7 + 1.3)
                z = bz + 0.02 * math.sin(2 * math.pi * t / 2.6 + 2.1)
                emit(t_ms, label, expected, x, y, z, 0.002)
            elif kind == "swipe":
                s = smoothstep(u)
                x = p["x0"] + (p["x1"] - p["x0"]) * s
                emit(t_ms, label, expected, x, by, bz, 0.002)
            elif kind == "hold":
                emit(t_ms, label, expected, bx, by, bz, 0.003)

    stream = {"streamVersion": "1.0", "fps": FPS, "frames": frames}
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w") as f:
        json.dump(stream, f)
    print(f"wrote {OUT}: {len(frames)} frames, "
          f"{frames[-1]['t_ms']} ms, seed={SEED}")


if __name__ == "__main__":
    main()
