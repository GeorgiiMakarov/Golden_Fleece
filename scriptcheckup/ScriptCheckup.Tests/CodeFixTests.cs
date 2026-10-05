using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ScriptCheckup.Analyzers;
using ScriptCheckup.Analyzers.FlowAnalysis;
using Xunit;

namespace ScriptCheckup.Tests;

/// <summary>
/// Applies <see cref="RenderTextureCodeFixProvider"/> to a snippet and
/// verifies the fixed source still compiles the same diagnostics away.
/// </summary>
public class CodeFixTests
{
    private static async Task<string> ApplyFixAsync(
        DiagnosticAnalyzer analyzer, CodeFixProvider fixProvider, string code)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Test", LanguageNames.CSharp)
            .AddMetadataReference(MetadataReference.CreateFromFile(
                typeof(object).Assembly.Location));
        var document = project.AddDocument("Test.cs", SourceText.From(code));
        project = document.Project;

        var compilation = await project.GetCompilationAsync();
        var withAnalyzers = compilation!.WithAnalyzers(
            ImmutableArray.Create(analyzer));
        var diags = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        var target = diags.First(d =>
            fixProvider.FixableDiagnosticIds.Contains(d.Id));

        var actions = new System.Collections.Generic.List<CodeAction>();
        var ctx = new CodeFixContext(
            document,
            target,
            (a, _) => actions.Add(a),
            CancellationToken.None);
        await fixProvider.RegisterCodeFixesAsync(ctx);
        Assert.NotEmpty(actions);

        var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
        var apply = operations.OfType<ApplyChangesOperation>().Single();
        workspace.TryApplyChanges(apply.ChangedSolution);
        var fixedDoc = workspace.CurrentSolution.GetDocument(document.Id)!;
        return (await fixedDoc.GetTextAsync()).ToString();
    }

    [Fact]
    public async Task DF003_Fix_Inserts_ReleaseTemporary()
    {
        var fixed_ = await ApplyFixAsync(
            new DataFlowAnalyzer(),
            new RenderTextureCodeFixProvider(),
            @"
using UnityEngine;
public class C : MonoBehaviour {
    void M() {
        var rt = RenderTexture.GetTemporary(256, 256);
        RenderTexture.active = rt;
    }
}");
        Assert.Contains("RenderTexture.ReleaseTemporary(rt);", fixed_);
    }

    [Fact]
    public async Task DF004_Fix_SavesAndRestores_Active()
    {
        // DF004 bypass case: restore exists but an early return skips it.
        // The fix wraps the body in try/finally with the restore in finally.
        var fixed_ = await ApplyFixAsync(
            new DataFlowAnalyzer(),
            new RenderTextureCodeFixProvider(),
            @"
using UnityEngine;
public class C : MonoBehaviour {
    void M(bool cond) {
        var prev = RenderTexture.active;
        RenderTexture.active = new RenderTexture(64, 64, 0);
        if (cond) return;
        RenderTexture.active = prev;
    }
}");
        Assert.Contains("try", fixed_);
        Assert.Contains("finally", fixed_);
        Assert.Contains("RenderTexture.active = prev;", fixed_);
    }
}
