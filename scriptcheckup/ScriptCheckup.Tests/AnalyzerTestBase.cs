using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using ScriptCheckup.Analyzers;
using Xunit;

namespace ScriptCheckup.Tests;

public class AnalyzerTestBase
{
    protected static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer, string code)
    {
        var parseOptions = new CSharpParseOptions(
            preprocessorSymbols: new[] { "UNITY_EDITOR" });
        var tree = CSharpSyntaxTree.ParseText(code, parseOptions);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create(analyzer));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    protected static void AssertHas(
        ImmutableArray<Diagnostic> diags, string id, string? contains = null)
    {
        var match = diags.FirstOrDefault(d => d.Id == id &&
            (contains == null || d.GetMessage().Contains(contains)));
        Assert.True(match != null,
            $"Expected diagnostic {id} not found. Got: {string.Join(", ", diags.Select(d => d.Id + ": " + d.GetMessage()))}");
    }

    protected static void AssertNotHas(ImmutableArray<Diagnostic> diags, string id)
    {
        Assert.DoesNotContain(diags, d => d.Id == id);
    }
}
