using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ScriptCheckup.Analyzers
{
    /// <summary>
    /// UL family (general Unity lint), second wave.
    /// Ports the remaining UL rules from the HTML analyzer as semantic analyzers:
    /// UL001 public fields, UL002 Debug.Log, UL003 string coroutines,
    /// UL004 string Invoke, UL005 SendMessage, UL006 per-frame allocations,
    /// UL007 string concat in Update, UL009 non-MonoBehaviour class, UL011 Find outside Update.
    /// (UL008 lives in UnityRulesAnalyzer.)
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class LintRulesAnalyzer : DiagnosticAnalyzer
    {
        public static readonly DiagnosticDescriptor UL001 = UnityHelpers.Descriptor(
            "UL001",
            "Public field breaks encapsulation",
            "Public field '{0}' is visible in the Inspector but breaks encapsulation — prefer [SerializeField] private.",
            DiagnosticSeverity.Info);

        public static readonly DiagnosticDescriptor UL002 = UnityHelpers.Descriptor(
            "UL002",
            "Debug.Log left in code",
            "Debug.Log — remember to remove it before the build.",
            DiagnosticSeverity.Info);

        public static readonly DiagnosticDescriptor UL003 = UnityHelpers.Descriptor(
            "UL003",
            "Coroutine by string name",
            "Coroutine started by string name: typos are not caught by the compiler, StopCoroutine is awkward. Use nameof(...).",
            DiagnosticSeverity.Info);

        public static readonly DiagnosticDescriptor UL004 = UnityHelpers.Descriptor(
            "UL004",
            "Invoke by string name",
            "'{0}' with a string method name — refactoring will silently break it. Use nameof(...) or direct calls.",
            DiagnosticSeverity.Info);

        public static readonly DiagnosticDescriptor UL005 = UnityHelpers.Descriptor(
            "UL005",
            "SendMessage usage",
            "'{0}' is slow and untyped — prefer an event or a direct call.",
            DiagnosticSeverity.Info);

        public static readonly DiagnosticDescriptor UL006 = UnityHelpers.Descriptor(
            "UL006",
            "Per-frame allocation",
            "Allocation 'new {0}' every frame — extra GC pressure. Hoist it out of the hot path.",
            DiagnosticSeverity.Info);

        public static readonly DiagnosticDescriptor UL007 = UnityHelpers.Descriptor(
            "UL007",
            "String concatenation in Update",
            "String concatenation in Update creates garbage — use StringBuilder or update on event.",
            DiagnosticSeverity.Info);

        public static readonly DiagnosticDescriptor UL009 = UnityHelpers.Descriptor(
            "UL009",
            "Class is not a MonoBehaviour",
            "Class '{0}' does not inherit MonoBehaviour — the script cannot be attached to a GameObject.",
            DiagnosticSeverity.Info);

        public static readonly DiagnosticDescriptor UL011 = UnityHelpers.Descriptor(
            "UL011",
            "Scene find outside Update",
            "'{0}' — better to find once and keep the reference. (Outside Update it is a lint note; in Update it is UW002.)",
            DiagnosticSeverity.Info);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(UL001, UL002, UL003, UL004, UL005, UL006, UL007, UL009, UL011);

        private static readonly string[] AllocTypes =
        {
            // Reference types only: structs (Vector3, Quaternion, Color, ...) do NOT
            // allocate on the heap. Arrays always allocate.
            "String", "List", "Dictionary", "HashSet", "Queue", "Stack",
            "StringBuilder", "LinkedList", "SortedList", "SortedDictionary", "SortedSet",
        };

        private static readonly string[] FindFamily =
        {
            "Find", "FindWithTag", "FindGameObjectWithTag", "FindGameObjectsWithTag"
        };

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeField, SyntaxKind.FieldDeclaration);
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeBinary, SyntaxKind.AddExpression);
            context.RegisterSyntaxNodeAction(AnalyzeClass, SyntaxKind.ClassDeclaration);
        }

        private void AnalyzeField(SyntaxNodeAnalysisContext context)
        {
            var field = (FieldDeclarationSyntax)context.Node;

            // UL001: public instance fields in MonoBehaviour (visible in the Inspector;
            // in plain C# classes a public field is not a Unity problem)
            bool isPublic = field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
            if (!isPublic) return;
            if (field.Modifiers.Any(m =>
                m.IsKind(SyntaxKind.ConstKeyword) ||
                m.IsKind(SyntaxKind.ReadOnlyKeyword) ||
                m.IsKind(SyntaxKind.StaticKeyword)))
                return;
            var cls = field.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (cls is null || !InheritsUnityBase(context.SemanticModel, cls))
                return;

            foreach (var v in field.Declaration.Variables)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UL001, v.Identifier.GetLocation(), v.Identifier.ValueText));
            }
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var inv = (InvocationExpressionSyntax)context.Node;
            var name = UnityHelpers.GetInvokedName(inv);
            if (string.IsNullOrEmpty(name)) return;

            // UL002: Debug.Log / Debug.LogWarning / Debug.LogError
            if ((name == "Log" || name == "LogWarning" || name == "LogError") &&
                UnityHelpers.ReceiverMentions(inv, "Debug"))
            {
                context.ReportDiagnostic(Diagnostic.Create(UL002, inv.GetLocation()));
                return;
            }

            // UL003: StartCoroutine("Name")
            if (name == "StartCoroutine" && HasStringLiteralArg(inv))
            {
                context.ReportDiagnostic(Diagnostic.Create(UL003, inv.GetLocation()));
                return;
            }

            // UL004: Invoke("Name") / InvokeRepeating("Name", ...)
            if ((name == "Invoke" || name == "InvokeRepeating") && HasStringLiteralArg(inv))
            {
                context.ReportDiagnostic(Diagnostic.Create(UL004, inv.GetLocation(), name));
                return;
            }

            // UL005: SendMessage / BroadcastMessage
            if (name == "SendMessage" || name == "BroadcastMessage")
            {
                context.ReportDiagnostic(Diagnostic.Create(UL005, inv.GetLocation(), name));
                return;
            }

            // UL011: find-family outside hot methods (UW002 covers hot)
            if (!UnityHelpers.IsInHotMethod(inv) &&
                FindFamily.Contains(name) &&
                UnityHelpers.ReceiverMentions(inv, "GameObject"))
            {
                context.ReportDiagnostic(Diagnostic.Create(UL011, inv.GetLocation(), name));
            }
        }

        private static bool HasStringLiteralArg(InvocationExpressionSyntax inv)
        {
            return inv.ArgumentList != null &&
                   inv.ArgumentList.Arguments.Any(a =>
                       a.Expression is LiteralExpressionSyntax lit &&
                       lit.IsKind(SyntaxKind.StringLiteralExpression));
        }

        private void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
        {
            var creation = (ObjectCreationExpressionSyntax)context.Node;

            // UL006: heap allocations in hot methods (reference types and arrays;
            // value types like Vector3 do not allocate)
            if (!UnityHelpers.IsInHotMethod(creation)) return;
            if (creation.Type is ArrayTypeSyntax)
            {
                context.ReportDiagnostic(Diagnostic.Create(UL006, creation.GetLocation(), "array"));
                return;
            }
            var typeName = creation.Type.ToString();
            // strip namespace / generic arity for the comparison
            var shortName = typeName.Split('.').Last().Split('<').First();
            if (AllocTypes.Contains(shortName))
            {
                context.ReportDiagnostic(Diagnostic.Create(UL006, creation.GetLocation(), shortName));
            }
        }

        private void AnalyzeBinary(SyntaxNodeAnalysisContext context)
        {
            var bin = (BinaryExpressionSyntax)context.Node;

            // UL007: "a" + b string concatenation in hot methods
            if (!UnityHelpers.IsInHotMethod(bin)) return;
            if (IsStringLiteral(bin.Left) || IsStringLiteral(bin.Right))
            {
                context.ReportDiagnostic(Diagnostic.Create(UL007, bin.GetLocation()));
            }
        }

        private static bool IsStringLiteral(ExpressionSyntax expr)
        {
            while (expr is ParenthesizedExpressionSyntax p)
                expr = p.Expression;
            return expr is LiteralExpressionSyntax lit &&
                   lit.IsKind(SyntaxKind.StringLiteralExpression) ||
                   expr is InterpolatedStringExpressionSyntax;
        }

        private void AnalyzeClass(SyntaxNodeAnalysisContext context)
        {
            var cls = (ClassDeclarationSyntax)context.Node;

            // UL009: non-static class not inheriting MonoBehaviour.
            // ScriptableObject is a valid Unity base too; abstract classes are bases.
            if (cls.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) return;
            if (cls.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword))) return;
            if (InheritsUnityBase(context.SemanticModel, cls)) return;

            context.ReportDiagnostic(Diagnostic.Create(
                UL009, cls.Identifier.GetLocation(), cls.Identifier.ValueText));
        }

        private static readonly string[] UnityBaseNames = { "MonoBehaviour", "ScriptableObject" };

        /// <summary>
        /// Semantic base-type walk when it binds, syntax substring fallback otherwise.
        /// </summary>
        private static bool InheritsUnityBase(SemanticModel model, ClassDeclarationSyntax cls)
        {
            var symbol = model.GetDeclaredSymbol(cls) as INamedTypeSymbol;
            for (var b = symbol?.BaseType; b is not null; b = b.BaseType)
            {
                if (UnityBaseNames.Contains(b.Name))
                    return true;
            }
            // Syntax fallback (no UnityEngine.dll or base in another file): check the
            // direct base list textually.
            if (cls.BaseList != null)
            {
                foreach (var t in cls.BaseList.Types)
                {
                    var text = t.Type.ToString();
                    if (UnityBaseNames.Any(n => text.IndexOf(n, StringComparison.Ordinal) >= 0))
                        return true;
                }
            }
            return false;
        }
    }
}
