// LayerSwitcherModel.cs — headless LayerSwitcher semantics for the emulator.
// Mirrors Scripts/Layers/LayerSwitcher.cs: same index state machine
// (Next / Prev / DismissAll / ShowLayer), timed dispatch, same 100 ms budget.
// Visual Show()/Dismiss() calls are device-side; the model tracks only which
// layer index is visible, which is what the gesture contract decides.
using System.Diagnostics;

namespace XrLayerSwitcherEmulator;

public sealed class LayerSwitcherModel
{
    public const long SwitchBudgetMs = 100; // rev3 §2: gesture -> visible switch

    readonly int _count;
    int _current = -1; // -1 = nothing visible

    public int VisibleLayer => _current;
    public double MaxSwitchMs { get; private set; }

    public LayerSwitcherModel(int layerCount) { _count = layerCount; }

    /// <returns>(visible layer index, dispatch time in ms)</returns>
    public (int VisibleLayer, double SwitchMs) OnGesture(GestureType g)
    {
        var sw = Stopwatch.StartNew();
        switch (g)
        {
            case GestureType.SwipeRight: Next(); break;
            case GestureType.SwipeLeft:  Prev(); break;
            case GestureType.PalmHold:   DismissAll(); break;
        }
        sw.Stop();
        double ms = sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
        if (ms > MaxSwitchMs) MaxSwitchMs = ms;
        return (_current, ms);
    }

    void Next()
    {
        if (_count == 0) return;
        _current++;
        if (_current >= _count) _current = -1; // swipe after last = dismiss all
    }

    void Prev()
    {
        if (_count == 0) return;
        _current = Math.Max(0, _current - 1);
    }

    public void DismissAll() { _current = -1; }

    // Test hook: spawn layer N without a video clock (mirrors ShowLayer).
    public void ShowLayer(int i)
    {
        if (i < 0 || i >= _count) return;
        _current = i;
    }
}
