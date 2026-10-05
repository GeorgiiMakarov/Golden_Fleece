using System;
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
/// Round 2 reproducers, written against ScriptCheckup-Roslyn-v2_3-df__1_.zip
/// (the revision that already contains the fixes for ReproTests.cs).
/// Drop this file next to ReproTests.cs.
///
/// Same conventions as round 1. These tests were NOT compiled or executed on my side
/// (no .NET SDK available), so read a failure together with the comment on the test.
/// Every test asserts the CORRECT behaviour. Trait "Expect" is my prediction for the
/// current code: red = should fail until fixed, green = should pass today and keep passing.
///
/// Run:  dotnet test ScriptCheckup.Tests --filter "FullyQualifiedName~ReproRound2Tests"
/// </summary>
[Trait("Category", "Repro")]
public class ReproRound2Tests : AnalyzerTestBase
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

    private static int CountOf(string text, string needle) =>
        text.Split(new[] { needle }, StringSplitOptions.None).Length - 1;

    // ------------------------------------------------------------------ tests

    [Fact, Trait("Expect", "red")]
    public async Task Round2_A_Df003Fix_NestedRelease_IsFoldedIntoFinally()
    {
        // README promises: "pre-existing releases of the same lease are folded into it".
        // AddReleaseAsync only drops releases that are top-level statements of the block
        // (IsReleaseOf on block.Statements). A release nested in an if-branch, which is
        // exactly the "released on one branch only" case DF003 exists for, stays in place.
        // The finally then releases the same lease a second time on that branch.
        const string code = @"
using UnityEngine;
public class C : MonoBehaviour {
    void M(bool cond) {
        var rt = RenderTexture.GetTemporary(64, 64);
        if (cond) {
            RenderTexture.ReleaseTemporary(rt);
            return;
        }
        RenderTexture.active = rt;
    }
}";
        var (fixedCode, count) = await ApplyFixAsync(Flow, new RenderTextureCodeFixProvider(), "DF003", code);
        Assert.True(count > 0, "The DF003 fix was not offered.");

        int releases = CountOf(fixedCode, "ReleaseTemporary(rt)");
        Assert.True(
            releases == 1,
            "Expected exactly one release point (the finally). Found " + releases +
            " occurrences, so the cond branch releases the lease twice.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Round2_B_NullGuardedRelease_IsNotALeak()
    {
        // Right after GetTemporary the lease is non-null, so the false edge of
        // "if (rt != null)" is infeasible. ExistsLeakingPath explores both successors of a
        // conditional block and never looks at the branch value, so the false edge reaches
        // the exit without a release and DF003 is reported.
        var diags = await RunAsync(Flow, @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() {
        var rt = RenderTexture.GetTemporary(64, 64);
        RenderTexture.active = rt;
        RenderTexture.active = null;
        if (rt != null) RenderTexture.ReleaseTemporary(rt);
    }
}");
        AssertNone(diags, "DF003", "A null-guarded release of a freshly leased texture is a valid release on every feasible path.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Round2_C_GuardReturnBetweenStoreAndTry_ReadInCatch_IsNotADeadStore()
    {
        // IsReadInAssociatedCatch reuses IsCoveredByTry, which requires that no
        // return/throw/goto/break/continue sits between the store and the try. That
        // precondition is needed to PROVE protection (DF003/DF004), but for liveness it is
        // too strict: any path from the store to the try is enough for the catch to see it.
        var diags = await RunAsync(Flow, @"
using System;
public class C {
    string GetPath() { return null; }
    void Process() { }
    void Log(string s) { }
    void M(bool enabled) {
        string path = GetPath();
        if (!enabled) return;
        try { Process(); }
        catch (Exception e) { Log(path + e.Message); }
    }
}");
        AssertNone(diags, "DF002", "path is read in catch on every path that reaches the try.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Round2_D_Df004Fix_RestoreToNull_IsOfferedButChangesNothing()
    {
        // Restoring to null is a valid restore for DF004, so the diagnostic fires. The fix
        // only knows how to wrap code after "var prev = RenderTexture.active;". Without such
        // a declaration AddRestoreAsync returns the document unchanged, yet the action
        // "Save and restore RenderTexture.active" is still registered and shown in the IDE.
        const string code = @"
using UnityEngine;
public class C : MonoBehaviour {
    void M(bool cond) {
        RenderTexture.active = new RenderTexture(64, 64, 0);
        if (cond) return;
        RenderTexture.active = null;
    }
}";
        var (fixedCode, count) = await ApplyFixAsync(Flow, new RenderTextureCodeFixProvider(), "DF004", code);
        Assert.True(
            count == 0 || fixedCode != code,
            "A fix is offered (" + count + " action) but applying it does not change the document.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Round2_E_UE001_UnbalancedBraces_IsReported()
    {
        // UE001 is declared in UnityRulesAnalyzer and listed in SupportedDiagnostics, but
        // there is no Diagnostic.Create(UE001, ...) anywhere. The README lists it as an
        // error rule, so the catalogue says 46 rules while 45 can fire.
        var diags = await RunAsync(new UnityRulesAnalyzer(), @"
using UnityEngine;
public class C : MonoBehaviour {
    void Update() {
        int x = 1;
    }
");
        AssertSome(diags, "UE001", "The class is never closed.");
    }

    [Fact, Trait("Expect", "green")]
    public async Task Round2_E_UE001_BalancedBraces_IsSilent()
    {
        // Guard for the day UE001 gets implemented: braces inside strings, chars and
        // comments must not count.
        var diags = await RunAsync(new UnityRulesAnalyzer(), @"
using UnityEngine;
public class C : MonoBehaviour {
    // a brace in a comment {
    void Update() {
        string s = ""{"";
        char c = '}';
    }
}");
        AssertNone(diags, "UE001", "The braces are balanced once strings and comments are ignored.");
    }
}
