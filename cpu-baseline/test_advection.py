#!/usr/bin/env python3
"""
Unit check of the MacCormack advection operator (independent of the baseline scene).

Solid-body rotation of a sharp dye disc: after one full revolution the exact
solution equals the initial field, so any loss of sharpness is pure numerical
diffusion. Compares semi-Lagrangian vs MacCormack+clamp.
"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from simulate import N, DT, _coords, advect, advect_maccormack

def tv(f):
    return float(np.abs(np.diff(f, axis=0)).sum()
                 + np.abs(np.diff(f, axis=1)).sum()
                 + np.abs(np.diff(f, axis=2)).sum())

# sharp disc in the x-y plane (axis0=x, axis1=y), radius 8 cells, centered
X, Y = _coords[0], _coords[1]
cx = cy = (N - 1) / 2
f0 = ((X - cx) ** 2 + (Y - cy) ** 2 <= 8.0 ** 2).astype(np.float64)

# solid-body rotation about z-axis through center: omega chosen so one
# revolution takes exactly 400 steps
omega = 2 * np.pi / (400 * DT)
vel = np.zeros((N, N, N, 3))
vel[..., 0] = -omega * (Y - cy)   # u = -omega*(y-cy)
vel[..., 1] = omega * (X - cx)    # v =  omega*(x-cx)

for name, fn in (("SL", advect), ("MC", advect_maccormack)):
    f = f0.copy()
    for _ in range(400):
        f = fn(f, vel)
        assert np.all(np.isfinite(f)), f"{name}: non-finite"
    print(f"{name}: TV {tv(f0):.0f} -> {tv(f):.0f} "
          f"(retained {tv(f)/tv(f0)*100:.1f}%), "
          f"mass {f0.sum():.0f} -> {f.sum():.0f}, "
          f"range [{f.min():.3f}, {f.max():.3f}]")
