using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SourceGenerators
{
    internal class SourceGeneratorSyntaxReceiver : ISyntaxReceiver
    {
        public List<string> Enums { get; } = [];

        public Dictionary<string, List<PropertyDeclarationSyntax>> ModelObjectMembers { get; } = [];

        public Dictionary<string, List<MethodDeclarationSyntax>> ModelObjectMethods { get; } = [];

        public List<string> DynamicModelObjectClasses { get; } = [];

        public Dictionary<string, List<PropertyDeclarationSyntax>> ModelCollectionMembers { get; } = [];

        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
        {
            if (syntaxNode is ClassDeclarationSyntax cds)
            {
                Tuple<List<PropertyDeclarationSyntax>, List<MethodDeclarationSyntax>> GetClassMembersAndMethods()
                {
                    List<PropertyDeclarationSyntax> members = [];
                    List<MethodDeclarationSyntax> methods = [];
                    foreach (MemberDeclarationSyntax member in cds.Members)
                    {
                        if (member is PropertyDeclarationSyntax pds)
                        {
                            members.Add(pds);
                        }
                        else if (member is MethodDeclarationSyntax mds)
                        {
                            methods.Add(mds);
                        }
                    }
                    return Tuple.Create(members, methods);
                }

                if (cds.BaseList != null)
                {
                    if (cds.BaseList.Types.Any(type => type.Type is IdentifierNameSyntax ins && ins.Identifier.ValueText == "ModelObject"))
                    {
                        var membersAndMethods = GetClassMembersAndMethods();
                        ModelObjectMembers.Add(cds.Identifier.ValueText, membersAndMethods.Item1);
                        ModelObjectMethods.Add(cds.Identifier.ValueText, membersAndMethods.Item2);
                        if (cds.BaseList.Types.Any(type => type.Type is IdentifierNameSyntax ins && ins.Identifier.ValueText == "IDynamicModelObject"))
                        {
                            DynamicModelObjectClasses.Add(cds.Identifier.ValueText);
                        }
                    }
                    else if (cds.BaseList.Types.Any(type => type.Type is GenericNameSyntax gns && gns.Identifier.ValueText == "ModelCollection"))
                    {
                        var membersAndMethods = GetClassMembersAndMethods();
                        ModelCollectionMembers.Add(cds.Identifier.ValueText, membersAndMethods.Item1);
                    }
                }
            }
            else if (syntaxNode is EnumDeclarationSyntax eds)
            {
                Enums.Add(eds.Identifier.ValueText);
            }
        }
    }
}
