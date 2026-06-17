using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace DocGen
{
    /// <summary>
    /// Helper class for reading the XML documentation file
    /// </summary>
    /// <remarks>
    /// See https://docs.microsoft.com/en-us/archive/msdn-magazine/2019/october/csharp-accessing-xml-documentation-via-reflection
    /// </remarks>
    public static class XMLHelper
    {
        /// <summary>
        /// Dictionary holding member names vs. documentation content
        /// </summary>
        private static readonly Dictionary<string, string> _loadedXmlDocumentation = [];

        /// <summary>
        /// Initialize the XML documentation
        /// </summary>
        /// <param name="xmlDocumentation">XML documentation filename</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Init(string xmlDocumentation)
        {
            await using FileStream fs = new(xmlDocumentation, FileMode.Open, FileAccess.Read);
            using XmlReader xmlReader = XmlReader.Create(fs, new() { Async = true });
            while (await xmlReader.ReadAsync())
            {
                if (xmlReader.NodeType == XmlNodeType.Element && xmlReader.Name == "member")
                {
                    string rawName = xmlReader["name"]!;
                    _loadedXmlDocumentation[rawName] = await xmlReader.ReadOuterXmlAsync();
                }
            }
        }

        /// <summary>
        /// Helper function to retrieve the key name for the XML documentation
        /// </summary>
        /// <param name="typeFullNameString">Full type name</param>
        /// <param name="memberNameString">Name of the member</param>
        /// <returns>XML key</returns>
        private static string XmlDocumentationKeyHelper(string typeFullNameString, string? memberNameString)
        {
            string key = Regex.Replace(typeFullNameString, @"\[.*\]", string.Empty).Replace('+', '.');
            if (memberNameString is not null)
            {
                key += "." + memberNameString;
            }
            return key;
        }

        /// <summary>
        /// Trim every line of this text
        /// </summary>
        /// <param name="text">Text to trim</param>
        /// <returns>Trimmed text</returns>
        [return: NotNullIfNotNull(nameof(text))]
        private static string? TrimLines(this string text)
        {
            if (text is null)
            {
                return null;
            }

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = lines[i].Trim();
            }
            return string.Join(Environment.NewLine, lines).Trim();
        }

        /// <summary>
        /// Regular expression to find and replace "see cref" instances
        /// </summary>
        private static readonly Regex seeRegex = new("<see cref=\\\"(?:\\w:)?(?:.*\\.)+?(.*)\"\\s*/>", RegexOptions.Compiled);

        /// <summary>
        /// Maximum number of &lt;inheritdoc/&gt; references to follow before giving up
        /// </summary>
        private const int MaxInheritDepth = 8;

        /// <summary>
        /// Build the XML documentation key (doc ID) for a reflected member
        /// </summary>
        /// <param name="member">Member to build the key for</param>
        /// <returns>Documentation key or null if the member kind is unsupported</returns>
        private static string? GetDocumentationKey(MemberInfo member)
        {
            if (member is Type type)
            {
                return "T:" + XmlDocumentationKeyHelper(type.FullName ?? type.Name, null);
            }
            if (member is PropertyInfo property)
            {
                return "P:" + XmlDocumentationKeyHelper(property.DeclaringType!.FullName!, property.Name);
            }
            return null;
        }

        /// <summary>
        /// Enumerate the base type and interface members a member may inherit documentation from.
        /// Base types are yielded before interfaces to match the C# &lt;inheritdoc/&gt; resolution order
        /// </summary>
        /// <param name="member">Member whose documentation uses &lt;inheritdoc/&gt;</param>
        /// <returns>Candidate members to inherit documentation from</returns>
        private static IEnumerable<MemberInfo> GetInheritanceCandidates(MemberInfo member)
        {
            if (member is Type type)
            {
                if (type.BaseType is not null && type.BaseType != typeof(object))
                {
                    yield return type.BaseType;
                }
                foreach (Type implementedInterface in type.GetInterfaces())
                {
                    yield return implementedInterface;
                }
            }
            else if (member is PropertyInfo property && property.DeclaringType is not null)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                for (Type? baseType = property.DeclaringType.BaseType; baseType is not null && baseType != typeof(object); baseType = baseType.BaseType)
                {
                    PropertyInfo? baseProperty = baseType.GetProperty(property.Name, flags);
                    if (baseProperty is not null)
                    {
                        yield return baseProperty;
                    }
                }
                foreach (Type implementedInterface in property.DeclaringType.GetInterfaces())
                {
                    PropertyInfo? interfaceProperty = implementedInterface.GetProperty(property.Name);
                    if (interfaceProperty is not null)
                    {
                        yield return interfaceProperty;
                    }
                }
            }
        }

        /// <summary>
        /// Resolve the summary and remarks of a documented member, following &lt;inheritdoc/&gt; references.
        /// When a member only carries &lt;inheritdoc/&gt; (optionally with a cref), the documentation of the
        /// referenced or inherited member is used instead
        /// </summary>
        /// <param name="documentationKey">Documentation key of the member</param>
        /// <param name="member">Reflected member, used to walk base types and interfaces (null for cref targets)</param>
        /// <param name="depth">Current inheritance depth</param>
        /// <returns>Resolved summary and remarks</returns>
        private static (string? Summary, string? Remarks) ResolveSummaryAndRemarks(string? documentationKey, MemberInfo? member, int depth)
        {
            if (depth > MaxInheritDepth || documentationKey is null ||
                !_loadedXmlDocumentation.TryGetValue(documentationKey, out string? xmlContent))
            {
                return (null, null);
            }

            string? summary = null, remarks = null, inheritCref = null;
            bool hasInheritDoc = false;
            XmlDocument xmlDocument = new();
            xmlDocument.LoadXml(xmlContent);
            foreach (XmlNode node in xmlDocument.FirstChild!)
            {
                if (node.NodeType == XmlNodeType.Element)
                {
                    if (node.Name == "summary")
                    {
                        summary = node.InnerXml.TrimLines();
                    }
                    else if (node.Name == "remarks")
                    {
                        remarks = node.InnerXml.TrimLines();
                    }
                    else if (node.Name == "inheritdoc")
                    {
                        hasInheritDoc = true;
                        inheritCref = node.Attributes?["cref"]?.Value;
                    }
                }
            }

            // Follow <inheritdoc/> only when the member does not document its own summary
            if (summary is null && hasInheritDoc)
            {
                if (inheritCref is not null)
                {
                    // The compiler emits crefs as fully-qualified documentation keys, so look them up directly
                    return ResolveSummaryAndRemarks(inheritCref, null, depth + 1);
                }
                if (member is not null)
                {
                    foreach (MemberInfo candidate in GetInheritanceCandidates(member))
                    {
                        (string? inheritedSummary, string? inheritedRemarks) = ResolveSummaryAndRemarks(GetDocumentationKey(candidate), candidate, depth + 1);
                        if (inheritedSummary is not null)
                        {
                            return (inheritedSummary, remarks ?? inheritedRemarks);
                        }
                    }
                }
            }

            return (summary, remarks);
        }

        /// <summary>
        /// Format a resolved summary and remarks into human-readable markup text
        /// </summary>
        /// <param name="summary">Resolved summary or null</param>
        /// <param name="remarks">Resolved remarks or null</param>
        /// <returns>Content formatted in markup language</returns>
        private static string? FormatDocumentation(string? summary, string? remarks)
        {
            if (summary is null)
            {
                return null;
            }
            summary = seeRegex.Replace(summary, "$1");
            if (remarks is null)
            {
                return summary;
            }

            StringBuilder builder = new();
            builder.AppendLine(summary);
            builder.AppendLine();
            builder.Append("*Note:* ");
            builder.Append(remarks);
            return builder.ToString();
        }

        /// <summary>
        /// Attached method to retrieve the XML documentation for a particular type
        /// </summary>
        /// <param name="type">Instance type</param>
        /// <returns>Documentation string</returns>
        public static string? GetDocumentation(this Type type)
        {
            (string? summary, string? remarks) = ResolveSummaryAndRemarks(GetDocumentationKey(type), type, 0);
            return FormatDocumentation(summary, remarks);
        }

        /// <summary>
        /// Attached method to retrieve the XML documentation for a particular property
        /// </summary>
        /// <param name="propertyInfo">Property info</param>
        /// <returns>XML documentation</returns>
        public static string? GetDocumentation(this PropertyInfo propertyInfo)
        {
            (string? summary, string? remarks) = ResolveSummaryAndRemarks(GetDocumentationKey(propertyInfo), propertyInfo, 0);
            return FormatDocumentation(summary, remarks);
        }

        public static string? GetEnumDocumentation(Type enumType, object value)
        {
            string key = "F:" + XmlDocumentationKeyHelper(enumType.FullName!, Enum.GetName(enumType, value));
            (string? summary, string? remarks) = ResolveSummaryAndRemarks(key, null, 0);
            return FormatDocumentation(summary, remarks);
        }
    }
}
