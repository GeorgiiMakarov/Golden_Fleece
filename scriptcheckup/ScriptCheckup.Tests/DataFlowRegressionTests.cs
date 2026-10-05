using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using ScriptCheckup.Analyzers;
using ScriptCheckup.Analyzers.FlowAnalysis;
using Xunit;

namespace ScriptCheckup.Tests;

/// <summary>
/// Regression tests for the dataflow rules (DF001–DF005),
/// covering the adversarial t1–t8 patterns from the v2.2 validation.
/// </summary>
public class DataFlowRegressionTests : AnalyzerTestBase
{
    private static DiagnosticAnalyzer Analyzer => new DataFlowAnalyzer();

    [Fact]
    public async Task DF003_TemporaryRT_NotReleased_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() {
        var rt = RenderTexture.GetTemporary(256, 256);
        RenderTexture.active = rt;
    }
}");
        AssertHas(diags, "DF003");
    }

    [Fact]
    public async Task DF003_TemporaryRT_Released_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() {
        var rt = RenderTexture.GetTemporary(256, 256);
        RenderTexture.active = rt;
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
    }
}");
        AssertNotHas(diags, "DF003");
    }

    [Fact]
    public async Task DF004_Active_NotRestored_Warns()
    {
        // DF004 fires when a restore exists but an early return bypasses it.
        // (No restore at all is RB002, not DF004.)
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M(bool cond) {
        var prev = RenderTexture.active;
        RenderTexture.active = new RenderTexture(64, 64, 0);
        if (cond) return;
        RenderTexture.active = prev;
    }
}");
        AssertHas(diags, "DF004");
    }

    [Fact]
    public async Task DF004_Active_Restored_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() {
        var prev = RenderTexture.active;
        RenderTexture.active = new RenderTexture(64, 64, 0);
        RenderTexture.active = prev;
    }
}");
        AssertNotHas(diags, "DF004");
    }

    [Fact]
    public async Task DF005_DereferenceDefinitelyNull_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() {
        GameObject go = null;
        var n = go.name;
    }
}");
        AssertHas(diags, "DF005");
    }

    [Fact]
    public async Task DF005_MaybeNull_Branch_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M(bool b) {
        GameObject go = null;
        if (b) go = new GameObject();
        var n = go.name;
    }
}");
        AssertNotHas(diags, "DF005");
    }

    [Fact]
    public async Task DF002_DeadStore_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() {
        int x = 1;
        x = 2;
        var y = x;
    }
}");
        AssertHas(diags, "DF002");
    }

    [Fact]
    public async Task DF002_OutParam_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() {
        int x;
        int.TryParse(""1"", out x);
        var y = x;
    }
}");
        AssertNotHas(diags, "DF002");
    }

    [Fact]
    public async Task DF003_ReassignedLease_StillWarns()
    {
        // t6: reassignment must not hide the unreleased temporary
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() {
        var rt = RenderTexture.GetTemporary(256, 256);
        RenderTexture.active = rt;
        rt = RenderTexture.GetTemporary(128, 128);
        RenderTexture.active = rt;
    }
}");
        AssertHas(diags, "DF003");
    }

    [Fact]
    public async Task DF003_TryFinally_ReleaseInFinally_Silent()
    {
        // t8-style: release in finally compensates
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() {
        var rt = RenderTexture.GetTemporary(256, 256);
        try {
            RenderTexture.active = rt;
        } finally {
            RenderTexture.ReleaseTemporary(rt);
        }
    }
}");
        AssertNotHas(diags, "DF003");
    }
}
