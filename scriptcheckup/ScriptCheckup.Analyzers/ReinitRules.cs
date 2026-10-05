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
    /// RX family (Play Mode reinit / dirty statics), second wave.
    /// Ports the remaining RX rules from the HTML analyzer as semantic analyzers:
    /// RX002 singleton never nulled, RX003 static event without unsubscription,
    /// RX004 static counter, RX005 DontDestroyOnLoad without duplicate guard,
    /// RX006 Editor API isolation note, RX008 SceneManager event subscription.
    /// (RX001 DestroyImmediate lives in UnityRulesAnalyzer.)
    /// These rules matter when "Enter Play Mode Options" disables Domain Reload:
    /// static state survives between Play sessions and causes ghost bugs.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ReinitRulesAnalyzer : DiagnosticAnalyzer
    {
        public static readonly DiagnosticDescriptor RX002 = UnityHelpers.Descriptor(
            "RX002",
            "Singleton never nulled",
            "Singleton '{0}' is never set to null: with Domain Reload disabled it will keep pointing at a destroyed object and Awake of a new scene will break. Null it in OnDestroy.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor RX003 = UnityHelpers.Descriptor(
            "RX003",
            "Static event without unsubscription",
            "Static event '{0}': subscription without unsubscription. Subscribers survive scene reloads and Play Mode sessions — the handler list grows forever.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor RX004 = UnityHelpers.Descriptor(
            "RX004",
            "Static counter survives Play Mode",
            "Static counter '{0}' keeps counting from the previous Play Mode session — set its start value explicitly (e.g. in RuntimeInitializeOnLoadMethod).",
            DiagnosticSeverity.Info);

        public static readonly DiagnosticDescriptor RX005 = UnityHelpers.Descriptor(
            "RX005",
            "DontDestroyOnLoad without duplicate guard",
            "DontDestroyOnLoad without a duplicate guard: re-entering the scene spawns a second singleton. Add 'if (Instance != null && Instance != this) Destroy(gameObject)'.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor RX006 = UnityHelpers.Descriptor(
            "RX006",
            "Editor API isolated",
            "Editor API is isolated with #if UNITY_EDITOR — as it should be. Just don't forget #endif and don't leave half the logic inside conditional compilation.",
            DiagnosticSeverity.Info);

        public static readonly DiagnosticDescriptor RX008 = UnityHelpers.Descriptor(
            "RX008",
            "SceneManager event without unsubscription",
            "Subscription to SceneManager.{0} without unsubscription — after a scene reload there will be two, three, ten subscriptions.",
            DiagnosticSeverity.Warning);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(RX002, RX003, RX004, RX005, RX006, RX008);

        private static readonly string[] EditorApis =
        {
            "UnityEditor", "AssetDatabase", "EditorApplication", "EditorGUILayout",
            "EditorPrefs", "SessionState", "MenuItem", "EditorUtility", "Handles",
            "EditorGUI", "EditorWindow"
        };

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeClass, SyntaxKind.ClassDeclaration);
            context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
            context.RegisterSyntaxNodeAction(AnalyzeAttribute, SyntaxKind.Attribute);
        }

        private void AnalyzeClass(SyntaxNodeAnalysisContext context)
        {
            var cls = (ClassDeclarationSyntax)context.Node;

            AnalyzeSingleton(context, cls);
            AnalyzeStaticEvents(context, cls);
            AnalyzeStaticCounters(context, cls);
            AnalyzeDontDestroyOnLoad(context, cls);
            AnalyzeSceneManagerSubs(context, cls);
        }

        // RX002: `<Name> = this` (singleton assign) with no `<Name> = null` anywhere in the class.
        private void AnalyzeSingleton(SyntaxNodeAnalysisContext context, ClassDeclarationSyntax cls)
        {
            var assignsThis = cls.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                            a.Right is ThisExpressionSyntax &&
                            a.Left is IdentifierNameSyntax)
                .ToList();
            if (assignsThis.Count == 0) return;

            bool nulled = cls.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Any(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                          a.Right.IsKind(SyntaxKind.NullLiteralExpression) &&
                          a.Left is IdentifierNameSyntax);

            if (nulled) return;

            foreach (var a in assignsThis)
            {
                var id = (IdentifierNameSyntax)a.Left;
                context.ReportDiagnostic(Diagnostic.Create(
                    RX002, a.GetLocation(), id.Identifier.ValueText));
            }
        }

        // RX003: static event declaration without a matching `-=` in the class.
        private void AnalyzeStaticEvents(SyntaxNodeAnalysisContext context, ClassDeclarationSyntax cls)
        {
            var staticEvents = cls.Members
                .OfType<EventFieldDeclarationSyntax>()
                .Where(e => e.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                .SelectMany(e => e.Declaration.Variables)
                .Select(v => v.Identifier.ValueText)
                .ToList();
            if (staticEvents.Count == 0) return;

            var unsubs = new HashSet<string>(cls.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression))
                .Select(a => EventTargetName(a.Left))
                .Where(n => n != null), StringComparer.Ordinal);

            foreach (var ev in staticEvents)
            {
                if (unsubs.Contains(ev)) continue;
                var decl = cls.Members.OfType<EventFieldDeclarationSyntax>()
                    .SelectMany(e => e.Declaration.Variables)
                    .First(v => v.Identifier.ValueText == ev);
                context.ReportDiagnostic(Diagnostic.Create(
                    RX003, decl.Identifier.GetLocation(), ev));
            }
        }

        private static string EventTargetName(ExpressionSyntax left)
        {
            if (left is IdentifierNameSyntax id) return id.Identifier.ValueText;
            if (left is MemberAccessExpressionSyntax ma) return ma.Name.Identifier.ValueText;
            return null;
        }

        // RX004: static field incremented with ++ (info: it survives Play Mode).
        private void AnalyzeStaticCounters(SyntaxNodeAnalysisContext context, ClassDeclarationSyntax cls)
        {
            var statics = new HashSet<string>(cls.Members
                .OfType<FieldDeclarationSyntax>()
                .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                .SelectMany(f => f.Declaration.Variables)
                .Select(v => v.Identifier.ValueText), StringComparer.Ordinal);
            if (statics.Count == 0) return;

            foreach (var incr in cls.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
                .Where(u => u.IsKind(SyntaxKind.PostIncrementExpression) &&
                            u.Operand is IdentifierNameSyntax id &&
                            statics.Contains(id.Identifier.ValueText)))
            {
                var id = (IdentifierNameSyntax)incr.Operand;
                context.ReportDiagnostic(Diagnostic.Create(
                    RX004, incr.GetLocation(), id.Identifier.ValueText));
            }
        }

        // RX005: DontDestroyOnLoad without a duplicate guard in the same method.
        private void AnalyzeDontDestroyOnLoad(SyntaxNodeAnalysisContext context, ClassDeclarationSyntax cls)
        {
            foreach (var ddol in cls.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => UnityHelpers.GetInvokedName(i) == "DontDestroyOnLoad"))
            {
                var method = UnityHelpers.GetContainingMethod(ddol);
                if (method == null) continue;
                var body = method.Body?.ToString() ?? "";
                bool hasGuard = body.IndexOf("!= null", StringComparison.Ordinal) >= 0 &&
                                body.IndexOf("Destroy", StringComparison.Ordinal) >= 0;
                if (!hasGuard)
                {
                    context.ReportDiagnostic(Diagnostic.Create(RX005, ddol.GetLocation()));
                }
            }
        }

        // RX008: SceneManager.sceneLoaded|sceneUnloaded|activeSceneChanged += without -=
        private void AnalyzeSceneManagerSubs(SyntaxNodeAnalysisContext context, ClassDeclarationSyntax cls)
        {
            var subs = cls.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression) &&
                            a.Left is MemberAccessExpressionSyntax ma &&
                            ma.Expression.ToString().IndexOf("SceneManager", StringComparison.Ordinal) >= 0)
                .Select(a => new
                {
                    Node = (SyntaxNode)a,
                    Event = ((MemberAccessExpressionSyntax)a.Left).Name.Identifier.ValueText
                })
                .Where(x => x.Event == "sceneLoaded" || x.Event == "sceneUnloaded" ||
                            x.Event == "activeSceneChanged")
                .ToList();
            if (subs.Count == 0) return;

            var unsubs = new HashSet<string>(cls.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression) &&
                            a.Left is MemberAccessExpressionSyntax)
                .Select(a => ((MemberAccessExpressionSyntax)a.Left).Name.Identifier.ValueText), StringComparer.Ordinal);

            foreach (var s in subs)
            {
                if (!unsubs.Contains(s.Event))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        RX008, s.Node.GetLocation(), s.Event));
                }
            }
        }

        // RX006: Editor API usage inside #if UNITY_EDITOR — positive note (HTML parity).
        private void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
        {
            var ma = (MemberAccessExpressionSyntax)context.Node;
            var root = RootIdentifier(ma.Expression);
            if (root == null || !EditorApis.Contains(root)) return;
            // Skip the inner part of a longer chain (report once per chain root).
            if (ma.Parent is MemberAccessExpressionSyntax parent &&
                parent.Expression == ma)
                return;
            if (UnityHelpers.HasUnityEditorGuard(ma))
            {
                context.ReportDiagnostic(Diagnostic.Create(RX006, ma.GetLocation()));
            }
        }

        private void AnalyzeAttribute(SyntaxNodeAnalysisContext context)
        {
            var attr = (AttributeSyntax)context.Node;
            var name = attr.Name.ToString().Split('.').Last();
            if (!EditorApis.Contains(name)) return;
            if (UnityHelpers.HasUnityEditorGuard(attr))
            {
                context.ReportDiagnostic(Diagnostic.Create(RX006, attr.GetLocation()));
            }
        }

        private static string RootIdentifier(ExpressionSyntax expr)
        {
            while (expr is MemberAccessExpressionSyntax ma)
                expr = ma.Expression;
            return (expr as IdentifierNameSyntax)?.Identifier.ValueText;
        }
    }
}
