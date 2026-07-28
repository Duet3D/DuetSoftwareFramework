using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.SourceGenerators;

internal class SourceGeneratorSyntaxReceiver : ISyntaxReceiver
{
    public List<string> Enums { get; } = [];

    public Dictionary<string, List<PropertyDeclarationSyntax>> CommandMembers { get; } = [];

    public Dictionary<string, List<PropertyDeclarationSyntax>> ModelObjectMembers { get; } = [];

    public Dictionary<string, List<MethodDeclarationSyntax>> ModelObjectMethods { get; } = [];

    public Dictionary<ClassDeclarationSyntax, string> InheritedClasses { get; } = [];

    public List<string> DynamicModelObjectClasses { get; } = [];

    public Dictionary<string, Location> IncompleteModelObjectClasses { get; } = [];

    public Dictionary<string, List<PropertyDeclarationSyntax>> ModelCollectionMembers { get; } = [];

    public Dictionary<string, string> ModelCollectionItemTypes { get; } = [];

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
                    if (member is PropertyDeclarationSyntax pds && !pds.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == "JsonIgnore")))
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
                if (cds.BaseList.Types.Any(type => type.Type is IdentifierNameSyntax ins && ins.Identifier.ValueText == "Command") ||
                    cds.BaseList.Types.Any(type => type.Type is GenericNameSyntax gns && gns.Identifier.ValueText == "Command"))
                {
                    List<PropertyDeclarationSyntax> members = [];
                    foreach (MemberDeclarationSyntax member in cds.Members)
                    {
                        if (member is PropertyDeclarationSyntax pds && !pds.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == "JsonIgnore")))
                        {
                            members.Add(pds);
                        }
                    }
                    CommandMembers.Add(cds.Identifier.ValueText, members);
                }
                else if (cds.BaseList.Types.Any(type => type.Type is IdentifierNameSyntax ins && ins.Identifier.ValueText == "ModelObject"))
                {
                    var membersAndMethods = GetClassMembersAndMethods();
                    ModelObjectMembers.Add(cds.Identifier.ValueText, membersAndMethods.Item1);
                    ModelObjectMethods.Add(cds.Identifier.ValueText, membersAndMethods.Item2);
                    if (cds.BaseList.Types.Any(type => type.Type is IdentifierNameSyntax ins && ins.Identifier.ValueText == "IDynamicModelObject"))
                    {
                        DynamicModelObjectClasses.Add(cds.Identifier.ValueText);
                    }
                    else if (!cds.BaseList.Types.Any(type => type.Type is IdentifierNameSyntax ins && ins.Identifier.ValueText == "IStaticModelObject"))
                    {
                        IncompleteModelObjectClasses.Add(cds.Identifier.ValueText, cds.GetLocation());
                    }
                }
                else if (cds.BaseList.Types.Any(type => type.Type is GenericNameSyntax gns && gns.Identifier.ValueText is "DynamicModelCollection" or "StaticModelCollection"))
                {
                    var membersAndMethods = GetClassMembersAndMethods();
                    ModelCollectionMembers.Add(cds.Identifier.ValueText, membersAndMethods.Item1);

                    GenericNameSyntax baseCollection = cds.BaseList.Types.Select(type => type.Type).OfType<GenericNameSyntax>().First(gns => gns.Identifier.ValueText is "DynamicModelCollection" or "StaticModelCollection");
                    ModelCollectionItemTypes[cds.Identifier.ValueText] = baseCollection.TypeArgumentList.Arguments[0].ToString().TrimEnd('?');
                }
                else if (cds.BaseList.Types.Any() && !InheritedClasses.ContainsKey(cds))
                {
                    InheritedClasses.Add(cds, cds.BaseList.Types.First().Type.ToString());
                }
            }
        }
        else if (syntaxNode is EnumDeclarationSyntax eds)
        {
            Enums.Add(eds.Identifier.ValueText);
        }
    }

    public void Prepare()
    {
        // Some model objects are inherited from other ones. We need to find these as well to ensure code is generated for them as well
        bool classesUpdated;
        do
        {
            classesUpdated = false;
            foreach (var item in InheritedClasses)
            {
                Tuple<List<PropertyDeclarationSyntax>, List<MethodDeclarationSyntax>> GetClassMembersAndMethods()
                {
                    List<PropertyDeclarationSyntax> members = [.. ModelObjectMembers[item.Value]];
                    List<MethodDeclarationSyntax> methods = [.. ModelObjectMethods[item.Value]];
                    foreach (MemberDeclarationSyntax member in item.Key.Members)
                    {
                        if (member is PropertyDeclarationSyntax pds && !pds.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == "JsonIgnore")))
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

                if (item.Key.BaseList != null)
                {
                    if (ModelObjectMembers.ContainsKey(item.Value) && !ModelObjectMembers.ContainsKey(item.Key.Identifier.ValueText))
                    {
                        var membersAndMethods = GetClassMembersAndMethods();
                        ModelObjectMembers.Add(item.Key.Identifier.ValueText, membersAndMethods.Item1);
                        ModelObjectMethods.Add(item.Key.Identifier.ValueText, membersAndMethods.Item2);
                        if (DynamicModelObjectClasses.Contains(item.Value))
                        {
                            DynamicModelObjectClasses.Add(item.Key.Identifier.ValueText);
                        }
                        classesUpdated = true;
                    }
                    else if (ModelCollectionMembers.ContainsKey(item.Value) && !ModelCollectionMembers.ContainsKey(item.Key.Identifier.ValueText))
                    {
                        var membersAndMethods = GetClassMembersAndMethods();
                        ModelCollectionMembers.Add(item.Key.Identifier.ValueText, membersAndMethods.Item1);
                        if (ModelCollectionItemTypes.TryGetValue(item.Value, out string? itemType))
                        {
                            ModelCollectionItemTypes[item.Key.Identifier.ValueText] = itemType;
                        }
                        classesUpdated = true;
                    }
                }
            }
        } while (classesUpdated);
    }
}
