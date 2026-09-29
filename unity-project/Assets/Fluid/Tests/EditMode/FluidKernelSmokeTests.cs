using NUnit.Framework;
using UnityEditor;
using UnityEngine;

/// <summary>
/// CPU-side smoke test: every compute kernel the driver looks up must resolve.
/// Runs in batch mode without a GPU — FindKernel only needs the shader to have
/// compiled at import, which is exactly what this verifies (C# <-> shader
/// wiring + shader syntax). It does NOT dispatch anything.
/// </summary>
public class FluidKernelSmokeTests
{
    static readonly (string file, string[] kernels)[] kShaders =
    {
        ("advection", new[] { "AdvectScalar", "AdvectVector" }),
        ("advection_maccormack", new[] { "MacCormackScalar", "MacCormackVector" }),
        ("diffusion", new[] { "DiffuseScalar", "DiffuseVector" }),
        ("penalization", new[] { "Penalize" }),
        ("computedivergence", new[] { "ComputeDivergence" }),
        ("jacobbipressure", new[] { "JacobiPressure" }),
        ("subtractgradient", new[] { "SubtractGradient" }),
        ("vorticity", new[] { "VorticityConfinement" }),
        ("buoyancy", new[] { "InitTemperature", "ApplyBuoyancy" }),
        ("Forces", new[] { "ApplyForces" }),
    };

    [Test]
    public void AllDriverKernelsResolve()
    {
        foreach (var (file, kernels) in kShaders)
        {
            var path = $"Assets/Fluid/Shaders/{file}.compute";
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            Assert.IsNotNull(shader, $"ComputeShader asset failed to import: {path}");
            foreach (var kernel in kernels)
            {
                Assert.DoesNotThrow(() => shader.FindKernel(kernel),
                    $"{file}.compute: kernel '{kernel}' not found (C#/shader wiring mismatch)");
            }
        }
    }
}
