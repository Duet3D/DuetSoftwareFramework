using DocGen;
using DuetAPI.ObjectModel;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Version of this application
/// </summary>
string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

/// <summary>
/// List of available types from the DuetAPI library
/// </summary>
Type[] apiTypes = Assembly.GetAssembly(typeof(ObjectModel))!.GetTypes();

/// <summary>
/// Minimum markup indentation level
/// </summary>
const int MinDepth = 2;

/// <summary>
/// Maximum markup indentation level
/// </summary>
const int MaxDepth = 5;

Console.WriteLine("Documentation generator for DSF object model v{0}", Version);
Console.WriteLine("Written by Christian Hammacher for Duet3D");
Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
Console.WriteLine();

// Load the XML documentation
Console.Write("Loading XML documentation... ");
try
{
    await XMLHelper.Init("../DuetAPI/DuetAPI.xml");
    Console.WriteLine("Done!");
}
catch (Exception e)
{
    Console.WriteLine("Error: {0}", e.Message);
    return;
}

// Generate the documentation page
Console.Write("Generating documentation.md... ");
try
{
    await using FileStream fs = new("./documentation.md", FileMode.Create, FileAccess.Write);

    // Copy header
    await using (FileStream headerFs = new("./header.md", FileMode.Open, FileAccess.Read))
    {
        await headerFs.CopyToAsync(fs);
    }

    // Generate documentation
    await using (StreamWriter writer = new(fs, leaveOpen: true))
    {
        await WriteDocumentation(writer);
    }

    // Copy footer
    await using (FileStream footerFs = new("./footer.md", FileMode.Open, FileAccess.Read))
    {
        await footerFs.CopyToAsync(fs);
    }

    Console.WriteLine("Done!");
}
catch (Exception e)
{
    Console.WriteLine("Error: {0}", e.Message);
}

/// <summary>
/// Helper function to write the object model documentation
/// </summary>
/// <param name="writer">Stream writer</param>
/// <returns>Asynchronous task</returns>
async Task WriteDocumentation(StreamWriter writer)
{
    PropertyInfo[] mainProperties = typeof(ObjectModel).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    foreach (PropertyInfo property in mainProperties)
    {
        await WritePropertyDocumentation(writer, property, null, null, MinDepth);
    }
}

/// <summary>
/// Write documentation for a property
/// </summary>
/// <param name="writer">Writer to use</param>
/// <param name="property">Property to document</param>
/// <param name="path">Path to the property</param>
/// <param name="classDescription">Description of the class or null if not applicable</param>
/// <param name="depth">Current indentation depth</param>
/// <returns>Asynchronous task</returns>
async Task WritePropertyDocumentation(StreamWriter writer, PropertyInfo property, string? path, string? classDescription, int depth)
{
    if (depth > MaxDepth)
    {
        depth = MaxDepth;
    }

    string? documentation = property.GetDocumentation();
    if (!string.IsNullOrEmpty(documentation))
    {
        string indentation = string.Empty;
        for (int i = 0; i < depth; i++)
        {
            indentation += '#';
        }

        // Write title
        string propertyName = (path is not null) ? $"{path}." : string.Empty;
        propertyName += JsonNamingPolicy.CamelCase.ConvertName(property.Name);
        if (typeof(IEnumerable).IsAssignableFrom(property.PropertyType) &&
            property.PropertyType != typeof(string) &&
            (!property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(ModelDictionary<>)))
        {
            propertyName += "[]";
        }

        if (classDescription is null)
        {
            await writer.WriteLineAsync($"{indentation} {propertyName}");
        }
        else
        {
            await writer.WriteLineAsync($"{indentation} {propertyName} ({classDescription})");
        }

        // Write node documentation
        bool writeNL = false;
        if (Attribute.IsDefined(property, typeof(ObsoleteAttribute)))
        {
            ObsoleteAttribute attribute = (ObsoleteAttribute)Attribute.GetCustomAttribute(property, typeof(ObsoleteAttribute))!;
            if (string.IsNullOrWhiteSpace(attribute.Message))
            {
                await writer.WriteLineAsync("*This field is obsolete and will be removed from the object model in the future*");
            }
            else
            {
                await writer.WriteLineAsync($"*This field is obsolete and will be removed in the future: {attribute.Message}*");
            }
            writeNL = true;
        }
        if (Attribute.IsDefined(property, typeof(SbcPropertyAttribute)))
        {
            SbcPropertyAttribute attribute = (SbcPropertyAttribute)Attribute.GetCustomAttribute(property, typeof(SbcPropertyAttribute))!;
            if (attribute.AvailableInStandaloneMode)
            {
                await writer.WriteLineAsync("*This field is maintained by DSF in SBC mode*");
            }
            else
            {
                await writer.WriteLineAsync("*This field is exclusively maintained by DSF in SBC mode and/or by DWC. It is not available in standalone mode*");
            }
            writeNL = true;
        }
        if (Attribute.IsDefined(property, typeof(LimitedResponseCountAttribute)))
        {
            LimitedResponseCountAttribute attribute = (LimitedResponseCountAttribute)Attribute.GetCustomAttribute(property, typeof(LimitedResponseCountAttribute))!;
            await writer.WriteLineAsync($"*Standard model responses return up to {attribute.MaxCount} elements of this field. It may be necessary to request more of this field using the 'a' flag.*");
            writeNL = true;
        }
        if (writeNL)
        {
            await writer.WriteLineAsync();
        }
        await writer.WriteLineAsync(documentation);
        await writer.WriteLineAsync();

        Type baseType = property.PropertyType.IsGenericType ? property.PropertyType.GetGenericArguments()[0] : property.PropertyType;
        if (baseType.IsEnum)
        {
            // Write enum values
            if (Attribute.IsDefined(property, typeof(FlagsAttribute)))
            {
                await writer.WriteLineAsync("This property may be a combination of the following:");
            }
            else
            {
                await writer.WriteLineAsync("This property may be one of the following:");
            }

            Array possibleValues = Enum.GetValues(baseType);
            foreach (object value in possibleValues)
            {
                string jsonValue = JsonSerializer.Serialize(value, baseType).Trim('"', '[', ']');
                if (!string.IsNullOrEmpty(jsonValue))
                {
                    string? memberDocs = XMLHelper.GetEnumDocumentation(baseType, value);
                    await writer.WriteLineAsync($"- {jsonValue}: {memberDocs}");
                }
            }
            await writer.WriteLineAsync();
        }
        else
        {
            // Write documentation for (inherited) types
            Type[] relatedTypes;
            if (baseType == typeof(Inputs))
            {
                // Inputs is a pseudo-list so it requires special treatment
                relatedTypes = new Type[] { typeof(InputChannel) };
            }
            else if (baseType == typeof(Message))
            {
                // Message is not inherited from ModelObject
                relatedTypes = new Type[] { typeof(Message) };
            }
            else if (baseType == typeof(Plugin))
            {
                // Instead of this we could check for base classes as well but so far Plugin is an exception
                relatedTypes = new Type[] { typeof(PluginManifest), typeof(Plugin) };
            }
            else
            {
                // Find related types
                relatedTypes = apiTypes.Where(type => baseType.IsSubclassOf(typeof(ModelObject)) && baseType.IsAssignableFrom(type)).ToArray();
            }

            if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(ModelDictionary<>))
            {
                propertyName += @"\{\}";
            }

            if (relatedTypes.Length == 1)
            {
                // Need to document the type for inputs[] as well...
                await WriteTypeDocumentation(writer, relatedTypes[0], propertyName, classDescription, depth + 1, false);
            }
            else
            {
                foreach (Type type in relatedTypes)
                {
                    // Only write plain type documentation if this property type does not directly inherit from ModelObject
                    classDescription = (type.BaseType != typeof(ModelObject)) ? $"{type.Name} : {type.BaseType!.Name}" : type.Name;
                    await WriteTypeDocumentation(writer, type, propertyName, classDescription, depth + 1, type.BaseType != typeof(ModelObject));
                }
            }
        }
    }
}

/// <summary>
/// Write documentation for an item type
/// </summary>
/// <param name="writer">Writer to use</param>
/// <param name="type">Type to document</param>
/// <param name="path">Path to the property</param>
/// <param name="classDescription">Description of the class or null if not applicable</param>
/// <param name="depth">Current indentation depth</param>
/// <param name="writeTypeDocs">Whether the type should be documented too</param>
/// <returns>Asynchronous task</returns>
async Task WriteTypeDocumentation(StreamWriter writer, Type type, string path, string? classDescription, int depth, bool writeTypeDocs)
{
    if (depth > MaxDepth)
    {
        depth = MaxDepth;
    }

    // Write type documentation only if needed
    if (writeTypeDocs)
    {
        string? documentation = type.GetDocumentation();
        if (!string.IsNullOrEmpty(documentation))
        {
            string indentation = string.Empty;
            for (int i = 0; i < depth; i++)
            {
                indentation += '#';
            }

            if (classDescription is null)
            {
                await writer.WriteLineAsync($"{indentation} {path}");
            }
            else
            {
                await writer.WriteLineAsync($"{indentation} {path} ({classDescription})");
            }
            await writer.WriteLineAsync(documentation);
            await writer.WriteLineAsync();
        }
    }

    // Write documentation for other properties
    PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    foreach (PropertyInfo property in properties)
    {
        await WritePropertyDocumentation(writer, property, path, classDescription, depth + 1);
    }
}

