using System;
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
    /// DF001 / DF002 — store-related rules built on <see cref="SemanticModel.AnalyzeDataFlow"/>
    /// (Roslyn's <c>DataFlowAnalysis</c>), combined with the <see cref="ControlFlowGraph"/>
    /// for the path-sensitive "is this stored value ever observed" check behind DF002.
    ///
    /// Key mapping: statements (the unit of <c>AnalyzeDataFlow</c>) are located on the CFG
    /// by grouping each block's operations by their innermost containing statement, so the
    /// "what happens after this store" walk follows real control-flow edges.
    /// </summary>
    internal static class StoreFlow
    {
        public static void Analyze(
            SyntaxNodeAnalysisContext context,
            SemanticModel model,
            SyntaxNode bodyRoot,
            ControlFlowGraph cfg)
        {
            var bodyFlow = DataFlowAnalysisHelpers.TryAnalyze(model, bodyRoot);
            if (bodyFlow is null)
                return;

            ReportUnusedLocals(context, bodyFlow);

            var stmtInfo = BuildStatementInfo(model, bodyRoot);
            if (stmtInfo.Count == 0)
                return;

            var blockGroups = BuildBlockGroups(cfg, bodyRoot);
            ReportDeadStores(context, cfg, bodyRoot, bodyFlow, stmtInfo, blockGroups);
        }

        // ---------------- DF001: unused local ----------------

        private static void ReportUnusedLocals(SyntaxNodeAnalysisContext context, DataFlowAnalysis bodyFlow)
        {
            var read = new HashSet<ISymbol>(bodyFlow.ReadInside, FlowHelpers.SymCmp);

            foreach (var local in bodyFlow.WrittenInside.OfType<ILocalSymbol>())
            {
                if (read.Contains(local))
                    continue;
                if (local.Name == "_")
                    continue; // intentional discard
                if (local.IsRef)
                    continue; // 'ref' locals alias memory; "unused" is not meaningful
                if (DataFlowAnalysisHelpers.IsCaptured(bodyFlow, local))
                    continue; // may be read through the closure

                var decl = local.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax())
                    .OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault();
                if (decl is null)
                    continue;

                // 'using var x = ...' owns its lifetime through Dispose(); not reading it is fine.
                if (decl.Parent?.Parent is LocalDeclarationStatementSyntax lds &&
                    !lds.UsingKeyword.IsKind(SyntaxKind.None))
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    DataFlowAnalyzer.DF001, decl.Identifier.GetLocation(), local.Name));
            }
        }

        // ---------------- DF002: dead store ----------------

        private sealed class StmtInfo
        {
            public HashSet<ISymbol> Read = new(FlowHelpers.SymCmp);
            public HashSet<ISymbol> Written = new(FlowHelpers.SymCmp);
            public HashSet<ISymbol> OutRefArgs = new(FlowHelpers.SymCmp);
            public bool IsUsingDeclaration;
            public bool HasUnknownFlow = true; // stays true when AnalyzeDataFlow failed
        }

        private static Dictionary<StatementSyntax, StmtInfo> BuildStatementInfo(
            SemanticModel model, SyntaxNode bodyRoot)
        {
            var result = new Dictionary<StatementSyntax, StmtInfo>();
            foreach (var stmt in bodyRoot.DescendantNodesAndSelf().OfType<StatementSyntax>())
            {
                var info = new StmtInfo();
                if (IsBranchingStatement(stmt))
                {
                    // A branching statement's nested statements live on their own CFG blocks and
                    // are walked path-sensitively; collapsing the whole 'if'/'while' into one
                    // summary would treat "written on some branch" as "overwritten on every path"
                    // (false positives). The summary therefore carries only the header reads
                    // (condition, lock object, ...) and no writes.
                    info.HasUnknownFlow = false;
                    info.Read = GetHeaderReads(model, stmt);
                }
                else
                {
                    var df = DataFlowAnalysisHelpers.TryAnalyze(model, stmt);
                    if (df is not null)
                    {
                        info.HasUnknownFlow = false;
                        info.Read = new HashSet<ISymbol>(df.ReadInside, FlowHelpers.SymCmp);
                        info.Written = new HashSet<ISymbol>(df.WrittenInside, FlowHelpers.SymCmp);
                        // Roslyn's WrittenInside treats `p.y = 0` as a write of the struct
                        // local p, but a member/element write is partial — the stored value
                        // of p is not fully overwritten (audit item N2). Drop symbols that
                        // are only partially written in this statement.
                        var fullyWritten = new HashSet<string>(
                            stmt.DescendantNodesAndSelf()
                                .OfType<AssignmentExpressionSyntax>()
                                .Where(aes => aes.Left is IdentifierNameSyntax)
                                .Select(aes => ((IdentifierNameSyntax)aes.Left).Identifier.ValueText)
                            .Concat(
                                stmt.DescendantNodesAndSelf()
                                    .OfType<VariableDeclaratorSyntax>()
                                    .Where(vd => vd.Initializer is not null)
                                    .Select(vd => vd.Identifier.ValueText)));
                        info.Written.RemoveWhere(s => !fullyWritten.Contains(s.Name));
                    }
                }
                info.OutRefArgs = GetOutRefLocals(model, stmt);
                info.IsUsingDeclaration = stmt is LocalDeclarationStatementSyntax lds &&
                                          !lds.UsingKeyword.IsKind(SyntaxKind.None);
                result[stmt] = info;
            }
            return result;
        }

        /// <summary>
        /// Statements whose nested statements execute conditionally: their writes must be observed
        /// through the CFG's own edges, not through a whole-statement summary.
        /// </summary>
        private static bool IsBranchingStatement(StatementSyntax stmt) =>
            stmt is IfStatementSyntax || stmt is WhileStatementSyntax || stmt is DoStatementSyntax ||
            stmt is ForStatementSyntax || stmt is ForEachStatementSyntax ||
            stmt is ForEachVariableStatementSyntax || stmt is SwitchStatementSyntax ||
            stmt is LockStatementSyntax || stmt is UsingStatementSyntax || stmt is TryStatementSyntax;

        /// <summary>Header expressions of a branching statement: conditions, lock objects, ...</summary>
        private static IEnumerable<ExpressionSyntax> HeaderExpressions(StatementSyntax stmt)
        {
            switch (stmt)
            {
                case IfStatementSyntax i:
                    yield return i.Condition;
                    break;
                case WhileStatementSyntax w:
                    yield return w.Condition;
                    break;
                case DoStatementSyntax d:
                    yield return d.Condition;
                    break;
                case ForStatementSyntax f:
                    if (f.Declaration != null)
                        foreach (var v in f.Declaration.Variables)
                            if (v.Initializer != null)
                                yield return v.Initializer.Value;
                    foreach (var init in f.Initializers)
                        yield return init;
                    if (f.Condition != null)
                        yield return f.Condition;
                    foreach (var incr in f.Incrementors)
                        yield return incr;
                    break;
                case ForEachStatementSyntax fe:
                    yield return fe.Expression;
                    break;
                case ForEachVariableStatementSyntax fv:
                    yield return fv.Expression;
                    break;
                case SwitchStatementSyntax sw:
                    yield return sw.Expression;
                    break;
                case LockStatementSyntax l:
                    yield return l.Expression;
                    break;
                case UsingStatementSyntax u:
                    if (u.Declaration != null)
                        foreach (var v in u.Declaration.Variables)
                            if (v.Initializer != null)
                                yield return v.Initializer.Value;
                    if (u.Expression != null)
                        yield return u.Expression;
                    break;
            }
        }

        /// <summary>Locals/parameters read by a branching statement's header (never its branches).</summary>
        private static HashSet<ISymbol> GetHeaderReads(SemanticModel model, StatementSyntax stmt)
        {
            var set = new HashSet<ISymbol>(FlowHelpers.SymCmp);
            foreach (var expr in HeaderExpressions(stmt))
            {
                foreach (var id in expr.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    var sym = model.GetSymbolInfo(id).Symbol;
                    if (sym is ILocalSymbol || sym is IParameterSymbol)
                        set.Add(sym);
                }
            }
            return set;
        }

        private static HashSet<ISymbol> GetOutRefLocals(SemanticModel model, StatementSyntax stmt)
        {
            var set = new HashSet<ISymbol>(FlowHelpers.SymCmp);
            foreach (var arg in stmt.DescendantNodes().OfType<ArgumentSyntax>())
            {
                if (!arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) &&
                    !arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))
                    continue;
                var argSymbol = model.GetSymbolInfo(arg.Expression).Symbol;
                if (argSymbol is ILocalSymbol || argSymbol is IParameterSymbol)
                    set.Add(argSymbol);
            }
            return set;
        }

        /// <summary>
        /// Per block, operations grouped by innermost statement in execution order.
        /// <c>AfterOpIndex</c> is the operation index just past the group's last operation —
        /// the position where "what happens next" starts.
        /// </summary>
        private sealed class StmtGroup
        {
            public StmtGroup(StatementSyntax? statement, int afterOpIndex)
            {
                Statement = statement;
                AfterOpIndex = afterOpIndex;
            }

            public StatementSyntax? Statement { get; }
            public int AfterOpIndex { get; }
        }

        private static Dictionary<BasicBlock, List<StmtGroup>> BuildBlockGroups(
            ControlFlowGraph cfg, SyntaxNode bodyRoot)
        {
            var result = new Dictionary<BasicBlock, List<StmtGroup>>();
            foreach (var block in cfg.Blocks)
            {
                var groups = new List<StmtGroup>();
                var ops = new List<IOperation>(block.Operations);
                if (block.BranchValue is not null)
                    ops.Add(block.BranchValue);

                StatementSyntax? current = null;
                bool hasCurrent = false;
                for (int i = 0; i < ops.Count; i++)
                {
                    var st = InnermostStatement(ops[i], bodyRoot);
                    if (!hasCurrent || st != current)
                    {
                        if (hasCurrent)
                            groups.Add(new StmtGroup(current, i));
                        current = st;
                        hasCurrent = true;
                    }
                }
                if (hasCurrent)
                    groups.Add(new StmtGroup(current, ops.Count));
                result[block] = groups;
            }
            return result;
        }

        private static StatementSyntax? InnermostStatement(IOperation op, SyntaxNode bodyRoot)
        {
            return op.Syntax.AncestorsAndSelf()
                .OfType<StatementSyntax>()
                .FirstOrDefault(s => bodyRoot.Span.Contains(s.Span));
        }

        private static void ReportDeadStores(
            SyntaxNodeAnalysisContext context,
            ControlFlowGraph cfg,
            SyntaxNode bodyRoot,
            DataFlowAnalysis bodyFlow,
            Dictionary<StatementSyntax, StmtInfo> stmtInfo,
            Dictionary<BasicBlock, List<StmtGroup>> blockGroups)
        {
            var methodRead = new HashSet<ISymbol>(bodyFlow.ReadInside, FlowHelpers.SymCmp);

            foreach (var kvp in stmtInfo)
            {
                var stmt = kvp.Key;
                var info = kvp.Value;
                if (info.HasUnknownFlow || info.IsUsingDeclaration)
                    continue;

                foreach (var symbol in info.Written)
                {
                    if (!(symbol is ILocalSymbol) && !(symbol is IParameterSymbol))
                        continue;
                    if (symbol is IParameterSymbol ps && ps.RefKind != RefKind.None)
                        continue; // out/ref store is observed by the caller, not dead
                    if (info.Read.Contains(symbol))
                        continue; // x = x + 1, x++, x += y — the old value is read
                    if (info.OutRefArgs.Contains(symbol))
                        continue; // the callee writes through out/ref; value may be observed there
                    if (symbol.Name == "_")
                        continue;
                    if (DataFlowAnalysisHelpers.IsCaptured(bodyFlow, symbol))
                        continue;
                    if (symbol is ILocalSymbol && !methodRead.Contains(symbol))
                        continue; // never read anywhere: DF001 owns this case

                    if (IsDeadStore(cfg, blockGroups, stmtInfo, bodyRoot, stmt, symbol))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            DataFlowAnalyzer.DF002,
                            FindStoreLocation(stmt, symbol.Name) ?? stmt.GetLocation(),
                            symbol.Name));
                    }
                }
            }
        }

        private static Location? FindStoreLocation(StatementSyntax stmt, string name)
        {
            var decl = stmt.DescendantNodesAndSelf()
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.ValueText == name);
            if (decl is not null)
                return decl.Identifier.GetLocation();

            var assign = stmt.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .FirstOrDefault(a => a.Left is IdentifierNameSyntax id && id.Identifier.ValueText == name);
            return assign?.GetLocation();
        }

        /// <summary>
        /// True when <paramref name="startStmt"/> is covered by a try whose finally
        /// clause may read <paramref name="name"/>. The CFG never routes control through
        /// finally regions, so such a read is invisible to the path walk; treating the
        /// store as live is the sound (no false positive) direction.
        /// </summary>
        private static bool IsReadInCoveringFinally(
            SyntaxNode bodyRoot, StatementSyntax startStmt, string name)
        {
            foreach (var tryStmt in bodyRoot.DescendantNodesAndSelf().OfType<TryStatementSyntax>())
            {
                if (tryStmt.Finally is null)
                    continue;
                if (!FlowHelpers.IsCoveredByTry(tryStmt, startStmt))
                    continue;
                if (!FinallyMayRead(tryStmt.Finally, name))
                    continue;
                // If the variable is reassigned after this store, the finally reads the
                // *new* value, so it does not keep this store live.
                if (IsReassignedAfter(bodyRoot, startStmt, name))
                    continue;
                return true;
            }
            return false;
        }

        private static bool FinallyMayRead(FinallyClauseSyntax finallyClause, string name)
        {
            foreach (var id in finallyClause.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (id.Identifier.ValueText != name)
                    continue;
                if (IsPureWrite(id))
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Roslyn's CFG has no exceptional edges, so a catch block is never reached by
        /// the DF002 walk. If the store reaches a try (inside its block, or textually
        /// before it — a guard return between them only prunes paths, it does not hide
        /// the value on paths that do reach the try) and any associated catch may read
        /// the variable, the store is live. Treating the store as live is the sound
        /// (no false positive) direction.
        /// </summary>
        private static bool IsReadInAssociatedCatch(
            SyntaxNode bodyRoot, StatementSyntax startStmt, string name)
        {
            foreach (var tryStmt in bodyRoot.DescendantNodesAndSelf().OfType<TryStatementSyntax>())
            {
                if (tryStmt.Catches.Count == 0)
                    continue;
                if (!IsReachingTry(tryStmt, startStmt))
                    continue;
                foreach (var catchClause in tryStmt.Catches)
                {
                    if (!CatchMayRead(catchClause, name))
                        continue;
                    // A full reassignment between the store and the try (on the normal
                    // path) means the catch observes the new value, not this store's.
                    // NB: a reassignment *inside* the try does not count — the exception
                    // may be thrown before it executes (round 1, item N1).
                    if (IsReassignedBetween(bodyRoot, startStmt, tryStmt, name))
                        continue;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The store is inside the try block, or textually before it. Unlike
        /// <see cref="FlowHelpers.IsCoveredByTry"/> this deliberately allows guard
        /// returns between the store and the try: for liveness, reachability on some
        /// path is enough (round 2, item C).
        /// </summary>
        private static bool IsReachingTry(TryStatementSyntax tryStmt, StatementSyntax startStmt)
        {
            if (tryStmt.Block.Span.Contains(startStmt.Span))
                return true;
            return startStmt.Span.End <= tryStmt.SpanStart;
        }

        private static bool IsReassignedBetween(
            SyntaxNode bodyRoot, StatementSyntax startStmt, TryStatementSyntax tryStmt, string name)
        {
            foreach (var node in bodyRoot.DescendantNodes())
            {
                if (node.SpanStart < startStmt.Span.End || node.SpanStart >= tryStmt.SpanStart)
                    continue;
                if (node is AssignmentExpressionSyntax aes &&
                    aes.Left is IdentifierNameSyntax id &&
                    id.Identifier.ValueText == name &&
                    !IsConditionalWrite(aes))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True when the assignment sits under an `if` / `?:` / `switch` / loop, i.e.
        /// it does not execute on every path — the old value can still reach the catch
        /// (round 3, item C).
        /// </summary>
        private static bool IsConditionalWrite(SyntaxNode node)
        {
            for (var cur = node.Parent; cur is not null; cur = cur.Parent)
            {
                if (cur is MethodDeclarationSyntax || cur is LocalFunctionStatementSyntax)
                    break;
                if (cur is IfStatementSyntax || cur is ConditionalExpressionSyntax ||
                    cur is SwitchSectionSyntax || cur is WhileStatementSyntax ||
                    cur is ForStatementSyntax || cur is ForEachStatementSyntax ||
                    cur is DoStatementSyntax)
                    return true;
            }
            return false;
        }

        private static bool CatchMayRead(CatchClauseSyntax catchClause, string name)
        {
            foreach (var id in catchClause.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (id.Identifier.ValueText != name)
                    continue;
                if (IsPureWrite(id))
                    continue;
                return true;
            }
            return false;
        }

        private static bool IsPureWrite(IdentifierNameSyntax id)
        {
            // x = ... (simple assignment target; compound assignments read as well)
            if (id.Parent is AssignmentExpressionSyntax aes && aes.Left == id &&
                aes.IsKind(SyntaxKind.SimpleAssignmentExpression))
                return true;
            // out x
            if (id.Parent is ArgumentSyntax arg && arg.Expression == id &&
                arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                return true;
            return false;
        }

        /// <summary>
        /// True when <paramref name="name"/> is assigned anywhere in
        /// <paramref name="bodyRoot"/> after <paramref name="startStmt"/>.
        /// </summary>
        private static bool IsReassignedAfter(SyntaxNode bodyRoot, StatementSyntax startStmt, string name)
        {
            int start = startStmt.SpanStart;
            foreach (var aes in bodyRoot.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
            {
                if (aes.SpanStart <= start)
                    continue;
                if (aes.Left is IdentifierNameSyntax id && id.Identifier.ValueText == name)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True when no CFG path starting just after <paramref name="startStmt"/> reads
        /// <paramref name="symbol"/> before overwriting it or leaving the method.
        /// Sound direction: returns false (live) as soon as any path observes the value,
        /// and also when the flow information is incomplete.
        /// </summary>
        private static bool IsDeadStore(
            ControlFlowGraph cfg,
            Dictionary<BasicBlock, List<StmtGroup>> blockGroups,
            Dictionary<StatementSyntax, StmtInfo> stmtInfo,
            SyntaxNode bodyRoot,
            StatementSyntax startStmt,
            ISymbol symbol)
        {
            // The CFG does not route exits through finally blocks, so a read that only
            // happens in finally is invisible to the walk below. If the store is covered
            // by a try whose finally may read the symbol, the value is observed.
            if (IsReadInCoveringFinally(bodyRoot, startStmt, symbol.Name))
                return false;

            // Same for catch: Roslyn's CFG has no exceptional edges, so a read that
            // only happens in catch is invisible to the walk. If the store is covered
            // by a try whose catch may read the symbol, the value is observed
            // (audit item N1).
            if (IsReadInAssociatedCatch(bodyRoot, startStmt, symbol.Name))
                return false;

            var starts = new List<(BasicBlock Block, int GroupIndex)>();
            foreach (var block in cfg.Blocks)
            {
                var groups = blockGroups[block];
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    if (groups[gi].Statement == startStmt)
                        starts.Add((block, gi + 1)); // continue after the store
                }
            }
            if (starts.Count == 0)
                return false; // cannot map the statement onto the CFG: stay silent

            var visited = new HashSet<(BasicBlock, int)>();
            var queue = new Queue<(BasicBlock, int)>(starts);
            while (queue.Count > 0)
            {
                var (block, gi) = queue.Dequeue();
                if (!visited.Add((block, gi)))
                    continue;

                var groups = blockGroups[block];
                if (gi >= groups.Count)
                {
                    foreach (var succ in FlowHelpers.Successors(block))
                    {
                        if (succ.Kind == BasicBlockKind.Exit)
                            continue; // this path leaves the value unread
                        queue.Enqueue((succ, 0));
                    }
                    continue;
                }

                var group = groups[gi];
                if (group.Statement is null || !stmtInfo.TryGetValue(group.Statement, out var info) ||
                    info.HasUnknownFlow)
                    return false; // unknown statement effect: assume the value may be observed

                if (info.Read.Contains(symbol))
                    return false; // some path observes the stored value: live
                if (info.Written.Contains(symbol) || info.OutRefArgs.Contains(symbol))
                    continue; // this path overwrites it (out/ref argument included) before any read
                queue.Enqueue((block, gi + 1));
            }

            return true; // every path overwrites the value or exits before reading it
        }
    }
}
