// HandJointReader.cs — polls the XR Hands subsystem, exposes a clean per-frame
// hand snapshot. OpenXR gives us RAW 26-joint poses only; everything semantic
// (swipe, hold) is classified downstream. Phase 1, rev3 §6.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace LightweightXR.Gestures
{
    public struct HandSnapshot
    {
        public bool   Tracked;
        public Vector3 PalmPosition;   // world space
        public Vector3 IndexTip;       // world space
        public Vector3 ThumbTip;       // world space
        public float  Time;
    }

    [DefaultExecutionOrder(-100)]
    public class HandJointReader : MonoBehaviour
    {
        XRHandSubsystem _subsystem;

        // Prefer right hand for gestures; fall back to left.
        public HandSnapshot Current { get; private set; }

        void Start()
        {
            var list = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(list);
            if (list.Count > 0)
            {
                _subsystem = list[0];
                if (!_subsystem.running) _subsystem.Start();
            }
            else
            {
                Debug.LogWarning("[XR] No XRHandSubsystem found. Enable Hand Tracking in OpenXR Feature Groups.");
            }
        }

        void Update()
        {
            var snap = new HandSnapshot { Time = Time.time };
            if (_subsystem != null && _subsystem.running)
            {
                var hand = _subsystem.rightHand.isTracked ? _subsystem.rightHand
                         : _subsystem.leftHand.isTracked  ? _subsystem.leftHand
                         : default;
                if (hand.isTracked)
                {
                    snap.Tracked = true;
                    if (hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm))
                        snap.PalmPosition = palm.position;
                    if (hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var index))
                        snap.IndexTip = index.position;
                    if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumb))
                        snap.ThumbTip = thumb.position;
                }
            }
            Current = snap;
        }
    }
}
