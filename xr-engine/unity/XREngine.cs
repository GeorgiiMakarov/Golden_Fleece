// XREngine.cs — bootstrap/orchestrator. Wires manifest -> stack -> switcher -> gestures.
// Phase 1: no video clock yet — layers are prefetched and switchable via
// gestures (or the test hook). Phase 2 plugs the media-sync adapters here.
using UnityEngine;
using LightweightXR.Gestures;
using LightweightXR.Layers;
using LightweightXR.Manifest;

namespace LightweightXR.Core
{
    public class XREngine : MonoBehaviour
    {
        [Header("Manifest")]
        [Tooltip("JSON text of the layer manifest (or leave empty to use the embedded demo).")]
        public TextAsset manifestJson;

        [TextArea(4, 12)]
        public string manifestJsonInline;

        GestureEventBus _bus;
        LayerStack _stack;
        LayerSwitcher _switcher;

        async void Start()
        {
            // Order matters: bus first (it owns the hand reader), then stack, then switcher.
            _bus = gameObject.AddComponent<GestureEventBus>();
            _stack = gameObject.AddComponent<LayerStack>();
            _switcher = gameObject.AddComponent<LayerSwitcher>();

            string json = manifestJson != null ? manifestJson.text : manifestJsonInline;
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogError("[XR] No manifest provided. Assign manifestJson or manifestJsonInline.");
                return;
            }

            LayerManifest manifest = ManifestParser.Parse(json);
            Debug.Log($"[XR] Manifest '{manifest.videoId}' v{manifest.manifestVersion}, " +
                      $"gesture contract {manifest.gestureDictionaryVersion}, {manifest.layers.Length} layers.");
            await _stack.LoadManifestAsync(manifest);

            // Phase 1 smoke test: show the first layer immediately.
            _switcher.ShowLayer(0);
        }

        // Runtime API for Phase 2 media-sync adapters.
        public void ShowLayer(int i) => _switcher?.ShowLayer(i);
        public void InjectGesture(GestureDictionary.GestureType g) => _bus?.Inject(g);
    }
}
