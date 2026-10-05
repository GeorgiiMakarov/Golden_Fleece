using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ScriptCheckup.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class UnityRulesAnalyzer : DiagnosticAnalyzer
    {
        public const string Category = "Unity";

        public static readonly DiagnosticDescriptor RB002 = new DiagnosticDescriptor(
            "RB002",
            "RenderTexture.active left modified",
            "RenderTexture.active is assigned but not restored to the previous value (or null) before the method exits. Temporary RTs may leak and subsequent blits go to the wrong target.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
            description: "Always save RenderTexture.active, assign a temporary, then restore the original value.");

        public static readonly DiagnosticDescriptor UW007 = new DiagnosticDescriptor(
            "UW007",
            "Event subscription without unsubscription",
            "Subscription to event '{0}' without a matching unsubscription in OnDisable/OnDestroy. This causes memory leaks.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RB001 = new DiagnosticDescriptor(
            "RB001",
            "ReadPixels in hot path",
            "Texture2D.ReadPixels is a GPU to CPU sync point. Avoid in Update/FixedUpdate/LateUpdate; prefer AsyncGPUReadback.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RB003 = new DiagnosticDescriptor(
            "RB003",
            "AsyncGPUReadback without hasError check",
            "AsyncGPUReadback request should check request.hasError before reading data.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RB005 = new DiagnosticDescriptor(
            "RB005",
            "GetTemporary without ReleaseTemporary",
            "RenderTexture.GetTemporary must be paired with ReleaseTemporary, otherwise the temporary pool overflows.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UE001 = new DiagnosticDescriptor(
            "UE001",
            "Unbalanced braces",
            "Unbalanced curly braces detected in the file.",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UL008 = new DiagnosticDescriptor(
            "UL008",
            "Empty Unity message method",
            "Empty {0}() — Unity still invokes it. Remove the method if it does nothing.",
            Category, DiagnosticSeverity.Info, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UW001 = new DiagnosticDescriptor(
            "UW001",
            "FindObjectOfType in hot path",
            "FindObjectOfType / FindObjectsOfType is expensive. Cache the result outside Update loops.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RX001 = new DiagnosticDescriptor(
            "RX001",
            "DestroyImmediate outside editor",
            "DestroyImmediate should only be used in Editor scripts. Use Object.Destroy at runtime.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get
            {
                return ImmutableArray.Create(RB002, UW007, RB001, RB003, RB005, UE001, UL008, UW001, RX001);
            }
        }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxTreeAction(AnalyzeBraces);
        }

        /// <summary>
        /// UE001 — unbalanced curly braces. Counts open/close brace tokens; trivia
        /// (comments) and string/char literals never produce brace tokens, and
        /// interpolation holes are self-balanced, so they cannot skew the count.
        /// </summary>
        private void AnalyzeBraces(SyntaxTreeAnalysisContext context)
        {
            var root = context.Tree.GetRoot(context.CancellationToken);
            int depth = 0;
            // descendIntoTrivia: a stray "}" at the top level is kept by the parser as
            // a skipped token inside trivia — without this it would never be counted
            // (round 3, item D). Comment/string braces never surface as brace tokens.
            foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
            {
                // Skip parser-inserted missing tokens: Roslyn synthesizes a missing
                // `}` for unclosed blocks, which would mask the imbalance.
                if (token.IsMissing)
                    continue;
                if (token.IsKind(SyntaxKind.OpenBraceToken))
                {
                    depth++;
                }
                else if (token.IsKind(SyntaxKind.CloseBraceToken))
                {
                    depth--;
                    if (depth < 0)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            UE001, token.GetLocation()));
                        return;
                    }
                }
            }
            if (depth > 0)
            {
                var last = root.DescendantTokens().LastOrDefault();
                var location = last.RawKind != 0
                    ? last.GetLocation()
                    : Location.Create(context.Tree, new Microsoft.CodeAnalysis.Text.TextSpan(0, 0));
                context.ReportDiagnostic(Diagnostic.Create(
                    UE001, location));
            }
        }

        private static readonly HashSet<string> HotMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "Update", "FixedUpdate", "LateUpdate"
        };

        private static readonly HashSet<string> TeardownMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "OnDisable", "OnDestroy", "OnApplicationQuit", "OnApplicationPause"
        };

        private static readonly HashSet<string> UnityMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "OnEnable", "OnDisable",
            "OnDestroy", "OnGUI", "OnValidate", "Reset"
        };

        private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
        {
            var method = (MethodDeclarationSyntax)context.Node;
            var name = method.Identifier.ValueText;

            if (UnityMessages.Contains(name) && method.Body != null)
            {
                var statements = method.Body.Statements;
                if (statements.Count == 0 ||
                    (statements.Count == 1 && statements[0] is EmptyStatementSyntax))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UL008, method.Identifier.GetLocation(), name));
                }
            }

            if (method.Body != null)
            {
                AnalyzeRenderTextureActive(context, method);
                AnalyzeEventSubscriptions(context, method);
            }
        }

        /// <summary>
        /// RB-002 fixed: detects save (local = RenderTexture.active) + restore patterns.
        /// No longer flags correct save/restore sequences.
        /// </summary>
        private static void AnalyzeRenderTextureActive(SyntaxNodeAnalysisContext context, MethodDeclarationSyntax method)
        {
            var assignments = method.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(a => IsRenderTextureActive(a.Left))
                .ToList();

            if (assignments.Count == 0) return;

            var locals = new HashSet<string>(StringComparer.Ordinal);

            foreach (var v in method.DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>()
                .SelectMany(ld => ld.Declaration.Variables))
            {
                if (v.Initializer != null && IsRenderTextureActive(v.Initializer.Value))
                    locals.Add(v.Identifier.ValueText);
            }

            foreach (var a in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (IsRenderTextureActive(a.Right) && a.Left is IdentifierNameSyntax id)
                    locals.Add(id.Identifier.ValueText);
            }

            bool hasRestore = method.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Any(a => IsRenderTextureActive(a.Left) &&
                          (a.Right.IsKind(SyntaxKind.NullLiteralExpression) ||
                           (a.Right is IdentifierNameSyntax id && locals.Contains(id.Identifier.ValueText))));

            bool hasNonRestoreAssign = assignments.Any(a =>
                !a.Right.IsKind(SyntaxKind.NullLiteralExpression) &&
                !(a.Right is IdentifierNameSyntax id && locals.Contains(id.Identifier.ValueText)));

            if (hasNonRestoreAssign && !hasRestore)
            {
                var first = assignments.First(a =>
                    !a.Right.IsKind(SyntaxKind.NullLiteralExpression) &&
                    !(a.Right is IdentifierNameSyntax id && locals.Contains(id.Identifier.ValueText)));
                context.ReportDiagnostic(Diagnostic.Create(RB002, first.GetLocation()));
            }
        }

        private static bool IsRenderTextureActive(ExpressionSyntax expr)
        {
            if (expr is MemberAccessExpressionSyntax ma)
            {
                if (ma.Name.Identifier.ValueText != "active") return false;
                if (ma.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == "RenderTexture")
                    return true;
                if (ma.Expression is MemberAccessExpressionSyntax ma2 &&
                    ma2.Name.Identifier.ValueText == "RenderTexture")
                    return true;
            }
            return false;
        }

        /// <summary>
        /// UW-007 fixed: skips arithmetic compound assignments (dMass += d).
        /// Bare `identifier += handler` is treated as an event subscription only when
        /// the identifier is a declared event (or delegate-typed field) of the containing
        /// type — this fixes the missed `OnSomething += Handle` case without flagging
        /// numeric accumulations like `score += bonus`.
        /// </summary>
        private static void AnalyzeEventSubscriptions(SyntaxNodeAnalysisContext context, MethodDeclarationSyntax method)
        {
            var name = method.Identifier.ValueText;
            if (name != "OnEnable" && name != "Awake" && name != "Start" && name != "OnValidate")
                return;

            var typeDecl = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (typeDecl == null) return;

            foreach (var add in method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)))
            {
                if (IsLikelyArithmetic(add.Left, add.Right)) continue;

                // Ambiguous bare-identifier += : only an event subscription when the target
                // is a declared event / delegate field; otherwise likely numeric.
                if (add.Left is IdentifierNameSyntax idLeft &&
                    !DeclaresEventLike(typeDecl, idLeft.Identifier.ValueText))
                    continue;

                if (!IsHandlerExpression(add.Right)) continue;

                var eventName = GetEventName(add.Left);
                if (string.IsNullOrEmpty(eventName)) continue;

                bool hasUnsub = typeDecl.Members
                    .OfType<MethodDeclarationSyntax>()
                    .Where(m => TeardownMethods.Contains(m.Identifier.ValueText))
                    .SelectMany(m => m.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    .Any(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression) &&
                              GetEventName(a.Left) == eventName);

                if (!hasUnsub)
                {
                    context.ReportDiagnostic(Diagnostic.Create(UW007, add.GetLocation(), eventName));
                }
            }
        }

        private static bool DeclaresEventLike(TypeDeclarationSyntax typeDecl, string name)
        {
            foreach (var m in typeDecl.Members)
            {
                if (m is EventFieldDeclarationSyntax ef &&
                    ef.Declaration.Variables.Any(v => v.Identifier.ValueText == name))
                    return true;
                if (m is EventDeclarationSyntax ed &&
                    ed.Identifier.ValueText == name)
                    return true;
                if (m is FieldDeclarationSyntax fd &&
                    fd.Declaration.Variables.Any(v => v.Identifier.ValueText == name))
                {
                    var t = fd.Declaration.Type?.ToString() ?? string.Empty;
                    if (t.IndexOf("Action", StringComparison.Ordinal) >= 0 ||
                        t.IndexOf("Func", StringComparison.Ordinal) >= 0 ||
                        t.IndexOf("Handler", StringComparison.Ordinal) >= 0 ||
                        t.IndexOf("delegate", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            return false;
        }

        private static bool IsLikelyArithmetic(ExpressionSyntax left, ExpressionSyntax right)
        {
            // Numeric / char literal on the right → arithmetic, e.g. `x += 1`.
            if (right is LiteralExpressionSyntax lit &&
                (lit.IsKind(SyntaxKind.NumericLiteralExpression) || lit.IsKind(SyntaxKind.CharacterLiteralExpression)))
                return true;

            // Arithmetic expression on the right → arithmetic, e.g. `x += a * b`.
            if (right is BinaryExpressionSyntax)
                return true;

            return false;
        }

        private static bool IsHandlerExpression(ExpressionSyntax expr)
        {
            return expr is IdentifierNameSyntax ||
                   expr is MemberAccessExpressionSyntax ||
                   expr is AnonymousMethodExpressionSyntax ||
                   expr is ParenthesizedLambdaExpressionSyntax ||
                   expr is SimpleLambdaExpressionSyntax ||
                   expr is InvocationExpressionSyntax;
        }

        private static string GetEventName(ExpressionSyntax left)
        {
            if (left is IdentifierNameSyntax id) return id.Identifier.ValueText;
            if (left is MemberAccessExpressionSyntax ma) return ma.Name.Identifier.ValueText;
            return null;
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var inv = (InvocationExpressionSyntax)context.Node;
            var methodName = GetInvokedName(inv);
            if (string.IsNullOrEmpty(methodName)) return;

            var containingMethod = inv.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var isHot = containingMethod != null && HotMethods.Contains(containingMethod.Identifier.ValueText);

            if (methodName == "ReadPixels" && isHot)
            {
                context.ReportDiagnostic(Diagnostic.Create(RB001, inv.GetLocation()));
            }

            if (methodName.StartsWith("Request", StringComparison.Ordinal) &&
                inv.Expression is MemberAccessExpressionSyntax ma &&
                ma.Expression.ToString().IndexOf("AsyncGPUReadback", StringComparison.Ordinal) >= 0)
            {
                var hasErrorCheck = containingMethod != null && containingMethod.DescendantNodes()
                    .OfType<MemberAccessExpressionSyntax>()
                    .Any(m => m.Name.Identifier.ValueText == "hasError");
                if (!hasErrorCheck)
                {
                    context.ReportDiagnostic(Diagnostic.Create(RB003, inv.GetLocation()));
                }
            }

            if (methodName == "GetTemporary")
            {
                var hasRelease = containingMethod != null && containingMethod.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(i => GetInvokedName(i) == "ReleaseTemporary");
                if (!hasRelease)
                {
                    context.ReportDiagnostic(Diagnostic.Create(RB005, inv.GetLocation()));
                }
            }

            if ((methodName == "FindObjectOfType" || methodName == "FindObjectsOfType" ||
                 methodName == "FindObjectsByType") && isHot)
            {
                context.ReportDiagnostic(Diagnostic.Create(UW001, inv.GetLocation()));
            }

            if (methodName == "DestroyImmediate")
            {
                context.ReportDiagnostic(Diagnostic.Create(RX001, inv.GetLocation()));
            }
        }

        private static string GetInvokedName(InvocationExpressionSyntax inv)
        {
            if (inv.Expression is IdentifierNameSyntax id) return id.Identifier.ValueText;
            if (inv.Expression is GenericNameSyntax gn) return gn.Identifier.ValueText;
            if (inv.Expression is MemberAccessExpressionSyntax ma)
            {
                if (ma.Name is GenericNameSyntax mgn) return mgn.Identifier.ValueText;
                return ma.Name.Identifier.ValueText;
            }
            if (inv.Expression is MemberBindingExpressionSyntax mb) return mb.Name.Identifier.ValueText;
            return null;
        }
    }
}
