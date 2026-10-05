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
    /// RB family (GPU readback / render-target discipline), second wave.
    /// Ports the remaining RB rules from the HTML analyzer as semantic analyzers:
    /// RB004 WaitAllRequests, RB006 ComputeBuffer.GetData, RB007 Texture.Apply(),
    /// RB008 CaptureScreenshot, RB009 GetPixels, RB010 targetTexture, RB011 GetRawTextureData.
    /// (RB001/RB002/RB003/RB005 live in UnityRulesAnalyzer.)
    /// Hot-path (Update/FixedUpdate/LateUpdate) occurrences are warnings, others are info —
    /// mirroring the HTML analyzer's hot() severity switch.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ReadbackRulesAnalyzer : DiagnosticAnalyzer
    {
        public static readonly DiagnosticDescriptor RB004 = UnityHelpers.Descriptor(
            "RB004",
            "WaitAllRequests placement",
            "AsyncGPUReadback.WaitAllRequests blocks the main thread until the GPU delivers all data — the same pipeline stall as a sync readback. Only wait in teardown (OnDisable/OnDestroy/OnApplicationQuit).",
            DiagnosticSeverity.Warning,
            "Waiting for GPU readbacks is only acceptable when leaving play mode or tearing down.");

        public static readonly DiagnosticDescriptor RB006 = UnityHelpers.Descriptor(
            "RB006",
            "ComputeBuffer.GetData sync read",
            "ComputeBuffer/GraphicsBuffer.GetData is a synchronous GPU read: the CPU blocks until the data is ready. Split reads across frames or use AsyncGPUReadback.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor RB007 = UnityHelpers.Descriptor(
            "RB007",
            "Texture.Apply() without arguments",
            "Apply() without arguments performs a synchronous upload and keeps the CPU copy of the texture in memory. Pass makeNoLongerReadable: true when the CPU copy is no longer needed.",
            DiagnosticSeverity.Info);

        public static readonly DiagnosticDescriptor RB008 = UnityHelpers.Descriptor(
            "RB008",
            "ScreenCapture.CaptureScreenshot",
            "CaptureScreenshot synchronizes GPU with CPU and reserves a full screen buffer: a profiler spike. For video/preview capture prefer AsyncGPUReadback.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor RB009 = UnityHelpers.Descriptor(
            "RB009",
            "GetPixels CPU read",
            "GetPixels reads the CPU copy of the texture but requires Read/Write Enabled, is expensive, and usually follows a readback. Avoid in hot paths.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor RB010 = UnityHelpers.Descriptor(
            "RB010",
            "Render to targetTexture",
            "Rendering into targetTexture: do not read this RT in the same frame — the data is not ready yet and the CPU will stall. Read with a 1–2 frame delay.",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor RB011 = UnityHelpers.Descriptor(
            "RB011",
            "GetRawTextureData platform caveat",
            "GetRawTextureData<T>() gives cheap access to raw data without conversion, but layout and format depend on platform and texture format — verify on target devices.",
            DiagnosticSeverity.Info);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(RB004, RB006, RB007, RB008, RB009, RB010, RB011);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var inv = (InvocationExpressionSyntax)context.Node;
            var name = UnityHelpers.GetInvokedName(inv);
            if (string.IsNullOrEmpty(name)) return;

            bool hot = UnityHelpers.IsInHotMethod(inv);

            // RB004: AsyncGPUReadback.WaitAllRequests
            if (name == "WaitAllRequests" &&
                UnityHelpers.ReceiverMentions(inv, "AsyncGPUReadback"))
            {
                if (UnityHelpers.IsInTeardownMethod(inv))
                {
                    // Correct usage — info, same rule id (HTML parity: log in teardown).
                    var ok = new DiagnosticDescriptor(
                        RB004.Id, RB004.Title,
                        "WaitAllRequests() inside teardown — correct: wait for the GPU only on exit, not in a game frame.",
                        RB004.Category, DiagnosticSeverity.Info, RB004.IsEnabledByDefault, RB004.Description);
                    context.ReportDiagnostic(Diagnostic.Create(ok, inv.GetLocation()));
                }
                else
                {
                    context.ReportDiagnostic(Diagnostic.Create(RB004, inv.GetLocation()));
                }
                return;
            }

            // RB006: ComputeBuffer/GraphicsBuffer.GetData (name-based; UnityEngine.dll absent)
            if (name == "GetData" && inv.Expression is MemberAccessExpressionSyntax)
            {
                ReportHotAware(context, inv, RB006, hot);
                return;
            }

            // RB007: Texture.Apply() with no arguments
            if (name == "Apply" && inv.ArgumentList != null && inv.ArgumentList.Arguments.Count == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(RB007, inv.GetLocation()));
                return;
            }

            // RB008: ScreenCapture.CaptureScreenshot
            if (name == "CaptureScreenshot" &&
                UnityHelpers.ReceiverMentions(inv, "ScreenCapture"))
            {
                ReportHotAware(context, inv, RB008, hot);
                return;
            }

            // RB009: GetPixels / GetPixels32
            if ((name == "GetPixels" || name == "GetPixels32") &&
                inv.Expression is MemberAccessExpressionSyntax)
            {
                ReportHotAware(context, inv, RB009, hot);
                return;
            }

            // RB011: GetRawTextureData<T>
            if (name == "GetRawTextureData" && inv.Expression is MemberAccessExpressionSyntax)
            {
                context.ReportDiagnostic(Diagnostic.Create(RB011, inv.GetLocation()));
            }
        }

        private void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
        {
            var assign = (AssignmentExpressionSyntax)context.Node;

            // RB010: <anything>.targetTexture = ...
            if (assign.Left is MemberAccessExpressionSyntax ma &&
                ma.Name.Identifier.ValueText == "targetTexture")
            {
                bool hot = UnityHelpers.IsInHotMethod(assign);
                ReportHotAware(context, assign, RB010, hot);
            }
        }

        private static void ReportHotAware(
            SyntaxNodeAnalysisContext context, SyntaxNode node,
            DiagnosticDescriptor hotDescriptor, bool hot)
        {
            if (hot)
            {
                context.ReportDiagnostic(Diagnostic.Create(hotDescriptor, node.GetLocation()));
            }
            else
            {
                // Same rule id, info severity outside the hot path (HTML parity).
                var info = new DiagnosticDescriptor(
                    hotDescriptor.Id, hotDescriptor.Title, hotDescriptor.MessageFormat,
                    hotDescriptor.Category, DiagnosticSeverity.Info,
                    hotDescriptor.IsEnabledByDefault, hotDescriptor.Description);
                context.ReportDiagnostic(Diagnostic.Create(info, node.GetLocation()));
            }
        }
    }
}
