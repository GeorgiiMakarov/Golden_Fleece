// TestSceneBootstrap.cs — builds the Phase 1 test scene in code.
// Why: no binary .unity file in the skeleton; drop this on an empty scene's
// camera rig and press Play. Spawns the engine with the demo manifest and
// keyboard stand-ins for gestures (1/2/3 = swipe right/left/hold) so the
// layer switcher is testable with zero hand-tracking hardware.
using UnityEngine;
using LightweightXR.Core;
using LightweightXR.Gestures;

namespace LightweightXR.Debug
{
    public class TestSceneBootstrap : MonoBehaviour
    {
        [TextArea(4, 12)]
        public string demoManifestJson;

        XREngine _engine;

        void Start()
        {
            var go = new GameObject("XREngine");
            _engine = go.AddComponent<XREngine>();
            _engine.manifestJsonInline = demoManifestJson;
            UnityEngine.Debug.Log("[XR-Test] Press 1=SwipeRight 2=SwipeLeft 3=PalmHold to drive the switcher.");
        }

        void Update()
        {
            if (_engine == null) return;
            if (Input.GetKeyDown(KeyCode.Alpha1)) _engine.InjectGesture(GestureDictionary.GestureType.SwipeRight);
            if (Input.GetKeyDown(KeyCode.Alpha2)) _engine.InjectGesture(GestureDictionary.GestureType.SwipeLeft);
            if (Input.GetKeyDown(KeyCode.Alpha3)) _engine.InjectGesture(GestureDictionary.GestureType.PalmHold);
        }
    }
}
