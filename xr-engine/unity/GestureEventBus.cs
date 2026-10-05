// GestureEventBus.cs — single funnel: classifiers in, layer switcher consumes.
using UnityEngine;

namespace LightweightXR.Gestures
{
    public class GestureEventBus : MonoBehaviour
    {
        public event System.Action<GestureDictionary.GestureType> Gesture;

        SwipeClassifier _swipe;
        PalmHoldClassifier _hold;

        void Awake()
        {
            var reader = gameObject.AddComponent<HandJointReader>();
            _swipe = gameObject.AddComponent<SwipeClassifier>();
            _hold  = gameObject.AddComponent<PalmHoldClassifier>();
            _swipe.OnSwipe   += g => Gesture?.Invoke(g);
            _hold.OnPalmHold += g => Gesture?.Invoke(g);
        }

        // Editor / test hook: inject a gesture without a hand (Phase 1 debugging).
        public void Inject(GestureDictionary.GestureType g) => Gesture?.Invoke(g);
    }
}
