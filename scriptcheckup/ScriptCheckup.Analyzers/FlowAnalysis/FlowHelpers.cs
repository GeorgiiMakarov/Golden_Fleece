using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace ScriptCheckup.Analyzers.FlowAnalysis
{
    /// <summary>
    /// Shared helpers for control-flow / operation based analyses.
    /// All Unity API matching is name-based: without a UnityEngine.dll reference the
    /// semantic model carries error symbols, but their names survive.
    /// </summary>
    internal static class FlowHelpers
    {
        public static readonly SymbolEqualityComparer SymCmp = SymbolEqualityComparer.Default;

        public static IOperation Unwrap(IOperation op)
        {
            while (op is IConversionOperation c)
                op = c.Operand;
            return op;
        }

        public static bool IsNullConstant(IOperation? op)
        {
            if (op is null)
                return false;
            var u = Unwrap(op);
            return u.ConstantValue is { HasValue: true, Value: null };
        }

        /// <summary>
        /// Maps flow-capture ids to their possible values across the whole CFG.
        /// The CFG lowers <c>?:</c> / <c>??</c> / <c>?.</c> into <see cref="IFlowCaptureOperation"/>
        /// nodes spread over several blocks; an expression that reads the lowered value sees an
        /// <see cref="IFlowCaptureReferenceOperation"/> instead of the source-level expression.
        /// </summary>
        public static Dictionary<CaptureId, List<IOperation>> CollectFlowCaptures(ControlFlowGraph cfg)
        {
            var result = new Dictionary<CaptureId, List<IOperation>>();
            foreach (var b in cfg.Blocks)
            {
                foreach (var node in b.Operations.SelectMany(o => o.DescendantsAndSelf()))
                {
                    if (node is IFlowCaptureOperation cap)
                    {
                        if (!result.TryGetValue(cap.Id, out var list))
                            result[cap.Id] = list = new List<IOperation>();
                        list.Add(cap.Value);
                    }
                }
            }
            return result;
        }

        /// <summary>True for <c>RenderTexture.active</c> (or <c>something.RenderTexture.active</c>) as an assignment target.</summary>
        public static bool IsRenderTextureActiveTarget(IOperation? target)
        {
            if (target is null)
                return false;
            // Semantic check when the member binds.
            if (target is IMemberReferenceOperation m)
            {
                if (m.Member.Name != "active")
                    return false;
                if (m.Member.ContainingType?.Name == "RenderTexture")
                    return true;
            }
            // Fallback for missing UnityEngine.dll: syntax-level check.
            // (Without the reference the target surfaces as an Invalid operation.)
            return target.Syntax is MemberAccessExpressionSyntax ma &&
                   ma.Name.Identifier.ValueText == "active" &&
                   IsRenderTextureName(ma.Expression);
        }

        /// <summary>
        /// True when the expression names the <c>RenderTexture</c> type exactly:
        /// <c>RenderTexture</c>, <c>UnityEngine.RenderTexture</c>,
        /// <c>global::UnityEngine.RenderTexture</c>.
        /// Deliberately NOT a substring check — <c>MyRenderTexturePool</c> or
        /// <c>RenderTextureHelper</c> must not match (audit item 1.1).
        /// </summary>
        public static bool IsRenderTextureName(ExpressionSyntax expr)
        {
            while (expr is ParenthesizedExpressionSyntax p)
                expr = p.Expression;
            return expr switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText == "RenderTexture",
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText == "RenderTexture",
                _ => false
            };
        }

        public static bool IsGetTemporaryCall(IOperation op)
        {
            // Semantic check when the call binds (UnityEngine.dll referenced).
            if (op is IInvocationOperation inv)
            {
                if (inv.TargetMethod.Name != "GetTemporary")
                    return false;
                if (inv.TargetMethod.ContainingType?.Name == "RenderTexture")
                    return true;
            }
            // Syntax fallback: without UnityEngine.dll the call does not bind and
            // surfaces as an Invalid operation — match by shape instead.
            return op.Syntax is InvocationExpressionSyntax ies &&
                   ies.Expression is MemberAccessExpressionSyntax ma &&
                   ma.Name.Identifier.ValueText == "GetTemporary" &&
                   IsRenderTextureName(ma.Expression);
        }

        public static bool IsReleaseTemporaryOf(IOperation op, ISymbol lease)
        {
            if (op is IInvocationOperation inv)
            {
                if (inv.TargetMethod.Name != "ReleaseTemporary")
                    return false;
                if (inv.Arguments.Length == 0)
                    return false;
                var arg = Unwrap(inv.Arguments[0].Value);
                return arg is ILocalReferenceOperation lr && SymCmp.Equals(lr.Local, lease);
            }
            // Syntax fallback: without UnityEngine.dll the call does not bind and
            // surfaces as an Invalid operation. Match by shape, then look for a
            // bound local reference to the lease among the descendants.
            if (op.Syntax is InvocationExpressionSyntax ies &&
                ies.Expression is MemberAccessExpressionSyntax ma &&
                ma.Name.Identifier.ValueText == "ReleaseTemporary")
            {
                foreach (var desc in op.Descendants())
                {
                    if (Unwrap(desc) is ILocalReferenceOperation lr &&
                        SymCmp.Equals(lr.Local, lease))
                        return true;
                }
            }
            return false;
        }

        /// <summary>Local or parameter referenced by an operation (through conversions), or null.</summary>
        public static ISymbol? AsLocalOrParameter(IOperation? op)
        {
            if (op is null)
                return null;
            return Unwrap(op) switch
            {
                ILocalReferenceOperation lr => lr.Local,
                IParameterReferenceOperation pr => pr.Parameter,
                _ => null
            };
        }

        public static IEnumerable<BasicBlock> Successors(BasicBlock block)
        {
            var seen = new HashSet<BasicBlock>();
            if (block.FallThroughSuccessor?.Destination is BasicBlock f && seen.Add(f))
                yield return f;
            if (block.ConditionalSuccessor?.Destination is BasicBlock c && seen.Add(c))
                yield return c;
        }

        public static Dictionary<BasicBlock, List<BasicBlock>> Predecessors(ControlFlowGraph cfg)
        {
            var d = new Dictionary<BasicBlock, List<BasicBlock>>();
            foreach (var b in cfg.Blocks)
                d[b] = new List<BasicBlock>();
            foreach (var b in cfg.Blocks)
                foreach (var s in Successors(b))
                    d[s].Add(b);
            return d;
        }

        /// <summary>True when <paramref name="value"/> restores RenderTexture.active: null or a saved previous value.</summary>
        public static bool IsActiveRestoreValue(IOperation? value, HashSet<string> saved)
        {
            if (IsNullConstant(value))
                return true;
            return AsLocalOrParameter(value) is ILocalSymbol lr && saved.Contains(lr.Name);
        }

        /// <summary>True when <paramref name="node"/> sits inside a lambda/local-function nested in <paramref name="root"/>.</summary>
        public static bool IsInsideNestedFunction(IOperation node, IOperation root)
        {
            for (var p = node.Parent; p is not null && !ReferenceEquals(p, root); p = p.Parent)
            {
                if (p is IAnonymousFunctionOperation || p is ILocalFunctionOperation)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True when <paramref name="node"/> is inside <paramref name="tryStmt"/>'s
        /// try-block, or its statement linearly precedes the try in the same block with
        /// no exits in between (so every path from the node must enter the try).
        /// Used to compensate for Roslyn's CFG not routing exits through finally blocks.
        /// </summary>
        public static bool IsCoveredByTry(TryStatementSyntax tryStmt, SyntaxNode node)
        {
            if (tryStmt.Block.Span.Contains(node.Span))
                return true;
            var stmt = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
            var parentBlock = stmt?.Parent as BlockSyntax;
            return parentBlock is not null && IsLinearlyBeforeTry(tryStmt, stmt, parentBlock);
        }

        public static bool IsLinearlyBeforeTry(
            TryStatementSyntax tryStmt, StatementSyntax? stmt, BlockSyntax parentBlock)
        {
            if (stmt is null)
                return false;
            var stmts = parentBlock.Statements;
            int stmtIdx = -1, tryIdx = -1;
            for (int i = 0; i < stmts.Count; i++)
            {
                if (ReferenceEquals(stmts[i], stmt))
                    stmtIdx = i;
                if (ReferenceEquals(stmts[i], tryStmt))
                    tryIdx = i;
            }
            if (stmtIdx < 0 || tryIdx < 0 || stmtIdx >= tryIdx)
                return false;
            for (int i = stmtIdx + 1; i < tryIdx; i++)
            {
                if (ContainsExitStatement(stmts[i]))
                    return false;
            }
            return true;
        }

        public static bool ContainsExitStatement(StatementSyntax stmt)
        {
            return stmt.DescendantNodesAndSelf().OfType<StatementSyntax>().Any(s =>
                s is ReturnStatementSyntax || s is ThrowStatementSyntax ||
                s is GotoStatementSyntax || s is BreakStatementSyntax ||
                s is ContinueStatementSyntax);
        }

        /// <summary>
        /// Recognizes <c>x == null</c>, <c>x != null</c>, <c>x is null</c>, <c>x is not null</c>
        /// (plus a leading <c>!</c>). Reports which symbol is tested and whether the
        /// <i>true</i> edge means "is null". Shared by NullFlow and LeaseFlow
        /// (round 4, item B).
        /// </summary>
        public static bool IsNullTest(IOperation? branchValue, out ISymbol? symbol, out bool trueMeansNull)
        {
            symbol = null;
            trueMeansNull = false;
            if (branchValue is null)
                return false;

            var v = Unwrap(branchValue);
            bool negated = false;
            if (v is IUnaryOperation un && un.OperatorKind == UnaryOperatorKind.Not)
            {
                negated = true;
                v = Unwrap(un.Operand);
            }

            if (v is IBinaryOperation bin &&
                (bin.OperatorKind == BinaryOperatorKind.Equals || bin.OperatorKind == BinaryOperatorKind.NotEquals))
            {
                var left = Unwrap(bin.LeftOperand);
                var right = Unwrap(bin.RightOperand);
                IOperation? other = IsNullConstant(left) ? right
                                  : IsNullConstant(right) ? left
                                  : null;
                symbol = AsLocalOrParameter(other);
                if (symbol is null)
                    return false;
                trueMeansNull = (bin.OperatorKind == BinaryOperatorKind.Equals) ^ negated;
                return true;
            }

            if (v is IIsPatternOperation isPat)
            {
                symbol = AsLocalOrParameter(isPat.Value);
                if (symbol is null)
                    return false;
                var pat = isPat.Pattern;
                while (pat is INegatedPatternOperation negPat)
                {
                    negated = !negated;
                    pat = negPat.Pattern;
                }
                if (pat is IConstantPatternOperation c && IsNullConstant(c.Value))
                {
                    trueMeansNull = !negated; // 'is null' is true exactly when the value is null
                    return true;
                }
            }

            // Note: the internal IsNullOperation Roslyn generates for '??' tests a flow capture,
            // never a named local, so there is nothing to refine here. Falling through (false)
            // is the sound choice.
            return false;
        }
    }
}
