using UnityEngine;

public class HotPath : MonoBehaviour
{
    void Update()
    {
        // Should trigger UW-001 and RB-001
        var go = FindObjectOfType<Camera>();
        var tex = new Texture2D(64, 64);
        tex.ReadPixels(new Rect(0,0,64,64), 0, 0);
    }

    void EmptyUpdate() { } // not a Unity message name, ok
    void Start() { }       // empty Start — UL-008
}
