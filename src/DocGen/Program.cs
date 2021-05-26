using DuetAPI.ObjectModel;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DocGen
{
    /// <summary>
    /// Main program class
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Version of this application
        /// </summary>
        public static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

        /// <summary>
        /// List of available types from the DuetAPI library
        /// </summary>
        private static readonly Type[] apiTypes = Assembly.GetAssembly(typeof(ObjectModel)).GetTypes();

        /// <summary>
        /// Minimum markup indentation level
        /// </summary>
        private const int MinDepth = 2;

        /// <summary>
        /// Maximum markup indentation level
        /// </summary>
        private const int MaxDepth = 5;

        /// <summary>
        /// Entry point of this application
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        static async Task Main(string[] args)
        {
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
                using FileStream fs = new("./documentation.md", FileMode.Create, FileAccess.Write);

                // Copy header
                using (FileStream headerFs = new("./header.md", FileMode.Open, FileAccess.Read))
                {
                    await headerFs.CopyToAsync(fs);
                }

                // Generate documentation
                using (StreamWriter writer = new(fs, leaveOpen: true))
                {
                    await WriteDocumentation(writer);
                }

                // Copy footer
                using (FileStream footerFs = new("./footer.md", FileMode.Open, FileAccess.Read))
                {
                    await footerFs.CopyToAsync(fs);
                }

                Console.WriteLine("Done!");
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: {0}", e.Message);
            }
        }

        /// <summary>
        /// Helper function to write the object model documentation
        /// </summary>
        /// <param name="writer">Stream writer</param>
        /// <returns>Asynchronous task</returns>
        private static async Task WriteDocumentation(StreamWriter writer)
        {
            PropertyInfo[] mainProperties = typeof(ObjectModel).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (PropertyInfo property in mainProperties)
            {
                await WritePropertyDocumentation(writer, property, null, null, MinDepth);
            }
        }

        private static async Task WritePropertyDocumentation(StreamWriter writer, PropertyInfo property, string prefix, string suffix, int depth)
        {
            if (Attribute.IsDefined(property, typeof(JsonIgnoreAttribute)))
            {
                return;
            }
            if (depth > MaxDepth)
            {
                depth = MaxDepth;
            }

            string documentation = property.GetDocumentation();
            if (!string.IsNullOrEmpty(documentation))
            {
                string indentation = string.Empty;
                for (int i = 0; i < depth; i++)
                {
                    indentation += '#';
                }

                // Write title
                string propertyName = (prefix != null) ? $"{prefix}." : string.Empty;
                propertyName += JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                if (typeof(IEnumerable).IsAssignableFrom(property.PropertyType) && property.PropertyType != typeof(string))
                {
                    propertyName += "[]";
                }
                if (suffix == null)
                {
                    await writer.WriteLineAsync($"{indentation} {propertyName}");
                }
                else
                {
                    await writer.WriteLineAsync($"{indentation} {propertyName} ({suffix})");
                }

                // Write node documentation
                if (Attribute.IsDefined(property, typeof(LinuxPropertyAttribute)))
                {
                    await writer.WriteLineAsync("*This field is maintained by DSF in SBC mode and might not be available in standalone mode*");
                    await writer.WriteLineAsync();
                }
                await writer.WriteLineAsync(documentation);
                await writer.WriteLineAsync();

                // Write documentation for (inherited) types
                Type baseType = property.PropertyType.IsGenericType ? property.PropertyType.GetGenericArguments()[0] : property.PropertyType;
                Type[] relatedTypes = apiTypes.Where(type => baseType.IsSubclassOf(typeof(ModelObject)) && baseType.IsAssignableFrom(type)).ToArray();
                if (baseType == typeof(Plugin))
                {
                    // Instead of this we could check for base classes as well but so far Plugin is an exception
                    relatedTypes = new Type[] { typeof(PluginManifest), typeof(Plugin) };
                }

                if (relatedTypes.Length == 1)
                {
                    await WriteTypeDocumentation(writer, relatedTypes[0], propertyName, suffix, depth + 1);
                }
                else
                {
                    foreach (Type type in relatedTypes)
                    {
                        await WriteTypeDocumentation(writer, type, propertyName, type.Name, depth + 1);
                    }
                }
            }
        }

        private static async Task WriteTypeDocumentation(StreamWriter writer, Type type, string prefix, string suffix, int depth)
        {
            if (depth > MaxDepth)
            {
                depth = MaxDepth;
            }

            // Write type documentation only for minimum depth, because the property field already describes the type
            if (depth == MinDepth || suffix != null)
            {
                string documentation = type.GetDocumentation();
                if (!string.IsNullOrEmpty(documentation))
                {
                    string indentation = string.Empty;
                    for (int i = 0; i < depth; i++)
                    {
                        indentation += '#';
                    }

                    if (suffix == null)
                    {
                        await writer.WriteLineAsync($"{indentation} {prefix}");
                    }
                    else
                    {
                        await writer.WriteLineAsync($"{indentation} {prefix} ({suffix})");
                    }
                    await writer.WriteLineAsync(documentation);
                    await writer.WriteLineAsync();
                }
            }

            // Write documentation for other properties
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (PropertyInfo property in properties)
            {
                await WritePropertyDocumentation(writer, property, prefix, suffix, depth + 1);
            }
        }
    }
}
