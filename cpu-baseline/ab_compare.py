#!/usr/bin/env python3
"""
A/B compare: baseline v0.2 (semi-Lagrangian) vs upgrade #1 (MacCormack + clamp).

Inputs : cpu-baseline/output/checks.json + frames/        (sl)
         cpu-baseline/output-mc/checks.json + frames/     (mc)
Outputs: cpu-baseline/ab/
         ab_metrics.png  - dye mass / sharpness (TV) / peak / residual div over time
         ab_preview.gif  - side-by-side animation (left: SL baseline, right: MacCormack)
         ab_report.json  - summary numbers
"""
import json, os, sys
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))

# usage: ab_compare.py [dirA labelA dirB labelB outdir]
# default: A/B #1 — output (SL baseline v0.2) vs output-mc (MacCormack)
dA, lA, dB, lB, od = (sys.argv[1:6] + ["output", "SL baseline v0.2",
                                      "output-mc", "MacCormack + clamp", "ab"])[:5]
AB = os.path.join(HERE, od)
os.makedirs(AB, exist_ok=True)

def load(d):
    base = os.path.join(HERE, d)
    with open(os.path.join(base, "checks.json")) as f:
        checks = json.load(f)
    return base, checks

base_sl, c_sl = load(dA)
base_mc, c_mc = load(dB)
assert len(c_sl) == len(c_mc), "frame count mismatch"

steps = [c["step"] for c in c_sl]
keys = [("dye_mass", "dye mass (sum)"),
        ("dye_tv", "sharpness (total variation)"),
        ("mean_abs_vort", "mean |vorticity|"),
        ("max_abs_div_after", "residual div after projection")]

fig, axes = plt.subplots(2, 2, figsize=(10, 7))
for ax, (k, label) in zip(axes.flat, keys):
    ax.plot(steps, [c.get(k, 0) for c in c_sl], label=lA)
    ax.plot(steps, [c.get(k, 0) for c in c_mc], label=lB)
    ax.set_xlabel("step"); ax.set_ylabel(label); ax.legend(); ax.grid(alpha=0.3)
fig.suptitle(f"A/B: {lA} vs {lB} (32³, 240 steps)")
fig.tight_layout()
fig.savefig(os.path.join(AB, "ab_metrics.png"), dpi=100)

# side-by-side gif @64px per half
frames = []
for c in c_sl:
    a = Image.open(os.path.join(base_sl, "frames", f"frame_{c['frame']:03d}.png")).resize((64, 64), Image.BILINEAR)
    b = Image.open(os.path.join(base_mc, "frames", f"frame_{c['frame']:03d}.png")).resize((64, 64), Image.BILINEAR)
    side = Image.new("RGB", (128, 64))
    side.paste(a, (0, 0)); side.paste(b, (64, 0))
    frames.append(side)
frames[0].save(os.path.join(AB, "ab_preview.gif"), save_all=True,
               append_images=frames[1:], duration=80, loop=0)

def ratio(k, i=-1):
    return c_mc[i][k] / c_sl[i][k] if c_sl[i][k] else None

report = {
    "a": lA, "b": lB,
    "frames": len(c_sl),
    "last_frame": {
        "tv_b_over_a": ratio("dye_tv"),
        "mass_b_over_a": ratio("dye_mass"),
        "peak_b": c_mc[-1]["scalar_max"], "peak_a": c_sl[-1]["scalar_max"],
        "mean_abs_vort_b": c_mc[-1].get("mean_abs_vort", 0),
        "mean_abs_vort_a": c_sl[-1].get("mean_abs_vort", 0),
        "div_after_b": c_mc[-1]["max_abs_div_after"],
        "div_after_a": c_sl[-1]["max_abs_div_after"],
        "max_vel_b": c_mc[-1]["max_vel"], "max_vel_a": c_sl[-1]["max_vel"],
        "scalar_range_b": [c_mc[-1]["scalar_min"], c_mc[-1]["scalar_max"]],
        "scalar_range_a": [c_sl[-1]["scalar_min"], c_sl[-1]["scalar_max"]],
    },
}
with open(os.path.join(AB, "ab_report.json"), "w") as f:
    json.dump(report, f, indent=1)

r = report["last_frame"]
print(f"A: {lA}")
print(f"B: {lB}")
print(f"frames: {len(c_sl)}")
print(f"TV  b/a: {r['tv_b_over_a']:.3f}")
print(f"mass b/a: {r['mass_b_over_a']:.4f}")
print(f"mean|vort| b {r['mean_abs_vort_b']:.4f} vs a {r['mean_abs_vort_a']:.4f}")
print(f"div_after b {r['div_after_b']:.3e} vs a {r['div_after_a']:.3e}")
print(f"max|vel| b {r['max_vel_b']:.3f} vs a {r['max_vel_a']:.3f}")
print(f"scalar range b {r['scalar_range_b']} vs a {r['scalar_range_a']}")
