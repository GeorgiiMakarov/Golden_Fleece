# unity-package — Fluid Preset Studio v0.1 (baseline)

Reconciled, dependency-free Unity package extracted from the original
`Unity-NavierStokes-GPU` prototype. No UI, no WebSocket, no LLM code.

## Layout

```
unity-package/
├── Scripts/
│   ├── FluidStepBaseline.cs  # main driver (renamed, cleaned)
│   ├── FlipbookExporter.cs   # frame capture (UNTESTED IN EDITOR)
│   └── FluidForces.cs        # OPTIONAL force module, not in baseline loop
└── Shaders/
    ├── advection.compute      # verbatim from original
    ├── diffusion.compute      # verbatim from original
    ├── penalization.compute   # verbatim from original
    ├── computedivergence.compute
    ├── jacobbipressure.compute
    ├── subtractgradient.compute
    ├── advection_maccormack.compute  # NEW (upgrade #1): MacCormack correction
    │                                 # pass; forward SL still via advection.compute
    ├── vorticity.compute        # NEW (upgrade #2): vorticity confinement force
    ├── buoyancy.compute         # NEW (upgrade #3): InitTemperature + ApplyBuoyancy
    ├── Forces.compute         # optional module (known bug flagged in header)
    └── SliceCopy.shader       # new, for flipbook capture (UNTESTED)
```

## Baseline definition (v0.2)

- Operator splitting: Advect → Diffuse → Penalize → Projection
- Advection: semi-Lagrangian, trilinear, clamped
  (upgrade #1, default OFF: MacCormack + Selle clamp via `advection_maccormack.compute`,
  toggled by `useMacCormack` on the driver — CPU-validated 2026-09-27)
- Forces: none in baseline
  (upgrade #2, default OFF: vorticity confinement via `vorticity.compute`,
  toggled by `useVorticity` + `vorticityEpsilon` (0.15) — CPU-validated 2026-09-27;
  0.15 is the sustainable setting, 0.3 is richer but for short bakes only)
- Temperature/buoyancy: off in baseline
  (upgrade #3, default OFF: `buoyancy.compute` — `InitTemperature` + `ApplyBuoyancy`,
  toggled by `useBuoyancy` + `buoyancyBeta` (0.5) — CPU-validated 2026-09-27;
  temperature advected/diffused with the baseline shaders like dye;
  beta is PROVISIONAL — retune once after RBGS/multigrid and at bake resolution)
- Diffusion: explicit, `nu*dt/dx²`, boundary = copy input
- Penalization: explicit Brinkman, **static** hard cube chi
- Projection: central-difference divergence (clamped) → 50× Jacobi → subtract `∇p`
  (v0.2 fix: original `u −= dt·∇p` was inconsistent with Jacobi solving ∇²p = div
  without dt — damped the correction 50×; see Findings)
- Scene: constant +X flow, scalar dye left-half, centered cube obstacle, 32³

## Deviations from the original (documented)

1. `lambdaPen` default `1e3` → `25`. The original value is **unstable** under
   explicit penalization: `u_new = u·(1 − dt·λ·χ) = u·(1 − 20) = −19u` inside the
   obstacle. `25` gives `λ·dt = 0.5` (stable decay). Shader untouched.
2. Class renamed `FluidStep` → `FluidStepBaseline`; redundant double `dims`
   uniform sets removed; kernel/texture names unchanged.
3. v0.2: dt removed from `SubtractGradient` (`u −= ∇p`, was `u −= dt·∇p`).
   `JacobiPressure` solves ∇²p = div without dt, so the original pairing damped
   the correction 50× at dt=0.02. The `dt` uniform was removed from the shader.
3. `ClearRenderTexture` kept as-is (GL.Clear on 3D RT is driver-dependent);
   fresh RenderTextures are zero-filled, so this is cosmetic.

## Known original bugs (flagged, not fixed — upgrade-stage decisions)

- `Forces.compute`: `linearForce` computed but never applied (only vortex).
- The second `FluidStep` variant's `Update()` ran forces but never the simulation.

## How to open

Unity 2021+ with compute-shader support (DX11+/Vulkan/Metal). Create an empty
3D project, copy `Scripts/` and `Shaders/` under `Assets/FluidBaseline/`,
attach `FluidStepBaseline` to a GameObject, assign the six baseline compute shaders
(+ `advection_maccormack.compute`, `vorticity.compute`, `buoyancy.compute` for
upgrades #1–3 — all default OFF),
press Play. Attach `FlipbookExporter` + a material using `SliceCopy.shader`
to capture frames; pack the PNG sequence into an atlas offline
(see `../cpu-baseline/simulate.py` for the reference 12×10 packing).

## Upgrade order (after baseline is validated)

1. MacCormack advection + clamp → 2. vorticity confinement →
3. buoyancy + temperature field → 4. dynamic chi →
5. Red-Black Gauss-Seidel / multigrid replacing Jacobi → 6. resolution 128–256.
Each upgrade is A/B-compared against the baseline flipbook before merging.
