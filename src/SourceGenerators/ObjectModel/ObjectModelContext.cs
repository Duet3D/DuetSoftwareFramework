using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace SourceGenerators.ObjectModel
{
    internal class ObjectModelContext
    {
        public List<string> ModelObjectClasses { get; } = [];

        public List<string> ModelCollectionClasses { get; } = [];

        public void CheckSyntaxNode(SyntaxNode syntaxNode)
        {
            if (syntaxNode is ClassDeclarationSyntax cds)
            {
                if (cds.BaseList != null)
                {
                    if (cds.BaseList.Types.Any(type => type.Type is IdentifierNameSyntax ins && ins.Identifier.ValueText == "ModelObject"))
                    {
                        ModelObjectClasses.Add(cds.Identifier.ValueText);
                    }
                    else if (cds.BaseList.Types.Any(type => type.Type is GenericNameSyntax gns && gns.Identifier.ValueText == "ModelCollection"))
                    {
                        ModelCollectionClasses.Add(cds.Identifier.ValueText);
                    }
                }
            }
        }
    }
}
