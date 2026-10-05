using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using ScriptCheckup.Analyzers;
using Xunit;

namespace ScriptCheckup.Tests;

public class LintRulesTests : AnalyzerTestBase
{
    private static DiagnosticAnalyzer Analyzer => new LintRulesAnalyzer();

    [Fact]
    public async Task UL001_PublicField_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour { public int health; }");
        var d = diags.First(x => x.Id == "UL001");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Info, d.Severity);
    }

    [Fact]
    public async Task UL002_DebugLog_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour { void M() { Debug.Log(""x""); } }");
        AssertHas(diags, "UL002");
    }

    [Fact]
    public async Task UL003_StartCoroutine_String_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour { void M() { StartCoroutine(""Fade""); } }");
        AssertHas(diags, "UL003");
    }

    [Fact]
    public async Task UL004_Invoke_String_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour { void M() { Invoke(""Respawn"", 1f); } }");
        AssertHas(diags, "UL004");
    }

    [Fact]
    public async Task UL005_SendMessage_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour { void M() { SendMessage(""Ping""); } }");
        AssertHas(diags, "UL005");
    }

    [Fact]
    public async Task UL006_NewList_InUpdate_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using System.Collections.Generic;
using UnityEngine;
public class C : MonoBehaviour { void Update() { var xs = new List<int>(); } }");
        AssertHas(diags, "UL006");
    }

    [Fact]
    public async Task UL006_NewVector3_InUpdate_IsSilent()
    {
        // Value types do not allocate on the heap.
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour { void Update() { var v = new Vector3(1, 2, 3); } }");
        Assert.DoesNotContain(diags, d => d.Id == "UL006");
    }

    [Fact]
    public async Task UL007_StringConcat_InUpdate_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour { void Update() { var s = ""hp: "" + 1; } }");
        AssertHas(diags, "UL007");
    }

    [Fact]
    public async Task UL009_PlainClass_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
public class Helper { public void Do() { } }");
        AssertHas(diags, "UL009");
    }

    [Fact]
    public async Task UL011_Find_OutsideUpdate_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour { void Start() { var g = GameObject.Find(""X""); } }");
        var d = diags.First(x => x.Id == "UL011");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Info, d.Severity);
    }
}
