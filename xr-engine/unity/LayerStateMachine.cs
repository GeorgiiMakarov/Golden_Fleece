// LayerStateMachine.cs — pre-baked state machine per layer.
// Appear -> Idle -> React -> Dismissed. No real-time physics: the "illusion".
// v1 placeholder transitions are procedural tweens; swap in baked skeletal
// clips (Animator) without changing the interface.
using UnityEngine;

namespace LightweightXR.Layers
{
    public enum LayerState { Hidden, Appear, Idle, React, Dismissed }

    [RequireComponent(typeof(HeadLockedPlacement))]
    public class LayerStateMachine : MonoBehaviour
    {
        public LayerState State { get; private set; } = LayerState.Hidden;
        float _t;

        // Hook for baked clips: assign an Animator with Appear/Idle/React states;
        // when present, transitions drive animator parameters instead of tweens.
        public Animator bakedAnimator;

        void Awake() => gameObject.SetActive(false);

        public void Show()
        {
            gameObject.SetActive(true);
            SetState(LayerState.Appear);
        }

        public void Dismiss() => SetState(LayerState.Dismissed);

        void SetState(LayerState s)
        {
            State = s; _t = 0f;
            if (bakedAnimator != null)
                bakedAnimator.SetTrigger(s.ToString());
        }

        void Update()
        {
            _t += Time.deltaTime;
            if (bakedAnimator != null) return; // baked clips drive visuals

            // Procedural stand-ins so Phase 1 is testable without art.
            switch (State)
            {
                case LayerState.Appear: // scale-in 0.4s
                    transform.localScale = Vector3.one * Mathf.Min(1f, _t / 0.4f);
                    if (_t >= 0.4f) SetState(LayerState.Idle);
                    break;
                case LayerState.Idle: // gentle hover bob
                    transform.localScale = Vector3.one;
                    break;
                case LayerState.Dismissed: // scale-out 0.25s then off
                    transform.localScale = Vector3.one * Mathf.Max(0f, 1f - _t / 0.25f);
                    if (_t >= 0.25f) { gameObject.SetActive(false); State = LayerState.Hidden; }
                    break;
            }
        }
    }
}
