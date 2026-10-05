using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using ScriptCheckup.Analyzers;
using Xunit;

namespace ScriptCheckup.Tests;

public class UpdateWasteRulesTests : AnalyzerTestBase
{
    private static DiagnosticAnalyzer Analyzer => new UpdateWasteRulesAnalyzer();

    [Fact]
    public async Task UW002_Find_InUpdate_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void Update() { var g = GameObject.Find(""X""); }
}");
        AssertHas(diags, "UW002");
    }

    [Fact]
    public async Task UW003_CameraMain_InUpdate_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void Update() { var p = Camera.main.transform.position; }
}");
        AssertHas(diags, "UW003");
    }

    [Fact]
    public async Task UW004_AddForce_InUpdate_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void Update() { GetComponent<Rigidbody>().AddForce(Vector3.up); }
}");
        AssertHas(diags, "UW004");
    }

    [Fact]
    public async Task UW004_AddForce_InFixedUpdate_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void FixedUpdate() { GetComponent<Rigidbody>().AddForce(Vector3.up); }
}");
        AssertNotHas(diags, "UW004");
    }

    [Fact]
    public async Task UW005_Translate_WithoutDeltaTime_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void Update() { transform.Translate(Vector3.up * 5f); }
}");
        AssertHas(diags, "UW005");
    }

    [Fact]
    public async Task UW005_Translate_WithDeltaTime_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void Update() { transform.Translate(Vector3.up * 5f * Time.deltaTime); }
}");
        AssertNotHas(diags, "UW005");
    }

    [Fact]
    public async Task UW005_Velocity_Assignment_InUpdate_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    Rigidbody rb;
    void Update() { rb.velocity = Vector3.up; }
}");
        AssertHas(diags, "UW004");
    }

    [Fact]
    public async Task UW006_TagComparison_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() { if (gameObject.tag == ""Player"") { } }
}");
        AssertHas(diags, "UW006");
    }

    [Fact]
    public async Task UW006_CompareTag_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() { if (gameObject.CompareTag(""Player"")) { } }
}");
        AssertNotHas(diags, "UW006");
    }

    [Fact]
    public async Task UW008_AsyncVoid_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    async void Load() { await System.Threading.Tasks.Task.Delay(1); }
}");
        AssertHas(diags, "UW008");
    }

    [Fact]
    public async Task UW008_AsyncTask_Silent()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
using System.Threading.Tasks;
public class C : MonoBehaviour {
    async Task Load() { await Task.Delay(1); }
}");
        AssertNotHas(diags, "UW008");
    }

    [Fact]
    public async Task UW009_ResourcesLoad_InUpdate_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void Update() { var x = Resources.Load(""X""); }
}");
        AssertHas(diags, "UW009");
    }

    [Fact]
    public async Task UW010_FindObjectOfType_OutsideHot_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void Start() { var e = FindObjectOfType<Enemy>(); }
}
public class Enemy : MonoBehaviour { }");
        AssertHas(diags, "UW010");
    }

    [Fact]
    public async Task UW011_GetComponent_InUpdate_Warns()
    {
        var diags = await RunAsync(Analyzer, @"
using UnityEngine;
public class C : MonoBehaviour {
    void Update() { var r = GetComponent<Rigidbody>(); }
}");
        AssertHas(diags, "UW011");
    }
}
