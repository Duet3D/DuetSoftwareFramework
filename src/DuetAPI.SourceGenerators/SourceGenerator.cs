using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Linq;
using DuetAPI.SourceGenerators.ObjectModel;
using DuetAPI.SourceGenerators.ObjectModel.ModelObject;

namespace DuetAPI.SourceGenerators;

/// <summary>
/// Main source code generator to generate fast assign/clone/JSON update calls for all the object model files
/// </summary>
[Generator]
public class Generators : IIncrementalGenerator
{
    /// <summary>
    /// Initialize the incremental source generator
    /// </summary>
    /// <param name="context">Context</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Create a provider for all class declarations
        var classProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => s is ClassDeclarationSyntax,
                transform: static (ctx, _) => (ClassDeclarationSyntax)ctx.Node)
            .Where(static c => c is not null);

        // Create a provider for all enum declarations
        var enumProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => s is EnumDeclarationSyntax,
                transform: static (ctx, _) => (EnumDeclarationSyntax)ctx.Node)
            .Where(static e => e is not null);

        // Collect all classes and enums
        var allClasses = classProvider.Collect();
        var allEnums = enumProvider.Collect();

        // Combine classes and enums into a single provider
        var combinedProvider = allClasses.Combine(allEnums);

        // Process the syntax and generate code
        context.RegisterSourceOutput(combinedProvider, (spc, source) =>
        {
            var (classes, enums) = source;
            var receiver = new SourceGeneratorSyntaxReceiver();

            // Process all enums
            foreach (var enumDecl in enums)
            {
                receiver.Enums.Add(enumDecl.Identifier.ValueText);
            }

            // Process all classes
            foreach (var classDecl in classes)
            {
                receiver.OnVisitSyntaxNode(classDecl);
            }

            // Prepare the receiver (processes inherited classes)
            receiver.Prepare();

            // Execute the generators
            Commands.Generator.Execute(spc, receiver);
            ObjectModel.Generator.Execute(spc, receiver);
            ObjectModel.ModelObject.Generator.Execute(spc, receiver);
        });
    }
}
