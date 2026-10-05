using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ScriptCheckup.Analyzers
{
    /// <summary>
    /// UE family (hard errors), second wave: UE003 Thread.Sleep.
    /// (UE001 unbalanced braces lives in UnityRulesAnalyzer; UE002 "missing using"
    /// is intentionally not ported — the C# compiler already reports CS0246.)
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ErrorRulesAnalyzer : DiagnosticAnalyzer
    {
        public static readonly DiagnosticDescriptor UE003 = UnityHelpers.Descriptor(
            "UE003",
            "Thread.Sleep blocks the main thread",
            "Thread.Sleep blocks Unity's main thread — the game freezes. Use coroutines, async/await with Task.Delay, or Invoke instead.",
            DiagnosticSeverity.Error);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(UE003);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var inv = (InvocationExpressionSyntax)context.Node;
            if (UnityHelpers.GetInvokedName(inv) != "Sleep") return;
            if (UnityHelpers.ReceiverMentions(inv, "Thread"))
            {
                context.ReportDiagnostic(Diagnostic.Create(UE003, inv.GetLocation()));
            }
        }
    }
}
