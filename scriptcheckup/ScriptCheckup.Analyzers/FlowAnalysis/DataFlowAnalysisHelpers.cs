using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace ScriptCheckup.Analyzers.FlowAnalysis
{
    /// <summary>
    /// Per-method operation index built from a single <see cref="ControlFlowGraph"/> walk.
    /// Lets DF003 work with acquires/releases/escapes without re-walking the tree per rule.
    /// </summary>
    internal sealed class MethodFlowInfo
    {
        public IOperation Root { get; private set; } = null!;
        public List<TempAcquisition> Acquisitions { get; } = new();
        public List<IOperation> Releases { get; } = new();
        public List<ActiveStore> ActiveStores { get; } = new();
        public List<IReturnOperation> Returns { get; } = new();

        public sealed class TempAcquisition
        {
            public TempAcquisition(IOperation acquisitionOp, ILocalSymbol? lease, IOperation anchor)
            {
                AcquisitionOp = acquisitionOp;
                Lease = lease;
                Anchor = anchor;
            }

            public IOperation AcquisitionOp { get; }
            public ILocalSymbol? Lease { get; }
            public IOperation Anchor { get; }
        }

        public sealed class ActiveStore
        {
            public ActiveStore(ISimpleAssignmentOperation assign, bool isRestore)
            {
                Assign = assign;
                IsRestore = isRestore;
            }

            public ISimpleAssignmentOperation Assign { get; }
            public bool IsRestore { get; }
        }

        public bool HasRestore => ActiveStores.Any(s => s.IsRestore);

        public static MethodFlowInfo Build(
            ControlFlowGraph cfg,
            MethodDeclarationSyntax method,
            HashSet<string> savedActiveLocals)
        {
            var info = new MethodFlowInfo { Root = cfg.OriginalOperation };

            foreach (var node in cfg.OriginalOperation.DescendantsAndSelf())
            {
                switch (node)
                {
                    // NB: without UnityEngine.dll the call does not bind and surfaces as
                    // an Invalid operation — IsGetTemporaryCall falls back to syntax.
                    case IOperation op when FlowHelpers.IsGetTemporaryCall(op):
                        info.RegisterAcquisition(op);
                        break;

                    case IOperation op when IsReleaseTemporary(op):
                        info.Releases.Add(op);
                        break;

                    case ISimpleAssignmentOperation assign
                        when FlowHelpers.IsRenderTextureActiveTarget(assign.Target):
                        bool restore = FlowHelpers.IsActiveRestoreValue(assign.Value, savedActiveLocals);
                        info.ActiveStores.Add(new ActiveStore(assign, restore));
                        break;

                    case IReturnOperation ret:
                        info.Returns.Add(ret);
                        break;
                }
            }

            return info;
        }

        private void RegisterAcquisition(IOperation inv)
        {
            // Walk up through transparent wrappers (casts, parens, `?:` branches, `??`):
            // e.g. `var rt = hdr ? GetTemporary(64,64) : GetTemporary(32,32);`
            // registers each GetTemporary as its own acquisition (audit item 1.2).
            var anchor = inv.Parent;
            while (anchor is IConversionOperation
                   or IParenthesizedOperation
                   or IConditionalOperation
                   or ICoalesceOperation)
            {
                anchor = anchor.Parent;
            }

            // var rt = RenderTexture.GetTemporary(...);
            if (anchor is IVariableInitializerOperation init &&
                init.Parent is IVariableDeclaratorOperation decl)
            {
                Acquisitions.Add(new TempAcquisition(inv, decl.Symbol, init));
                return;
            }

            // rt = RenderTexture.GetTemporary(...);
            if (anchor is ISimpleAssignmentOperation assign &&
                FlowHelpers.AsLocalOrParameter(assign.Target) is ILocalSymbol local)
            {
                Acquisitions.Add(new TempAcquisition(inv, local, assign));
                return;
            }

            // RenderTexture.active = RenderTexture.GetTemporary(...): no local handle —
            // ownership can never be released. Reported at the acquisition site.
            if (anchor is ISimpleAssignmentOperation assignActive &&
                FlowHelpers.IsRenderTextureActiveTarget(assignActive.Target))
            {
                Acquisitions.Add(new TempAcquisition(inv, null, assignActive));
                return;
            }

            // RenderTexture.GetTemporary(...); — result discarded: guaranteed leak
            // (audit item 1.2).
            if (anchor is IExpressionStatementOperation)
            {
                Acquisitions.Add(new TempAcquisition(inv, null, anchor));
                return;
            }

            // Leased into an argument, field or return value: ownership is unclear — skip.
        }

        private static bool IsReleaseTemporary(IOperation op)
        {
            if (op is IInvocationOperation inv)
            {
                if (inv.TargetMethod.Name == "ReleaseTemporary")
                    return true;
            }
            // Syntax fallback for missing UnityEngine.dll (Invalid operation).
            return op.Syntax is InvocationExpressionSyntax ies &&
                   ies.Expression is MemberAccessExpressionSyntax ma &&
                   ma.Name.Identifier.ValueText == "ReleaseTemporary";
        }

        /// <summary>
        /// True when the lease value escapes the method, so the method is not responsible
        /// for releasing it: returned to the caller, stored into a member, or passed
        /// by out/ref to a callee.
        /// </summary>
        public bool Escapes(ILocalSymbol lease)
        {
            foreach (var node in Root.DescendantsAndSelf())
            {
                switch (node)
                {
                    // return rt; — the value itself leaves the method. Note: it must be
                    // the lease value, not merely mentioned (`return rt == null;`
                    // returns a bool — round 4, item C).
                    case IReturnOperation ret when IsLeaseValue(ret.ReturnedValue, lease):
                        return true;

                    // this.field = rt; (but 'RenderTexture.active = rt' is ordinary use)
                    case ISimpleAssignmentOperation a
                        when a.Target is IMemberReferenceOperation &&
                             !FlowHelpers.IsRenderTextureActiveTarget(a.Target) &&
                             ContainsLocal(a.Value, lease):
                        return true;

                    // M(out rt) / M(ref rt) — the callee may take ownership
                    case IArgumentOperation arg
                        when arg.Parameter?.RefKind != RefKind.None &&
                             ContainsLocal(arg.Value, lease):
                        return true;
                }
            }
            return false;
        }

        private static bool ContainsLocal(IOperation? op, ISymbol lease)
        {
            if (op is null)
                return false;
            return op.DescendantsAndSelf()
                .OfType<ILocalReferenceOperation>()
                .Any(lr => FlowHelpers.SymCmp.Equals(lr.Local, lease));
        }

        /// <summary>
        /// True when the operation <i>is</i> the lease value (through implicit
        /// conversions), not merely an expression mentioning it.
        /// </summary>
        private static bool IsLeaseValue(IOperation? op, ISymbol lease) =>
            FlowHelpers.AsLocalOrParameter(FlowHelpers.Unwrap(op)) is ISymbol s &&
            FlowHelpers.SymCmp.Equals(s, lease);
    }

    /// <summary>
    /// Thin, exception-safe wrappers around <see cref="SemanticModel.AnalyzeDataFlow"/>
    /// (Roslyn's <c>DataFlowAnalysis</c>).
    /// </summary>
    internal static class DataFlowAnalysisHelpers
    {
        public static DataFlowAnalysis? TryAnalyze(SemanticModel model, SyntaxNode node)
        {
            try
            {
                DataFlowAnalysis? df = node switch
                {
                    StatementSyntax s => model.AnalyzeDataFlow(s),
                    ExpressionSyntax e => model.AnalyzeDataFlow(e),
                    _ => null
                };
                return df?.Succeeded == true ? df : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool IsCaptured(DataFlowAnalysis df, ISymbol s) =>
            df.CapturedInside.Contains(s, FlowHelpers.SymCmp) ||
            df.CapturedOutside.Contains(s, FlowHelpers.SymCmp);
    }
}
