using System;
using System.Collections.Generic;
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
        private static readonly Dictionary<string, string> _loadedXmlDocumentation = new();

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
                    string rawName = xmlReader["name"];
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
        private static string XmlDocumentationKeyHelper(string typeFullNameString, string memberNameString)
        {
            string key = Regex.Replace(typeFullNameString, @"\[.*\]", string.Empty).Replace('+', '.');
            if (memberNameString != null)
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
        private static string TrimLines(this string text)
        {
            if (text == null)
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
        /// Convert an XML-based documentation node into human-readable markup text
        /// </summary>
        /// <param name="xmlContent">XML node content</param>
        /// <returns>Content formatted in markup language</returns>
        private static string GenerateDocFromXml(string xmlContent)
        {
            string summary = null, remarks = null;
            XmlDocument xmlDocument = new();
            xmlDocument.LoadXml(xmlContent);
            foreach (XmlNode node in xmlDocument.FirstChild)
            {
                if (node.NodeType == XmlNodeType.Element)
                {
                    if (node.Name == "summary")
                    {
                        summary = node.InnerText.TrimLines();
                    }
                    else if (node.Name == "remarks")
                    {
                        remarks = node.InnerText.TrimLines();
                    }
                }
            }

            if (summary == null)
            {
                return null;
            }
            if (remarks == null)
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
        public static string GetDocumentation(this Type type)
        {
            string key = "T:" + XmlDocumentationKeyHelper(type.FullName, null);
            if (_loadedXmlDocumentation.TryGetValue(key, out string documentation))
            {
                return GenerateDocFromXml(documentation);
            }
            return null;
        }

        /// <summary>
        /// Attached method to retrieve the XML documentation for a particular property
        /// </summary>
        /// <param name="propertyInfo">Property info</param>
        /// <returns>XML documentation</returns>
        public static string GetDocumentation(this PropertyInfo propertyInfo)
        {
            string key = "P:" + XmlDocumentationKeyHelper(propertyInfo.DeclaringType.FullName, propertyInfo.Name);
            if (_loadedXmlDocumentation.TryGetValue(key, out string documentation))
            {
                return GenerateDocFromXml(documentation);
            }
            return null;
        }

        public static string GetEnumDocumentation(Type enumType, object value)
        {
            string key = "F:" + XmlDocumentationKeyHelper(enumType.FullName, Enum.GetName(enumType, value));
            if (_loadedXmlDocumentation.TryGetValue(key, out string documentation))
            {
                return GenerateDocFromXml(documentation);
            }
            return null;
        }
    }
}
