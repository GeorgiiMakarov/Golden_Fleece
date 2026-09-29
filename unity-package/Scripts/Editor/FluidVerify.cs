// FluidVerify.cs — Editor verification harness for Fluid Preset Studio.
//
// ONE Editor session, two clicks:
//   FluidStudio/Verify/1. Baseline (SL, vorticity OFF)   -> verify_baseline.csv
//   FluidStudio/Verify/2. Vorticity ON (eps = 0.15)        -> verify_vorticity.csv
//
// Each run: fresh FluidStepBaseline (32^3, dt=0.02, dx=1, nu=0.001,
// lambdaPen=25, 50 Jacobi iters, warm-started pressure), 240 steps driven
// deterministically via StepOnce() in EDIT mode (no play mode needed).
// Every 2nd step the velocity + dye volumes are read back and metrics are
// written to CSV, mirroring cpu-baseline checks.json columns:
//   step, div_after, ke, max_vel, dye_min, dye_max, dye_mass, mean_abs_vort
//
// Compare against the CPU references:
//   baseline      <-> cpu-baseline/output-sl/checks.json  (SL, no vorticity)
//   vorticity ON  <-> cpu-baseline/output-sl-vort0.15/checks.json
// GPU is float32 vs CPU float64: expect close trajectories (div/KE within a
// few %), NOT bit-identity. A divergence of trajectories or a div_after that
// stays an order of magnitude above the CPU reference means a port bug.
//
// NOTE: written without access to a GPU/Unity Editor — compiled by eye against
// the Unity API. If a readback line throws, paste the error and it gets fixed.

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Text;

public static class FluidVerify
{
    const int STEPS = 240;
    const int CAPTURE_EVERY = 2;

    [MenuItem("FluidStudio/Verify/1. Baseline (SL, vorticity OFF)")]
    public static void RunBaseline()
    {
        Run("baseline", useVorticity: false);
    }

    [MenuItem("FluidStudio/Verify/2. Vorticity ON (eps = 0.15)")]
    public static void RunVorticity()
    {
        Run("vorticity", useVorticity: true);
    }

    static void Run(string mode, bool useVorticity)
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FluidVerify] run from EDIT mode (not play mode).");
            return;
        }

        var sim = new GameObject("FluidVerifySim").AddComponent<FluidStepBaseline>();
        try
        {
            sim.advectionShader   = FindCompute("advection");
            sim.diffusionShader   = FindCompute("diffusion");
            sim.penalizationShader= FindCompute("penalization");
            sim.divergenceShader  = FindCompute("computedivergence");
            sim.pressureShader    = FindCompute("jacobbipressure"); // note: filename typo is real
            sim.gradientShader    = FindCompute("subtractgradient");
            sim.vorticityShader   = FindCompute("vorticity");

            sim.resolution = 32;
            sim.dx = 1.0f;
            sim.dt = 0.02f;
            sim.nu = 0.001f;
            sim.lambdaPen = 25f;
            sim.pressureIters = 50;
            sim.useMacCormack = false;
            sim.useVorticity = useVorticity;
            sim.vorticityEpsilon = 0.15f;
            sim.useBuoyancy = false;

            sim.Reinit();

            int N = sim.resolution;
            var velStage = new Texture3D(N, N, N, TextureFormat.RGBAFloat, false);
            var dyeStage = new Texture3D(N, N, N, TextureFormat.RFloat, false);

            var sb = new StringBuilder();
            sb.AppendLine("step,div_after,ke,max_vel,dye_min,dye_max,dye_mass,mean_abs_vort");

            for (int step = 0; step < STEPS; step++)
            {
                sim.StepOnce();
                if (step % CAPTURE_EVERY != 0) continue;

                Graphics.CopyTexture(sim.velocityTex, 0, velStage, 0);
                Graphics.CopyTexture(sim.scalarTex, 0, dyeStage, 0);
                Color[] vpx = velStage.GetPixels();
                Color[] dpx = dyeStage.GetPixels();

                float divAfter = MaxAbsDiv(vpx, N);
                float ke = 0f, maxV = 0f, vortMean = 0f;
                float dMin = float.MaxValue, dMax = float.MinValue, dMass = 0f;
                for (int i = 0; i < vpx.Length; i++)
                {
                    float vx = vpx[i].r, vy = vpx[i].g, vz = vpx[i].b;
                    float s2 = vx * vx + vy * vy + vz * vz;
                    ke += 0.5f * s2;
                    float m = Mathf.Sqrt(s2);
                    if (m > maxV) maxV = m;
                    float d = dpx[i].r;
                    if (d < dMin) dMin = d;
                    if (d > dMax) dMax = d;
                    dMass += d;
                }
                vortMean = MeanAbsVort(vpx, N);

                sb.AppendLine($"{step},{divAfter:G6},{ke:G6},{maxV:G6},{dMin:G6},{dMax:G6},{dMass:G6},{vortMean:G6}");
            }

            string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(
                MonoScript.FromMonoBehaviour(sim)));
            // dir is .../Scripts ; verification CSVs go next to the package root
            string pkgRoot = Path.GetFullPath(Path.Combine(dir, ".."));
            string csvPath = Path.Combine(pkgRoot, $"verify_{mode}.csv");
            File.WriteAllText(csvPath, sb.ToString());
            Debug.Log($"[FluidVerify] {mode}: 240 steps done, CSV -> {csvPath}");
        }
        finally
        {
            Object.DestroyImmediate(sim.gameObject);
        }
    }

    static ComputeShader FindCompute(string name)
    {
        string[] guids = AssetDatabase.FindAssets(name + " t:ComputeShader");
        foreach (string g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            if (Path.GetFileNameWithoutExtension(path) == name)
                return AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
        }
        Debug.LogError($"[FluidVerify] compute shader '{name}' not found in project.");
        return null;
    }

    static float At(Color[] v, int N, int x, int y, int z, int c)
    {
        x = Mathf.Clamp(x, 0, N - 1);
        y = Mathf.Clamp(y, 0, N - 1);
        z = Mathf.Clamp(z, 0, N - 1);
        Color p = v[x + y * N + z * N * N];
        return c == 0 ? p.r : (c == 1 ? p.g : p.b);
    }

    static float MaxAbsDiv(Color[] v, int N)
    {
        float m = 0f;
        for (int z = 0; z < N; z++)
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dudx = (At(v, N, x + 1, y, z, 0) - At(v, N, x - 1, y, z, 0)) * 0.5f;
                    float dvdy = (At(v, N, x, y + 1, z, 1) - At(v, N, x, y - 1, z, 1)) * 0.5f;
                    float dwdz = (At(v, N, x, y, z + 1, 2) - At(v, N, x, y, z - 1, 2)) * 0.5f;
                    float d = Mathf.Abs(dudx + dvdy + dwdz);
                    if (d > m) m = d;
                }
        return m;
    }

    static float MeanAbsVort(Color[] v, int N)
    {
        // |curl(u)|, central differences, clamped edges — mirrors CPU curl()
        double acc = 0;
        for (int z = 0; z < N; z++)
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dwdy = (At(v, N, x, y + 1, z, 2) - At(v, N, x, y - 1, z, 2)) * 0.5f;
                    float dvdz = (At(v, N, x, y, z + 1, 1) - At(v, N, x, y, z - 1, 1)) * 0.5f;
                    float dudz = (At(v, N, x, y, z + 1, 0) - At(v, N, x, y, z - 1, 0)) * 0.5f;
                    float dwdx = (At(v, N, x + 1, y, z, 2) - At(v, N, x - 1, y, z, 2)) * 0.5f;
                    float dvdx = (At(v, N, x + 1, y, z, 1) - At(v, N, x - 1, y, z, 1)) * 0.5f;
                    float dudy = (At(v, N, x, y + 1, z, 0) - At(v, N, x, y - 1, z, 0)) * 0.5f;
                    float omx = dwdy - dvdz;
                    float omy = dudz - dwdx;
                    float omz = dvdx - dudy;
                    acc += System.Math.Sqrt(omx * omx + omy * omy + omz * omz);
                }
        return (float)(acc / (N * N * N));
    }
}
#endif
