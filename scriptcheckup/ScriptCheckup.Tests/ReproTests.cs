using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ScriptCheckup.Analyzers;
using ScriptCheckup.Analyzers.FlowAnalysis;
using Xunit;

namespace ScriptCheckup.Tests;

/// <summary>
/// Reproducers for the findings of the audit, revision 2
/// (ScriptCheckup-v2_3-audit-rev2.md). Drop this file into ScriptCheckup.Tests.
///
/// IMPORTANT: these tests were written without a .NET SDK at hand. They have NOT been
/// compiled or executed. A compile error or a wrong expectation on my side is possible,
/// so read a failure together with the comment on the test.
///
/// Every test asserts the CORRECT behaviour. Trait "Expect" says what I predict today:
///   red     defect confirmed by reading the code. The test should FAIL until it is fixed.
///   unknown hypothesis that depends on Roslyn behaviour. FAIL = defect is real,
///           PASS = hypothesis refuted.
///   green   guard or smoke test. Should PASS today and keep passing.
///
/// Run only these:   dotnet test ScriptCheckup.Tests --filter "Category=Repro"
/// Only the guards:  dotnet test ScriptCheckup.Tests --filter "Expect=green"
///
/// Test names start with the audit item they belong to: Item_1_1 is audit item 1.1,
/// Item_N1 is new finding N1, and so on.
/// </summary>
[Trait("Category", "Repro")]
public class ReproTests : AnalyzerTestBase
{
    private static DiagnosticAnalyzer Flow => new DataFlowAnalyzer();

    // ------------------------------------------------------------------ helpers

    private static void AssertNone(ImmutableArray<Diagnostic> diags, string id, string why)
    {
        var hits = diags.Where(d => d.Id == id).Select(d => d.GetMessage()).ToList();
        Assert.True(hits.Count == 0, why + " Unexpected " + id + ": " + string.Join(" | ", hits));
    }

    private static void AssertSome(ImmutableArray<Diagnostic> diags, string id, string why)
    {
        Assert.True(
            diags.Any(d => d.Id == id),
            why + " Expected " + id + ", got: [" + string.Join(", ", diags.Select(d => d.Id)) + "]");
    }

    /// <summary>
    /// Runs the analyzer, takes the first diagnostic with the given id, applies the first
    /// registered code action and returns the resulting text. Unlike the helper in
    /// CodeFixTests it picks the diagnostic by id (a snippet can raise DF003 and DF004 at
    /// once) and tolerates an action that changes nothing.
    /// </summary>
    private static async Task<(string Code, int ActionCount)> ApplyFixAsync(
        DiagnosticAnalyzer analyzer, CodeFixProvider provider, string diagId, string code)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Test", LanguageNames.CSharp)
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var document = project.AddDocument("Test.cs", SourceText.From(code));
        project = document.Project;

        var compilation = await project.GetCompilationAsync();
        var diags = await compilation!
            .WithAnalyzers(ImmutableArray.Create(analyzer))
            .GetAnalyzerDiagnosticsAsync();

        var target = diags.FirstOrDefault(d => d.Id == diagId);
        Assert.True(target is not null, "Precondition: " + diagId + " must be reported on the snippet.");

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, target!, (a, _) => actions.Add(a), CancellationToken.None);
        await provider.RegisterCodeFixesAsync(context);
        if (actions.Count == 0)
            return (code, 0);

        var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
        var apply = operations.OfType<ApplyChangesOperation>().SingleOrDefault();
        if (apply is null)
            return (code, actions.Count);

        var fixedDocument = apply.ChangedSolution.GetDocument(document.Id)!;
        var fixedText = (await fixedDocument.GetTextAsync()).ToString();
        return (fixedText, actions.Count);
    }

    // ------------------------------------------------------------------
    // Group A. Confirmed by reading the code. Expected today: RED.
    // ------------------------------------------------------------------

    [Fact, Trait("Expect", "red")]
    public async Task Item_1_1_LookalikeGetTemporary_IsNotARenderTextureLease()
    {
        // All symbols bind. IsGetTemporaryCall sees TargetMethod.ContainingType.Name ==
        // "MyRenderTexturePool", does not return false, and falls through to the
        // Contains("RenderTexture") syntax fallback, which accepts the receiver text.
        var diags = await RunAsync(Flow, @"
public class MyRenderTexturePool {
    public static object GetTemporary(int w, int h) { return null; }
}
public class C {
    void M() {
        var t = MyRenderTexturePool.GetTemporary(64, 64);
        System.GC.KeepAlive(t);
    }
}");
        AssertNone(diags, "DF003", "A lookalike API was treated as RenderTexture.GetTemporary.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Item_1_1_LookalikeActiveField_IsNotRenderTextureActive()
    {
        // Same fall-through in IsRenderTextureActiveTarget, plus CollectSavedActiveLocals
        // (IsActiveSyntax) which also matches by substring.
        var diags = await RunAsync(Flow, @"
public class MyRenderTextureState {
    public static object active;
}
public class C {
    void M(bool cond) {
        var prev = MyRenderTextureState.active;
        MyRenderTextureState.active = new object();
        if (cond) return;
        MyRenderTextureState.active = prev;
    }
}");
        AssertNone(diags, "DF004", "A lookalike static field named active was treated as RenderTexture.active.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Item_N1_ValueReadOnlyInCatch_IsNotADeadStore()
    {
        // Roslyn's CFG has no exceptional edges, so the catch block is never reached by the
        // DF002 walk. DF002 compensates for finally (IsReadInCoveringFinally) but not for catch.
        var diags = await RunAsync(Flow, @"
using System;
public class C {
    string GetPath() { return null; }
    void Process() { }
    void Log(string s) { }
    void M() {
        string path = GetPath();
        try { Process(); }
        catch (Exception e) { Log(path + e.Message); }
    }
}");
        AssertNone(diags, "DF002", "The value of path is read in catch, so the store is live.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Item_N1_DefaultBeforeTry_ReadInCatch_IsNotADeadStore()
    {
        // The initial null is observable in catch whenever Load() throws before assigning.
        var diags = await RunAsync(Flow, @"
using System;
public class C {
    string Load() { return null; }
    void Use(string s) { }
    void Log(string s) { }
    void M() {
        string s = null;
        try { s = Load(); Use(s); }
        catch (Exception) { Log(s); }
    }
}");
        AssertNone(diags, "DF002", "The initial value of s is read in catch when Load() throws.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Item_2_1_Df003Fix_DoesNotCloseEarlyReturnPath()
    {
        // The fix appends ReleaseTemporary to the end of the declaring block. The path that
        // leaves through "if (cond) return;" still has no release.
        const string code = @"
using UnityEngine;
public class C : MonoBehaviour {
    void M(bool cond) {
        var rt = RenderTexture.GetTemporary(256, 256);
        RenderTexture.active = rt;
        if (cond) return;
        RenderTexture.active = null;
    }
}";
        var (fixedCode, count) = await ApplyFixAsync(Flow, new RenderTextureCodeFixProvider(), "DF003", code);
        Assert.True(count > 0, "The DF003 fix was not offered.");

        var after = await RunAsync(Flow, fixedCode);
        AssertNone(after, "DF003", "DF003 survives its own fix: only the fall-through path got a release.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Item_2_1_Df003Fix_AfterFinalReturn_IsUnreachable()
    {
        // When the block ends with return, the appended statement lands after it.
        const string code = @"
using UnityEngine;
public class C : MonoBehaviour {
    void M(bool cond) {
        var rt = RenderTexture.GetTemporary(256, 256);
        if (cond) { RenderTexture.active = rt; }
        return;
    }
}";
        var (fixedCode, count) = await ApplyFixAsync(Flow, new RenderTextureCodeFixProvider(), "DF003", code);
        Assert.True(count > 0, "The DF003 fix was not offered.");

        var after = await RunAsync(Flow, fixedCode);
        AssertNone(after, "DF003", "The inserted release is unreachable (it sits after the final return).");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Item_2_2_Df004Fix_NestedSave_ChangesNothing()
    {
        // AddRestoreAsync only scans the top-level statements of the method body for
        // "var prev = RenderTexture.active;". With the save inside an if-block it returns the
        // document unchanged, but the action is still registered and shown in the IDE.
        const string code = @"
using UnityEngine;
public class C : MonoBehaviour {
    void M(bool cond, bool flag) {
        if (flag) {
            var prev = RenderTexture.active;
            RenderTexture.active = new RenderTexture(64, 64, 0);
            if (cond) return;
            RenderTexture.active = prev;
        }
    }
}";
        var (fixedCode, count) = await ApplyFixAsync(Flow, new RenderTextureCodeFixProvider(), "DF004", code);
        Assert.True(
            count == 0 || fixedCode != code,
            "A fix is offered (" + count + " action) but applying it does not change the document.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Item_1_2_LeaseInsideConditional_IsTracked()
    {
        // RegisterAcquisition accepts only a direct IVariableInitializerOperation or
        // ISimpleAssignmentOperation parent. A conditional expression is silently skipped.
        var diags = await RunAsync(Flow, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M(bool hdr) {
        var rt = hdr ? RenderTexture.GetTemporary(64, 64) : RenderTexture.GetTemporary(32, 32);
        RenderTexture.active = rt;
    }
}");
        AssertSome(diags, "DF003", "A lease created inside ?: is never released but is not tracked.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Item_1_2_DiscardedLease_IsReported()
    {
        // The result is thrown away, so the lease can never be released.
        var diags = await RunAsync(Flow, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() {
        RenderTexture.GetTemporary(64, 64);
    }
}");
        AssertSome(diags, "DF003", "A discarded lease is a guaranteed leak but is skipped silently.");
    }

    // ------------------------------------------------------------------
    // Group B. Hypotheses that depend on Roslyn behaviour.
    // FAIL = the defect is real. PASS = the hypothesis is refuted.
    // ------------------------------------------------------------------

    [Fact, Trait("Expect", "unknown")]
    public async Task Item_N2_StructFieldWrite_DoesNotKillTheInitialValue()
    {
        // User-defined struct so that the type binds (with the default CLI mode a Unity
        // Vector3 is an error type and the effect may be masked). If AnalyzeDataFlow reports
        // p as written but not read for "p.y = 0f", DF002 sees a full overwrite of p and
        // flags "V p = pos;".
        var diags = await RunAsync(Flow, @"
public struct V { public float x; public float y; }
public class C {
    V pos;
    void M() {
        V p = pos;
        p.y = 0f;
        pos = p;
    }
}");
        AssertNone(diags, "DF002", "A write to a field of a struct local is not a full overwrite of the struct.");
    }

    [Fact, Trait("Expect", "unknown")]
    public async Task Item_N3_OutArgumentOfUnboundCall_ClearsDefinitelyNull()
    {
        // Without UnityEngine.dll a Unity API call is an Invalid operation. Its arguments are
        // not IArgumentOperation, so the out write is invisible to NullFlow.ApplyTransfer.
        var diags = await RunAsync(Flow, @"
public class C {
    void M() {
        object o = null;
        Unknown.Fill(out o);
        var s = o.ToString();
    }
}");
        AssertNone(diags, "DF005", "The callee assigns o through out, so o is no longer definitely null.");
    }

    [Fact, Trait("Expect", "unknown")]
    public async Task Item_1_5_NestedInvalidAccess_DoesNotBlameTheArgument()
    {
        // IsUnconditionalDeref takes the first local under the outer Invalid member access.
        // Here that is the argument o, but the dereferenced value is the result of Get(o).
        var diags = await RunAsync(Flow, @"
public class C {
    void M() {
        object o = null;
        var n = Unknown.Get(o).Name;
    }
}");
        AssertNone(diags, "DF005", "o is an argument of Get, it is not the receiver of .Name.");
    }

    // ------------------------------------------------------------------
    // Group C. Guards and bound-mode smoke tests. Expected today: GREEN.
    // ------------------------------------------------------------------

    [Fact, Trait("Expect", "green")]
    public async Task Item_1_4_MaybeNullThenGuard_IsSilent()
    {
        // The scenario from audit item 1.4, which no existing test or sample contains.
        var diags = await RunAsync(Flow, @"
public class C {
    object MaybeNull() { return null; }
    void M1() {
        object x = MaybeNull();
        if (x != null) { x.ToString(); }
    }
    void M2() {
        object x;
        x = MaybeNull();
        if (x != null) { x.ToString(); }
    }
}");
        AssertNone(diags, "DF005", "A value from a method call is not definitely null.");
    }

    // Bound mode: the stub below makes RenderTexture a real type, so the semantic branch of
    // FlowHelpers (IInvocationOperation with ContainingType RenderTexture) is exercised.
    // Existing unit tests run only in the unbound mode; samples/DataFlow.cs is the one file
    // that covers this path, and no test reads it.
    private const string RenderTextureStub = @"
public class RenderTexture {
    public static RenderTexture active;
    public static RenderTexture GetTemporary(int w, int h) { return null; }
    public static void ReleaseTemporary(RenderTexture rt) { }
}";

    [Fact, Trait("Expect", "green")]
    public async Task Bound_RenderTexture_EarlyReturnLeak_WarnsDF003()
    {
        var diags = await RunAsync(Flow, RenderTextureStub + @"
public class C {
    void M(bool cond) {
        var rt = RenderTexture.GetTemporary(64, 64);
        if (cond) return;
        RenderTexture.ReleaseTemporary(rt);
    }
}");
        AssertSome(diags, "DF003", "Bound mode: the early return skips the release.");
    }

    [Fact, Trait("Expect", "green")]
    public async Task Bound_RenderTexture_ReleasedOnAllPaths_IsSilent()
    {
        var diags = await RunAsync(Flow, RenderTextureStub + @"
public class C {
    void M(bool cond) {
        var rt = RenderTexture.GetTemporary(64, 64);
        if (cond) { RenderTexture.ReleaseTemporary(rt); return; }
        RenderTexture.ReleaseTemporary(rt);
    }
}");
        AssertNone(diags, "DF003", "Bound mode: both paths release the lease.");
    }

    [Fact, Trait("Expect", "green")]
    public async Task Bound_RenderTexture_ActiveBypassedByReturn_WarnsDF004()
    {
        var diags = await RunAsync(Flow, RenderTextureStub + @"
public class C {
    RenderTexture target;
    void M(bool cond) {
        var prev = RenderTexture.active;
        RenderTexture.active = target;
        if (cond) return;
        RenderTexture.active = prev;
    }
}");
        AssertSome(diags, "DF004", "Bound mode: the early return skips the restore.");
    }
}
