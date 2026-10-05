using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using ScriptCheckup.Analyzers;
using Xunit;

namespace ScriptCheckup.Tests;

public class ReinitRulesTests : AnalyzerTestBase
{
    private static DiagnosticAnalyzer Analyzer => new ReinitRulesAnalyzer();

    [Fact]
    public async Task RX002_Singleton_NeverNulled_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    public static C Instance;
    void Awake() { Instance = this; }
}");
        AssertHas(diags, "RX002");
    }

    [Fact]
    public async Task RX002_Singleton_NulledInOnDestroy_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    public static C Instance;
    void Awake() { Instance = this; }
    void OnDestroy() { Instance = null; }
}");
        AssertNotHas(diags, "RX002");
    }

    [Fact]
    public async Task RX003_StaticEvent_NoUnsub_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    public static event System.Action OnWave;
    void Awake() { OnWave += H; }
    void H() { }
}");
        AssertHas(diags, "RX003");
    }

    [Fact]
    public async Task RX004_StaticCounter_Incremented_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    public static int total;
    void OnDestroy() { total++; }
}");
        AssertHas(diags, "RX004");
    }

    [Fact]
    public async Task RX005_DontDestroyOnLoad_NoGuard_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void Awake() { DontDestroyOnLoad(gameObject); }
}");
        AssertHas(diags, "RX005");
    }

    [Fact]
    public async Task RX005_DontDestroyOnLoad_WithGuard_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    public static C Instance;
    void Awake() {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }
}");
        AssertNotHas(diags, "RX005");
    }

    [Fact]
    public async Task RX006_EditorApi_InsideIfEditor_IsInfo()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
#if UNITY_EDITOR
public class E : MonoBehaviour {
    void OnGUI() { UnityEditor.EditorUtility.SetDirty(this); }
}
#endif");
        var d = diags.First(x => x.Id == "RX006");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Info, d.Severity);
    }

    [Fact]
    public async Task RX008_SceneLoaded_NoUnsub_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
using UnityEngine.SceneManagement;
public class C : MonoBehaviour {
    void OnEnable() { SceneManager.sceneLoaded += OnScene; }
    void OnScene(Scene s, LoadSceneMode m) { }
}");
        AssertHas(diags, "RX008");
    }

    [Fact]
    public async Task RX008_SceneLoaded_Unsubbed_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
using UnityEngine.SceneManagement;
public class C : MonoBehaviour {
    void OnEnable() { SceneManager.sceneLoaded += OnScene; }
    void OnDisable() { SceneManager.sceneLoaded -= OnScene; }
    void OnScene(Scene s, LoadSceneMode m) { }
}");
        AssertNotHas(diags, "RX008");
    }
}

public class ErrorRulesTests : AnalyzerTestBase
{
    private static DiagnosticAnalyzer Analyzer => new ErrorRulesAnalyzer();

    [Fact]
    public async Task UE003_ThreadSleep_IsError()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
using System.Threading;
public class C : MonoBehaviour {
    void M() { Thread.Sleep(100); }
}");
        var d = diags.First(x => x.Id == "UE003");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, d.Severity);
    }
}

public class UnityRulesRegressionTests : AnalyzerTestBase
{
    private static DiagnosticAnalyzer Analyzer => new UnityRulesAnalyzer();

    [Fact]
    public async Task UW001_Generic_FindObjectOfType_InUpdate_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void Update() { var e = FindObjectOfType<Enemy>(); }
}
public class Enemy : MonoBehaviour { }");
        AssertHas(diags, "UW001");
    }

    [Fact]
    public async Task UW007_IdentifierLeft_EventField_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    public event System.Action OnSomething;
    void Awake() { OnSomething += Handle; }
    void Handle() { }
}");
        AssertHas(diags, "UW007");
    }

    [Fact]
    public async Task UW007_Arithmetic_PlusEquals_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    int score;
    void M() { score += 10; }
}");
        AssertNotHas(diags, "UW007");
    }
}
