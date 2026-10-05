// PalmHoldClassifier.cs — open palm held still ~0.8s => dismiss.
// v1 approximation: palm position variance under threshold while tracked.
// Team to add finger-extension check on the gesture dataset (rev3 §6).
using UnityEngine;

namespace LightweightXR.Gestures
{
    [RequireComponent(typeof(HandJointReader))]
    public class PalmHoldClassifier : MonoBehaviour
    {
        HandJointReader _reader;
        Vector3 _anchor;
        float _stillSince = -1f;
        bool _fired;

        public System.Action<GestureDictionary.GestureType> OnPalmHold;

        void Awake() => _reader = GetComponent<HandJointReader>();

        void Update()
        {
            var s = _reader.Current;
            if (!s.Tracked) { _stillSince = -1f; _fired = false; return; }

            if (_stillSince < 0f) { _stillSince = s.Time; _anchor = s.PalmPosition; _fired = false; }

            if (Vector3.Distance(s.PalmPosition, _anchor) > GestureDictionary.PalmHoldTuning.MaxDriftM)
            {
                _stillSince = s.Time; _anchor = s.PalmPosition; _fired = false;
                return;
            }

            if (!_fired && s.Time - _stillSince >= GestureDictionary.PalmHoldTuning.HoldDurationS)
            {
                _fired = true;
                OnPalmHold?.Invoke(GestureDictionary.GestureType.PalmHold);
            }
        }
    }
}
