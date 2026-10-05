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
    /// DF005 — forward <i>must</i>-analysis over the CFG tracking definitely-null locals/parameters.
    ///
    /// Lattice: sets of symbols, ordered by inclusion, meet = intersection.
    /// The iteration starts from TOP (every referenced symbol) and decreases, which yields the
    /// greatest fixpoint — the standard sound solution for must-analyses ("definitely null on
    /// every path reaching this point").
    ///
    /// Transfer: <c>x = null</c> adds x; any other assignment / out-ref argument removes x.
    /// Branch refinement: on the edge where <c>x == null</c> (or <c>x is null</c>) holds, x is
    /// added; on the edge where it does not hold, x is removed (and vice versa for <c>!=</c> /
    /// <c>is not null</c>).
    ///
    /// Two Roslyn CFG facts this relies on (both verified empirically against 4.8):
    /// <list type="bullet">
    /// <item>Which CFG edge a branch value selects is given by <see cref="BasicBlock.ConditionKind"/>:
    /// <c>WhenTrue</c> means the <i>conditional</i> successor is the true edge (e.g. the null-test
    /// Roslyn generates for <c>??</c>); <c>WhenFalse</c> means the conditional successor is the
    /// false edge and the <i>fall-through</i> is the true edge (e.g. <c>if</c>/<c>while</c>).
    /// Assuming "conditional == true edge" unconditionally inverts null-guards.</item>
    /// <item>The CFG lowers <c>?:</c> / <c>??</c> / <c>?.</c> into flow captures spread over several
    /// blocks, so an assignment's value can be an <see cref="IFlowCaptureReferenceOperation"/>
    /// instead of the source-level expression. Transfers resolve captures to their possible
    /// values; a value is "definitely null" only when <i>every</i> alternative is.</item>
    /// </list>
    /// </summary>
    internal static class NullFlow
    {
        public static void Analyze(SyntaxNodeAnalysisContext context, ControlFlowGraph cfg)
        {
            var cmp = FlowHelpers.SymCmp;
            var universe = CollectSymbols(cfg);
            var preds = FlowHelpers.Predecessors(cfg);
            var captures = FlowHelpers.CollectFlowCaptures(cfg);

            var inSet = new Dictionary<BasicBlock, HashSet<ISymbol>>();
            var outSet = new Dictionary<BasicBlock, HashSet<ISymbol>>();
            foreach (var b in cfg.Blocks)
            {
                inSet[b] = new HashSet<ISymbol>(universe, cmp);
                outSet[b] = new HashSet<ISymbol>(universe, cmp);
            }

            int maxIter = cfg.Blocks.Length * 2 + 10;
            for (int iter = 0; iter < maxIter; iter++)
            {
                bool changed = false;
                foreach (var block in cfg.Blocks)
                {
                    var inn = RefinedIn(outSet, preds, block, cmp);
                    if (!inn.SetEquals(inSet[block]))
                    {
                        inSet[block] = inn;
                        changed = true;
                    }

                    var cur = new HashSet<ISymbol>(inn, cmp);
                    foreach (var op in block.Operations)
                        Transfer(cur, op, captures);
                    Transfer(cur, block.BranchValue, captures);

                    if (!cur.SetEquals(outSet[block]))
                    {
                        outSet[block] = cur;
                        changed = true;
                    }
                }
                if (!changed)
                    break;
            }

            // Second pass: report unconditional dereferences of definitely-null symbols.
            foreach (var block in cfg.Blocks)
            {
                var cur = new HashSet<ISymbol>(inSet[block], cmp);
                foreach (var op in block.Operations)
                    CheckAndTransfer(context, cur, op, captures);
                CheckAndTransfer(context, cur, block.BranchValue, captures);
            }
        }

        private static HashSet<ISymbol> CollectSymbols(ControlFlowGraph cfg)
        {
            var set = new HashSet<ISymbol>(FlowHelpers.SymCmp);
            foreach (var node in cfg.OriginalOperation.DescendantsAndSelf())
            {
                switch (node)
                {
                    case ILocalReferenceOperation lr:
                        set.Add(lr.Local);
                        break;
                    case IParameterReferenceOperation pr:
                        set.Add(pr.Parameter);
                        break;
                    case IVariableDeclaratorOperation d:
                        set.Add(d.Symbol);
                        break;
                }
            }
            return set;
        }

        private static HashSet<ISymbol> RefinedIn(
            Dictionary<BasicBlock, HashSet<ISymbol>> outSet,
            Dictionary<BasicBlock, List<BasicBlock>> preds,
            BasicBlock block,
            IEqualityComparer<ISymbol> cmp)
        {
            HashSet<ISymbol>? acc = null;
            foreach (var p in preds[block])
            {
                // Each CFG edge refines independently; the meet (intersection) keeps only
                // facts true on every incoming edge — this is what makes it a *must* analysis.
                if (p.ConditionalSuccessor?.Destination == block)
                    acc = Intersect(acc,
                        Refine(outSet[p], p, tookTrueEdge: p.ConditionKind == ControlFlowConditionKind.WhenTrue, cmp),
                        cmp);
                if (p.FallThroughSuccessor?.Destination == block)
                    acc = Intersect(acc,
                        Refine(outSet[p], p, tookTrueEdge: p.ConditionKind != ControlFlowConditionKind.WhenTrue, cmp),
                        cmp);
            }
            return acc ?? new HashSet<ISymbol>(cmp); // entry block: nothing is known
        }

        private static HashSet<ISymbol>? Intersect(HashSet<ISymbol>? acc, HashSet<ISymbol> next, IEqualityComparer<ISymbol> cmp)
        {
            if (acc is null)
                return new HashSet<ISymbol>(next, cmp);
            acc.IntersectWith(next);
            return acc;
        }

        private static HashSet<ISymbol> Refine(HashSet<ISymbol> fromPred, BasicBlock pred, bool tookTrueEdge, IEqualityComparer<ISymbol> cmp)
        {
            var r = new HashSet<ISymbol>(fromPred, cmp);
            if (FlowHelpers.IsNullTest(pred.BranchValue, out var symbol, out bool trueMeansNull) && symbol is not null)
            {
                bool nullOnThisEdge = tookTrueEdge ? trueMeansNull : !trueMeansNull;
                if (nullOnThisEdge)
                    r.Add(symbol);
                else
                    r.Remove(symbol);
            }
            return r;
        }

        private static void Transfer(HashSet<ISymbol> cur, IOperation? op, Dictionary<CaptureId, List<IOperation>> captures)
        {
            if (op is null)
                return;
            foreach (var node in op.DescendantsAndSelf())
                ApplyTransfer(cur, node, captures);
        }

        /// <summary>
        /// True when <paramref name="value"/> is definitely null at this program point:
        /// a null literal (through conversions), a flow-capture reference whose every
        /// alternative is definitely null, or a reference to a local/parameter that is
        /// itself definitely null here. Anything else (method calls, unknown values)
        /// conservatively counts as "not null".
        /// </summary>
        private static bool IsNullValue(
            IOperation? value,
            HashSet<ISymbol> cur,
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
                return alts.All(a => IsNullValue(a, cur, captures, depth + 1));
            }
            if (FlowHelpers.IsNullConstant(u))
                return true;
            return FlowHelpers.AsLocalOrParameter(u) is ISymbol s && cur.Contains(s);
        }

        /// <summary>
        /// Resolves an assignment target to a local/parameter, following flow-capture references.
        /// Roslyn lowers <c>w = (a ?? b)</c> / <c>w = (c ? a : b)</c> by capturing the <i>target</i>
        /// (<c>FlowCapture(w)</c>) and then emitting <c>CapRef(target) = CapRef(value)</c>, so the
        /// target operation is a capture reference rather than a local reference. The captured
        /// value is the L-value itself, so a reference to the same local on every alternative
        /// identifies the true target. Returns null when the target is not (or not
        /// unambiguously) a local/parameter.
        /// </summary>
        private static ISymbol? ResolveTarget(
            IOperation? target,
            Dictionary<CaptureId, List<IOperation>> captures,
            int depth = 0)
        {
            if (depth > 8)
                return null;
            var u = FlowHelpers.Unwrap(target);
            if (u is IFlowCaptureReferenceOperation cref)
            {
                if (!captures.TryGetValue(cref.Id, out var alts) || alts.Count == 0)
                    return null;
                ISymbol? agreed = null;
                foreach (var a in alts)
                {
                    var s = ResolveTarget(a, captures, depth + 1);
                    if (s is null)
                        return null;
                    if (agreed is null)
                        agreed = s;
                    else if (!FlowHelpers.SymCmp.Equals(agreed, s))
                        return null; // alternatives disagree: not a single target
                }
                return agreed;
            }
            return FlowHelpers.AsLocalOrParameter(u);
        }

        private static void ApplyTransfer(HashSet<ISymbol> cur, IOperation node, Dictionary<CaptureId, List<IOperation>> captures)
        {
            // Unbound invocation (missing assembly): `out`/`ref` arguments are not
            // IArgumentOperations, so the callee's write is invisible. Clear
            // definitely-null for locals passed with out/ref by syntax
            // (audit item N3).
            if (node is IInvalidOperation &&
                node.Syntax is InvocationExpressionSyntax ies)
            {
                foreach (var arg in ies.ArgumentList.Arguments)
                {
                    if (!arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) &&
                        !arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))
                        continue;
                    var s = FindLocalBySyntax(node, arg.Expression);
                    if (s is not null)
                        cur.Remove(s);
                }
            }

            switch (node)
            {
                case ISimpleAssignmentOperation a:
                    {
                        var s = ResolveTarget(a.Target, captures);
                        if (s is null)
                            break;
                        if (IsNullValue(a.Value, cur, captures))
                            cur.Add(s);
                        else
                            cur.Remove(s);
                        break;
                    }
                case ICompoundAssignmentOperation c:
                    {
                        // x += y / x++ change the value; a previous "definitely null" no longer holds.
                        var s = ResolveTarget(c.Target, captures);
                        if (s is not null)
                            cur.Remove(s);
                        break;
                    }
                case IVariableDeclaratorOperation d when d.Initializer is not null:
                    {
                        if (IsNullValue(d.Initializer.Value, cur, captures))
                            cur.Add(d.Symbol);
                        else
                            cur.Remove(d.Symbol);
                        break;
                    }
                case IArgumentOperation arg when arg.Parameter?.RefKind != RefKind.None:
                    {
                        // The callee may reassign an out/ref parameter or local.
                        var s = FlowHelpers.AsLocalOrParameter(arg.Value);
                        if (s is not null)
                            cur.Remove(s);
                        break;
                    }
            }
        }

        private static void CheckAndTransfer(
            SyntaxNodeAnalysisContext context,
            HashSet<ISymbol> cur,
            IOperation? op,
            Dictionary<CaptureId, List<IOperation>> captures)
        {
            if (op is null)
                return;
            foreach (var node in op.DescendantsAndSelf())
            {
                // Check the dereference *before* applying this node's own transfer, in document order.
                if (IsUnconditionalDeref(node, out var symbol) &&
                    symbol is not null &&
                    cur.Contains(symbol) &&
                    !FlowHelpers.IsInsideNestedFunction(node, op) &&
                    !IsInsideConditionalAccess(node, op))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DataFlowAnalyzer.DF005, node.Syntax.GetLocation(), symbol.Name));
                }
                ApplyTransfer(cur, node, captures);
            }
        }

        /// <summary>
        /// Finds the local/parameter symbol for a syntax node inside an (invalid)
        /// operation tree by matching the exact syntax node.
        /// </summary>
        private static ISymbol? FindLocalBySyntax(IOperation root, SyntaxNode syntax)
        {
            foreach (var desc in root.DescendantsAndSelf())
            {
                if (desc.Syntax == syntax &&
                    FlowHelpers.AsLocalOrParameter(desc) is ISymbol s)
                    return s;
            }
            return null;
        }

        private static bool IsUnconditionalDeref(IOperation node, out ISymbol? symbol)
        {
            symbol = null;
            IOperation? instance = node switch
            {
                IInvocationOperation i => i.Instance,
                IPropertyReferenceOperation p => p.Instance,
                IFieldReferenceOperation f => f.Instance,
                IEventReferenceOperation e => e.Instance,
                _ => null
            };
            if (instance is not null)
            {
                // A suppressed 'x!' is an explicit user assertion — respect it.
                symbol = FlowHelpers.AsLocalOrParameter(instance);
                return symbol is not null;
            }
            // Unbound member access (missing assembly): `local.Member` surfaces as
            // an Invalid operation. Resolve the *receiver* precisely: walk down the
            // MemberAccess chain to the root identifier (audit item 1.5 — taking the
            // first local among descendants wrongly blames call arguments, e.g. the
            // `o` in `Unknown.Get(o).Name`).
            if (node is IInvalidOperation &&
                node.Syntax is MemberAccessExpressionSyntax outerMa &&
                node.Syntax.Parent is not MemberAccessExpressionSyntax) // outermost only
            {
                var receiver = outerMa.Expression;
                while (receiver is MemberAccessExpressionSyntax nestedMa)
                    receiver = nestedMa.Expression;
                if (receiver is IdentifierNameSyntax &&
                    FindLocalBySyntax(node, receiver) is ISymbol s)
                {
                    symbol = s;
                    return true;
                }
            }
            return false;
        }

        private static bool IsInsideConditionalAccess(IOperation node, IOperation root)
        {
            for (var p = node.Parent; p is not null && !ReferenceEquals(p, root); p = p.Parent)
            {
                if (p is IConditionalAccessOperation)
                    return true;
            }
            return false;
        }
    }
}
