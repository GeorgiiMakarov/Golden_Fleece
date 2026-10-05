// LayerSwitcher.cs — the "illusion" core. Swipe cycles layers, hold dismisses.
// Budget: gesture -> visible switch < 100ms (rev3 §2). Timed with Stopwatch;
// anything slower is a bug, not a feature.
using System.Diagnostics;
using UnityEngine;
using LightweightXR.Gestures;

namespace LightweightXR.Layers
{
    [RequireComponent(typeof(LayerStack))]
    public class LayerSwitcher : MonoBehaviour
    {
        public const long SwitchBudgetMs = 100;

        LayerStack _stack;
        GestureEventBus _bus;
        int _current = -1; // -1 = nothing visible

        void Awake()
        {
            _stack = GetComponent<LayerStack>();
            _bus = FindFirstObjectByType<GestureEventBus>();
        }

        void OnEnable()  { if (_bus != null) _bus.Gesture += OnGesture; }
        void OnDisable() { if (_bus != null) _bus.Gesture -= OnGesture; }

        void OnGesture(GestureDictionary.GestureType g)
        {
            var sw = Stopwatch.StartNew();
            switch (g)
            {
                case GestureDictionary.GestureType.SwipeRight: Next(); break;
                case GestureDictionary.GestureType.SwipeLeft:  Prev(); break;
                case GestureDictionary.GestureType.PalmHold:   DismissAll(); break;
            }
            sw.Stop();
            if (sw.ElapsedMilliseconds > SwitchBudgetMs)
                Debug.LogWarning($"[XR] Switch took {sw.ElapsedMilliseconds}ms — over the {SwitchBudgetMs}ms budget.");
        }

        void HideCurrent()
        {
            if (_current >= 0 && _current < _stack.Count)
                _stack[_current].Dismiss();
        }

        public void Next()
        {
            if (_stack.Count == 0) return;
            HideCurrent();
            _current++;
            if (_current >= _stack.Count) { _current = -1; return; } // swipe after last = dismiss all
            _stack[_current].Show();
        }

        public void Prev()
        {
            if (_stack.Count == 0) return;
            HideCurrent();
            _current = Mathf.Max(0, _current - 1);
            _stack[_current].Show();
        }

        public void DismissAll()
        {
            HideCurrent();
            _current = -1;
        }

        // Phase 1 test hook: spawn layer N without video clock.
        public void ShowLayer(int i)
        {
            if (i < 0 || i >= _stack.Count) return;
            DismissAll();
            _current = i;
            _stack[_current].Show();
        }
    }
}
