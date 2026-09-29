# Golden_Fleece — Fluid Preset Studio

Offline Navier-Stokes bake pipeline. Turns a GPU fluid solver into deterministic,
pre-baked fluid presets (smoke / fire / fog / water) shipped as flipbook/atlas
assets for XR — zero runtime simulation cost, pure playback.

The XR runtime stays "just play". Fluid is a content layer, not device control,
locomotion, or safety logic.

## Layout

```
cpu-baseline/          numpy reference implementation (ground truth)
  simulate.py          baseline solver: advect -> diffuse -> forces ->
                       penalize -> divergence -> 50x Jacobi -> subtract gradient
  test_advection.py    unit check: solid-body rotation, SL vs MacCormack
  verify_reference.py  CI gate: fresh bake vs frozen reference metrics
  reference/           frozen checks.json from canonical runs
  run_64.py, analyze_solver.py, ab_compare.py   solver-line analysis tooling
unity-package/         reconciled Unity package (compute shaders + driver)
  Scripts/FluidStepBaseline.cs    main driver (StepOnce/Reinit for verification)
  Scripts/Editor/FluidVerify.cs   Editor verification harness (edit mode, CSV)
  Scripts/FlipbookExporter.cs     frame capture
  Shaders/             advection (+MacCormack), diffusion, divergence,
                       Jacobi pressure, subtract-gradient, penalization,
                       vorticity confinement, buoyancy, forces
.github/workflows/
  cpu-bake.yml         free-tier CI: canonical bake + reference check
```

## Quick start

```bash
pip install numpy scipy matplotlib pillow
cd cpu-baseline
python3 simulate.py sl 0 0 240 jacobi 50 static   # canonical run -> output/
python3 verify_reference.py                       # compare output/ vs reference/
```

CLI args: `simulate.py [sl|mc] [vort_eps] [buoy_beta] [steps] [solver] [iters] [chi_mode]`

## Bake rules (verified, non-negotiable)

- **Solver: jacobi-50 stays.** RBGS is ~1.8x faster on isolated Poisson but the
  pipeline floor is discretization-limited (collocated grid:
  `div_h(grad_h p) != laplacian_h p`), not iteration-limited. No multigrid.
- **Projection fix:** `u -= grad(p)`, no `dt` (v0.2).
- **Static obstacles:** implicit penalization, `lambda = 1000`
  (`u_new = (u + dt*lambda*chi*u_wall) / (1 + dt*lambda*chi)`). The old
  lambda=1000 blowup was an explicit-scheme artifact.
- **Moving geometry:** hard limit `|u_wall| * dt/dx <= 0.1` cells/step.
  Faster gestures must be substepped offline. A 1 cell/step hand was tested
  and rejected: KE 1.5e5 -> 2.1e11 with no saturation (energy injection,
  not a projection artifact; staggered grids don't fix it).
- **Atlas warmup:** first ~20 steps are pressure warm start, excluded from atlases.
- **Vorticity / buoyancy:** provisional (`eps = 0.15`, `beta = 0.5`), retuned
  once at higher resolution. Ceilings come from the energy balance —
  projection only removes the potential part of a force with non-zero curl.
- **Open:** what a "frame" is. A mid-slice is a cross-section, not smoke;
  sprites need view-aligned volume projection with alpha from density
  (fire: color from temperature). Sprite size dictates grid resolution —
  blocks the 64-128^3 decision.

## Verification status

- CPU baseline: verified, frozen as `reference/canonical-*-checks.json`.
- GPU compute shaders: kernel-by-kernel static audit against the CPU
  reference passed (pipeline order, formulas, clamped boundaries, pressure
  warm start). Live Editor runs pending on a GPU machine.
- `unity-package/Scripts/Editor/FluidVerify.cs`: edit-mode harness,
  `FluidStudio/Verify` menu — baseline, then vorticity ON — dumps the same
  CSV metrics as `checks.json` for direct comparison.

## CI

`cpu-bake` runs on every push (GitHub free runners): the canonical 240-step
bake plus the advection unit check. `unity-smoke` runs when Unity files
change: checks that `unity-package/` and `unity-project/Assets/Fluid` are in
sync, then (once the `UNITY_LICENSE` secret is set) opens the project in the
Editor on a CPU runner — C# must compile, compute shaders must import, and
every kernel the driver looks up must resolve (EditMode test, no dispatch).
Unity/GPU verification runs on local hardware, not in CI (no GPU on free
runners).
