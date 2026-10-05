using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ScriptCheckup.Analyzers;

/// <summary>
/// Code fixes for the RenderTexture dataflow rules:
/// DF003 — insert a missing ReleaseTemporary;
/// DF004 — restore a saved RenderTexture.active.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RenderTextureCodeFixProvider))]
[Shared]
public sealed class RenderTextureCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create("DF003", "DF004");

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document
            .GetSyntaxRootAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (root == null) return;

        var diag = context.Diagnostics.FirstOrDefault(d =>
            FixableDiagnosticIds.Contains(d.Id));
        if (diag == null) return;

        var node = root.FindNode(diag.Location.SourceSpan);
        if (node == null) return;

        if (diag.Id == "DF003")
        {
            var declarator = node.AncestorsAndSelf()
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault();
            var name = declarator?.Identifier.Text
                ?? (node as IdentifierNameSyntax)?.Identifier.Text;
            if (name == null) return;

            var block = declarator?.Ancestors().OfType<BlockSyntax>().FirstOrDefault()
                ?? node.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
            if (block == null) return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Release temporary RenderTexture '{name}'",
                    ct => AddReleaseAsync(context.Document, root, block, name, ct),
                    nameof(RenderTextureCodeFixProvider) + "_DF003"),
                diag);
        }
        else if (diag.Id == "DF004")
        {
            // DF004 is reported on the bypassing `return`/`throw` (or the method
            // identifier). Find the enclosing method block and the existing
            // `var prev = RenderTexture.active;` save.
            var block = node.AncestorsAndSelf()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault()?.Body;
            if (block == null) return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Save and restore RenderTexture.active",
                    ct => AddRestoreAsync(context.Document, root, block, ct),
                    nameof(RenderTextureCodeFixProvider) + "_DF004"),
                diag);
        }
    }

    private static Task<Document> AddReleaseAsync(
        Document document, SyntaxNode root, BlockSyntax block,
        string name, CancellationToken ct)
    {
        var statements = block.Statements.ToList();

        // Find the acquiring statement: `var name = ...GetTemporary...` or `name = ...`.
        int acqIdx = -1;
        for (int i = 0; i < statements.Count; i++)
        {
            if (IsGetTemporaryAcquisition(statements[i], name))
            {
                acqIdx = i;
                break;
            }
        }
        if (acqIdx < 0)
            return Task.FromResult(document); // cannot locate; do not offer a bogus edit

        var release = SyntaxFactory.ParseStatement(
                $"RenderTexture.ReleaseTemporary({name});\n")
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

        var after = statements.Skip(acqIdx + 1).ToList();

        if (after.Count == 0)
        {
            // Nothing after the acquisition: a plain append is fine (no paths to close).
            var simple = statements.ToList();
            simple.Insert(acqIdx + 1, release);
            return Task.FromResult(document.WithSyntaxRoot(
                root.ReplaceNode(block, block.WithStatements(SyntaxFactory.List(simple)))));
        }

        var tryBlock = SyntaxFactory.Block(after);

        // Drop pre-existing releases of the same lease *anywhere* inside the wrapped
        // region (including nested if-branches — the exact "released on one branch"
        // case DF003 exists for). The finally below becomes the single release point;
        // otherwise the released branch would free the lease twice (round 2, item A).
        // A release embedded in a brace-less `if` cannot just be deleted — that would
        // leave `if (cond)` with no body and swallow the next statement (round 3,
        // item B). Inside a block or switch section the release is removed outright;
        // a bare `;` is only left when the `if` itself is the statement being replaced
        // and its condition is side-effect free (round 4, item E).
        var nestedReleases = tryBlock.DescendantNodes()
            .OfType<ExpressionStatementSyntax>()
            .Where(s => IsReleaseOf(s, name))
            .ToList();
        if (nestedReleases.Count > 0)
        {
            var toRemove = new List<SyntaxNode>();
            var toReplace = new Dictionary<SyntaxNode, SyntaxNode>();
            foreach (var rel in nestedReleases)
            {
                SyntaxNode target = rel;
                IfStatementSyntax? ifs = null;
                if (rel.Parent is IfStatementSyntax i && i.Statement == rel && i.Else == null)
                {
                    target = i;
                    ifs = i;
                }
                if (target.Parent is BlockSyntax || target.Parent is SwitchSectionSyntax)
                {
                    toRemove.Add(target);
                }
                else if (ifs is not null && HasSideEffects(ifs.Condition))
                {
                    // Keep the condition (it may do work) with an empty body.
                    toReplace[target] = ifs.WithStatement(
                        SyntaxFactory.EmptyStatement()
                            .WithLeadingTrivia(ifs.Statement.GetLeadingTrivia()));
                }
                else if (ifs is not null)
                {
                    toReplace[target] = SyntaxFactory.EmptyStatement()
                        .WithLeadingTrivia(target.GetLeadingTrivia());
                }
                else
                {
                    toRemove.Add(target);
                }
            }
            if (toRemove.Count > 0)
                tryBlock = tryBlock.RemoveNodes(toRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
            if (toReplace.Count > 0)
                tryBlock = tryBlock.ReplaceNodes(
                    toReplace.Keys, (orig, _) => toReplace[orig])!;
        }

        var tryStmt = SyntaxFactory.TryStatement(
            tryBlock,
            default,
            SyntaxFactory.FinallyClause(SyntaxFactory.Block(release)));
        var newStatements = statements.Take(acqIdx + 1).ToList();
        newStatements.Add(tryStmt);
        return Task.FromResult(document.WithSyntaxRoot(
            root.ReplaceNode(block, block.WithStatements(SyntaxFactory.List(newStatements)))));
    }

    private static bool IsGetTemporaryAcquisition(StatementSyntax stmt, string name)
    {
        if (stmt is LocalDeclarationStatementSyntax decl)
        {
            var v = decl.Declaration.Variables.FirstOrDefault(vd =>
                vd.Identifier.ValueText == name);
            return v?.Initializer?.Value.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Any(IsGetTemporarySyntax) == true;
        }
        if (stmt is ExpressionStatementSyntax es &&
            es.Expression is AssignmentExpressionSyntax aes &&
            aes.Left is IdentifierNameSyntax id &&
            id.Identifier.ValueText == name)
        {
            return aes.Right.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Any(IsGetTemporarySyntax);
        }
        return false;
    }

    private static bool IsGetTemporarySyntax(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax ma &&
        ma.Name.Identifier.ValueText == "GetTemporary" &&
        FlowAnalysis.FlowHelpers.IsRenderTextureName(ma.Expression);

    private static bool IsReleaseOf(StatementSyntax stmt, string name) =>
        stmt is ExpressionStatementSyntax es &&
        es.Expression is InvocationExpressionSyntax inv &&
        inv.Expression is MemberAccessExpressionSyntax ma &&
        ma.Name.Identifier.ValueText == "ReleaseTemporary" &&
        inv.ArgumentList.Arguments.FirstOrDefault()?.Expression
            is IdentifierNameSyntax id &&
        id.Identifier.ValueText == name;

    private static Task<Document> AddRestoreAsync(
        Document document, SyntaxNode root, BlockSyntax block,
        CancellationToken ct)
    {
        // Look for an existing `var prev = RenderTexture.active;` (DF004 case:
        // the save exists but a restore is bypassed). Search nested blocks too
        // (audit item 2.2) and wrap the statements after the save *in its own
        // block* in try/finally with the restore in finally.
        foreach (var decl in block.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (decl.Declaration.Variables.Count != 1)
                continue;
            var v = decl.Declaration.Variables[0];
            if (v.Initializer?.Value is MemberAccessExpressionSyntax ma &&
                ma.Name.Identifier.ValueText == "active" &&
                FlowAnalysis.FlowHelpers.IsRenderTextureName(ma.Expression) &&
                decl.Parent is BlockSyntax saveBlock)
            {
                return Task.FromResult(WrapAfterSave(
                    document, root, saveBlock, decl, v.Identifier.ValueText));
            }
        }

        // No save, but DF004 fired — so there is a restore (e.g. to null) that a
        // path bypasses. Insert `var prev = RenderTexture.active;` before the first
        // *dirty* assignment and wrap the rest in try/finally (round 2, item D).
        var dirtyAssign = block.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(aes =>
                aes.Left is MemberAccessExpressionSyntax dma &&
                dma.Name.Identifier.ValueText == "active" &&
                FlowAnalysis.FlowHelpers.IsRenderTextureName(dma.Expression) &&
                !IsRestoreValue(aes.Right));
        if (dirtyAssign?.Parent is not StatementSyntax dirtyStmt ||
            dirtyStmt.Parent is not BlockSyntax dirtyBlock)
            return Task.FromResult(document); // cannot place the save safely

        var saveDecl = SyntaxFactory.ParseStatement(
                "var prev = RenderTexture.active;\n")
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        var dirtyStmts = dirtyBlock.Statements.ToList();
        int dirtyIdx = dirtyStmts.IndexOf(dirtyStmt);
        dirtyStmts.Insert(dirtyIdx, saveDecl);
        var newDirtyBlock = dirtyBlock.WithStatements(SyntaxFactory.List(dirtyStmts));
        var newRoot = root.ReplaceNode(dirtyBlock, newDirtyBlock);
        var newSave = newDirtyBlock.Statements[dirtyIdx];
        return Task.FromResult(WrapAfterSave(document, newRoot, newDirtyBlock, newSave, "prev"));
    }

    /// <summary>
    /// Wraps the statements after <paramref name="saveStmt"/> (in its own block) in
    /// try/finally with <c>RenderTexture.active = prevName;</c> in finally.
    /// </summary>
    private static Document WrapAfterSave(
        Document document, SyntaxNode root, BlockSyntax saveBlock,
        StatementSyntax saveStmt, string prevName)
    {
        var saveStatements = saveBlock.Statements.ToList();
        int saveIdx = saveStatements.IndexOf(saveStmt);
        var tryStatements = saveStatements.Skip(saveIdx + 1).ToList();
        var finallyRestore = SyntaxFactory.ParseStatement(
                $"RenderTexture.active = {prevName};\n")
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        var tryStmt = SyntaxFactory.TryStatement(
            SyntaxFactory.Block(tryStatements),
            default,
            SyntaxFactory.FinallyClause(SyntaxFactory.Block(finallyRestore)));
        var newStatements = saveStatements.Take(saveIdx + 1).ToList();
        newStatements.Add(tryStmt);
        var newSaveBlock = saveBlock.WithStatements(SyntaxFactory.List(newStatements));
        return document.WithSyntaxRoot(root.ReplaceNode(saveBlock, newSaveBlock));
    }

    /// <summary>Null literal or a bare identifier (a previously saved value).</summary>
    private static bool IsRestoreValue(ExpressionSyntax expr)
    {
        while (expr is ParenthesizedExpressionSyntax p)
            expr = p.Expression;
        return expr.IsKind(SyntaxKind.NullLiteralExpression) || expr is IdentifierNameSyntax;
    }

    /// <summary>
    /// True when evaluating the condition may do work (calls, assignments,
    /// ++/--): such an `if` keeps its condition with an empty body instead of
    /// being removed outright (round 4, item E).
    /// </summary>
    private static bool HasSideEffects(ExpressionSyntax cond) =>
        cond.DescendantNodesAndSelf().Any(n =>
            n is InvocationExpressionSyntax ||
            n is AssignmentExpressionSyntax ||
            n is PostfixUnaryExpressionSyntax ||
            (n is PrefixUnaryExpressionSyntax pre &&
             (pre.IsKind(SyntaxKind.PreIncrementExpression) ||
              pre.IsKind(SyntaxKind.PreDecrementExpression))));
}
