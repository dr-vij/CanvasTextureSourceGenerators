using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UTools.SourceGeneratorAttributes;

namespace UTools.SourceGenerators
{
    [Generator]
    public class FieldSubscriptionsGenerator : ISourceGenerator
    {
        private readonly List<MemberDeclarationSyntax> m_Members = new();

        private readonly List<UsingDirectiveSyntax> m_ExtraUsing = new()
        {
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("UTools")),
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System"))
        };

        private UsingDirectiveSyntax m_UToolsUsing = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("UTools"));
        private UsingDirectiveSyntax m_SystemUsing = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System"));

        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var compilation = context.Compilation;
            var disposableSubscriptionAttribute = nameof(DisposableSubscription);
            var eventSubscriptionAttribute = nameof(EventSubscription);

            var fieldAttributes = new[] { disposableSubscriptionAttribute, eventSubscriptionAttribute };

            var classNodes = compilation.GetClassesByFieldAttributes(fieldAttributes);
            var counter = 0;

            foreach (var classNode in classNodes)
            {
                var className = classNode.Identifier.Text;
                var fieldNodes = classNode.Members
                    .OfType<FieldDeclarationSyntax>()
                    .Where(fieldNode => fieldAttributes.Any(fieldNode.HasAttribute));

                var newClass = classNode.WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>()).WithoutTrivia();
                foreach (var fieldNode in fieldNodes)
                {
                    var isStatic = fieldNode.Modifiers.Any(SyntaxKind.StaticKeyword);
                    var fieldType = fieldNode.Declaration.Type;

                    var fieldName = fieldNode.Declaration.Variables.First().Identifier.Text;
                    var privateEventName = $"{fieldName}Changed";
                    var propertyName = fieldName.RemovePrefix();
                    var subscriptionEventName = $"{propertyName}Changed";
                    var subscriptionMethodName = $"SubscribeTo{propertyName}";
                    var partialMethodName = $"On{propertyName}Change";

                    var eventField = CreateEventField(fieldType, privateEventName, isStatic);
                    var propertyDeclaration = CreatePropertyAndCallbacks(fieldType, propertyName, fieldName, partialMethodName, privateEventName, isStatic);
                    var partialMethod = CreatePartialMethod(partialMethodName, fieldType, isStatic);

                    m_Members.Clear();
                    m_Members.Add(partialMethod);
                    m_Members.Add(eventField);
                    m_Members.Add(propertyDeclaration);

                    if (fieldNode.HasAttribute(disposableSubscriptionAttribute))
                    {
                        TryGetAttributeParameter<DisposableSubscription>(compilation, fieldNode);

                        var subscriptionMethod = CreateDisposableSubscriptionMethod(fieldType, subscriptionMethodName, fieldName, privateEventName, isStatic);
                        m_Members.Add(subscriptionMethod);
                    }

                    if (fieldNode.HasAttribute(eventSubscriptionAttribute))
                    {
                        TryGetAttributeParameter<EventSubscription>(compilation, fieldNode);

                        var subscriptionEvent = CreateSubscriptionEvent(fieldType, subscriptionEventName, fieldName, privateEventName, isStatic);
                        m_Members.Add(subscriptionEvent);
                    }

                    newClass = newClass.AddMembers(m_Members.ToArray());
                }

                var compilationUnit = SyntaxFactory.CompilationUnit()
                    .AddUsings(classNode.GetUsingArr())
                    .AddUsings(m_ExtraUsing.ToArray());

                var classWithHierarchy = classNode.CopyHierarchyTo(newClass);
                compilationUnit = compilationUnit.AddMembers(classWithHierarchy);

                var code = compilationUnit
                    .NormalizeWhitespace()
                    .ToFullString();
                context.AddSource(className + $"Gen{counter++}.cs", SourceText.From(code, Encoding.UTF8));
            }
        }

        private void TryGetAttributeParameter<T>(Compilation compilation, FieldDeclarationSyntax fieldNode) where T : Attribute
        {
            var model = compilation.GetSemanticModel(fieldNode.SyntaxTree);
            var subscriptionAttribute = fieldNode.AttributeLists
                .SelectMany(a => a.Attributes)
                .FirstOrDefault(attr =>
                {
                    var symbol = model.GetSymbolInfo(attr).Symbol;
                    var name = symbol?.ContainingType?.Name;
                    return name == nameof(T);
                });

            if (subscriptionAttribute != null)
            {
                var interfaceArgument = subscriptionAttribute.ArgumentList?.Arguments.FirstOrDefault(arg =>
                    arg.NameEquals?.Name.Identifier.ValueText == nameof(IAttributeInterface.Interface));

                if (interfaceArgument is { Expression: TypeOfExpressionSyntax typeOfExpression })
                {
                    var typeInfo = model.GetTypeInfo(typeOfExpression.Type);
                    var interfaceTypeSymbol = model.GetTypeInfo(typeOfExpression.Type).Type;

                    if (interfaceTypeSymbol != null)
                    {
                        // Now that you have the interface type symbol, you can generate the partial interface
                        // Here you would add the code to generate the partial interface based on the interfaceTypeSymbol
                        // GeneratePartialInterface(interfaceTypeSymbol, context);
                    }
                }
            }
        }

        /// <summary>
        /// Create the disposable subscription method for the given event name and field name
        /// </summary>
        /// <param name="fieldType"></param>
        /// <param name="subscriptionMethodName"></param>
        /// <param name="fieldName"></param>
        /// <param name="eventName"></param>
        /// <param name="isStatic"></param>
        /// <returns></returns>
        private static MethodDeclarationSyntax CreateDisposableSubscriptionMethod(
            TypeSyntax fieldType,
            string subscriptionMethodName,
            string fieldName,
            string eventName,
            bool isStatic)
        {
            var modifiers = isStatic
                ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword))
                : SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
            var eventHandlerTypeName = isStatic ? "Action" : "EventHandler";
            var eventHandlerTypeArgumentList = SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(fieldType));
            var eventHandlerType = SyntaxFactory.GenericName(SyntaxFactory.Identifier(eventHandlerTypeName))
                .WithTypeArgumentList(eventHandlerTypeArgumentList);

            var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier("handler"))
                .WithType(eventHandlerType);

            var eventNameExpression = SyntaxFactory.IdentifierName(eventName);
            var addAssignment = SyntaxFactory.AssignmentExpression(SyntaxKind.AddAssignmentExpression, eventNameExpression, SyntaxFactory.IdentifierName("handler"));
            var removeAssignment =
                SyntaxFactory.AssignmentExpression(SyntaxKind.SubtractAssignmentExpression, eventNameExpression, SyntaxFactory.IdentifierName("handler"));

            var handlerInvokeArguments = isStatic
                ? SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(fieldName))))
                : SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(
                        new[]
                        {
                            SyntaxFactory.Argument(SyntaxFactory.ThisExpression()),
                            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(fieldName))
                        }
                    )
                );

            var handlerInvokeStatement = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.ConditionalAccessExpression(
                    SyntaxFactory.IdentifierName("handler"),
                    SyntaxFactory.InvocationExpression(SyntaxFactory.MemberBindingExpression(SyntaxFactory.IdentifierName("Invoke")))
                        .WithArgumentList(handlerInvokeArguments)));

            var returnStatement = SyntaxFactory.ReturnStatement(
                SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName("DisposeAction"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                        SyntaxFactory.ParenthesizedLambdaExpression(removeAssignment))))));

            var methodBody = SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(addAssignment),
                handlerInvokeStatement,
                returnStatement);

            var methodDeclaration = SyntaxFactory.MethodDeclaration(SyntaxFactory.IdentifierName("IDisposable"), SyntaxFactory.Identifier(subscriptionMethodName))
                .WithModifiers(modifiers)
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(parameter)))
                .WithBody(methodBody);

            return methodDeclaration;
        }

        /// <summary>
        ///  Create the subscription event for the given event name and field name
        /// </summary>
        /// <param name="fieldType"></param>
        /// <param name="subscriptionEventName"></param>
        /// <param name="fieldName"></param>
        /// <param name="privateEventName"></param>
        /// <param name="isStatic"></param>
        /// <returns></returns>
        private static EventDeclarationSyntax CreateSubscriptionEvent(TypeSyntax fieldType, string subscriptionEventName, string fieldName, string privateEventName,
            bool isStatic)
        {
            var genericType = isStatic ? "Action" : "EventHandler";
            // public static or public
            var modifiers = isStatic
                ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword))
                : SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

            //Prepare add and remove statements
            var invokeStatement = isStatic
                ? $"value?.Invoke({fieldName});"
                : $"value?.Invoke(this, {fieldName});";
            var addStatements = SyntaxFactory.List(new StatementSyntax[]
            {
                SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression(invokeStatement)),
                SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression($"{privateEventName} += value"))
            });

            var removeStatements = SyntaxFactory.List(new StatementSyntax[]
            {
                SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression($"{privateEventName} -= value"))
            });

            var addAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.AddAccessorDeclaration)
                .WithBody(SyntaxFactory.Block(addStatements));

            var removeAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.RemoveAccessorDeclaration)
                .WithBody(SyntaxFactory.Block(removeStatements));

            //Buildup the event declaration
            var eventSyntax = SyntaxFactory.EventDeclaration(
                    SyntaxFactory.GenericName(SyntaxFactory.Identifier(genericType))
                        .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                            SyntaxFactory.SingletonSeparatedList(fieldType))),
                    SyntaxFactory.Identifier(subscriptionEventName))
                .WithModifiers(modifiers)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { addAccessor, removeAccessor })));
            return eventSyntax;
        }

        /// <summary>
        /// Create the event field declaration
        /// </summary>
        /// <param name="fieldType">the event parameters type</param>
        /// <param name="eventName">name of generated field</param>
        /// <param name="isStatic">makes event declaration static when true</param>
        /// <returns></returns>
        private static EventFieldDeclarationSyntax CreateEventField(TypeSyntax fieldType, string eventName, bool isStatic = false)
        {
            var eventHandlerType = isStatic ? $"Action<{fieldType}>" : $"EventHandler<{fieldType}>";

            var modifiers = isStatic ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.StaticKeyword)) : SyntaxFactory.TokenList();
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));

            return SyntaxFactory.EventFieldDeclaration(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(eventHandlerType))
                        .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(eventName))))
                .WithModifiers(modifiers);
        }

        /// <summary>
        /// Create the partial void method with given parameter
        /// </summary>
        /// <param name="methodName">the name of generated method</param>
        /// <param name="parameterType">type of generated method parameter</param>
        /// <param name="isStatic">makes method declaration static when true</param>
        /// <returns></returns>
        private static MethodDeclarationSyntax CreatePartialMethod(string methodName, TypeSyntax parameterType, bool isStatic)
        {
            var modifiers = isStatic
                ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.PartialKeyword))
                : SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PartialKeyword));
            return SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), methodName)
                .WithModifiers(modifiers)
                .WithParameterList(
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Parameter(SyntaxFactory.Identifier("newValue")).WithType(parameterType))))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }

        /// <summary>
        /// Create the property declaration with given field name and type.
        /// the property checks the field changes, and invokes OnChanged event and partial class
        /// </summary>
        /// <param name="propertyName"></param>
        /// <param name="fieldName"></param>
        /// <param name="methodCallbackName"></param>
        /// <param name="eventCallbackName"></param>
        /// <param name="fieldType"></param>
        /// <param name="isStatic"></param>
        /// <returns></returns>
        private PropertyDeclarationSyntax CreatePropertyAndCallbacks(
            TypeSyntax fieldType,
            string propertyName,
            string fieldName,
            string methodCallbackName,
            string eventCallbackName,
            bool isStatic)
        {
            var modifiers = SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
            if (isStatic)
                modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));

            var eventInvocation = isStatic ? $"{eventCallbackName}?.Invoke(value);" : $"{eventCallbackName}?.Invoke(this, value)";

            var getAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(
                SyntaxFactory.Block(SyntaxFactory.SingletonList<StatementSyntax>(
                    SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(fieldName)))));

            var setAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithBody(
                SyntaxFactory.Block(
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression, SyntaxFactory.IdentifierName(fieldName), SyntaxFactory.IdentifierName("value")),
                        SyntaxFactory.Block(
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName(fieldName), SyntaxFactory.IdentifierName("value"))),
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression(eventInvocation)),
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(methodCallbackName),
                                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("value"))))))
                        )
                    )
                )
            );

            var propertyDeclaration = SyntaxFactory.PropertyDeclaration(fieldType, propertyName)
                .WithModifiers(modifiers)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { getAccessor, setAccessor })));

            return propertyDeclaration;
        }
    }
}