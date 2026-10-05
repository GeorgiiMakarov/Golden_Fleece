// HeadLockedPlacement.cs — v1 placement (rev3 §4).
// Positions the layer in view space with a fixed offset each frame.
// No screen-CV needed; final composition stays close to the display.
using UnityEngine;

namespace LightweightXR.Layers
{
    public class HeadLockedPlacement : MonoBehaviour
    {
        public Vector3 viewOffset = new Vector3(0f, 0.10f, 0.60f);
        public float smooth = 12f;
        Camera _head;

        void Awake() => _head = Camera.main;

        void LateUpdate()
        {
            if (_head == null) return;
            Vector3 target = _head.transform.TransformPoint(viewOffset);
            transform.position = Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-smooth * Time.deltaTime));
            // Face the user, keep upright.
            transform.rotation = Quaternion.LookRotation(transform.position - _head.transform.position, Vector3.up);
        }
    }
}
