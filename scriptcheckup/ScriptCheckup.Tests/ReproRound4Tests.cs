using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ScriptCheckup.Analyzers;
using ScriptCheckup.Analyzers.FlowAnalysis;
using Xunit;

namespace ScriptCheckup.Tests;

/// <summary>
/// Round 4 reproducers, written against ScriptCheckup-Roslyn-v2_4-df.zip.
/// NOT compiled or executed on my side (no .NET SDK available), so read a failure
/// together with the comment on the test.
///
/// Every test asserts the CORRECT behaviour.
///   Expect=red      I am fairly sure it fails today (by reading the code).
///   Expect=unknown  depends on Roslyn CFG shape: FAIL = defect is real, PASS = refuted.
///   Expect=green    guard, should pass today and keep passing.
///
/// Run:  dotnet test ScriptCheckup.Tests --filter "FullyQualifiedName~ReproRound4Tests"
/// Items 2.1 (fail-closed duplicates, SARIF level, exit codes) live in the CLI and cannot
/// be reached from this test project; see the review document for a manual check.
/// </summary>
[Trait("Category", "Repro")]
public class ReproRound4Tests : AnalyzerTestBase
{
    private static DiagnosticAnalyzer Flow => new DataFlowAnalyzer();

    // A real RenderTexture type, so that the semantic branch of FlowHelpers is used and
    // "rt == null" / "rt is not null" bind as ordinary operations.
    private const string RenderTextureStub = @"
public class RenderTexture {
    public static RenderTexture active;
    public static RenderTexture GetTemporary(int w, int h) { return null; }
    public static void ReleaseTemporary(RenderTexture rt) { }
}";

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
        return ((await fixedDocument.GetTextAsync()).ToString(), actions.Count);
    }

    // ------------------------------------------------------------------

    [Fact, Trait("Expect", "red")]
    public async Task Round4_A_ReleaseInsideCallback_IsNotALeak()
    {
        // ContainsRelease deliberately skips releases inside lambdas ("may never run") and
        // Escapes() does not treat a lambda capture as a hand-over. The idiomatic Unity
        // pattern for asynchronous readback releases the texture in the callback, which is
        // exactly what rule RB003 recommends. The method path has no release, so DF003 fires.
        var diags = await RunAsync(Flow, RenderTextureStub + @"
public static class Gpu {
    public static void Request(RenderTexture src, System.Action<object> done) { }
}
public class C {
    void M() {
        var rt = RenderTexture.GetTemporary(64, 64);
        Gpu.Request(rt, req => { RenderTexture.ReleaseTemporary(rt); });
    }
}");
        AssertNone(diags, "DF003", "The lease is released in the readback callback.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Round4_B_IsNotNullGuard_IsNotALeak()
    {
        // IsInfeasibleNullCheckEdge understands only IBinaryOperation ==/!=. NullFlow.IsNullTest
        // also understands "is null", "is not null" and negated patterns, but LeaseFlow does not
        // reuse it, so the pattern form still produces a false DF003.
        var diags = await RunAsync(Flow, RenderTextureStub + @"
public class C {
    void M() {
        var rt = RenderTexture.GetTemporary(64, 64);
        if (rt is not null) RenderTexture.ReleaseTemporary(rt);
    }
}");
        AssertNone(diags, "DF003", "A freshly leased texture is never null, so the guarded release always runs.");
    }

    [Fact, Trait("Expect", "unknown")]
    public async Task Round4_C_ReturnOfNullCheck_StillLeaks()
    {
        // A block that ends in "return rt == null;" has BranchValue "rt == null" but
        // ConditionKind.None and a single fall-through to Exit. IsInfeasibleNullCheckEdge does not
        // look at ConditionKind == None, picks the fall-through as the "true edge" for == and
        // prunes the only way out, so the leak is not reported.
        var diags = await RunAsync(Flow, RenderTextureStub + @"
public class C {
    bool M() {
        var rt = RenderTexture.GetTemporary(64, 64);
        return rt == null;
    }
}");
        AssertSome(diags, "DF003", "The lease is never released on the only path.");
    }

    [Fact, Trait("Expect", "unknown")]
    public async Task Round4_D_EmptyIfBody_StillLeaks()
    {
        // FlowHelpers.Successors removes duplicate destinations. When both edges of the branch
        // lead to the same block, the single remaining successor is classified as the
        // infeasible edge and pruned, although the other edge is feasible.
        var diags = await RunAsync(Flow, RenderTextureStub + @"
public class C {
    void M() {
        var rt = RenderTexture.GetTemporary(64, 64);
        if (rt != null) { }
    }
}");
        AssertSome(diags, "DF003", "The lease is never released.");
    }

    [Fact, Trait("Expect", "red")]
    public async Task Round4_E_Df003Fix_LeavesNoEmptyStatements()
    {
        // Every nested release is replaced by an empty statement, also when it sits inside a
        // block, so the result contains "{ ; return; }". The release should be removed when its
        // parent is a block or a switch section.
        const string code = @"
using UnityEngine;
public class C : MonoBehaviour {
    void M(bool cond) {
        var rt = RenderTexture.GetTemporary(64, 64);
        if (cond) { RenderTexture.ReleaseTemporary(rt); return; }
        RenderTexture.active = rt;
    }
}";
        var (fixedCode, count) = await ApplyFixAsync(Flow, new RenderTextureCodeFixProvider(), "DF003", code);
        Assert.True(count > 0, "The DF003 fix was not offered.");

        var empties = CSharpSyntaxTree.ParseText(fixedCode).GetRoot()
            .DescendantNodes().OfType<EmptyStatementSyntax>().Count();
        Assert.True(empties == 0, "The fix left " + empties + " empty statement(s):\n" + fixedCode);
    }

    [Fact, Trait("Expect", "green")]
    public async Task Round4_F_UE001_DocCommentsInactiveBranchesAndInterpolation_AreSilent()
    {
        // Guard for the descendIntoTrivia change: braces in XML doc text, in a disabled
        // preprocessor branch, in an interpolation hole and in a char literal must not count.
        var diags = await RunAsync(new UnityRulesAnalyzer(), @"
using UnityEngine;
/// <summary>Handles the { and } characters.</summary>
public class C : MonoBehaviour {
#if SOME_UNDEFINED_SYMBOL
    void Broken() {
#endif
    void Update() {
        string s = $""{1 + 1}"";
        char c = '{';
    }
}");
        AssertNone(diags, "UE001", "The active code is balanced.");
    }
}
