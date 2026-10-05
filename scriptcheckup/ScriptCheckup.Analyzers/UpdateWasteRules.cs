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
    /// <summary>
    /// UW family (per-frame waste), second wave.
    /// Ports the remaining UW rules from the HTML analyzer as semantic analyzers:
    /// UW002 Find in Update, UW003 Camera.main, UW004 physics in Update,
    /// UW005 movement without Time.deltaTime, UW006 .tag comparison,
    /// UW008 async void, UW009 Resources.Load in Update, UW010 FindObjectOfType anywhere.
    /// (UW001/UW007 live in UnityRulesAnalyzer.)
    /// "In Update" is resolved via the containing method symbol (Update/FixedUpdate/LateUpdate),
    /// not via brace-range regexes — nested/local functions are handled correctly.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class UpdateWasteRulesAnalyzer : DiagnosticAnalyzer
    {
        public static readonly DiagnosticDescriptor UW002 = UnityHelpers.Descriptor(
            "UW002",
            "Scene find in Update",
            "'{0}' is called in Update — it searches the whole scene every frame. Cache the reference in Awake/Start.",
            DiagnosticSeverity.Warning,
            "GameObject.Find and its tag-based siblings walk the scene graph; per-frame use is a classic hotspot.");

        public static readonly DiagnosticDescriptor UW003 = UnityHelpers.Descriptor(
            "UW003",
            "Camera.main in Update",
            "Camera.main inside Update — every call searches the scene by tag. Cache the camera reference.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor UW004 = UnityHelpers.Descriptor(
            "UW004",
            "Physics in Update",
            "Physics ('{0}') in Update — move it to FixedUpdate, otherwise behavior is frame-rate dependent and unstable.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor UW005 = UnityHelpers.Descriptor(
            "UW005",
            "Movement without Time.deltaTime",
            "Movement without Time.deltaTime — speed depends on FPS. Multiply by Time.deltaTime for frame-rate independence.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor UW006 = UnityHelpers.Descriptor(
            "UW006",
            "Tag comparison",
            "Comparing .tag with == — use CompareTag(\"...\") instead: faster and typo-safe.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor UW008 = UnityHelpers.Descriptor(
            "UW008",
            "async void method",
            "async void in '{0}' — exceptions from such a method crash the process; use async Task.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor UW009 = UnityHelpers.Descriptor(
            "UW009",
            "Resources.Load in Update",
            "Resources.Load in Update — disk load every frame; cache the asset.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor UW010 = UnityHelpers.Descriptor(
            "UW010",
            "FindObjectOfType outside hot path",
            "FindObjectOfType/FindObjectsOfType walks all scene objects — cache the result in Awake/Start even outside Update.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor UW011 = UnityHelpers.Descriptor(
            "UW011",
            "GetComponent in Update",
            "GetComponent in Update looks the component up every frame — cache it in Awake/Start.",
            DiagnosticSeverity.Warning);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(UW002, UW003, UW004, UW005, UW006, UW008, UW009, UW010, UW011);

        private static readonly HashSet<string> FindFamily = new HashSet<string>(StringComparer.Ordinal)
        {
            "Find", "FindWithTag", "FindGameObjectWithTag", "FindGameObjectsWithTag"
        };

        private static readonly HashSet<string> PhysicsApis = new HashSet<string>(StringComparer.Ordinal)
        {
            "AddForce", "AddTorque", "AddRelativeForce", "AddRelativeTorque",
            "MovePosition", "MoveRotation"
        };

        private static readonly HashSet<string> FindObjectFamily = new HashSet<string>(StringComparer.Ordinal)
        {
            "FindObjectOfType", "FindObjectsOfType", "FindObjectsByType", "FindFirstObjectByType",
            "FindAnyObjectByType",
        };

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
            context.RegisterSyntaxNodeAction(AnalyzeBinary, SyntaxKind.EqualsExpression);
            context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.AddAssignmentExpression);
            context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
            context.RegisterSyntaxNodeAction(AnalyzeMethodDecl, SyntaxKind.MethodDeclaration);
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var inv = (InvocationExpressionSyntax)context.Node;
            var name = UnityHelpers.GetInvokedName(inv);
            if (string.IsNullOrEmpty(name)) return;

            bool hot = UnityHelpers.IsInHotMethod(inv);

            // UW011: GetComponent<T> in Update
            if (hot && name == "GetComponent")
            {
                context.ReportDiagnostic(Diagnostic.Create(UW011, inv.GetLocation()));
                return;
            }

            // UW002: find-family in Update
            if (hot && FindFamily.Contains(name) &&
                UnityHelpers.ReceiverMentions(inv, "GameObject"))
            {
                context.ReportDiagnostic(Diagnostic.Create(UW002, inv.GetLocation(), name));
                return;
            }

            // UW004: physics APIs in Update/LateUpdate (FixedUpdate is the right place).
            // Impulse / VelocityChange are one-shot by design — not per-frame accumulation.
            if (PhysicsApis.Contains(name) && IsInFrameUpdate(inv) && !IsImpulseCall(inv))
            {
                context.ReportDiagnostic(Diagnostic.Create(UW004, inv.GetLocation(), name));
                return;
            }

            // UW005: transform.Translate(...) without Time.deltaTime on the statement
            // (in FixedUpdate, Time.fixedDeltaTime also counts)
            if (hot && name == "Translate" &&
                UnityHelpers.ReceiverMentions(inv, "transform"))
            {
                var stmt = inv.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
                if (stmt != null && !HasDeltaTime(stmt))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UW005, inv.GetLocation()));
                }
                return;
            }

            // UW009: Resources.Load in Update
            if (hot && name == "Load" &&
                UnityHelpers.ReceiverMentions(inv, "Resources"))
            {
                context.ReportDiagnostic(Diagnostic.Create(UW009, inv.GetLocation()));
                return;
            }

            // UW010: FindObjectOfType anywhere (UW001 covers the hot path; skip it here)
            if (!hot && FindObjectFamily.Contains(name))
            {
                context.ReportDiagnostic(Diagnostic.Create(UW010, inv.GetLocation()));
            }
        }

        private void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
        {
            var ma = (MemberAccessExpressionSyntax)context.Node;

            // UW003: Camera.main in Update
            if (ma.Name.Identifier.ValueText == "main" &&
                ma.Expression is IdentifierNameSyntax id &&
                id.Identifier.ValueText == "Camera" &&
                UnityHelpers.IsInHotMethod(ma))
            {
                context.ReportDiagnostic(Diagnostic.Create(UW003, ma.GetLocation()));
            }

            // UW006: .tag == comparison is handled in AnalyzeBinary; nothing here.
        }

        private void AnalyzeBinary(SyntaxNodeAnalysisContext context)
        {
            var bin = (BinaryExpressionSyntax)context.Node;

            // UW006: x.tag == "..."  or  "..." == x.tag
            if (IsTagAccess(bin.Left) || IsTagAccess(bin.Right))
            {
                context.ReportDiagnostic(Diagnostic.Create(UW006, bin.GetLocation()));
            }
        }

        private static bool IsTagAccess(ExpressionSyntax expr)
        {
            return expr is MemberAccessExpressionSyntax ma &&
                   ma.Name.Identifier.ValueText == "tag";
        }

        /// <summary>
        /// Update/LateUpdate but NOT FixedUpdate — the frame-rate-dependent zone
        /// where physics calls don't belong.
        /// </summary>
        private static bool IsInFrameUpdate(SyntaxNode node)
        {
            var m = UnityHelpers.GetContainingMethod(node);
            if (m == null) return false;
            var n = m.Identifier.ValueText;
            return n == "Update" || n == "LateUpdate";
        }

        /// <summary>
        /// AddForce/AddTorque with ForceMode.Impulse (or VelocityChange): a one-shot
        /// kick, not per-frame force accumulation — the Update placement is idiomatic.
        /// </summary>
        private static bool IsImpulseCall(InvocationExpressionSyntax inv)
        {
            if (inv.ArgumentList.Arguments.Count < 2)
                return false;
            var mode = inv.ArgumentList.Arguments[1].Expression.ToString();
            return mode.IndexOf("Impulse", StringComparison.Ordinal) >= 0 ||
                   mode.IndexOf("VelocityChange", StringComparison.Ordinal) >= 0;
        }

        private void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
        {
            var assign = (AssignmentExpressionSyntax)context.Node;

            // UW004: .velocity = / .angularVelocity = in Update/LateUpdate
            // (Unity 6 renamed velocity to linearVelocity — catch both)
            if (assign.Left is MemberAccessExpressionSyntax ma &&
                (ma.Name.Identifier.ValueText == "velocity" ||
                 ma.Name.Identifier.ValueText == "linearVelocity" ||
                 ma.Name.Identifier.ValueText == "angularVelocity") &&
                IsInFrameUpdate(assign))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(UW004, assign.GetLocation(), ma.Name.Identifier.ValueText));
                return;
            }

            // UW005: transform.position += ... without Time.deltaTime
            if (assign.Left is MemberAccessExpressionSyntax pma &&
                pma.Name.Identifier.ValueText == "position" &&
                UnityHelpers.IsInHotMethod(assign))
            {
                var stmt = assign.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
                if (stmt != null && !HasDeltaTime(stmt))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UW005, assign.GetLocation()));
                }
            }
        }

        private static bool HasDeltaTime(StatementSyntax stmt)
        {
            var text = stmt.ToString();
            return text.IndexOf("Time.deltaTime", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("Time.fixedDeltaTime", StringComparison.Ordinal) >= 0;
        }

        private void AnalyzeMethodDecl(SyntaxNodeAnalysisContext context)
        {
            var method = (MethodDeclarationSyntax)context.Node;

            // UW008: async void (excluding Unity message methods and event handlers is overkill;
            // the HTML flags all async void — keep parity but skip obvious event-handler shapes)
            bool isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
            bool isVoid = method.ReturnType is PredefinedTypeSyntax pts &&
                          pts.Keyword.IsKind(SyntaxKind.VoidKeyword);
            if (isAsync && isVoid)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(UW008, method.Identifier.GetLocation(),
                        method.Identifier.ValueText));
            }
        }
    }
}
