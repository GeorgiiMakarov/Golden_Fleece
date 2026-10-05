// Samples for the dataflow rules DF001–DF005.
// Lines marked [DFxxx] are expected to produce a diagnostic; everything else must stay clean.

public class RenderTexture
{
    public static RenderTexture active;
    public static RenderTexture GetTemporary(int w, int h) => null;
    public static void ReleaseTemporary(RenderTexture rt) { }
    public int width => 0;
}

public static class Debug
{
    public static void Log(object o) { }
}

public class DataFlowSamples
{
    RenderTexture someTarget;

    // ---------- DF001: unused local ----------

    void UnusedLocal()
    {
        int unused = 42; // [DF001] assigned, never read
        int used = 1;
        Debug.Log(used);
    }

    void UsedLocalIsClean()
    {
        int x = 10;
        Debug.Log(x); // read: no DF001
    }

    void DiscardIsClean()
    {
        _ = Compute();
    }

    int Compute() => 1;

    // ---------- DF002: dead store ----------

    void DeadStore()
    {
        int x = 1; // [DF002] overwritten before any read
        x = 2;
        Debug.Log(x);
    }

    void DeadStoreOnBranch(bool flag)
    {
        int y = 10;
        if (flag)
            y = 20;
        Debug.Log(y);
    }
    // NOTE: no DF002 here — 'y = 10' is read on the flag==false path, and 'y = 20' is read below.
    // This method must stay clean.

    void DeadParam(int p)
    {
        p = 10; // [DF002] parameter assigned and never read
    }

    void LiveStoreIsClean(bool flag)
    {
        int z = flag ? 1 : 2; // read below: no DF002
        Debug.Log(z);
    }

    // ---------- DF003: GetTemporary lease ----------

    void LeakyTempEarlyReturn(bool cond)
    {
        var rt = RenderTexture.GetTemporary(256, 256); // [DF003] early return below leaks it
        RenderTexture.active = rt;
        if (cond) return; // [DF004] active is left pointing at rt on this exit path
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
    }

    void LeakyTempNoReleaseAtAll()
    {
        var rt = RenderTexture.GetTemporary(128, 128); // [DF003] never released
        RenderTexture.active = rt;
        RenderTexture.active = null;
    }

    void CleanTemp()
    {
        var rt = RenderTexture.GetTemporary(256, 256);
        RenderTexture.active = rt;
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt); // released on the only path: clean
    }

    void CleanTempEarlyReturn(bool cond)
    {
        var rt = RenderTexture.GetTemporary(256, 256);
        RenderTexture.active = rt;
        if (cond)
        {
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);
            return;
        }
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt); // released on both paths: clean
    }

    RenderTexture EscapedTempIsClean()
    {
        var rt = RenderTexture.GetTemporary(64, 64);
        return rt; // ownership transferred to the caller: clean
    }

    // ---------- DF004: RenderTexture.active restore ----------

    void ActiveBypassedByEarlyReturn(bool cond)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = someTarget;
        if (cond) return; // [DF004] active not restored on this exit path
        RenderTexture.active = prev;
    }

    void ActiveRestoredEverywhere(bool cond)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = someTarget;
        if (cond)
        {
            RenderTexture.active = prev;
            return;
        }
        RenderTexture.active = prev; // every exit restores: clean
    }

    // ---------- DF005: definite null dereference ----------

    void NullLiteralDeref()
    {
        string t = null;
        Debug.Log(t.Length); // [DF005] t is definitely null
    }

    void NullTestTrueBranch(string u)
    {
        if (u == null)
        {
            Debug.Log(u.Length); // [DF005] null on the true edge
        }
    }

    void NullTestFalseBranchIsClean(string s)
    {
        if (s == null) return;
        Debug.Log(s.Length); // refined to non-null: clean
    }

    void NullIsPattern(object o)
    {
        if (o is null)
        {
            Debug.Log(o.ToString()); // [DF005]
        }
    }

    void NotNullPatternIsClean(object o)
    {
        if (o is not null)
        {
            Debug.Log(o.ToString()); // clean
        }
    }

    void ConditionalAccessIsClean(string maybe)
    {
        Debug.Log(maybe?.Length ?? 0); // ?. : clean
    }

    void ReassignedBeforeDeref(string v)
    {
        string w = null; // [DF002] immediately overwritten below (but no DF005 on the deref)
        w = v ?? "fallback";
        Debug.Log(w.Length); // clean: w is not null here
    }

    // Roslyn's CFG does not route exits through finally blocks; a release/restore
    // in finally is recognized syntactically, so these must stay clean.

    void TryFinallyRelease(bool cond)
    {
        var rt = RenderTexture.GetTemporary(64, 64);
        try
        {
            RenderTexture.active = rt;
            if (cond) return;
        }
        finally
        {
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    void TryFinallyAcquireInside(bool cond)
    {
        try
        {
            var rt = RenderTexture.GetTemporary(64, 64);
            RenderTexture.active = rt;
            if (cond) return;
        }
        finally
        {
            RenderTexture.active = null;
            // NOTE: rt is not visible here; this pattern still warns [DF003]
            // because the lease cannot be named in the finally.
        }
    }

    void TryFinallyNoRestore(bool cond)
    {
        var rt = RenderTexture.GetTemporary(64, 64);
        try
        {
            RenderTexture.active = rt;
            if (cond) return; // [RB002] no restore anywhere; finally releases but does not restore
        }
        finally
        {
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    void TryFinallyPartialRestore(bool cond)
    {
        var prev = RenderTexture.active;
        var rt = RenderTexture.GetTemporary(64, 64);
        try
        {
            RenderTexture.active = rt;
            if (cond) return; // [DF004] return runs finally (release only), bypasses the restore below
        }
        finally
        {
            RenderTexture.ReleaseTemporary(rt);
        }
        RenderTexture.active = prev; // restore on the normal path only
    }

    void OutParamWritesAreLive()
    {
        string s = null; // [DF002] overwritten by out without being read
        TakeOut(out s);
        Debug.Log(s.Length);
    }

    void TakeOut(out string x) { x = "a"; } // clean: out store is observed by the caller
}
