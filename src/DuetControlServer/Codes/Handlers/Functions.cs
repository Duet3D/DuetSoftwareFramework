using DuetAPI;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Handlers
{
    /// <summary>
    /// Class to register SBC-dependent functions
    /// </summary>
    public static class Functions
    {
        /// <summary>
        /// Initializer function to register custom meta G-code functions
        /// </summary>
        public static void Init()
        {
            Model.Expressions.CustomFunctions.Add("exists", Exists);
            Model.Expressions.CustomFunctions.Add("fileexists", FileExists);
            Model.Expressions.CustomFunctions.Add("fileread", FileRead);
        }

        /// <summary>
        /// Implementation for exists() meta G-code call
        /// </summary>
        /// <param name="channel">Code channel</param>
        /// <param name="functionName">Function name</param>
        /// <param name="arguments">Function arguments</param>
        /// <returns>Whether the file exists</returns>
        public static async Task<object?> Exists(CodeChannel channel, string functionName, object?[] arguments)
        {
            if (arguments.Length == 1 && arguments[0] is string stringArgument)
            {
                stringArgument = stringArgument.Trim();
                if (Model.Filter.GetSpecific(stringArgument, true, out _))
                {
                    return true;
                }
                return await SPI.Interface.EvaluateExpression(channel, $"exists({stringArgument})");
            }
            throw new ArgumentException("exists requires an argument");
        }

        /// <summary>
        /// Implementation for fileexists() meta G-code call
        /// </summary>
        /// <param name="channel">Code channel</param>
        /// <param name="functionName">Function name</param>
        /// <param name="arguments">Function arguments</param>
        /// <returns>Whether the file exists</returns>
        public static async Task<object?> FileExists(CodeChannel channel, string functionName, object?[] arguments)
        {
            if (arguments.Length == 1 && arguments[0] is string stringArgument)
            {
                string resolvedPath = await Files.FilePath.ToPhysicalAsync(stringArgument);
                return File.Exists(resolvedPath);
            }
            throw new ArgumentException("fileexists requires a string argument");
        }

        /// <summary>
        /// Implementation for fileread() meta G-code call
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="functionName"></param>
        /// <param name="arguments"></param>
        /// <returns></returns>
        public static async Task<object?> FileRead(CodeChannel channel, string functionName, object?[] arguments)
        {
            if (arguments.Length != 4)
            {
                throw new ArgumentException("fileread requires 4 arguments");
            }
            if (arguments[0] is not string filePath)
            {
                throw new ArgumentException("fileread requires a string parameter for the filename");
            }
            if (arguments[1] is not int offset || offset < 0)
            {
                throw new ArgumentException("fileread requires a non-negative offset");
            }
            if (arguments[2] is not int elementsToRead || elementsToRead <= 0)
            {
                throw new ArgumentException("fileread requires a positive number of elements to read");
            }
            if (elementsToRead > 50)
            {
                throw new ArgumentException("fileread cannot read more than 50 elements at once");
            }
            if (arguments[3] is not char delimiter)
            {
                throw new ArgumentException("fileread requires a delimiter character");
            }

            // Get the first line of the file
            string resolvedPath = await Files.FilePath.ToPhysicalAsync(filePath);
            using StreamReader reader = File.OpenText(resolvedPath);
            string firstLine = await reader.ReadLineAsync() ?? string.Empty;
            if (firstLine.Trim().Length == 0)
            {
                return Array.Empty<object?>();
            }

            // Read the requested contents
            int i = 0, tokenStart = 0;
            object? lastElement = null;
            StringBuilder lastToken = new();
            List<object?> items = new();
            while (i <= firstLine.Length)
            {
                char c = (i < firstLine.Length) ? firstLine[i] : '\0';
                i++;

                if (c == '\'')
                {
                    if (i >= firstLine.Length) throw new ArgumentException($"in fileread() function: expected character after column {tokenStart}");
                    lastElement = firstLine[i++];
                    if (i++ >= firstLine.Length) throw new ArgumentException($"in fileread() function: unterminated single quote at column {tokenStart}");
                }
                else if (c == '\"')
                {
                    if (lastToken.Length > 0)
                    {
                        throw new ArgumentException($"in fileread() function: format error at column {tokenStart}");
                    }

                    while (true)
                    {
                        if (i >= firstLine.Length)
                        {
                            throw new ArgumentException($"in fileread() function: unterminated string after column {tokenStart}");
                        }

                        c = firstLine[i++];
                        if (c == '"')
                        {
                            if (i + 1 < firstLine.Length && firstLine[i + 1] == '"')
                            {
                                // escaped double-quote
                                lastToken.Append('"');
                            }
                            else
                            {
                                // end of string
                                break;
                            }
                        }
                        else
                        {
                            lastToken.Append(c);
                        }
                    }
                    lastElement = lastToken.ToString();
                    lastToken.Clear();
                }
                else if (c == delimiter || c == '\0')
                {
                    if (offset > 0)
                    {
                        offset--;
                        lastElement = null;
                        lastToken.Clear();
                    }
                    else if (lastElement is not null)
                    {
                        items.Add(lastElement);
                        lastElement = null;
                    }
                    else
                    {
                        if (lastToken.Length > 0)
                        {
                            // Check if it is a number
                            string stringToken = lastToken.ToString();
                            if (stringToken[0] == '+' || stringToken[0] == '-' || char.IsDigit(stringToken[0]))
                            {
                                if (int.TryParse(stringToken, NumberStyles.Any, CultureInfo.InvariantCulture, out int intValue))
                                {
                                    items.Add(intValue);
                                }
                                else if (float.TryParse(stringToken, NumberStyles.Any, CultureInfo.InvariantCulture, out float floatValue))
                                {
                                    items.Add(floatValue);
                                }
                                else
                                {
                                    throw new ArgumentException($"in fileread() function: format error at column {tokenStart}");
                                }
                            }
                            else
                            {
                                throw new ArgumentException($"in fileread() function: format error at column {tokenStart}");
                            }
                            lastToken.Clear();
                        }
                        else
                        {
                            // Nothing read between delimiters
                            items.Add(null);
                        }
                    }

                    if (items.Count == elementsToRead)
                    {
                        // Cannot read any more
                        break;
                    }
                    tokenStart = i;
                }
                else if (!char.IsWhiteSpace(c))
                {
                    if (lastElement is not null)
                    {
                        throw new ArgumentException($"in fileread() function: unexpected value after element at column ${tokenStart}");
                    }
                    lastToken.Append(c);
                }
            }
            return items.ToArray();
        }
    }
}
