using UnityEngine;

/// <summary>
/// FluidStepBaseline — reconciled baseline driver for Fluid Preset Studio v0.1.
///
/// Scope: the ORIGINAL numerics from Unity-NavierStokes-GPU, extracted into a
/// clean package with no UI and no WebSocket dependencies:
///   Advect (semi-Lagrangian) -> Diffuse (explicit) -> Penalize (static chi)
///   -> Projection (divergence -> 50x Jacobi -> subtract gradient)
///
/// One documented deviation from the original:
///   BUGFIX 2026-09-27 — original lambdaPen = 1e3 makes the EXPLICIT penalization
///   unstable: u_new = u * (1 - dt*lambda*chi) = u * (1 - 20) = -19u inside the
///   obstacle. Default is now 25 (lambda*dt = 0.5, stable decay). The shader is
///   untouched; only the default uniform value changed.
///   BUGFIX 2026-09-27 (v0.2) — dt removed from SubtractGradient. JacobiPressure
///   solves ∇²p = div WITHOUT dt, so the consistent step is u −= ∇p; the original
///   u −= dt·∇p damped the correction 50x, making projection a near no-op.
///
/// Deliberately NOT in the baseline loop (see roadmap):
///   - Force system (FluidForces.cs, optional module, not wired in)
///   - vorticity confinement, buoyancy, dynamic chi
///   - WebSocket / remote control
///
/// Upgrade #1 (MacCormack + clamp) is PORTED but default OFF: set useMacCormack
/// to enable. Forward pass reuses advection.compute; the correction pass is
/// advection_maccormack.compute (MacCormackScalar/MacCormackVector).
/// CPU A/B 2026-09-27: safe (scalar stays in [0,1], div on par with baseline);
/// visual payoff compounds with vorticity + buoyancy.
/// Upgrade #2 (vorticity confinement) is PORTED but default OFF: set useVorticity.
/// Upgrade #3 (buoyancy + temperature) is PORTED but default OFF: set useBuoyancy.
/// beta=0.5 is PROVISIONAL — tuned on 32^3 vs 50 Jacobi iters; retune once after
/// RBGS/multigrid (upgrade #5) and at target bake resolution.
/// </summary>
public class FluidStepBaseline : MonoBehaviour
{
    [Header("Compute Shaders")]
    public ComputeShader advectionShader;     // AdvectScalar, AdvectVector
    public ComputeShader diffusionShader;     // DiffuseScalar, DiffuseVector
    public ComputeShader penalizationShader;  // Penalize
    public ComputeShader divergenceShader;    // ComputeDivergence
    public ComputeShader pressureShader;      // JacobiPressure
    public ComputeShader gradientShader;      // SubtractGradient

    [Header("Upgrade #1: MacCormack (optional)")]
    [Tooltip("advection_maccormack.compute — MacCormackScalar, MacCormackVector. "
           + "Validated on CPU; default OFF = baseline semi-Lagrangian.")]
    public ComputeShader maccormackShader;
    public bool useMacCormack = false;

    [Header("Upgrade #2: Vorticity confinement (optional)")]
    [Tooltip("vorticity.compute — VorticityConfinement. CPU-validated: epsilon=0.15 "
           + "is the sustainable setting (flat div/KE at 600 steps); 0.3 looks richer "
           + "but slowly accumulates energy (OK for short bakes only). Default OFF.")]
    public ComputeShader vorticityShader;
    public bool useVorticity = false;
    public float vorticityEpsilon = 0.15f;

    [Header("Upgrade #3: Buoyancy + temperature (optional)")]
    [Tooltip("buoyancy.compute — InitTemperature, ApplyBuoyancy. Temperature is advected/"
           + "diffused with the baseline shaders. beta=0.5 is PROVISIONAL (tuned on 32^3 vs "
           + "50 Jacobi; retune once after RBGS/multigrid and at bake resolution). Default OFF.")]
    public ComputeShader buoyancyShader;
    public bool useBuoyancy = false;
    public float buoyancyBeta = 0.5f;

    [Header("Simulation Settings (baseline v0.1)")]
    [Tooltip("Grid resolution (NxNxN)")]
    public int resolution = 32;
    public float dx = 1.0f;
    public float dt = 0.02f;
    public float nu = 0.001f;

    [Tooltip("BUGFIX: original 1e3 is unstable with explicit penalization. 25 => lambda*dt = 0.5.")]
    public float lambdaPen = 25f;

    public int pressureIters = 50;

    [Header("Textures (optional assign)")]
    public RenderTexture velocityTex;     // ARGBFloat: velocity (x,y,z,_)
    public RenderTexture velocityTmpTex;  // ARGBFloat ping-pong
    public RenderTexture scalarTex;       // RFloat: scalar (dye/temperature)
    public RenderTexture scalarTmpTex;    // RFloat ping-pong
    public RenderTexture chiTex;          // RFloat: obstacle mask (0..1)

    private RenderTexture divTex;
    private RenderTexture pTex;
    private RenderTexture pTmpTex;
    private RenderTexture velocityMcTex;  // ARGBFloat: MacCormack correction output
    private RenderTexture scalarMcTex;    // RFloat: MacCormack correction output
    private RenderTexture temperatureTex;    // RFloat: temperature field (upgrade #3)
    private RenderTexture temperatureTmpTex; // RFloat ping-pong

    [Header("Visualization")]
    public Material volumeMaterial; // expects _VolumeTex (3D) for scalar visual

    private int advectScalarK, advectVectorK;
    private int diffuseScalarK, diffuseVectorK;
    private int penalizeK;
    private int divergenceK;
    private int pressureK;
    private int gradientK;
    private int mcScalarK, mcVectorK;
    private int vorticityK;
    private int buoyancyK, initTempK;

    private int threadGroups;

    void Start()
    {
        InitializeKernels();
        CreateAndInitTextures();
        SetShaderCommonParams();
        UpdateMaterialTexture();
    }

    void OnValidate()
    {
        if (Application.isPlaying) SetShaderCommonParams();
    }

    void InitializeKernels()
    {
        if (advectionShader != null)
        {
            advectScalarK = advectionShader.FindKernel("AdvectScalar");
            advectVectorK = advectionShader.FindKernel("AdvectVector");
        }
        if (diffusionShader != null)
        {
            diffuseScalarK = diffusionShader.FindKernel("DiffuseScalar");
            diffuseVectorK = diffusionShader.FindKernel("DiffuseVector");
        }
        if (penalizationShader != null)
            penalizeK = penalizationShader.FindKernel("Penalize");
        if (divergenceShader != null)
            divergenceK = divergenceShader.FindKernel("ComputeDivergence");
        if (pressureShader != null)
            pressureK = pressureShader.FindKernel("JacobiPressure");
        if (gradientShader != null)
            gradientK = gradientShader.FindKernel("SubtractGradient");
        if (maccormackShader != null)
        {
            mcScalarK = maccormackShader.FindKernel("MacCormackScalar");
            mcVectorK = maccormackShader.FindKernel("MacCormackVector");
        }
        if (vorticityShader != null)
            vorticityK = vorticityShader.FindKernel("VorticityConfinement");
        if (buoyancyShader != null)
        {
            buoyancyK = buoyancyShader.FindKernel("ApplyBuoyancy");
            initTempK = buoyancyShader.FindKernel("InitTemperature");
        }

        threadGroups = Mathf.CeilToInt(resolution / 8.0f);
    }

    void CreateAndInitTextures()
    {
        if (velocityTex == null) velocityTex = Create3DTexture(RenderTextureFormat.ARGBFloat);
        if (velocityTmpTex == null) velocityTmpTex = Create3DTexture(RenderTextureFormat.ARGBFloat);
        if (scalarTex == null) scalarTex = Create3DTexture(RenderTextureFormat.RFloat);
        if (scalarTmpTex == null) scalarTmpTex = Create3DTexture(RenderTextureFormat.RFloat);
        if (chiTex == null) chiTex = Create3DTexture(RenderTextureFormat.RFloat);

        divTex = Create3DTexture(RenderTextureFormat.RFloat);
        pTex = Create3DTexture(RenderTextureFormat.RFloat);
        pTmpTex = Create3DTexture(RenderTextureFormat.RFloat);
        velocityMcTex = Create3DTexture(RenderTextureFormat.ARGBFloat);
        scalarMcTex = Create3DTexture(RenderTextureFormat.RFloat);
        temperatureTex = Create3DTexture(RenderTextureFormat.RFloat);
        temperatureTmpTex = Create3DTexture(RenderTextureFormat.RFloat);

        InitializeVelocityTexture();
        InitializeScalarTexture();
        InitializeChiTexture();
        InitializeTemperatureTexture();

        ClearRenderTexture(pTex, 0f);
        ClearRenderTexture(pTmpTex, 0f);
        ClearRenderTexture(divTex, 0f);
    }

    RenderTexture Create3DTexture(RenderTextureFormat format)
    {
        var rt = new RenderTexture(resolution, resolution, 0, format)
        {
            dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
            volumeDepth = resolution,
            enableRandomWrite = true,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        rt.Create();
        return rt;
    }

    void InitializeVelocityTexture()
    {
        // Baseline scene: constant flow to +X: (1,0,0)
        Texture3D tex3D = new Texture3D(resolution, resolution, resolution, TextureFormat.RGBAFloat, false);
        Color[] cols = new Color[resolution * resolution * resolution];
        Color vcol = new Color(1f, 0f, 0f, 0f);
        for (int i = 0; i < cols.Length; i++) cols[i] = vcol;
        tex3D.SetPixels(cols);
        tex3D.Apply();
        Graphics.CopyTexture(tex3D, 0, velocityTex, 0);
        Graphics.CopyTexture(tex3D, 0, velocityTmpTex, 0);
    }

    void InitializeScalarTexture()
    {
        // Baseline scene: left half = 1, right half = 0 (in x)
        Texture3D tex3D = new Texture3D(resolution, resolution, resolution, TextureFormat.RFloat, false);
        Color[] cols = new Color[resolution * resolution * resolution];
        int half = resolution / 2;
        for (int z = 0; z < resolution; z++)
            for (int y = 0; y < resolution; y++)
                for (int x = 0; x < resolution; x++)
                {
                    int idx = x + y * resolution + z * resolution * resolution;
                    cols[idx] = (x < half) ? new Color(1f, 0f, 0f, 1f) : new Color(0f, 0f, 0f, 0f);
                }
        tex3D.SetPixels(cols);
        tex3D.Apply();
        Graphics.CopyTexture(tex3D, 0, scalarTex, 0);
        Graphics.CopyTexture(tex3D, 0, scalarTmpTex, 0);
    }

    void InitializeChiTexture()
    {
        // Baseline scene: static centered cube obstacle, chi = 1 inside, 0 outside
        Texture3D tex3D = new Texture3D(resolution, resolution, resolution, TextureFormat.RFloat, false);
        Color[] cols = new Color[resolution * resolution * resolution];
        int a = resolution / 3;
        int b = 2 * resolution / 3;
        for (int z = 0; z < resolution; z++)
            for (int y = 0; y < resolution; y++)
                for (int x = 0; x < resolution; x++)
                {
                    int idx = x + y * resolution + z * resolution * resolution;
                    bool inside = (x >= a && x < b && y >= a && y < b && z >= a && z < b);
                    cols[idx] = inside ? new Color(1f, 0f, 0f, 1f) : new Color(0f, 0f, 0f, 0f);
                }
        tex3D.SetPixels(cols);
        tex3D.Apply();
        Graphics.CopyTexture(tex3D, 0, chiTex, 0);
    }

    void InitializeTemperatureTexture()
    {
        // Upgrade #3: hot sphere via the InitTemperature kernel (resolution-relative,
        // matches the CPU reference). Falls back to zeros if the shader is unassigned.
        if (buoyancyShader == null)
        {
            ClearRenderTexture(temperatureTex, 0f);
            return;
        }
        buoyancyShader.SetInts("dims", new int[] { resolution, resolution, resolution });
        buoyancyShader.SetTexture(initTempK, "InitResult", temperatureTex);
        int tg = threadGroups;
        buoyancyShader.Dispatch(initTempK, tg, tg, tg);
        ClearRenderTexture(temperatureTmpTex, 0f);
    }

    void ClearRenderTexture(RenderTexture rt, float value)
    {
        if (rt == null) return;
        // NOTE: GL.Clear on a 3D RenderTexture only reliably clears slice 0 on some
        // drivers; kept from the original — revisit if pressure/divergence show
        // non-zero initial state. Fresh RenderTextures are zero-filled anyway.
        RenderTexture active = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, new Color(value, value, value, value));
        RenderTexture.active = active;
    }

    /// <summary>
    /// Re-creates and re-initializes all textures (clean state for a verification run).
    /// </summary>
    public void Reinit()
    {
        CreateAndInitTextures();
        SetShaderCommonParams();
        UpdateMaterialTexture();
    }

    void SetShaderCommonParams()
    {
        int[] dims = new int[] { resolution, resolution, resolution };
        if (advectionShader != null)
        {
            advectionShader.SetInts("dims", dims);
            advectionShader.SetFloat("dt", dt);
            advectionShader.SetFloat("dx", dx);
        }
        if (diffusionShader != null)
        {
            diffusionShader.SetInts("dims", dims);
            diffusionShader.SetFloat("nu", nu);
            diffusionShader.SetFloat("dt", dt);
            diffusionShader.SetFloat("dx", dx);
        }
        if (penalizationShader != null)
        {
            penalizationShader.SetInts("dims", dims);
            penalizationShader.SetFloat("lambda_pen", lambdaPen);
            penalizationShader.SetFloat("dt", dt);
        }
        if (divergenceShader != null)
        {
            divergenceShader.SetInts("dims", dims);
            divergenceShader.SetFloat("dx", dx);
        }
        if (pressureShader != null)
        {
            pressureShader.SetInts("dims", dims);
            pressureShader.SetFloat("dx", dx);
        }
        if (gradientShader != null)
        {
            gradientShader.SetInts("dims", dims);
            gradientShader.SetFloat("dx", dx);
            // v0.2: no dt — see SubtractGradient.compute header
        }
        if (maccormackShader != null)
        {
            maccormackShader.SetInts("dims", dims);
            maccormackShader.SetFloat("dt", dt);
            maccormackShader.SetFloat("dx", dx);
        }
        if (vorticityShader != null)
        {
            vorticityShader.SetInts("dims", dims);
            vorticityShader.SetFloat("dx", dx);
            vorticityShader.SetFloat("dt", dt);
            vorticityShader.SetFloat("epsilon", vorticityEpsilon);
        }
        if (buoyancyShader != null)
        {
            buoyancyShader.SetFloat("dt", dt);
            buoyancyShader.SetFloat("beta", buoyancyBeta);
            buoyancyShader.SetFloat("t_ambient", 0f);
        }
    }

    void Update()
    {
        if (!Application.isPlaying) return;
        StepOnce();
    }

    /// <summary>
    /// One full simulation step. Public so the Editor verification harness
    /// (FluidVerify) can drive the sim deterministically outside play mode:
    /// baseline first, then vorticity ON, metrics read back per frame.
    /// </summary>
    public void StepOnce()
    {
        SetShaderCommonParams();

        int tg = threadGroups;

        // 1. Advection: velocity (self) then scalar.
        // MacCormack (upgrade #1, useMacCormack): forward SL into tmp, then the
        // correction pass reads (original, forward SL) and writes the corrected
        // field into the MC temp texture (no in-place hazard: the corrector
        // samples neighborhoods of both inputs).
        bool mc = useMacCormack && maccormackShader != null;
        if (advectionShader != null)
        {
            advectionShader.SetTexture(advectVectorK, "FieldVec", velocityTex);
            advectionShader.SetTexture(advectVectorK, "VelocityRGBA", velocityTex);
            advectionShader.SetTexture(advectVectorK, "ResultVec", velocityTmpTex);
            advectionShader.Dispatch(advectVectorK, tg, tg, tg);
            if (mc)
            {
                maccormackShader.SetTexture(mcVectorK, "FieldVec", velocityTex);
                maccormackShader.SetTexture(mcVectorK, "FieldVecSL", velocityTmpTex);
                maccormackShader.SetTexture(mcVectorK, "VelocityRGBA", velocityTex);
                maccormackShader.SetTexture(mcVectorK, "ResultVec", velocityMcTex);
                maccormackShader.Dispatch(mcVectorK, tg, tg, tg);
                Swap(ref velocityTex, ref velocityMcTex);
            }
            else
            {
                Swap(ref velocityTex, ref velocityTmpTex);
            }

            advectionShader.SetTexture(advectScalarK, "VelocityRGBA", velocityTex);
            advectionShader.SetTexture(advectScalarK, "Field", scalarTex);
            advectionShader.SetTexture(advectScalarK, "ResultField", scalarTmpTex);
            advectionShader.Dispatch(advectScalarK, tg, tg, tg);
            if (mc)
            {
                maccormackShader.SetTexture(mcScalarK, "Field", scalarTex);
                maccormackShader.SetTexture(mcScalarK, "FieldSL", scalarTmpTex);
                maccormackShader.SetTexture(mcScalarK, "VelocityRGBA", velocityTex);
                maccormackShader.SetTexture(mcScalarK, "ResultField", scalarMcTex);
                maccormackShader.Dispatch(mcScalarK, tg, tg, tg);
                Swap(ref scalarTex, ref scalarMcTex);
            }
            else
            {
                Swap(ref scalarTex, ref scalarTmpTex);
            }

            // temperature (upgrade #3): advected with the new velocity, like dye.
            // Reuses the scalar kernels; in MC mode the correction output goes
            // through scalarMcTex (stale at this point) — no extra texture needed.
            if (useBuoyancy && buoyancyShader != null)
            {
                advectionShader.SetTexture(advectScalarK, "VelocityRGBA", velocityTex);
                advectionShader.SetTexture(advectScalarK, "Field", temperatureTex);
                advectionShader.SetTexture(advectScalarK, "ResultField", temperatureTmpTex);
                advectionShader.Dispatch(advectScalarK, tg, tg, tg);
                if (mc)
                {
                    maccormackShader.SetTexture(mcScalarK, "Field", temperatureTex);
                    maccormackShader.SetTexture(mcScalarK, "FieldSL", temperatureTmpTex);
                    maccormackShader.SetTexture(mcScalarK, "VelocityRGBA", velocityTex);
                    maccormackShader.SetTexture(mcScalarK, "ResultField", scalarMcTex);
                    maccormackShader.Dispatch(mcScalarK, tg, tg, tg);
                    Swap(ref temperatureTex, ref scalarMcTex);
                }
                else
                {
                    Swap(ref temperatureTex, ref temperatureTmpTex);
                }
            }
        }

        // 2. Diffusion: velocity then scalar
        if (diffusionShader != null)
        {
            diffusionShader.SetTexture(diffuseVectorK, "FieldVec", velocityTex);
            diffusionShader.SetTexture(diffuseVectorK, "ResultVec", velocityTmpTex);
            diffusionShader.Dispatch(diffuseVectorK, tg, tg, tg);
            Swap(ref velocityTex, ref velocityTmpTex);

            diffusionShader.SetTexture(diffuseScalarK, "Field", scalarTex);
            diffusionShader.SetTexture(diffuseScalarK, "ResultField", scalarTmpTex);
            diffusionShader.Dispatch(diffuseScalarK, tg, tg, tg);
            Swap(ref scalarTex, ref scalarTmpTex);

            // temperature diffusion (upgrade #3)
            if (useBuoyancy && buoyancyShader != null)
            {
                diffusionShader.SetTexture(diffuseScalarK, "Field", temperatureTex);
                diffusionShader.SetTexture(diffuseScalarK, "ResultField", temperatureTmpTex);
                diffusionShader.Dispatch(diffuseScalarK, tg, tg, tg);
                Swap(ref temperatureTex, ref temperatureTmpTex);
            }
        }

        // 2b. Vorticity confinement (upgrade #2, default OFF).
        // Force between diffusion and penalization, mirroring the CPU reference.
        if (useVorticity && vorticityShader != null && vorticityEpsilon > 0f)
        {
            vorticityShader.SetTexture(vorticityK, "Velocity", velocityTex);
            vorticityShader.SetTexture(vorticityK, "Result", velocityTmpTex);
            vorticityShader.Dispatch(vorticityK, tg, tg, tg);
            Swap(ref velocityTex, ref velocityTmpTex);
        }

        // 2c. Buoyancy force (upgrade #3, default OFF).
        // CPU order: after vorticity confinement, before penalization.
        if (useBuoyancy && buoyancyShader != null && buoyancyBeta > 0f)
        {
            buoyancyShader.SetTexture(buoyancyK, "Velocity", velocityTex);
            buoyancyShader.SetTexture(buoyancyK, "Temperature", temperatureTex);
            buoyancyShader.SetTexture(buoyancyK, "Result", velocityTmpTex);
            buoyancyShader.Dispatch(buoyancyK, tg, tg, tg);
            Swap(ref velocityTex, ref velocityTmpTex);
        }

        // 3. Penalization (static chi)
        if (penalizationShader != null)
        {
            penalizationShader.SetTexture(penalizeK, "Velocity", velocityTex);
            penalizationShader.SetTexture(penalizeK, "Chi", chiTex);
            penalizationShader.SetTexture(penalizeK, "Result", velocityTmpTex);
            penalizationShader.Dispatch(penalizeK, tg, tg, tg);
            Swap(ref velocityTex, ref velocityTmpTex);
        }

        // 4. Projection: divergence -> Jacobi pressure -> subtract gradient
        if (divergenceShader != null && pressureShader != null && gradientShader != null)
        {
            divergenceShader.SetTexture(divergenceK, "Velocity", velocityTex);
            divergenceShader.SetTexture(divergenceK, "Result", divTex);
            divergenceShader.Dispatch(divergenceK, tg, tg, tg);

            for (int it = 0; it < pressureIters; it++)
            {
                pressureShader.SetTexture(pressureK, "Divergence", divTex);
                pressureShader.SetTexture(pressureK, "Pressure", pTex);
                pressureShader.SetTexture(pressureK, "Result", pTmpTex);
                pressureShader.Dispatch(pressureK, tg, tg, tg);
                Swap(ref pTex, ref pTmpTex);
            }

            gradientShader.SetTexture(gradientK, "Velocity", velocityTex);
            gradientShader.SetTexture(gradientK, "Pressure", pTex);
            gradientShader.SetTexture(gradientK, "Result", velocityTmpTex);
            gradientShader.Dispatch(gradientK, tg, tg, tg);
            Swap(ref velocityTex, ref velocityTmpTex);
        }

        UpdateMaterialTexture();
    }

    void UpdateMaterialTexture()
    {
        if (volumeMaterial != null) volumeMaterial.SetTexture("_VolumeTex", scalarTex);
    }

    void Swap(ref RenderTexture a, ref RenderTexture b)
    {
        var t = a; a = b; b = t;
    }

    private void OnDestroy()
    {
        ReleaseRT(velocityTex);
        ReleaseRT(velocityTmpTex);
        ReleaseRT(scalarTex);
        ReleaseRT(scalarTmpTex);
        ReleaseRT(chiTex);
        ReleaseRT(divTex);
        ReleaseRT(pTex);
        ReleaseRT(pTmpTex);
        ReleaseRT(velocityMcTex);
        ReleaseRT(scalarMcTex);
        ReleaseRT(temperatureTex);
        ReleaseRT(temperatureTmpTex);
    }

    void ReleaseRT(RenderTexture rt)
    {
        if (rt != null)
        {
            if (Application.isPlaying) rt.Release();
            DestroyImmediate(rt);
        }
    }
}
