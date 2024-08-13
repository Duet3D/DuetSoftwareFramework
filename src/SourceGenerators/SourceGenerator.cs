using Microsoft.CodeAnalysis;
using SourceGenerators.ObjectModel;

namespace SourceGenerators
{
    /// <summary>
    /// Main source code generator to generate fast assign/clone/JSON update calls for all the object model files
    /// </summary>
    [Generator]
    public class SourceGenerators : ISourceGenerator
    {
        /// <summary>
        /// Initialize the source generator
        /// </summary>
        /// <param name="context">Context</param>
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new SourceGeneratorSyntaxReceiver());
        }

        /// <summary>
        /// Execute the source generator
        /// </summary>
        /// <param name="context">Context</param>
        public void Execute(GeneratorExecutionContext context)
        {
            (context.SyntaxReceiver as SourceGeneratorSyntaxReceiver)!.Prepare();
            ObjectModelGenerator.Execute(context);
            ModelObjectGenerator.Execute(context);
        }
    }
}
