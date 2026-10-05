using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace ScriptCheckup.Analyzers.FlowAnalysis
{
    /// <summary>
    /// Data-flow powered analyzer (rules DF001–DF005).
    ///
    /// Unlike <see cref="UnityRulesAnalyzer"/>, which is mostly syntax-driven, this analyzer
    /// builds a real <see cref="ControlFlowGraph"/> per method and combines it with
    /// <see cref="SemanticModel.AnalyzeDataFlow"/> (Roslyn's <c>DataFlowAnalysis</c>).
    ///
    /// Rules:
    ///   DF001 — local assigned but never read            (DataFlowAnalysis over the method body)
    ///   DF002 — dead store: stored value never observed  (per-statement DataFlowAnalysis + CFG reachability)
    ///   DF003 — RenderTexture.GetTemporary not released on every exit path (CFG lease analysis)
    ///   DF004 — RenderTexture.active not restored on every exit path       (forward may-analysis on CFG)
    ///   DF005 — dereference of a definitely-null local/parameter            (forward must-analysis on CFG)
    ///
    /// Name-based matching for Unity APIs is intentional: the CLI analyzes scripts without a
    /// UnityEngine.dll reference, so symbols are error symbols and only names survive.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DataFlowAnalyzer : DiagnosticAnalyzer
    {
        public const string Category = "Unity.DataFlow";

        public static readonly DiagnosticDescriptor DF001 = new DiagnosticDescriptor(
            "DF001",
            "Unused local variable",
            "Local '{0}' is assigned a value that is never read.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
            description: "An assigned-but-never-read local is dead weight and often hints at a forgotten use or a wrong variable name.");

        public static readonly DiagnosticDescriptor DF002 = new DiagnosticDescriptor(
            "DF002",
            "Dead store",
            "The value assigned to '{0}' here is never read before it is overwritten or goes out of scope.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
            description: "A store whose value cannot be observed on any control-flow path is a likely logic error.");

        public static readonly DiagnosticDescriptor DF003 = new DiagnosticDescriptor(
            "DF003",
            "Temporary RenderTexture may leak",
            "Temporary RenderTexture {0} is not released with ReleaseTemporary on all paths to method exit.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
            description: "RenderTexture.GetTemporary leases a texture from a shared pool; every lease must be returned on every exit path, including early returns and throws.");

        public static readonly DiagnosticDescriptor DF004 = new DiagnosticDescriptor(
            "DF004",
            "RenderTexture.active not restored on all exit paths",
            "RenderTexture.active may not be restored to its previous value on this exit path.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
            description: "Path-sensitive companion to RB002: even when a restore exists, early returns and throws can bypass it.");

        public static readonly DiagnosticDescriptor DF005 = new DiagnosticDescriptor(
            "DF005",
            "Definite null dereference",
            "'{0}' is definitely null here; this dereference will throw NullReferenceException.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
            description: "Forward must-analysis over the control-flow graph: null is assigned (or proven by a null test) and dereferenced before any reassignment.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(DF001, DF002, DF003, DF004, DF005);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        }

        private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
        {
            var method = (MethodDeclarationSyntax)context.Node;
            SyntaxNode? bodyRoot = (SyntaxNode?)method.Body ?? method.ExpressionBody?.Expression;
            if (bodyRoot is null)
                return;

            var model = context.SemanticModel;

            IMethodBodyOperation? bodyOp;
            try
            {
                bodyOp = model.GetOperation(method, context.CancellationToken) as IMethodBodyOperation;
            }
            catch (Exception)
            {
                return; // never let an analyzer crash the host on unusual code
            }
            if (bodyOp is null)
                return;

            ControlFlowGraph? cfg;
            try
            {
                cfg = ControlFlowGraph.Create(bodyOp, context.CancellationToken);
            }
            catch (Exception)
            {
                return;
            }

            StoreFlow.Analyze(context, model, bodyRoot, cfg);
            LeaseFlow.Analyze(context, method, cfg);
            NullFlow.Analyze(context, cfg);
        }
    }
}
