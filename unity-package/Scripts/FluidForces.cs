using UnityEngine;

/// <summary>
/// FluidForces — OPTIONAL module, NOT part of the baseline loop (v0.1).
///
/// Faithful port of the force system from the second (conflicting) FluidStep
/// variant of the original prototype: a ComputeBuffer of up to 100 forces,
/// each either a linear force or a vortex (axis * strength), applied with a
/// Gaussian falloff kernel.
///
/// KNOWN ORIGINAL BUG (flagged, not fixed — fix is an upgrade-stage decision):
///   Forces.compute computes `linearForce` but never applies it —
///   only `u += vortexForce;` runs. So SetForceAtPosition() currently does
///   nothing visible. Uncomment the linear term in the shader to enable.
///
/// Wiring suggestion (upgrade stage): call ApplyForces() from
/// FluidStepBaseline AFTER the projection step, targeting velocityTex with a
/// ping-pong, instead of the original's standalone Update() which never ran
/// the actual simulation.
/// </summary>
public class FluidForces : MonoBehaviour
{
    [Header("Wiring")]
    public FluidStepBaseline fluid;   // source of velocityTex + dims
    public ComputeShader forcesShader; // kernel "ApplyForces"

    [Header("Settings")]
    public float forceRadius = 3.0f;
    public int maxForces = 100;

    struct ForceGPU
    {
        public Vector3 position; public float pad0;
        public Vector3 force;    public float pad1;
        public Vector3 vortexAxis; public float pad2;
    }

    private ComputeBuffer forceBuffer;
    private ForceGPU[] forceData;
    private int forceCount = 0;

    void Start()
    {
        forceBuffer = new ComputeBuffer(maxForces, sizeof(float) * 12);
        forceData = new ForceGPU[maxForces];
    }

    /// <summary>Queue a linear force at normalized position (0..1 per axis).</summary>
    public void SetForceAtPosition(Vector3 position, Vector3 force)
    {
        if (forceCount >= maxForces) return;
        forceData[forceCount].position = position;
        forceData[forceCount].force = force;
        forceData[forceCount].vortexAxis = Vector3.zero;
        forceCount++;
    }

    /// <summary>Queue a vortex at normalized position (0..1 per axis).</summary>
    public void SetVortex(Vector3 position, Vector3 axis, float strength)
    {
        if (forceCount >= maxForces) return;
        forceData[forceCount].position = position;
        forceData[forceCount].force = Vector3.zero;
        forceData[forceCount].vortexAxis = axis.normalized * strength;
        forceCount++;
    }

    /// <summary>
    /// Applies queued forces to the baseline velocity field (ping-pong).
    /// NOT called automatically — wire into FluidStepBaseline when needed.
    /// </summary>
    public void ApplyForces()
    {
        if (forceCount == 0 || fluid == null || fluid.velocityTex == null) return;

        forceBuffer.SetData(forceData);

        int kernel = forcesShader.FindKernel("ApplyForces");
        forcesShader.SetBuffer(kernel, "Forces", forceBuffer);
        forcesShader.SetInt("forceCount", forceCount);
        forcesShader.SetFloat("forceRadius", forceRadius);
        forcesShader.SetFloat("dt", fluid.dt);
        int res = fluid.resolution;
        forcesShader.SetInts("dims", new int[] { res, res, res });
        forcesShader.SetTexture(kernel, "Velocity", fluid.velocityTex);
        forcesShader.SetTexture(kernel, "Result", fluid.velocityTmpTex);
        forcesShader.Dispatch(kernel,
            Mathf.CeilToInt(res / 8.0f), Mathf.CeilToInt(res / 8.0f), Mathf.CeilToInt(res / 8.0f));

        // ping-pong back
        var t = fluid.velocityTex;
        fluid.velocityTex = fluid.velocityTmpTex;
        fluid.velocityTmpTex = t;

        forceCount = 0;
    }

    void OnDestroy()
    {
        if (forceBuffer != null) forceBuffer.Release();
    }
}
