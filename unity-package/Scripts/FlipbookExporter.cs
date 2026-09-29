using UnityEngine;
using System.IO;

/// <summary>
/// FlipbookExporter — baseline frame capture for Fluid Preset Studio v0.1.
///
/// UNTESTED IN EDITOR (2026-09-27): written without a Unity run. Validate in the
/// Editor before trusting output: attach next to FluidStepBaseline, assign the
/// slice material (SliceCopy.shader), call CaptureFrame() once per captured step
/// (e.g. every 2nd Update), then pack the PNG sequence into an atlas offline
/// (see cpu-baseline/render_atlas.py for the reference packing: 12x10 grid).
///
/// Captures the mid-Z slice of the scalar volume. For final presets the slice
/// axis and the volume-render LUT are preset parameters, not hardcoded.
/// </summary>
public class FlipbookExporter : MonoBehaviour
{
    [Header("Source")]
    public FluidStepBaseline fluid;
    public Material sliceMaterial; // uses Shaders/SliceCopy.shader

    [Header("Export")]
    public int exportResolution = 128;
    [Range(0f, 1f)] public float sliceZ = 0.5f;
    public string outputFolder = "FlipbookFrames";

    private int frameIndex = 0;

    public void CaptureFrame()
    {
        if (fluid == null || fluid.scalarTex == null || sliceMaterial == null)
        {
            Debug.LogWarning("[FlipbookExporter] fluid / scalarTex / sliceMaterial not assigned.");
            return;
        }

        sliceMaterial.SetTexture("_VolumeTex", fluid.scalarTex);
        sliceMaterial.SetFloat("_SliceZ", sliceZ);

        RenderTexture rt = RenderTexture.GetTemporary(exportResolution, exportResolution, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(null, rt, sliceMaterial);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(exportResolution, exportResolution, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, exportResolution, exportResolution), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        string dir = Path.Combine(Application.dataPath, outputFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, string.Format("frame_{0:000}.png", frameIndex)), tex.EncodeToPNG());
        Destroy(tex);

        frameIndex++;
    }

    public void ResetCounter() { frameIndex = 0; }
}
