#!/usr/bin/env python3
"""
CPU reference of the Fluid Preset Studio baseline (v0.1).

Faithful to unity-package/Shaders/*.compute and Scripts/FluidStepBaseline.cs:
  Advect (semi-Lagrangian, trilinear, clamped) -> Diffuse (explicit, boundary copy)
  -> Penalize (explicit, static hard chi) -> Projection
  (central-difference divergence w/ clamped indices -> 50x Jacobi -> subtract dt*grad(p))

ONE documented deviation: LAMBDA=25 instead of the original 1e3 (explicit
penalization is unstable for lambda*dt=20: u *= -19 inside the obstacle).

v0.2 FIX: dt removed from the projection correction. JacobiPressure solves
∇²p = div WITHOUT dt, so the consistent step is u −= ∇p. The original
u −= dt·∇p damped the correction 50x, making projection a near no-op.

Outputs (cpu-baseline/output/):
  frames/frame_###.png  - mid-Z slice of the scalar field, inferno colormap, fixed [0,1] range
  atlas.png             - 12x10 flipbook atlas @128px cells
  preview.gif           - downsampled animation preview
  manifest.json         - bake parameters (proto preset manifest)
  checks.json           - numeric checks per captured frame
"""
import numpy as np, os, json, time, sys
from scipy.ndimage import map_coordinates
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import cm
from PIL import Image

# ---------------- baseline parameters (mirror FluidStepBaseline defaults) ----------------
N = 32
DX = 1.0
DT = 0.02
NU = 0.001
LAMBDA = 25.0          # BUGFIX: original 1e3 unstable (see module docstring)
P_ITERS = 50
SOLVER = "jacobi"  # "jacobi" or "rbgs"; one rbgs sweep ~= one jacobi iter in work
STEPS = 240
CAPTURE_EVERY = 2
FRAME_PX = 128

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "output")
FRAMES = os.path.join(OUT, "frames")
os.makedirs(FRAMES, exist_ok=True)

# Analysis instrumentation: FLUID_DUMP_STEPS="8,60,120,200" saves
# state_stepNNNN.npz (vel_pre, p_warm) before projection at those steps.
# Used by the 64^3 solver-verdict experiment; empty by default.
_DUMP_STEPS = frozenset(
    int(x) for x in os.environ.get("FLUID_DUMP_STEPS", "").split(",") if x.strip()
)

# ---------------- field init (mirror Initialize*Texture) ----------------
def init_fields():
    vel = np.zeros((N, N, N, 3), dtype=np.float64)
    vel[..., 0] = 1.0                                   # constant flow +X
    s = np.zeros((N, N, N), dtype=np.float64)
    s[:N // 2, :, :] = 1.0                             # left half = 1
    chi = np.zeros((N, N, N), dtype=np.float64)
    a, b = N // 3, 2 * N // 3
    chi[a:b, a:b, a:b] = 1.0                           # static centered cube
    return vel, s, chi

_coords = np.stack(
    np.meshgrid(np.arange(N), np.arange(N), np.arange(N), indexing="ij")
).astype(np.float64)  # (3,N,N,N)

# ---------------- operators (mirror the .compute kernels) ----------------
def advect(field, vel):
    back = _coords - np.transpose(vel, (3, 0, 1, 2)) * (DT / DX)
    if field.ndim == 3:
        return map_coordinates(field, back, order=1, mode="nearest")
    out = np.empty_like(field)
    for c in range(field.shape[-1]):
        out[..., c] = map_coordinates(field[..., c], back, order=1, mode="nearest")
    return out

# ---------------- MacCormack advection + clamp (upgrade #1) ----------------
# f1   = SL(f,  u)          (forward)
# fbar = SL(f1, -u)         (backward, sampled at x + dt*u)
# f    = f1 + 0.5*(f - fbar), clamped to [min,max] of f over the 8 grid
#        corners around the departure point (Selle et al. limiter).
def _departure(vel):
    return _coords - np.transpose(vel, (3, 0, 1, 2)) * (DT / DX)

def _corner_minmax(f, dep):
    i0 = np.clip(np.floor(dep).astype(np.int64), 0, N - 1)   # (3,N,N,N)
    i1 = np.clip(i0 + 1, 0, N - 1)
    X0, Y0, Z0 = i0[0], i0[1], i0[2]
    X1, Y1, Z1 = i1[0], i1[1], i1[2]
    corners = np.stack([
        f[X0, Y0, Z0], f[X0, Y0, Z1], f[X0, Y1, Z0], f[X0, Y1, Z1],
        f[X1, Y0, Z0], f[X1, Y0, Z1], f[X1, Y1, Z0], f[X1, Y1, Z1],
    ])
    return corners.min(axis=0), corners.max(axis=0)

def _mc_scalar(f, dep):
    f1 = map_coordinates(f, dep, order=1, mode="nearest")
    fbar = map_coordinates(f1, 2.0 * _coords - dep, order=1, mode="nearest")
    fcorr = f1 + 0.5 * (f - fbar)
    lo, hi = _corner_minmax(f, dep)
    return np.clip(fcorr, lo, hi)

def advect_maccormack(field, vel):
    dep = _departure(vel)
    if field.ndim == 3:
        return _mc_scalar(field, dep)
    out = np.empty_like(field)
    for c in range(field.shape[-1]):
        out[..., c] = _mc_scalar(field[..., c], dep)
    return out

def diffuse(f):
    coeff = NU * DT / (DX * DX)
    lap = (np.roll(f, 1, 0) + np.roll(f, -1, 0)
           + np.roll(f, 1, 1) + np.roll(f, -1, 1)
           + np.roll(f, 1, 2) + np.roll(f, -1, 2) - 6.0 * f)
    out = f + coeff * lap
    # boundary: copy input (shader early-out branch); interior roll values are exact
    # because only boundary cells used wrapped neighbors and they are overwritten.
    out[0] = f[0]; out[-1] = f[-1]
    out[:, 0] = f[:, 0]; out[:, -1] = f[:, -1]
    out[:, :, 0] = f[:, :, 0]; out[:, :, -1] = f[:, :, -1]
    return out

def penalize(vel, chi):
    return vel * (1.0 - DT * LAMBDA * chi[..., None])

# ---------------- dynamic chi + implicit penalization (upgrade #5) ----------------
# The obstacle is a cube (side N//3) sweeping sinusoidally in x:
#   cx(t) = N/2 + (N/8)*sin(2*pi*step/480), cy = cz = N/2.
# One smooth sweep per 240-step bake (starts centered, like the static case).
# Max displacement: (N*AMP)*(2*pi/PERIOD) cells/step
#   = 0.052 at N=32, 0.105 at N=64  (<= ~1 cell/step, no tunneling).
# Max |u_wall| = displacement/DT ~ 2.6 at N=32 (~2.6x the inflow — a brisk hand,
# kept O(1) so the projection stays in its comfort zone; retune per resolution).
# FLUID_CHI_PERIOD / FLUID_CHI_AMP_FRAC override the trajectory for experiments
# (e.g. the fast-hand regime: period ~25 steps at 32^3 -> ~1 cell/step).
# The chi mask is swept: union of the cube at step and step+1 (anti-tunneling
# insurance along the trajectory, per review).
# u_wall is the EXACT per-step wall velocity in cells per unit time
# (same units as vel: advection departure is vel*DT/DX cells).
#   u_wall = (cx(step+1) - cx(step)) / DT  (x-component only)
# If u_wall were 0, the obstacle would just eat momentum without pushing fluid.
# Implicit penalization (unconditionally stable):
#   u_new = (u + dt*λ*chi*u_wall) / (1 + dt*λ*chi)
# Inside chi, dye is zeroed and T is reset to T_amb so neither is dragged
# along with the hand.
CHI_PERIOD = int(os.environ.get("FLUID_CHI_PERIOD", "480"))
CHI_AMP_FRAC = float(os.environ.get("FLUID_CHI_AMP_FRAC", "0.125"))

def chi_trajectory(step):
    a = N // 3
    cx = N / 2 + (N * CHI_AMP_FRAC) * np.sin(2 * np.pi * step / CHI_PERIOD)
    return cx, N / 2, N / 2, a

def chi_wall_velocity(step):
    cx0, _, _, _ = chi_trajectory(step)
    cx1, _, _, _ = chi_trajectory(step + 1)
    return np.array([(cx1 - cx0) / DT, 0.0, 0.0])

def make_chi_swept(step):
    chi = np.zeros((N, N, N))
    for s in (step, step + 1):
        cx, cy, cz, a = chi_trajectory(s)
        x0, x1 = max(int(np.floor(cx - a / 2)), 0), min(int(np.ceil(cx + a / 2)), N)
        y0, y1 = max(int(np.floor(cy - a / 2)), 0), min(int(np.ceil(cy + a / 2)), N)
        z0, z1 = max(int(np.floor(cz - a / 2)), 0), min(int(np.ceil(cz + a / 2)), N)
        chi[x0:x1, y0:y1, z0:z1] = 1.0
    return chi

def penalize_implicit(vel, chi, u_wall):
    w = DT * LAMBDA * chi[..., None]
    return (vel + w * u_wall) / (1.0 + w)

# ---------------- vorticity confinement (upgrade #2, Fedkiw) ----------------
# omega = curl(u); eta = grad(|omega|); N = eta/|eta|
# F = eps * h * (N x omega);  u += dt * F
# Inserted as a force between diffusion and penalization.
def curl(vel):
    u, v, w = vel[..., 0], vel[..., 1], vel[..., 2]
    up, vp, wp = (np.pad(x, 1, mode="edge") for x in (u, v, w))
    # axis0=x, axis1=y, axis2=z (same convention as divergence())
    dudy = (up[1:-1, 2:, 1:-1] - up[1:-1, :-2, 1:-1]) / (2 * DX)
    dudz = (up[1:-1, 1:-1, 2:] - up[1:-1, 1:-1, :-2]) / (2 * DX)
    dvdx = (vp[2:, 1:-1, 1:-1] - vp[:-2, 1:-1, 1:-1]) / (2 * DX)
    dvdz = (vp[1:-1, 1:-1, 2:] - vp[1:-1, 1:-1, :-2]) / (2 * DX)
    dwdx = (wp[2:, 1:-1, 1:-1] - wp[:-2, 1:-1, 1:-1]) / (2 * DX)
    dwdy = (wp[1:-1, 2:, 1:-1] - wp[1:-1, :-2, 1:-1]) / (2 * DX)
    wx = dwdy - dvdz
    wy = dudz - dwdx
    wz = dvdx - dudy
    return np.stack([wx, wy, wz], axis=-1)

def vorticity_confinement(vel, eps):
    if eps == 0.0:
        return vel, 0.0
    om = curl(vel)
    mag = np.linalg.norm(om, axis=-1)
    mp = np.pad(mag, 1, mode="edge")
    gx = (mp[2:, 1:-1, 1:-1] - mp[:-2, 1:-1, 1:-1]) / (2 * DX)
    gy = (mp[1:-1, 2:, 1:-1] - mp[1:-1, :-2, 1:-1]) / (2 * DX)
    gz = (mp[1:-1, 1:-1, 2:] - mp[1:-1, 1:-1, :-2]) / (2 * DX)
    eta = np.stack([gx, gy, gz], axis=-1)
    n = eta / (np.linalg.norm(eta, axis=-1, keepdims=True) + 1e-8)
    f = eps * DX * np.cross(n, om)
    return vel + DT * f, float(mag.mean())

def divergence(vel):
    u, v, w = vel[..., 0], vel[..., 1], vel[..., 2]
    up, vp, wp = (np.pad(x, 1, mode="edge") for x in (u, v, w))
    dudx = (up[2:, 1:-1, 1:-1] - up[:-2, 1:-1, 1:-1]) / (2 * DX)
    dvdy = (vp[1:-1, 2:, 1:-1] - vp[1:-1, :-2, 1:-1]) / (2 * DX)
    dwdz = (wp[1:-1, 1:-1, 2:] - wp[1:-1, 1:-1, :-2]) / (2 * DX)
    return dudx + dvdy + dwdz

def project(vel, p):
    # NOTE: p persists across steps (warm start), faithful to FluidStepBaseline.cs
    # where pTex/pTmpTex are ping-ponged without clearing between frames.
    div = divergence(vel)
    if SOLVER == "rbgs":
        p = rbgs_solve(div, p, P_ITERS)
    else:
        for _ in range(P_ITERS):
            pp = np.pad(p, 1, mode="edge")
            p = ((pp[2:, 1:-1, 1:-1] + pp[:-2, 1:-1, 1:-1]
                  + pp[1:-1, 2:, 1:-1] + pp[1:-1, :-2, 1:-1]
                  + pp[1:-1, 1:-1, 2:] + pp[1:-1, 1:-1, :-2])
                 - DX * DX * div) / 6.0
    pp = np.pad(p, 1, mode="edge")
    gx = (pp[2:, 1:-1, 1:-1] - pp[:-2, 1:-1, 1:-1]) / (2 * DX)
    gy = (pp[1:-1, 2:, 1:-1] - pp[1:-1, :-2, 1:-1]) / (2 * DX)
    gz = (pp[1:-1, 1:-1, 2:] - pp[1:-1, 1:-1, :-2]) / (2 * DX)
    vel = vel.copy()
    # v0.2 FIX: u −= ∇p (no dt) — consistent with Jacobi solving ∇²p = div
    vel[..., 0] -= gx
    vel[..., 1] -= gy
    vel[..., 2] -= gz
    return vel, p

def rbgs_solve(div, p, sweeps):
    # Red-Black Gauss-Seidel, in-place, edge (Neumann) boundaries like Jacobi.
    # One sweep = red pass + black pass ≈ the work of ONE Jacobi iteration
    # (N^3 cell updates, 2 pads), but converges ~2x faster per work unit.
    # So rbgs-25 sweeps ≈ jacobi-50 iterations in cost.
    rhs = DX * DX * div
    for _ in range(sweeps):
        for parity in (0, 1):
            pp = np.pad(p, 1, mode="edge")  # re-pad: red updates visible to black
            views = ([(0, 0, 0), (0, 1, 1), (1, 0, 1), (1, 1, 0)] if parity == 0
                     else [(0, 0, 1), (0, 1, 0), (1, 0, 0), (1, 1, 1)])
            for ax, ay, az in views:
                ix = np.arange(ax, N, 2); iy = np.arange(ay, N, 2); iz = np.arange(az, N, 2)
                cx, cy, cz = ix + 1, iy + 1, iz + 1
                s = (pp[np.ix_(ix + 2, cy, cz)] + pp[np.ix_(ix, cy, cz)]
                     + pp[np.ix_(cx, iy + 2, cz)] + pp[np.ix_(cx, iy, cz)]
                     + pp[np.ix_(cx, cy, iz + 2)] + pp[np.ix_(cx, cy, iz)])
                p[np.ix_(ix, iy, iz)] = (s - rhs[np.ix_(ix, iy, iz)]) / 6.0
    return p

# ---------------- buoyancy (upgrade #3, Fedkiw-style) ----------------
# F = beta * (T - T_amb) * up ;  up = +Y (axis1). Applied as a force between
# vorticity confinement and penalization. T is advected/diffused like dye.
T_AMB = 0.0

def make_temperature():
    T = np.zeros((N, N, N))
    X, Y, Z = np.meshgrid(np.arange(N), np.arange(N), np.arange(N), indexing="ij")
    T[((X - 8) ** 2 + (Y - 8) ** 2 + (Z - 16) ** 2) < 16] = 1.0  # hot sphere, low, near inflow
    return T

def buoyancy(vel, T, beta):
    if beta == 0.0:
        return vel
    f = np.zeros_like(vel)
    f[..., 1] += beta * (T - T_AMB)
    return vel + DT * f

# ---------------- main loop ----------------
def main(scheme="sl", vort_eps=0.0, buoy_beta=0.0, steps=240, solver="jacobi", iters=50,
         chi_mode="static"):
    global OUT, FRAMES, STEPS, SOLVER, P_ITERS
    STEPS = steps
    SOLVER = solver
    P_ITERS = iters
    parts = [scheme]
    if vort_eps > 0:
        parts.append(f"vort{vort_eps:g}")
    if buoy_beta > 0:
        parts.append(f"buoy{buoy_beta:g}")
    if solver != "jacobi":
        parts.append(f"{solver}{iters}")
    if chi_mode != "static":
        parts.append(f"chi-{chi_mode}")
    if os.environ.get("FLUID_TAG_SUFFIX"):
        parts.append(os.environ["FLUID_TAG_SUFFIX"])
    if steps != 240:
        parts.append(f"s{steps}")
    tag = "-".join(parts)
    if tag != "sl":
        OUT = os.path.join(HERE, f"output-{tag}")
        FRAMES = os.path.join(OUT, "frames")
        os.makedirs(FRAMES, exist_ok=True)
    adv = advect_maccormack if scheme == "mc" else advect

    vel, s, chi = init_fields()
    T = make_temperature() if buoy_beta > 0 else None
    p = np.zeros((N, N, N), dtype=np.float64)  # persistent pressure (warm start)
    cmap = cm.get_cmap("inferno")
    checks = []
    t0 = time.time()

    n_frames = STEPS // CAPTURE_EVERY
    for step in range(STEPS):
        vel = adv(vel, vel)
        s = adv(s, vel)
        if T is not None:
            T = adv(T, vel)
        vel = diffuse(vel)
        s = diffuse(s)
        if T is not None:
            T = diffuse(T)
        vel, vort_mean = vorticity_confinement(vel, vort_eps)
        if T is not None:
            vel = buoyancy(vel, T, buoy_beta)
        if chi_mode == "sweep":
            chi = make_chi_swept(step)
            u_wall = chi_wall_velocity(step)
            vel = penalize_implicit(vel, chi, u_wall)
            s[chi > 0.5] = 0.0          # don't drag dye with the hand
            if T is not None:
                T[chi > 0.5] = T_AMB     # ... nor heat
        elif chi_mode == "static-implicit":
            vel = penalize_implicit(vel, chi, np.zeros(3))
        else:
            vel = penalize(vel, chi)

        div_before = float(np.max(np.abs(divergence(vel))))
        if step in _DUMP_STEPS:
            np.savez(os.path.join(OUT, f"state_step{step:04d}.npz"),
                     vel_pre=vel.copy(), p_warm=p.copy())
        vel, p = project(vel, p)
        div_after = float(np.max(np.abs(divergence(vel))))

        if not (np.all(np.isfinite(vel)) and np.all(np.isfinite(s))
                and (T is None or np.all(np.isfinite(T)))):
            raise RuntimeError(f"non-finite values at step {step} — baseline unstable")

        if step % CAPTURE_EVERY == 0:
            fi = step // CAPTURE_EVERY
            sl = np.clip(s[:, :, N // 2], 0.0, 1.0)          # mid-Z slice, fixed range
            rgba = (cmap(sl)[..., :3] * 255).astype(np.uint8)
            Image.fromarray(rgba).resize((FRAME_PX, FRAME_PX), Image.BILINEAR).save(
                os.path.join(FRAMES, f"frame_{fi:03d}.png"))
            checks.append({
                "frame": fi, "step": step,
                "max_abs_div_before": div_before,
                "max_abs_div_after": div_after,
                "max_vel": float(np.max(np.abs(vel))),
                "ke": 0.5 * float((vel ** 2).sum()),  # total kinetic energy
                "scalar_min": float(s.min()), "scalar_max": float(s.max()),
                "dye_mass": float(s.sum()),
                "dye_tv": float(np.abs(np.diff(s, axis=0)).sum()
                                + np.abs(np.diff(s, axis=1)).sum()
                                + np.abs(np.diff(s, axis=2)).sum()),
                "mean_abs_vort": vort_mean,
            })
            if fi % 20 == 0:
                print(f"frame {fi}/{n_frames}  div {div_before:.2e}->{div_after:.2e}", flush=True)

    # atlas: 12 cols x ceil(frames/12) rows
    cols, rows = 12, (n_frames + 11) // 12
    atlas = Image.new("RGB", (cols * FRAME_PX, rows * FRAME_PX))
    for i, c in enumerate(checks):
        im = Image.open(os.path.join(FRAMES, f"frame_{i:03d}.png"))
        atlas.paste(im, ((i % cols) * FRAME_PX, (i // cols) * FRAME_PX))
    atlas.save(os.path.join(OUT, "atlas.png"))

    # gif preview @64px
    gif_frames = [Image.open(os.path.join(FRAMES, f"frame_{c['frame']:03d}.png")).resize((64, 64), Image.BILINEAR)
                  for c in checks]
    gif_frames[0].save(os.path.join(OUT, "preview.gif"), save_all=True,
                       append_images=gif_frames[1:], duration=80, loop=0)

    with open(os.path.join(OUT, "checks.json"), "w") as f:
        json.dump(checks, f, indent=1)

    # reference fields for cross-checking ports (e.g. Unity Editor runs):
    # final velocity / scalar / temperature, float32
    np.save(os.path.join(OUT, "velocity_final.npy"), vel.astype(np.float32))
    np.save(os.path.join(OUT, "scalar_final.npy"), s.astype(np.float32))
    if T is not None:
        np.save(os.path.join(OUT, "temperature_final.npy"), T.astype(np.float32))

    manifest = {
        "preset": "baseline-windtunnel-smoke",
        "pipeline": "fluid-preset-studio",
        "version": "0.2.0-baseline",
        "projection_fix": "v0.2: SubtractGradient now u −= ∇p (dt removed); consistent with Jacobi solving ∇²p = div",
        "grid": [N, N, N], "dx": DX, "dt": DT, "nu": NU,
        "lambda_pen": LAMBDA,
        "lambda_note": "BUGFIX: original 1e3 unstable under explicit penalization; 25 => lambda*dt=0.5",
        "pressure_iters": P_ITERS, "pressure_solver": SOLVER,
        "advection": "maccormack-clamped" if scheme == "mc" else "semi-lagrangian",
        "vorticity": ("confinement-fedkiw", vort_eps) if vort_eps > 0 else "off",
        "buoyancy": ("fedkiw", buoy_beta, "T_hot_sphere", T_AMB) if buoy_beta > 0 else "off",
        "chi": ("static-cube" if chi_mode == "static" else
                "static-cube-implicit-penalization" if chi_mode == "static-implicit" else
                "sweep-cube-implicit: cx=N/2+(N/6)*sin(2π·step/120), swept mask, u_wall exact"),
        "steps": STEPS, "capture_every": CAPTURE_EVERY, "frames": n_frames,
        "capture": "mid-Z slice of scalar field, inferno colormap, fixed [0,1] range",
        "atlas": {"file": "atlas.png", "cols": cols, "rows": rows, "cell_px": FRAME_PX},
        "preview": "preview.gif",
        "sim_seconds": round(time.time() - t0, 1),
    }
    with open(os.path.join(OUT, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=1)

    if "--freeze" in sys.argv:
        # Frozen numeric reference for GPU-vs-CPU comparison in the Editor.
        # Saves the final fields + sha256 so a future HLSL run can be
        # diffed numerically, not just by eye on PNGs.
        import hashlib
        def sha256(a):
            return hashlib.sha256(np.ascontiguousarray(a).tobytes()).hexdigest()
        np.save(os.path.join(OUT, "ref_vel_final.npy"), vel)
        np.save(os.path.join(OUT, "ref_s_final.npy"), s)
        ref = {
            "config": {"scheme": scheme, "vort_eps": vort_eps,
                       "buoy_beta": buoy_beta, "steps": STEPS,
                       "grid": N, "dx": DX, "dt": DT, "nu": NU,
                       "lambda_pen": LAMBDA, "pressure_iters": P_ITERS,
                       "projection": "u -= grad(p), no dt (v0.2 fix)"},
            "ref_vel_final.npy": {"sha256": sha256(vel), "shape": list(vel.shape),
                                  "dtype": str(vel.dtype),
                                  "mean_abs": float(np.mean(np.abs(vel))),
                                  "max_abs": float(np.max(np.abs(vel)))},
            "ref_s_final.npy": {"sha256": sha256(s), "shape": list(s.shape),
                                "dtype": str(s.dtype),
                                "mass": float(s.sum()),
                                "min": float(s.min()), "max": float(s.max())},
            "ke_final": checks[-1]["ke"],
            "note": "compare Editor HLSL vorticity run against these hashes; "
                    "tolerance: exact bit-match on CPU rerun, <1e-5 rel on GPU (float32)",
        }
        with open(os.path.join(OUT, "reference.json"), "w") as f:
            json.dump(ref, f, indent=1)
        print("frozen reference: ref_vel_final.npy / ref_s_final.npy / reference.json")

    d0 = checks[0]["max_abs_div_before"], checks[0]["max_abs_div_after"]
    d1 = checks[-1]["max_abs_div_before"], checks[-1]["max_abs_div_after"]
    print(f"done in {time.time()-t0:.1f}s")
    print(f"div first frame: {d0[0]:.3e} -> {d0[1]:.3e}")
    print(f"div last frame:  {d1[0]:.3e} -> {d1[1]:.3e}")
    print(f"max|vel| last: {checks[-1]['max_vel']:.3f}, scalar range: "
          f"[{checks[-1]['scalar_min']:.3f}, {checks[-1]['scalar_max']:.3f}]")

if __name__ == "__main__":
    scheme = sys.argv[1] if len(sys.argv) > 1 else "sl"
    vort_eps = float(sys.argv[2]) if len(sys.argv) > 2 else 0.0
    buoy_beta = float(sys.argv[3]) if len(sys.argv) > 3 else 0.0
    steps = int(sys.argv[4]) if len(sys.argv) > 4 else 240
    solver = sys.argv[5] if len(sys.argv) > 5 else "jacobi"
    iters = int(sys.argv[6]) if len(sys.argv) > 6 else 50
    chi_mode = sys.argv[7] if len(sys.argv) > 7 else "static"
    assert scheme in ("sl", "mc"), "usage: simulate.py [sl|mc] [vort_eps] [buoy_beta] [steps] [solver] [iters] [chi_mode]"
    assert solver in ("jacobi", "rbgs"), "solver must be jacobi or rbgs"
    assert chi_mode in ("static", "static-implicit", "sweep"), "chi_mode must be static, static-implicit or sweep"
    main(scheme, vort_eps, buoy_beta, steps, solver, iters, chi_mode)
