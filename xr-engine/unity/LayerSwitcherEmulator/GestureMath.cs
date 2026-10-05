// GestureMath.cs — the gesture classifiers as pure C#, no UnityEngine.
//
// This is a 1:1 port of the runtime state machines:
//   SwipeDetector      <- Scripts/Gestures/SwipeClassifier.cs
//   PalmHoldDetector   <- Scripts/Gestures/PalmHoldClassifier.cs
// Tuning constants mirror GestureDictionary.SwipeTuning / PalmHoldTuning (v1);
// keep them in sync — they are the contract, not a copy.
//
// Design note: the Unity MonoBehaviour classifiers should delegate to these
// classes (thin wrappers), so the emulator and the device run identical math.
// That refactor is pending the first Unity compile; until then this file is
// the reference implementation and tools/check_pipeline.py is its oracle.
//
// Head space: on device, SwipeClassifier transforms the palm delta into head
// space via the camera. Synthetic streams are authored in head space already,
// so the emulator treats head rotation as identity.
namespace XrLayerSwitcherEmulator;

public enum GestureType
{
    None,
    SwipeRight, // next layer; swipe after last = dismiss all
    SwipeLeft,  // previous layer
    PalmHold,   // dismiss now (open palm held ~0.8 s)
    Pinch,      // RESERVED — not bound in v1
}

public static class GestureTuning
{
    // Swipe — mirrors GestureDictionary.SwipeTuning
    public const float SwipeMinDistanceM = 0.22f; // min horizontal palm travel
    public const float SwipeMaxDurationS = 0.60f; // max time for that travel
    public const float SwipeMaxVerticalM = 0.15f; // vertical drift allowed
    public const float SwipeDebounceS = 0.50f;    // ignore new swipes after one fires
    public const float SwipeWindowS = 0.35f;      // sliding window for velocity
    // Palm hold — mirrors GestureDictionary.PalmHoldTuning
    public const float HoldDurationS = 0.80f;
    public const float HoldMaxDriftM = 0.05f;     // palm must stay ~still
}

public readonly struct Vec3
{
    public readonly float X, Y, Z;
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static Vec3 operator -(Vec3 a, Vec3 b) =>
        new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public float Length() => MathF.Sqrt(X * X + Y * Y + Z * Z);
}

/// <summary>Directional swipe from raw palm positions (sliding window + debounce).</summary>
public sealed class SwipeDetector
{
    readonly Queue<(Vec3 pos, float t)> _trail = new();
    float _lastFireTime = -10f;

    /// <summary>Returns the detected gesture, or null when nothing fired.</summary>
    public GestureType? Update(Vec3 palm, float t, bool tracked)
    {
        if (!tracked) { _trail.Clear(); return null; }

        _trail.Enqueue((palm, t));
        while (_trail.Count > 0 && t - _trail.Peek().t > GestureTuning.SwipeWindowS)
            _trail.Dequeue();
        if (_trail.Count < 2) return null;

        var oldest = _trail.Peek();
        float dt = t - oldest.t;
        if (dt <= 0.001f || dt > GestureTuning.SwipeMaxDurationS) return null;
        if (t - _lastFireTime < GestureTuning.SwipeDebounceS) return null;

        Vec3 d = palm - oldest.pos; // head space == world space in the emulator
        if (MathF.Abs(d.X) >= GestureTuning.SwipeMinDistanceM &&
            MathF.Abs(d.Y) <= GestureTuning.SwipeMaxVerticalM)
        {
            _lastFireTime = t;
            _trail.Clear();
            return d.X > 0 ? GestureType.SwipeRight : GestureType.SwipeLeft;
        }
        return null;
    }
}

/// <summary>Open palm held still ~0.8 s. v1: position variance only.</summary>
public sealed class PalmHoldDetector
{
    Vec3 _anchor;
    float _stillSince = -1f;
    bool _fired;

    /// <summary>Returns PalmHold once per still episode, else null.</summary>
    public GestureType? Update(Vec3 palm, float t, bool tracked)
    {
        if (!tracked) { _stillSince = -1f; _fired = false; return null; }

        if (_stillSince < 0f) { _stillSince = t; _anchor = palm; _fired = false; }

        if ((palm - _anchor).Length() > GestureTuning.HoldMaxDriftM)
        {
            _stillSince = t; _anchor = palm; _fired = false;
            return null;
        }

        if (!_fired && t - _stillSince >= GestureTuning.HoldDurationS)
        {
            _fired = true;
            return GestureType.PalmHold;
        }
        return null;
    }
}
