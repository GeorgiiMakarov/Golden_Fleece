using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ScriptCheckup.Analyzers
{
    /// <summary>
    /// Shared helpers for the Unity rule analyzers: Unity message method
    /// classification, invocation name extraction (generic-aware), and
    /// preprocessor-guard detection.
    /// </summary>
    internal static class UnityHelpers
    {
        public static readonly HashSet<string> HotMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "Update", "FixedUpdate", "LateUpdate"
        };

        public static readonly HashSet<string> TeardownMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "OnDisable", "OnDestroy", "OnApplicationQuit", "OnApplicationPause"
        };

        public static readonly HashSet<string> UnityMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "OnEnable", "OnDisable",
            "OnDestroy", "OnGUI", "OnValidate", "Reset"
        };

        /// <summary>
        /// Generic-aware invoked method name: handles Foo(), obj.Foo(),
        /// Foo&lt;T&gt;() and obj.Foo&lt;T&gt;().
        /// </summary>
        public static string GetInvokedName(InvocationExpressionSyntax inv)
        {
            switch (inv.Expression)
            {
                case IdentifierNameSyntax id:
                    return id.Identifier.ValueText;
                case GenericNameSyntax gn:
                    return gn.Identifier.ValueText;
                case MemberAccessExpressionSyntax ma:
                    return GetSimpleName(ma.Name);
                case MemberBindingExpressionSyntax mb:
                    return GetSimpleName(mb.Name);
                default:
                    return null;
            }
        }

        private static string GetSimpleName(SimpleNameSyntax name)
        {
            switch (name)
            {
                case IdentifierNameSyntax id: return id.Identifier.ValueText;
                case GenericNameSyntax gn: return gn.Identifier.ValueText;
                default: return name.Identifier.ValueText;
            }
        }

        /// <summary>
        /// For `a.b.C(...)` returns "C"; for `C(...)` returns null (no receiver).
        /// </summary>
        public static string GetReceiverName(InvocationExpressionSyntax inv)
        {
            if (inv.Expression is MemberAccessExpressionSyntax ma)
            {
                return ma.Expression.ToString();
            }
            return null;
        }

        public static MethodDeclarationSyntax GetContainingMethod(SyntaxNode node)
        {
            return node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        }

        public static bool IsInHotMethod(SyntaxNode node)
        {
            var m = GetContainingMethod(node);
            return m != null && HotMethods.Contains(m.Identifier.ValueText);
        }

        public static bool IsInTeardownMethod(SyntaxNode node)
        {
            var m = GetContainingMethod(node);
            return m != null && TeardownMethods.Contains(m.Identifier.ValueText);
        }

        /// <summary>
        /// Best-effort check that a node sits inside a `#if UNITY_EDITOR` region.
        /// Walks the directive trivia in document order with a stack (handles nesting):
        /// a node is guarded when any still-open #if at its position mentions UNITY_EDITOR.
        /// </summary>
        public static bool HasUnityEditorGuard(SyntaxNode node)
        {
            var root = node.SyntaxTree.GetRoot();
            int nodePos = node.SpanStart;
            var open = new Stack<bool>();

            foreach (var trivia in root.DescendantTrivia())
            {
                if (trivia.SpanStart > nodePos) break;
                if (trivia.IsKind(SyntaxKind.IfDirectiveTrivia))
                {
                    open.Push(trivia.ToString().IndexOf("UNITY_EDITOR", StringComparison.Ordinal) >= 0);
                }
                else if (trivia.IsKind(SyntaxKind.EndIfDirectiveTrivia))
                {
                    if (open.Count > 0) open.Pop();
                }
            }

            return open.Any(o => o);
        }

        /// <summary>
        /// Returns true when the invocation's receiver expression mentions one of
        /// the given type names (name-based heuristic for builds without UnityEngine.dll).
        /// </summary>
        public static bool ReceiverMentions(InvocationExpressionSyntax inv, params string[] typeNames)
        {
            var recv = GetReceiverName(inv);
            if (recv == null) return false;
            return typeNames.Any(t => recv.IndexOf(t, StringComparison.Ordinal) >= 0);
        }

        public static DiagnosticDescriptor Descriptor(
            string id, string title, string message,
            DiagnosticSeverity severity = DiagnosticSeverity.Warning,
            string description = null)
        {
            return new DiagnosticDescriptor(
                id, title, message, "Unity", severity,
                isEnabledByDefault: true, description: description);
        }
    }
}
