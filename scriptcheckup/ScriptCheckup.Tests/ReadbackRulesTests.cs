using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using ScriptCheckup.Analyzers;
using Xunit;

namespace ScriptCheckup.Tests;

public class ReadbackRulesTests : AnalyzerTestBase
{
    private static DiagnosticAnalyzer Analyzer => new ReadbackRulesAnalyzer();

    [Fact]
    public async Task RB004_WaitAllRequests_InUpdate_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void Update() { AsyncGPUReadback.WaitAllRequests(); }
}");
        AssertHas(diags, "RB004");
    }

    [Fact]
    public async Task RB004_WaitAllRequests_InOnDestroy_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void OnDestroy() { AsyncGPUReadback.WaitAllRequests(); }
}");
        var d = diags.First(x => x.Id == "RB004");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Info, d.Severity);
    }

    [Fact]
    public async Task RB006_GetData_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() { var b = new ComputeBuffer(4, 4); b.GetData(new int[4]); }
}");
        AssertHas(diags, "RB006");
    }

    [Fact]
    public async Task RB007_ApplyNoArgs_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() { var t = new Texture2D(4, 4); t.Apply(); }
}");
        var d = diags.First(x => x.Id == "RB007");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Info, d.Severity);
    }

    [Fact]
    public async Task RB008_CaptureScreenshot_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() { ScreenCapture.CaptureScreenshot(""x.png""); }
}");
        AssertHas(diags, "RB008");
    }

    [Fact]
    public async Task RB009_GetPixels_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() { var t = new Texture2D(4, 4); var p = t.GetPixels(); }
}");
        AssertHas(diags, "RB009");
    }

    [Fact]
    public async Task RB010_TargetTexture_InUpdate_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    Camera cam;
    void Update() { cam.targetTexture = new RenderTexture(64, 64, 0); }
}");
        var d = diags.First(x => x.Id == "RB010");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, d.Severity);
    }

    [Fact]
    public async Task RB010_TargetTexture_OutsideHot_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    Camera cam;
    void Start() { cam.targetTexture = new RenderTexture(64, 64, 0); }
}");
        var d = diags.First(x => x.Id == "RB010");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Info, d.Severity);
    }

    [Fact]
    public async Task RB011_GetRawTextureData_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() { var t = new Texture2D(4, 4); var r = t.GetRawTextureData<byte>(); }
}");
        var d = diags.First(x => x.Id == "RB011");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Info, d.Severity);
    }
}
