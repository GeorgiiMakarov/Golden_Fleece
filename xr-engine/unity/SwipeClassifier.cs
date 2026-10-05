// SwipeClassifier.cs — directional swipe from raw palm positions.
// Velocity-vector analysis over a sliding window + debounce. In-house
// classifier; thresholds tuned on the team's gesture dataset (rev3 §6).
using System.Collections.Generic;
using UnityEngine;

namespace LightweightXR.Gestures
{
    [RequireComponent(typeof(HandJointReader))]
    public class SwipeClassifier : MonoBehaviour
    {
        HandJointReader _reader;
        Camera _head;
        readonly Queue<(Vector3 pos, float t)> _trail = new Queue<(Vector3, float)>();
        float _lastFireTime = -10f;

        public System.Action<GestureDictionary.GestureType> OnSwipe;

        void Awake()
        {
            _reader = GetComponent<HandJointReader>();
            _head = Camera.main;
        }

        void Update()
        {
            var s = _reader.Current;
            if (!s.Tracked) { _trail.Clear(); return; }

            _trail.Enqueue((s.PalmPosition, s.Time));
            while (_trail.Count > 0 && s.Time - _trail.Peek().t > GestureDictionary.SwipeTuning.WindowS)
                _trail.Dequeue();
            if (_trail.Count < 2) return;

            var oldest = _trail.Peek();
            float dt = s.Time - oldest.t;
            if (dt <= 0.001f || dt > GestureDictionary.SwipeTuning.MaxDurationS) return;
            if (s.Time - _lastFireTime < GestureDictionary.SwipeTuning.DebounceS) return;

            // Work in head space so "right" means the user's right.
            Vector3 headSpaceDelta = _head.transform.InverseTransformDirection(s.PalmPosition - oldest.pos);
            float dx = headSpaceDelta.x;
            float dy = Mathf.Abs(headSpaceDelta.y);

            if (Mathf.Abs(dx) >= GestureDictionary.SwipeTuning.MinDistanceM &&
                dy <= GestureDictionary.SwipeTuning.MaxVerticalM)
            {
                _lastFireTime = s.Time;
                _trail.Clear();
                var g = dx > 0 ? GestureDictionary.GestureType.SwipeRight
                               : GestureDictionary.GestureType.SwipeLeft;
                OnSwipe?.Invoke(g);
            }
        }
    }
}
