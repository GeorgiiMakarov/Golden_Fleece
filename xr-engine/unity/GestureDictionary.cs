// GestureDictionary.cs — versioned gesture contract (rev3, §2).
// The ONLY gestures the runtime recognises in v1. Bumped only via manifest versioning.
namespace LightweightXR.Gestures
{
    public static class GestureDictionary
    {
        public const string Version = "v1";

        public enum GestureType
        {
            None = 0,
            SwipeRight = 1, // next XR layer (cycles; swipe after last layer = dismiss all)
            SwipeLeft  = 2, // previous XR layer
            PalmHold   = 3, // dismiss current layer immediately (open palm held ~0.8s)
            Pinch      = 4, // RESERVED for future select/confirm — not bound in v1
        }

        // Tunables live here so the team tunes thresholds in one place,
        // on the collected gesture dataset — not "from the box".
        public static class SwipeTuning
        {
            public const float MinDistanceM   = 0.22f; // min horizontal palm travel
            public const float MaxDurationS   = 0.60f; // max time for that travel
            public const float MaxVerticalM   = 0.15f; // vertical drift allowed
            public const float DebounceS      = 0.50f; // ignore new swipes after one fires
            public const float WindowS        = 0.35f; // sliding window for velocity
        }

        public static class PalmHoldTuning
        {
            public const float HoldDurationS  = 0.80f;
            public const float MaxDriftM      = 0.05f; // palm must stay ~still
        }
    }
}
