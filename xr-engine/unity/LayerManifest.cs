// LayerManifest.cs — JSON schema for the XR-track (rev3 §2).
// Mirrors the VAST/VMAP pattern: timeline triggers + versioned gesture contract.
using System;
using UnityEngine;

namespace LightweightXR.Manifest
{
    [Serializable]
    public class PlacementSpec
    {
        public string mode = "headLocked"; // v1: headLocked | v2: phoneBody
        public float[] offset = { 0f, 0.10f, 0.60f }; // metres, view space
    }

    [Serializable]
    public class LayerSpec
    {
        public string layerId;
        public string bundleUrl;      // GLB (gltf). KTX2/Draco pre-compressed by creator pipeline.
        public long   spawnAtMs;      // video-clock trigger (Phase 2); Phase 1: manual/test
        public string initialState = "Appear";
        public PlacementSpec placement = new PlacementSpec();
    }

    [Serializable]
    public class LayerManifest
    {
        public string manifestVersion = "1.0";
        public string gestureDictionaryVersion = "v1"; // must match GestureDictionary.Version
        public string videoId;
        public LayerSpec[] layers = Array.Empty<LayerSpec>();
    }

    [Serializable]
    public class GestureContract
    {
        public string version;
        public string[] gestures;
    }
}
