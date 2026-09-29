"""Solver-verdict experiment (per review 2026-09-29).

For each dumped state (vel_pre, p_warm) from a 64^3 run:
  - exact:  discrete Poisson solved from p=0 to convergence (20000 Jacobi),
            u_exact = vel_pre - grad(p_exact)   <- best the discrete system can do
  - j50:    50 Jacobi iterations warm-started from the run's p  (production setting)
  - j400:   400 iterations warm-started (8x the knob, offline bake allows it)
Compares post-projection VELOCITY against exact (not div_after, which is blind
to slow pressure modes). Small error -> jacobi-50 verdict is final.

Usage: python3 analyze_solver.py output-<tag>-n64
"""
import glob
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
OUTDIR = sys.argv[1] if len(sys.argv) > 1 else "output-mc-vort0.15-chi-sweep-n64"

src = open(os.path.join(HERE, "simulate.py")).read()
src = src.replace("\nN = 32\n", "\nN = 64\n", 1)
src = src.split('if __name__ == "__main__":')[0]
g = {"__name__": "notmain64a", "__file__": os.path.join(HERE, "simulate.py")}
exec(src, g)

project = g["project"]
divergence = g["divergence"]


def solve(vel_pre, p0, iters):
    g["SOLVER"] = "jacobi"
    g["P_ITERS"] = iters
    u, p = project(vel_pre.copy(), p0.copy())
    return u, p


for path in sorted(glob.glob(os.path.join(HERE, OUTDIR, "state_step*.npz"))):
    step = os.path.basename(path)
    d = np.load(path)
    vel_pre, p_warm = d["vel_pre"], d["p_warm"]

    u_exact, p_exact = solve(vel_pre, np.zeros_like(p_warm), 20000)
    u_50, _ = solve(vel_pre, p_warm, 50)
    u_400, _ = solve(vel_pre, p_warm, 400)

    def err(u):
        d = u - u_exact
        return float(np.max(np.abs(d))), float(np.sqrt(np.mean(d ** 2)))

    e50, r50 = err(u_50)
    e400, r400 = err(u_400)
    scale = float(np.max(np.abs(vel_pre)))
    print(f"{step}: |u|_max={scale:.3f}")
    print(f"  div_after: exact={np.max(np.abs(divergence(u_exact))):.4f} "
          f"j50={np.max(np.abs(divergence(u_50))):.4f} "
          f"j400={np.max(np.abs(divergence(u_400))):.4f}")
    print(f"  |u_j50 - u_exact|:  max={e50:.3e} ({e50/scale*100:.2f}% of |u|_max)  rms={r50:.3e}")
    print(f"  |u_j400 - u_exact|: max={e400:.3e} ({e400/scale*100:.2f}% of |u|_max)  rms={r400:.3e}")
print("done")
