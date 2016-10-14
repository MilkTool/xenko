﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SiliconStudio.Core.Diagnostics;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace SiliconStudio.Xenko.Assets.Scripts
{
    public class BasicBlock
    {
        internal readonly int Index;

        public BasicBlock(int index)
        {
            Index = index;
        }

        public BasicBlock NextBlock { get; set; }

        internal LabeledStatementSyntax Label { get; set; }

        internal List<StatementSyntax> Statements = new List<StatementSyntax>();
    }

    public class VisualScriptCompilerContext
    {
        private int labelCount;
        private int localCount;
        private VisualScriptAsset asset;

        // If a specific output Slot was stored in a local variable, this will store its name
        private Dictionary<Slot, string> outputSlotLocals = new Dictionary<Slot, string>();

        internal Queue<Tuple<BasicBlock, ExecutionBlock>> CodeToGenerate = new Queue<Tuple<BasicBlock, ExecutionBlock>>();

        public Logger Log { get; }

        public Dictionary<Block, BasicBlock> BlockMapping { get; } = new Dictionary<Block, BasicBlock>();

        public List<BasicBlock> Blocks { get; } = new List<BasicBlock>();

        public BasicBlock CurrentBasicBlock { get; internal set; }

        public Block CurrentBlock { get; internal set; }

        internal VisualScriptCompilerContext(VisualScriptAsset asset, Logger log)
        {
            // Create first block
            this.asset = asset;
            Log = log;
        }

        public BasicBlock GetOrCreateBasicBlockFromSlot(Slot nextExecutionSlot)
        {
            // Automatically flow to next execution slot (if it has a null name => default behavior)
            if (nextExecutionSlot != null)
            {
                var nextExecutionLink = asset.Links.FirstOrDefault(x => x.Source == nextExecutionSlot && x.Target != null);
                if (nextExecutionLink != null)
                {
                    return GetOrCreateBasicBlock((ExecutionBlock)nextExecutionLink.Target.Owner);
                }
            }

            return null;
        }

        public IEnumerable<Link> FindOutputLinks(Slot outputSlot)
        {
            return asset.Links.Where(x => x.Source == outputSlot && x.Target != null);
        }

        public Link FindInputLink(Slot inputSlot)
        {
            return asset.Links.FirstOrDefault(x => x.Target == inputSlot && x.Source != null);
        }

        public ExpressionSyntax GenerateExpression(Slot slot)
        {
            // Automatically flow to next execution slot (if it has a null name => default behavior)
            if (slot != null)
            {
                // 1. First check if there is a link and use its expression
                var sourceLink = asset.Links.FirstOrDefault(x => x.Target == slot);
                if (sourceLink != null)
                {
                    ExpressionSyntax expression;

                    string localName;
                    if (outputSlotLocals.TryGetValue(sourceLink.Source, out localName))
                    {
                        expression = IdentifierName(localName);
                    }
                    else
                    {
                        // Generate code
                        expression = ((IExpressionBlock)sourceLink.Source.Owner).GenerateExpression(this, sourceLink.Source);
                    }

                    // Add annotation on both source block and link (so that we can keep track of what block/link generated what source code)
                    expression = expression.WithAdditionalAnnotations(GenerateAnnotation(sourceLink.Source.Owner), GenerateAnnotation(sourceLink));

                    return expression;
                }

                // 2. If a custom value is set, use it
                if (slot.Value != null)
                {
                    return ParseExpression(slot.Value).WithAdditionalAnnotations(GenerateAnnotation(slot.Owner));
                }

                // 3. Fallback: use slot name
                return IdentifierName(slot.Name).WithAdditionalAnnotations(GenerateAnnotation(slot.Owner));
            }

            // TODO: Issue an error
            return IdentifierName("unknown").WithAdditionalAnnotations(GenerateAnnotation(CurrentBlock));
        }

        public BasicBlock GetOrCreateBasicBlock(ExecutionBlock block)
        {
            BasicBlock newBasicBlock;
            if (!BlockMapping.TryGetValue(block, out newBasicBlock))
            {
                newBasicBlock = new BasicBlock(Blocks.Count);
                Blocks.Add(newBasicBlock);
                BlockMapping.Add(block, newBasicBlock);
                GenerateCode(newBasicBlock, block);
            }

            return newBasicBlock;
        }

        public GotoStatementSyntax CreateGotoStatement(BasicBlock target)
        {
            return GotoStatement(SyntaxKind.GotoStatement, IdentifierName(GetOrCreateLabel(target).Identifier));
        }

        public void AddStatement(StatementSyntax statement)
        {
            // Add annotation on block (so that we can keep track of what block generated what source code)
            statement = statement.WithAdditionalAnnotations(GenerateAnnotation(CurrentBlock));

            // If there is already a label with an empty statement (still no instructions), replace its inner statement
            if (CurrentBasicBlock.Label != null && CurrentBasicBlock.Statements.Count == 1 && CurrentBasicBlock.Label.Statement is EmptyStatementSyntax)
            {
                CurrentBasicBlock.Label = CurrentBasicBlock.Label.WithStatement(statement);
                CurrentBasicBlock.Statements[0] = CurrentBasicBlock.Label;
            }
            else
            {
                CurrentBasicBlock.Statements.Add(statement);
            }
        }

        public void GenerateCode(BasicBlock basicBlock, Block block)
        {
            CodeToGenerate.Enqueue(Tuple.Create(basicBlock, (ExecutionBlock)block));
        }

        private LabeledStatementSyntax GetOrCreateLabel(BasicBlock basicBlock)
        {
            if (basicBlock.Label == null)
            {
                basicBlock.Label = LabeledStatement(Identifier($"block{labelCount++}"), basicBlock.Statements.Count > 0 ? basicBlock.Statements[0] : EmptyStatement());
                basicBlock.Statements.Insert(0, basicBlock.Label);
            }

            return basicBlock.Label;
        }

        public string GenerateLocalVariableName(string nameHint = null)
        {
            return $"{nameHint ?? "local"}{localCount++}";
        }


        public void RegisterLocalVariable(Slot returnSlot, string localVariableName)
        {
            outputSlotLocals.Add(returnSlot, localVariableName);
        }

        private SyntaxAnnotation GenerateAnnotation(Block block)
        {
            return new SyntaxAnnotation("Block", block.Id.ToString());
        }

        private SyntaxAnnotation GenerateAnnotation(Link link)
        {
            return new SyntaxAnnotation("Link", link.Id.ToString());
        }
    }

    public class VisualScriptCompilerResult : LoggerResult
    {
        public string GeneratedSource { get; set; }
        public SyntaxTree SyntaxTree { get; set; }
    }

    public class VisualScriptCompilerOptions
    {
        public string Namespace { get; set; }

        public string Class { get; set; }

        public List<string> UsingDirectives { get; set; } = new List<string>();

        public string BaseClass { get; set; }
    }

    public class VisualScriptCompiler
    {
        public static VisualScriptCompilerResult Generate(VisualScriptAsset visualScriptAsset, VisualScriptCompilerOptions options)
        {
            var result = new VisualScriptCompilerResult();

            var members = new List<MemberDeclarationSyntax>();
            var className = options.Class;

            // Generate variables
            foreach (var variable in visualScriptAsset.Variables)
            {
                var variableType = variable.Type;
                if (variableType == null)
                {
                    result.Error($"Variable {variable.Name} has no type, using \"object\" instead.");
                    variableType = "object";
                }

                var field =
                    FieldDeclaration(
                        VariableDeclaration(
                            ParseTypeName(variableType))
                        .WithVariables(
                            SingletonSeparatedList(
                                VariableDeclarator(
                                    Identifier(variable.Name)))))
                    .WithModifiers(
                        TokenList(
                            Token(SyntaxKind.PublicKeyword)));

                members.Add(field);
            }

            // Process each function
            foreach (var functionStartBlock in visualScriptAsset.Blocks.OfType<FunctionStartBlock>())
            {
                var context = new VisualScriptCompilerContext(visualScriptAsset, result);

                // Force generation of start block
                context.GetOrCreateBasicBlock(functionStartBlock);

                // Process blocks to generate statements
                while (context.CodeToGenerate.Count > 0)
                {
                    var codeToGenerate = context.CodeToGenerate.Dequeue();
                    context.CurrentBasicBlock = codeToGenerate.Item1;
                    context.CurrentBlock = codeToGenerate.Item2;
                    var currentBlock = codeToGenerate.Item2;

                    // Generate code for current node
                    currentBlock.GenerateCode(context);

                    // Automatically flow to next execution slot (if it has a null name => default behavior)
                    var nextExecutionSlot = currentBlock.Slots.FirstOrDefault(x => x.Kind == SlotKind.Execution && x.Direction == SlotDirection.Output && x.Flags == SlotFlags.AutoflowExecution);
                    if (nextExecutionSlot != null)
                    {
                        var nextExecutionLink = visualScriptAsset.Links.FirstOrDefault(x => x.Source == nextExecutionSlot && x.Target != null);
                        if (nextExecutionLink == null)
                        {
                            // Nothing connected, no need to generate a goto to an empty return
                            goto GenerateReturn;
                        }

                        var nextBasicBlock = context.GetOrCreateBasicBlock((ExecutionBlock)nextExecutionLink.Target.Owner);
                        context.CurrentBasicBlock.NextBlock = nextBasicBlock;
                    }

                    // Is there a next block to flow to?
                    if (context.CurrentBasicBlock.NextBlock != null)
                    {
                        var nextBlock = context.CurrentBasicBlock.NextBlock;

                        // Do we need a goto? (in case there is some intermediary block in between)
                        if (nextBlock.Index != context.CurrentBasicBlock.Index + 1)
                        {
                            context.AddStatement(context.CreateGotoStatement(nextBlock));
                        }

                        continue;
                    }

                GenerateReturn:
                    // No next node, let's put a return so that we don't flow into any basic blocks that were after this one
                    context.AddStatement(ReturnStatement());
                }

                // Generate method
                var method =
                    MethodDeclaration(
                        PredefinedType(
                            Token(SyntaxKind.VoidKeyword)),
                        Identifier(functionStartBlock.FunctionName))
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                    .WithBody(
                        Block(context.Blocks.SelectMany(x => x.Statements)));

                members.Add(method);
            }

            // Generate class
            var @class =
                ClassDeclaration(className)
                .WithMembers(List(members))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.PartialKeyword)));

            if (options.BaseClass != null)
                @class = @class.WithBaseList(BaseList(SingletonSeparatedList<BaseTypeSyntax>(SimpleBaseType(IdentifierName(options.BaseClass)))));

            // Generate namespace around class (if any)
            MemberDeclarationSyntax namespaceOrClass = @class;
            if (options.Namespace != null)
            {
                namespaceOrClass =
                    NamespaceDeclaration(
                        IdentifierName(options.Namespace))
                    .WithMembers(
                        SingletonList<MemberDeclarationSyntax>(@class));
            }

            // Generate compilation unit
            var compilationUnit =
                CompilationUnit()
                .WithUsings(
                    List(options.UsingDirectives.Select(x => 
                        UsingDirective(
                            IdentifierName(x)))))
                .WithMembers(
                    SingletonList(namespaceOrClass))
                .NormalizeWhitespace();

            // Generate actual source code
            result.GeneratedSource = compilationUnit.ToFullString();
            result.SyntaxTree = SyntaxTree(compilationUnit);

            return result;
        }
    }
}