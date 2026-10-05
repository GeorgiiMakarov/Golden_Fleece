// LayerStack.cs — ordered stack of layers with prefetch + LRU eviction.
// All bundles are loaded BEFORE any switch happens, so switching is just
// activate/deactivate (the <100ms path). Requires glTFast package.
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using GLTFast;
using LightweightXR.Manifest;

namespace LightweightXR.Layers
{
    public class LayerStack : MonoBehaviour
    {
        public const int PrefetchBudget = 10;

        readonly List<LayerStateMachine> _layers = new List<LayerStateMachine>();
        readonly Queue<LayerStateMachine> _lru = new Queue<LayerStateMachine>();
        Transform _root;

        public int Count => _layers.Count;
        public LayerStateMachine this[int i] => _layers[i];

        void Awake()
        {
            _root = new GameObject("LayerRoot").transform;
            _root.SetParent(transform, false);
        }

        // Prefetch every bundle in the manifest. Call once at startup.
        public async Task LoadManifestAsync(LayerManifest manifest)
        {
            var tasks = new List<Task<LayerStateMachine>>();
            foreach (var spec in manifest.layers)
                tasks.Add(LoadOneAsync(spec));
            var results = await Task.WhenAll(tasks);
            foreach (var l in results)
            {
                if (l == null) continue;
                _layers.Add(l);
                _lru.Enqueue(l);
                while (_lru.Count > PrefetchBudget) // LRU eviction
                {
                    var old = _lru.Dequeue();
                    _layers.Remove(old);
                    Destroy(old.gameObject);
                }
            }
            Debug.Log($"[XR] LayerStack ready: {_layers.Count} layers prefetched.");
        }

        async Task<LayerStateMachine> LoadOneAsync(LayerSpec spec)
        {
            var go = new GameObject($"Layer_{spec.layerId}");
            go.transform.SetParent(_root, false);
            var placement = go.AddComponent<HeadLockedPlacement>();
            if (spec.placement?.offset is { Length: 3 } o)
                placement.viewOffset = new Vector3(o[0], o[1], o[2]);
            var sm = go.AddComponent<LayerStateMachine>();

            var import = new GltfImport();
            bool ok = await import.Load(spec.bundleUrl);
            if (!ok) { Debug.LogError($"[XR] Failed to load {spec.bundleUrl}"); Destroy(go); return null; }
            bool inst = await import.InstantiateMainSceneAsync(go.transform);
            if (!inst) { Debug.LogError($"[XR] Failed to instantiate {spec.bundleUrl}"); Destroy(go); return null; }
            return sm;
        }
    }
}
