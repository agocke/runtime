// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if DEBUG
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ILLink.CodeFixProvider;
using ILLink.RoslynAnalyzer;
using ILLink.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace ILLink.CodeFix
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RequiresUnsafeCodeFixProvider)), Shared]
    public sealed class RequiresUnsafeCodeFixProvider : BaseAttributeCodeFixProvider
    {
        private const string WrapInUnsafeBlockTitle = "Wrap in unsafe block";

        public static ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(DiagnosticDescriptors.GetDiagnosticDescriptor(DiagnosticId.RequiresUnsafe));

        public sealed override ImmutableArray<string> FixableDiagnosticIds => SupportedDiagnostics.Select(dd => dd.Id).ToImmutableArray();

        private protected override LocalizableString CodeFixTitle => new LocalizableResourceString(nameof(Resources.RequiresUnsafeCodeFixTitle), Resources.ResourceManager, typeof(Resources));

        private protected override string FullyQualifiedAttributeName => RequiresUnsafeAnalyzer.FullyQualifiedRequiresUnsafeAttribute;

        private protected override AttributeableParentTargets AttributableParentTargets => AttributeableParentTargets.MethodOrConstructor;

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            // Register the base code fix (add RequiresUnsafe attribute)
            await BaseRegisterCodeFixesAsync(context).ConfigureAwait(false);

            // Register the "wrap in unsafe block" code fix
            var document = context.Document;
            var diagnostic = context.Diagnostics.First();

            if (await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false) is not { } root)
                return;

            SyntaxNode targetNode = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

            // Find the statement containing the unsafe call
            var containingStatement = targetNode.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
            if (containingStatement is null || containingStatement is BlockSyntax)
            {
                // Try expression-bodied member
                var arrowExpr = targetNode.AncestorsAndSelf().OfType<ArrowExpressionClauseSyntax>().FirstOrDefault();
                if (arrowExpr != null && !HasDirectiveTrivia(arrowExpr))
                {
                    context.RegisterCodeFix(CodeAction.Create(
                        title: WrapInUnsafeBlockTitle,
                        createChangedDocument: ct => ConvertExpressionBodyToUnsafeBlockAsync(document, arrowExpr, ct),
                        equivalenceKey: WrapInUnsafeBlockTitle), diagnostic);
                }
                return;
            }

            // Find the parent block containing this statement
            var parentBlock = containingStatement.Parent as BlockSyntax;
            if (parentBlock == null)
                return;

            context.RegisterCodeFix(CodeAction.Create(
                title: WrapInUnsafeBlockTitle,
                createChangedDocument: ct => WrapStatementsInUnsafeBlockAsync(document, parentBlock, containingStatement, ct),
                equivalenceKey: WrapInUnsafeBlockTitle), diagnostic);
        }

        private static async Task<Document> WrapStatementsInUnsafeBlockAsync(
            Document document,
            BlockSyntax parentBlock,
            StatementSyntax triggerStatement,
            CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel == null)
                return document;

            // Find the range of statements to wrap using data flow analysis
            var statements = parentBlock.Statements;
            int triggerIndex = statements.IndexOf(triggerStatement);
            if (triggerIndex < 0)
                return document;

            // Start with just the trigger statement
            int startIndex = triggerIndex;
            int endIndex = triggerIndex;

            // Expand the range until no variables flow out
            bool expanded = true;
            while (expanded && endIndex < statements.Count - 1)
            {
                expanded = false;

                // Get the statements in our current range
                var rangeStatements = statements.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();

                // Analyze data flow for the range
                var dataFlow = semanticModel.AnalyzeDataFlow(rangeStatements.First(), rangeStatements.Last());
                if (dataFlow == null || !dataFlow.Succeeded)
                    break;

                // Check if any variables defined in the range are used after the range
                var definedInRange = dataFlow.VariablesDeclared;
                var usedAfterRange = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

                for (int i = endIndex + 1; i < statements.Count; i++)
                {
                    var afterFlow = semanticModel.AnalyzeDataFlow(statements[i]);
                    if (afterFlow != null && afterFlow.Succeeded)
                    {
                        foreach (var used in afterFlow.DataFlowsIn)
                        {
                            usedAfterRange.Add(used);
                        }
                    }
                }

                // If any variable defined in range is used after, expand to include the next statement
                if (definedInRange.Any(usedAfterRange.Contains))
                {
                    endIndex++;
                    expanded = true;
                }
            }

            // Now wrap the range [startIndex, endIndex] in an unsafe block
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);

            var statementsToWrap = statements.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();

            // Create the TODO comment
            var todoComment = SyntaxFactory.Comment("// TODO(unsafe): Baselining unsafe usage");
            var newLine = SyntaxFactory.CarriageReturnLineFeed;

            // Create the unsafe block
            var unsafeBlock = SyntaxFactory.UnsafeStatement(
                SyntaxFactory.Block(statementsToWrap.Select(s => s.WithoutTrivia())))
                .WithLeadingTrivia(statementsToWrap[0].GetLeadingTrivia().InsertRange(0, new[] { todoComment, newLine }))
                .WithTrailingTrivia(statementsToWrap[statementsToWrap.Count - 1].GetTrailingTrivia());

            // Build the new list of statements
            var newStatements = new List<StatementSyntax>();
            for (int i = 0; i < statements.Count; i++)
            {
                if (i == startIndex)
                {
                    newStatements.Add(unsafeBlock);
                }
                else if (i > startIndex && i <= endIndex)
                {
                    // Skip - already included in unsafe block
                }
                else
                {
                    newStatements.Add(statements[i]);
                }
            }

            var newBlock = parentBlock.WithStatements(SyntaxFactory.List(newStatements));
            editor.ReplaceNode(parentBlock, newBlock);

            return editor.GetChangedDocument();
        }

        private static async Task<Document> ConvertExpressionBodyToUnsafeBlockAsync(
            Document document,
            ArrowExpressionClauseSyntax arrowExpr,
            CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);

            // Create the TODO comment
            var todoComment = SyntaxFactory.Comment("// TODO(unsafe): Baselining unsafe usage");
            var newLine = SyntaxFactory.CarriageReturnLineFeed;

            // Get the parent member to determine the return type
            var parent = arrowExpr.Parent;
            bool isVoid = false;

            if (parent is MethodDeclarationSyntax method)
            {
                isVoid = method.ReturnType is PredefinedTypeSyntax pts && pts.Keyword.IsKind(SyntaxKind.VoidKeyword);
            }
            else if (parent is LocalFunctionStatementSyntax localFunc)
            {
                isVoid = localFunc.ReturnType is PredefinedTypeSyntax pts && pts.Keyword.IsKind(SyntaxKind.VoidKeyword);
            }
            else if (parent is AccessorDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax)
            {
                isVoid = false;
            }

            // Create the statement that goes inside the unsafe block
            StatementSyntax innerStatement = isVoid
                ? SyntaxFactory.ExpressionStatement(arrowExpr.Expression.WithoutTrivia())
                : SyntaxFactory.ReturnStatement(arrowExpr.Expression.WithoutTrivia());

            // Create the unsafe block
            var unsafeBlock = SyntaxFactory.UnsafeStatement(
                SyntaxFactory.Block(innerStatement))
                .WithLeadingTrivia(todoComment, newLine);

            // Create the block body
            var blockBody = SyntaxFactory.Block(unsafeBlock);

            // Replace based on parent type
            switch (parent)
            {
                case MethodDeclarationSyntax methodDecl:
                    var newMethod = methodDecl
                        .WithExpressionBody(null)
                        .WithSemicolonToken(default)
                        .WithBody(blockBody);
                    editor.ReplaceNode(methodDecl, newMethod);
                    break;

                case LocalFunctionStatementSyntax localFunc:
                    var newLocalFunc = localFunc
                        .WithExpressionBody(null)
                        .WithSemicolonToken(default)
                        .WithBody(blockBody);
                    editor.ReplaceNode(localFunc, newLocalFunc);
                    break;

                case PropertyDeclarationSyntax propDecl:
                    var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                        .WithBody(SyntaxFactory.Block(unsafeBlock));
                    var accessorList = SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter));
                    var newProp = propDecl
                        .WithExpressionBody(null)
                        .WithSemicolonToken(default)
                        .WithAccessorList(accessorList);
                    editor.ReplaceNode(propDecl, newProp);
                    break;

                case AccessorDeclarationSyntax accessor:
                    var newAccessor = accessor
                        .WithExpressionBody(null)
                        .WithSemicolonToken(default)
                        .WithBody(SyntaxFactory.Block(unsafeBlock));
                    editor.ReplaceNode(accessor, newAccessor);
                    break;

                case IndexerDeclarationSyntax indexerDecl:
                    var indexerGetter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                        .WithBody(SyntaxFactory.Block(unsafeBlock));
                    var indexerAccessorList = SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(indexerGetter));
                    var newIndexer = indexerDecl
                        .WithExpressionBody(null)
                        .WithSemicolonToken(default)
                        .WithAccessorList(indexerAccessorList);
                    editor.ReplaceNode(indexerDecl, newIndexer);
                    break;
            }

            return editor.GetChangedDocument();
        }

        protected override SyntaxNode[] GetAttributeArguments(ISymbol? attributableSymbol, ISymbol targetSymbol, SyntaxGenerator syntaxGenerator, Diagnostic diagnostic) =>
            RequiresHelpers.GetAttributeArgumentsForRequires(targetSymbol, syntaxGenerator, HasPublicAccessibility(attributableSymbol));

        /// <summary>
        /// Checks if the arrow expression clause or its expression has preprocessor directive trivia.
        /// Converting expression bodies with directives to block bodies is error-prone, so we skip the fix.
        /// </summary>
        private static bool HasDirectiveTrivia(ArrowExpressionClauseSyntax arrowExpr)
        {
            // Check the arrow token's trailing trivia (e.g., #if right after =>)
            if (arrowExpr.ArrowToken.TrailingTrivia.Any(t => t.IsDirective))
                return true;

            // Check the expression's leading trivia (e.g., #if before the expression)
            if (arrowExpr.Expression.GetLeadingTrivia().Any(t => t.IsDirective))
                return true;

            // Check the expression's trailing trivia (e.g., #endif after the expression)
            if (arrowExpr.Expression.GetTrailingTrivia().Any(t => t.IsDirective))
                return true;

            return false;
        }
    }
}
#endif
