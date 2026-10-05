using UnityEngine;

public class BadRenderTexture : MonoBehaviour
{
    void Capture()
    {
        // Missing restore — SHOULD trigger RB-002
        RenderTexture.active = RenderTexture.GetTemporary(512, 512);
        // forgot to restore
    }
}
