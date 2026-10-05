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
/// Round 3 reproducers, written against ScriptCheckup-Roslyn-v2_3-df__2_.zip.
/// Four places where the new code looks risky but I could not settle it by reading.
/// NOT compiled or executed on my side (no .NET SDK available), so read a failure
/// together with the comment on the test.
///
/// Every test asserts the CORRECT behaviour.
///   Expect=red      I am fairly sure it fails today.
///   Expect=unknown  depends on Roslyn behaviour: FAIL = defect is real, PASS = refuted.
///
/// Run:  dotnet test ScriptCheckup.Tests --filter "FullyQualifiedName~ReproRound3Tests"
/// </summary>
[Trait("Category", "Repro")]
public class ReproRound3Tests : AnalyzerTestBase
{
    private static DiagnosticAnalyzer Flow => new DataFlowAnalyzer();

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

    private static int CountOf(string text, string needle) =>
        text.Split(new[] { needle }, StringSplitOptions.None).Length - 1;

    // ------------------------------------------------------------------

    [Fact, Trait("Expect", "unknown")]
    public async Task Round3_A_ShortCircuitNullCheck_DoesNotHideLeakPath()
    {
        // IsInfeasibleNullCheckEdge assumes the if-shaped CFG: conditional successor = FALSE
        // edge. It never reads BasicBlock.ConditionKind (NullFlow does). For the first operand
        // of "||" Roslyn emits the branch with ConditionKind.WhenTrue, so the conditional
        // successor is the TRUE edge. For "rt == null" the code then prunes the fall-through,
        // which is the FEASIBLE edge (rt != null), and the path to the end of the method,
        // where the lease is never released, is no longer explored.
        // Stub type on purpose: with a real RenderTexture type "rt == null" binds as an
        // ordinary IBinaryOperation, so the test isolates the ConditionKind question.
        var diags = await RunAsync(Flow, @"
public class RenderTexture {
    public static RenderTexture GetTemporary(int w, int h) { return null; }
    public static void ReleaseTemporary(RenderTexture rt) { }
}
public class C {
    void Use(RenderTexture r) { }
    void M(bool flag) {
        var rt = RenderTexture.GetTemporary(64, 64);
        if (rt == null || flag) { RenderTexture.ReleaseTemporary(rt); return; }
        Use(rt);
    }
}");
        AssertSome(diags, "DF003", "When rt != null and flag is false the method ends without a release.");
    }

    [Fact, Trait("Expect", "unknown")]
    public async Task Round3_B_Df003Fix_ReleaseAsSingleStatementIf_KeepsTreeValid()
    {
        // The fix removes pre-existing releases with RemoveNodes(..., KeepNoTrivia). Here the
        // release is the embedded statement of a brace-less if, a very common form. A removed
        // required child can leave an if without a body, and then the next statement is
        // swallowed by the if (silent semantic change) or the tree is invalid.
        const string code = @"
using UnityEngine;
public class C : MonoBehaviour {
    void M(bool cond) {
        var rt = RenderTexture.GetTemporary(64, 64);
        if (cond) RenderTexture.ReleaseTemporary(rt);
        RenderTexture.active = rt;
    }
}";
        var (fixedCode, count) = await ApplyFixAsync(Flow, new RenderTextureCodeFixProvider(), "DF003", code);
        Assert.True(count > 0, "The DF003 fix was not offered.");

        var tree = CSharpSyntaxTree.ParseText(fixedCode);
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.GetMessage())
            .ToList();
        Assert.True(errors.Count == 0, "The fixed code has syntax errors: " + string.Join(" | ", errors));

        var activeStmt = tree.GetRoot().DescendantNodes()
            .OfType<ExpressionStatementSyntax>()
            .FirstOrDefault(s => s.ToString().Replace(" ", "").Contains("RenderTexture.active=rt"));
        Assert.True(activeStmt is not null, "The statement 'RenderTexture.active = rt;' disappeared.\n" + fixedCode);
        Assert.False(
            activeStmt!.Ancestors().OfType<IfStatementSyntax>().Any(),
            "'RenderTexture.active = rt;' became conditional.\n" + fixedCode);

        Assert.True(
            CountOf(fixedCode, "ReleaseTemporary(rt)") == 1,
            "Expected exactly one release point (the finally).\n" + fixedCode);
    }

    [Fact, Trait("Expect", "red")]
    public async Task Round3_C_ConditionalReassignBeforeTry_ReadInCatch_IsNotADeadStore()
    {
        // IsReassignedBetween counts ANY simple assignment between the store and the try,
        // including one under an if. On the path where flag is false the first value still
        // reaches the catch, but the check treats the store as overwritten and falls back to
        // the CFG walk, which cannot see the catch.
        var diags = await RunAsync(Flow, @"
using System;
public class C {
    string GetPath() { return null; }
    string Other() { return null; }
    void Process() { }
    void Log(string s) { }
    void M(bool flag) {
        string path = GetPath();
        if (flag) path = Other();
        try { Process(); }
        catch (Exception e) { Log(path + e.Message); }
    }
}");
        AssertNone(diags, "DF002", "On the flag == false path the first value of path is read in catch.");
    }

    [Fact, Trait("Expect", "unknown")]
    public async Task Round3_D_UE001_ExtraClosingBrace_IsReported()
    {
        // AnalyzeBraces walks root.DescendantTokens() with the default descendIntoTrivia: false.
        // A stray "}" at the top level is normally kept by the parser as a skipped token inside
        // trivia, so it would never be counted and the depth below zero branch would never run.
        // Reporting tree.GetDiagnostics() errors (CS1022 etc.) would not have this blind spot.
        var diags = await RunAsync(new UnityRulesAnalyzer(), @"
using UnityEngine;
public class C : MonoBehaviour {
    void Update() { }
}
}
");
        AssertSome(diags, "UE001", "There is one closing brace too many.");
    }
}
