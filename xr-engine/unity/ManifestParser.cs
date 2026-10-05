// ManifestParser.cs — parses + validates the manifest. Refuses to run on a
// gesture-dictionary mismatch: the dictionary is a versioned contract (rev3 §2).
using UnityEngine;
using LightweightXR.Gestures;

namespace LightweightXR.Manifest
{
    public static class ManifestParser
    {
        public static LayerManifest Parse(string json)
        {
            var m = JsonUtility.FromJson<LayerManifest>(json);
            if (m == null) throw new System.ArgumentException("Manifest JSON is null/unparseable.");
            if (m.gestureDictionaryVersion != GestureDictionary.Version)
                throw new System.InvalidOperationException(
                    $"Gesture contract mismatch: manifest wants {m.gestureDictionaryVersion}, " +
                    $"runtime has {GestureDictionary.Version}. Refusing to run.");
            if (m.layers == null || m.layers.Length == 0)
                throw new System.ArgumentException("Manifest has no layers.");
            if (m.layers.Length > 10)
                Debug.LogWarning($"[XR] Manifest has {m.layers.Length} layers; prefetch budget is ~10 (LRU will evict).");
            return m;
        }
    }
}
