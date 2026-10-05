using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace ScriptCheckup.Analyzers.FlowAnalysis
{
    /// <summary>
    /// DF003 / DF004 — control-flow analyses around <c>RenderTexture</c> leases.
    ///
    /// DF003 is the path-sensitive successor of RB005: RB005 only checks that a
    /// ReleaseTemporary call exists *somewhere* in the method, while DF003 verifies that
    /// every CFG path from the GetTemporary lease to a method exit passes through a release.
    ///
    /// DF004 is the path-sensitive companion of RB002: RB002 fires when there is no
    /// restore at all; DF004 fires when a restore exists but an early return / throw
    /// can bypass it (forward may-analysis of the "active is tainted" fact).
    ///
    /// Limitation: implicit exceptional edges (an exception thrown mid-method jumping to
    /// a catch/finally or out of the method) are not modelled by Roslyn's CFG, so a leak
    /// that only happens via an exception is a false negative, never a false positive.
    /// </summary>
    internal static class LeaseFlow
    {
        public static void Analyze(
            SyntaxNodeAnalysisContext context,
            MethodDeclarationSyntax method,
            ControlFlowGraph cfg)
        {
            var saved = CollectSavedActiveLocals(method);
            var info = MethodFlowInfo.Build(cfg, method, saved);

            AnalyzeTemporaryLease(context, method, cfg, info);
            AnalyzeActiveRestore(context, method, cfg, info, saved);
        }

        // ---------------- DF003: GetTemporary lease ----------------

        private static void AnalyzeTemporaryLease(
            SyntaxNodeAnalysisContext context,
            MethodDeclarationSyntax method,
            ControlFlowGraph cfg,
            MethodFlowInfo info)
        {
            foreach (var acq in info.Acquisitions)
            {
                // RenderTexture.active = RenderTexture.GetTemporary(...): no local handle,
                // ownership can never be released.
                if (acq.Lease is null)
                {
                    // No local handle: either assigned straight into RenderTexture.active
                    // or discarded as an expression statement — both can never be released.
                    var reason = acq.Anchor is IExpressionStatementOperation
                        ? "discarded — the return value is never stored"
                        : "assigned directly to RenderTexture.active";
                    context.ReportDiagnostic(Diagnostic.Create(
                        DataFlowAnalyzer.DF003,
                        acq.AcquisitionOp.Syntax.GetLocation(),
                        reason));
                    continue;
                }

                if (info.Escapes(acq.Lease))
                    continue; // ownership transferred (returned to the caller)

                // Roslyn's CFG does not route normal exits through finally blocks, so a
                // release in finally looks like a leak to the path walk. Suppress when the
                // lease is provably finally-protected (see IsLeaseFinallyProtected).
                if (IsLeaseFinallyProtected(method, acq.AcquisitionOp, acq.Lease))
                    continue;

                // The idiomatic async pattern releases the lease inside a callback
                // (e.g. AsyncGPUReadback.Request(..., req => ReleaseTemporary(rt)) — the
                // very pattern RB003 recommends). A captured lease released in a lambda
                // or local function is a deferred release, not a leak (round 4, item A).
                if (IsReleasedInCallback(method, acq.Lease, context.SemanticModel))
                    continue;

                var starts = FindAnchorPositions(cfg, acq.AcquisitionOp);
                if (starts.Count == 0)
                    continue;

                if (ExistsLeakingPath(cfg, starts, acq.Lease))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DataFlowAnalyzer.DF003,
                        acq.AcquisitionOp.Syntax.GetLocation(),
                        $"'{acq.Lease.Name}'"));
                }
            }
        }

        /// <summary>
        /// CFG positions just past the acquiring invocation, per block.
        /// The CFG lowers operations (flow captures, synthesized assignments), so the anchor taken
        /// from <c>OriginalOperation</c> is matched by syntax span, not by reference.
        /// </summary>
        private static List<(BasicBlock Block, int OpIndex)> FindAnchorPositions(
            ControlFlowGraph cfg, IOperation acquisition)
        {
            var result = new List<(BasicBlock, int)>();
            var span = acquisition.Syntax.Span;
            var tree = acquisition.Syntax.SyntaxTree;
            foreach (var block in cfg.Blocks)
            {
                for (int i = 0; i < block.Operations.Length; i++)
                {
                    // NB: without UnityEngine.dll the call surfaces as Invalid, not
                    // IInvocationOperation — match via the syntax-tolerant helper.
                    if (block.Operations[i].DescendantsAndSelf().Any(n =>
                            FlowHelpers.IsGetTemporaryCall(n) &&
                            n.Syntax.Span == span &&
                            n.Syntax.SyntaxTree == tree))
                    {
                        result.Add((block, i + 1));
                        break;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// True when some CFG path from <paramref name="starts"/> reaches a method exit
        /// without passing a <c>ReleaseTemporary(lease)</c>, or when the lease is
        /// overwritten by a fresh <c>GetTemporary</c> before being released.
        /// Existential: one leaking path is enough to report.
        /// </summary>
        private static bool ExistsLeakingPath(
            ControlFlowGraph cfg,
            List<(BasicBlock Block, int OpIndex)> starts,
            ILocalSymbol lease)
        {
            var visited = new HashSet<(BasicBlock, int)>();
            var queue = new Queue<(BasicBlock, int)>(starts);
            while (queue.Count > 0)
            {
                var (block, index) = queue.Dequeue();
                if (!visited.Add((block, index)))
                    continue;

                bool released = false;
                bool leaked = false;
                for (int i = index; i < block.Operations.Length && !released && !leaked; i++)
                {
                    released = ContainsRelease(block.Operations[i], lease);
                    if (!released)
                        leaked = ContainsReacquire(block.Operations[i], lease);
                }
                if (leaked)
                    return true; // overwritten by a fresh lease before release
                if (released)
                    continue; // this path is fine

                foreach (var succ in FlowHelpers.Successors(block))
                {
                    // A freshly leased texture is non-null, so the `rt == null` edge of
                    // `if (rt != null) ReleaseTemporary(rt);` can never be taken — the
                    // null-guarded release covers every feasible path (round 2, item B).
                    // Check before the exit test: the infeasible edge may lead to Exit.
                    if (IsInfeasibleNullCheckEdge(block, succ, lease))
                        continue;
                    if (succ.Kind == BasicBlockKind.Exit)
                        return true; // leaking path found
                    queue.Enqueue((succ, 0));
                }
            }
            return false;
        }

        /// <summary>
        /// True when <paramref name="succ"/> is the infeasible edge of a
        /// <c>lease != null</c> / <c>lease == null</c> branch: a fresh
        /// <c>GetTemporary</c> lease is never null.
        /// Limitation: if the lease is reassigned to null mid-method this gives a
        /// false negative instead of exploring the (now feasible) null edge.
        /// </summary>
        private static bool IsInfeasibleNullCheckEdge(
            BasicBlock block, BasicBlock succ, ILocalSymbol lease)
        {
            // Round 4, items C/D: a block with no condition (e.g. `return rt == null;`)
            // must not prune anything, and when both edges land in the same block the
            // single successor must not be classified as infeasible.
            if (block.ConditionKind == ControlFlowConditionKind.None)
                return false;
            var condDest = block.ConditionalSuccessor?.Destination;
            var fallDest = block.FallThroughSuccessor?.Destination;
            if (condDest is not null && ReferenceEquals(condDest, fallDest))
                return false;
            // The branch value is often wrapped in an implicit conversion.
            var branch = block.BranchValue is not null
                ? FlowHelpers.Unwrap(block.BranchValue)
                : null;
            // Reuses NullFlow's null-test recognition: `==`/`!=`, `is null`,
            // `is not null`, and negations (round 4, item B).
            if (!FlowHelpers.IsNullTest(branch, out var s, out bool trueMeansNull))
                return false;
            if (s is null || !FlowHelpers.SymCmp.Equals(s, lease))
                return false;
            // Which successor is taken when the null-check is TRUE depends on
            // ConditionKind: for `||`/`&&` Roslyn emits WhenTrue branches where the
            // conditional successor is the *true* edge (round 3, item A). The
            // infeasible edge is the one where `rt == null` holds — a fresh lease
            // is never null.
            bool trueIsConditional =
                block.ConditionKind == ControlFlowConditionKind.WhenTrue;
            var trueEdge = trueIsConditional
                ? block.ConditionalSuccessor?.Destination
                : block.FallThroughSuccessor?.Destination;
            var falseEdge = ReferenceEquals(trueEdge, block.ConditionalSuccessor?.Destination)
                ? block.FallThroughSuccessor?.Destination
                : block.ConditionalSuccessor?.Destination;
            // trueMeansNull: the true edge means "value is null" (e.g. `==`, `is null`).
            // The infeasible edge is the one where the lease would be null.
            var infeasible = trueMeansNull ? trueEdge : falseEdge;
            return ReferenceEquals(succ, infeasible);
        }

        /// <summary>
        /// True when the lease is captured by a lambda or local function that calls
        /// <c>ReleaseTemporary(lease)</c> inside — a deferred release, not a leak
        /// (round 4, item A: the AsyncGPUReadback callback pattern RB003 recommends).
        /// </summary>
        private static bool IsReleasedInCallback(
            MethodDeclarationSyntax method, ILocalSymbol lease, SemanticModel model)
        {
            var funcs = method.DescendantNodes().Where(n =>
                n is AnonymousFunctionExpressionSyntax || n is LocalFunctionStatementSyntax);
            foreach (var func in funcs)
            {
                var df = model.AnalyzeDataFlow(func);
                if (!df.Captured.Any(s => FlowHelpers.SymCmp.Equals(s, lease)))
                    continue;
                foreach (var inv in func.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var op = model.GetOperation(inv);
                    if (op is not null && FlowHelpers.IsReleaseTemporaryOf(op, lease))
                        return true;
                }
            }
            return false;
        }

        private static bool ContainsRelease(IOperation root, ILocalSymbol lease)
        {
            foreach (var node in root.DescendantsAndSelf())
            {
                if (FlowHelpers.IsInsideNestedFunction(node, root))
                    continue; // a release inside a lambda may never run
                if (IsReleaseTemporaryOf(node, lease))
                    return true;
            }
            return false;
        }

        private static bool ContainsReacquire(IOperation root, ILocalSymbol lease)
        {
            // A fresh GetTemporary stored into the same local before the previous lease
            // was released: the previous texture is lost. (The scan starts after the
            // original acquisition, so any match here is a genuine re-acquire.)
            foreach (var node in root.DescendantsAndSelf())
            {
                if (FlowHelpers.IsInsideNestedFunction(node, root))
                    continue;
                if (FlowHelpers.IsGetTemporaryCall(node))
                {
                    // Syntax-level target check: the lowered operation tree may not preserve
                    // the original parent chain (synthesized assignments), but syntax does.
                    var target = LeaseTargetName(node.Syntax);
                    if (target is not null && target == lease.Name)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Name of the local a <c>GetTemporary</c> invocation result is stored into,
        /// or null when the value goes elsewhere (argument, return, ...).
        /// </summary>
        private static string? LeaseTargetName(SyntaxNode invocationSyntax)
        {
            foreach (var anc in invocationSyntax.Ancestors())
            {
                switch (anc)
                {
                    case VariableDeclaratorSyntax vd:
                        return vd.Identifier.ValueText;
                    case AssignmentExpressionSyntax aes when aes.Left is IdentifierNameSyntax id:
                        return id.Identifier.ValueText;
                    case StatementSyntax:
                        return null; // left the target position without finding one
                }
            }
            return null;
        }

        private static bool IsReleaseTemporaryOf(IOperation op, ILocalSymbol lease) =>
            FlowHelpers.IsReleaseTemporaryOf(op, lease);

        // ---------------- finally-suppression (DF003 / DF004) ----------------
        //
        // Roslyn's ControlFlowGraph does not route normal exits through finally blocks:
        // a try body steps straight to the exit block, leaving the finally region
        // orphaned (no CFG predecessors). A release/restore placed in finally is therefore
        // invisible to the path analyses and would be reported as a leak / unrestored
        // state. The helpers below suppress the diagnostic for the two idiomatic,
        // provably-safe patterns:
        //   1. the acquisition (or tainting store) is inside the try-block whose finally
        //      performs the release/restore;
        //   2. the acquisition (or tainting store) linearly precedes such a try in the
        //      same statement block with no return/throw/goto/break/continue between,
        //      so every path from it must enter the try.
        // For DF003 we additionally require that the lease local is never reassigned
        // after the acquisition; otherwise the finally might release a *different*
        // lease than the one we are checking.

        /// <summary>
        /// True when <paramref name="lease"/> is acquired inside (or linearly before)
        /// a try whose finally releases that lease, and the lease is never reassigned
        /// afterwards. Then every path from the acquisition runs the finally, so no
        /// CFG path can leak it even though the CFG itself skips the finally region.
        /// </summary>
        private static bool IsLeaseFinallyProtected(
            MethodDeclarationSyntax method,
            IOperation acquisition,
            ILocalSymbol lease)
        {
            var acqSyntax = acquisition.Syntax;
            var acqSpan = acqSyntax.Span;
            foreach (var tryStmt in method.DescendantNodesAndSelf().OfType<TryStatementSyntax>())
            {
                if (tryStmt.Finally is null)
                    continue;
                if (!FlowHelpers.IsCoveredByTry(tryStmt, acqSyntax))
                    continue;
                if (!FinallyReleasesLease(tryStmt.Finally, lease.Name))
                    continue;
                if (IsReassignedAfter(method, lease.Name, acqSpan))
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// True when stepping from <paramref name="block"/> to the method exit must run
        /// a finally that restores <c>RenderTexture.active</c>: the block's code sits
        /// inside (or linearly before) the try-block of such a try.
        /// </summary>
        private static bool IsActiveFinallyRestored(BasicBlock block, HashSet<string> saved)
        {
            if (block.Operations.Length == 0)
                return false;
            var repSyntax = block.Operations[0].Syntax;
            foreach (var tryStmt in repSyntax.AncestorsAndSelf().OfType<TryStatementSyntax>())
            {
                // The block must be in the try-block itself, not in a catch/finally/filter.
                if (!tryStmt.Block.Span.Contains(repSyntax.Span))
                    continue;
                if (tryStmt.Finally is null)
                    continue;
                if (FinallyRestoresActive(tryStmt.Finally, saved))
                    return true;
            }
            // Linearly-before case: the block's statement precedes the try in the same
            // block with no exits between, so control must enter the try.
            var stmt = repSyntax.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
            var parentBlock = stmt?.Parent as BlockSyntax;
            if (parentBlock is null)
                return false;
            foreach (var tryStmt in parentBlock.Statements.OfType<TryStatementSyntax>())
            {
                if (tryStmt.Finally is null)
                    continue;
                if (!FlowHelpers.IsLinearlyBeforeTry(tryStmt, stmt, parentBlock))
                    continue;
                if (FinallyRestoresActive(tryStmt.Finally, saved))
                    return true;
            }
            return false;
        }

        private static bool FinallyReleasesLease(FinallyClauseSyntax finallyClause, string leaseName)
        {
            return finallyClause.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Any(inv =>
                    inv.Expression is MemberAccessExpressionSyntax ma &&
                    ma.Name.Identifier.ValueText == "ReleaseTemporary" &&
                    FlowHelpers.IsRenderTextureName(ma.Expression) &&
                    inv.ArgumentList.Arguments.FirstOrDefault()?.Expression
                        is IdentifierNameSyntax rid &&
                    rid.Identifier.ValueText == leaseName);
        }

        private static bool FinallyRestoresActive(FinallyClauseSyntax finallyClause, HashSet<string> saved)
        {
            return finallyClause.DescendantNodesAndSelf()
                .OfType<AssignmentExpressionSyntax>()
                .Any(aes =>
                    aes.Left is MemberAccessExpressionSyntax ma &&
                    ma.Name.Identifier.ValueText == "active" &&
                    FlowHelpers.IsRenderTextureName(ma.Expression) &&
                    IsRestoreValueSyntax(aes.Right, saved));
        }

        /// <summary>
        /// Syntax-level counterpart of <c>FlowHelpers.IsActiveRestoreValue</c>: null
        /// literal or a reference to a previously saved active local.
        /// </summary>
        private static bool IsRestoreValueSyntax(ExpressionSyntax expr, HashSet<string> saved)
        {
            if (expr.IsKind(SyntaxKind.NullLiteralExpression))
                return true;
            return expr is IdentifierNameSyntax id && saved.Contains(id.Identifier.ValueText);
        }

        private static bool IsReassignedAfter(
            MethodDeclarationSyntax method, string leaseName, Microsoft.CodeAnalysis.Text.TextSpan acqSpan)
        {
            bool assigned = method.DescendantNodesAndSelf()
                .OfType<AssignmentExpressionSyntax>()
                .Any(aes =>
                    aes.SpanStart > acqSpan.End &&
                    aes.Left is IdentifierNameSyntax id &&
                    id.Identifier.ValueText == leaseName);
            if (assigned)
                return true;
            // out/ref arguments also overwrite the local.
            return method.DescendantNodesAndSelf()
                .OfType<ArgumentSyntax>()
                .Any(arg =>
                    arg.SpanStart > acqSpan.End &&
                    !arg.RefKindKeyword.IsKind(SyntaxKind.None) &&
                    arg.Expression is IdentifierNameSyntax id2 &&
                    id2.Identifier.ValueText == leaseName);
        }

        // ---------------- DF004: RenderTexture.active restore on all paths ----------------

        private static void AnalyzeActiveRestore(
            SyntaxNodeAnalysisContext context,
            MethodDeclarationSyntax method,
            ControlFlowGraph cfg,
            MethodFlowInfo info,
            HashSet<string> saved)
        {
            if (!info.HasRestore)
                return; // RB002 owns the no-restore-at-all case

            // Forward may-analysis of one boolean fact: "active currently holds a value we
            // set and have not restored yet". Gen: active = <non-restored value>.
            // Kill: active = null / saved-local.
            var preds = FlowHelpers.Predecessors(cfg);
            var inTaint = new Dictionary<BasicBlock, bool>();
            var outTaint = new Dictionary<BasicBlock, bool>();
            var captures = FlowHelpers.CollectFlowCaptures(cfg);

            for (int iter = 0; iter < cfg.Blocks.Length + 1; iter++)
            {
                bool changed = false;
                foreach (var block in cfg.Blocks)
                {
                    bool inn = false;
                    foreach (var p in preds[block])
                    {
                        if (outTaint.TryGetValue(p, out var o) && o)
                        {
                            inn = true;
                            break;
                        }
                    }

                    if (!inTaint.TryGetValue(block, out var oldIn) || oldIn != inn)
                    {
                        inTaint[block] = inn;
                        changed = true;
                    }

                    bool cur = inn;
                    foreach (var op in block.Operations)
                        cur = TransferActive(cur, op, saved, captures);

                    if (!outTaint.TryGetValue(block, out var oldOut) || oldOut != cur)
                    {
                        outTaint[block] = cur;
                        changed = true;
                    }
                }
                if (!changed)
                    break;
            }

            // Any block that can step into the exit block while tainted is a leaking exit.
            foreach (var block in cfg.Blocks)
            {
                if (!FlowHelpers.Successors(block).Any(s => s.Kind == BasicBlockKind.Exit))
                    continue;
                if (!outTaint.TryGetValue(block, out var tainted) || !tainted)
                    continue;

                // The CFG skips finally regions; a finally that restores active kills
                // the taint on the real execution path.
                if (IsActiveFinallyRestored(block, saved))
                    continue;

                var exitOp = block.Operations
                    .SelectMany(o => o.DescendantsAndSelf())
                    .LastOrDefault(o => o is IReturnOperation || o is IThrowOperation);

                context.ReportDiagnostic(Diagnostic.Create(
                    DataFlowAnalyzer.DF004,
                    exitOp?.Syntax.GetLocation() ?? method.Identifier.GetLocation()));
            }
        }

        private static bool TransferActive(
            bool tainted,
            IOperation op,
            HashSet<string> saved,
            Dictionary<CaptureId, List<IOperation>> captures)
        {
            foreach (var node in op.DescendantsAndSelf())
            {
                if (node is ISimpleAssignmentOperation a &&
                    FlowHelpers.IsRenderTextureActiveTarget(a.Target))
                {
                    if (FlowHelpers.IsInsideNestedFunction(node, op))
                        continue; // assignment inside a lambda: not straight-line flow
                    tainted = !IsRestoreValue(a.Value, saved, captures);
                }
            }
            return tainted;
        }

        /// <summary>
        /// True when the assigned value restores <c>RenderTexture.active</c>: a null literal,
        /// a saved previous value, or — through the CFG's flow-capture lowering of
        /// <c>?:</c>/<c>??</c> — a capture whose every alternative restores.
        /// </summary>
        private static bool IsRestoreValue(
            IOperation? value,
            HashSet<string> saved,
            Dictionary<CaptureId, List<IOperation>> captures,
            int depth = 0)
        {
            if (depth > 8)
                return false;
            var u = FlowHelpers.Unwrap(value);
            if (u is IFlowCaptureReferenceOperation cref)
            {
                if (!captures.TryGetValue(cref.Id, out var alts) || alts.Count == 0)
                    return false;
                return alts.All(a => IsRestoreValue(a, saved, captures, depth + 1));
            }
            return FlowHelpers.IsActiveRestoreValue(u, saved);
        }

        /// <summary>Names of locals ever assigned from <c>RenderTexture.active</c> — the "saved previous value" set.</summary>
        private static HashSet<string> CollectSavedActiveLocals(MethodDeclarationSyntax method)
        {
            var saved = new HashSet<string>(System.StringComparer.Ordinal);

            foreach (var decl in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (decl.Initializer is not null && IsActiveSyntax(decl.Initializer.Value))
                    saved.Add(decl.Identifier.ValueText);
            }
            foreach (var assign in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (IsActiveSyntax(assign.Right) && assign.Left is IdentifierNameSyntax id)
                    saved.Add(id.Identifier.ValueText);
            }
            return saved;
        }

        private static bool IsActiveSyntax(ExpressionSyntax expr)
        {
            while (expr is ParenthesizedExpressionSyntax p)
                expr = p.Expression;
            return expr is MemberAccessExpressionSyntax ma &&
                   ma.Name.Identifier.ValueText == "active" &&
                   FlowHelpers.IsRenderTextureName(ma.Expression);
        }
    }
}
