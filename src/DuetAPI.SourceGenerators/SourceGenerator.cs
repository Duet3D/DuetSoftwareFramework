using Microsoft.CodeAnalysis;
using DuetAPI.SourceGenerators.ObjectModel;
using DuetAPI.SourceGenerators.ObjectModel.ModelObject;

namespace DuetAPI.SourceGenerators
{
    /// <summary>
    /// Main source code generator to generate fast assign/clone/JSON update calls for all the object model files
    /// </summary>
    [Generator]
    public class Generators : ISourceGenerator
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
            ObjectModel.Generator.Execute(context);
            ObjectModel.ModelObject.Generator.Execute(context);
        }
    }
}
