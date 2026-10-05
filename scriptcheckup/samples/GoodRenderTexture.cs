using UnityEngine;

public class GoodRenderTexture : MonoBehaviour
{
    void Capture()
    {
        // Correct save/restore — should NOT trigger RB-002
        var prev = RenderTexture.active;
        var rt = RenderTexture.GetTemporary(512, 512);
        RenderTexture.active = rt;
        // ... read pixels ...
        RenderTexture.active = prev;   // restore
        RenderTexture.ReleaseTemporary(rt);
    }

    void CaptureNull()
    {
        var prev = RenderTexture.active;
        RenderTexture.active = RenderTexture.GetTemporary(256, 256);
        RenderTexture.active = null;   // also valid
    }
}
