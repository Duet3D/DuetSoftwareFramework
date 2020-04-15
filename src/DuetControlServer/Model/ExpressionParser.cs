using DuetAPI.Commands;
using DuetAPI.Machine;
using System;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Class providing functions for parsing expressions
    /// </summary>
    public static class ExpressionParser
    {
        /// <summary>
        /// Checks if the given code contains any Linux object model fields
        /// </summary>
        /// <param name="code">Code to check</param>
        /// <returns>Whether the code contains any Linux object model fields</returns>
        /// <exception cref="CodeParserException">Failed to parse expression</exception>
        public static bool HasLinuxExpressions(Code code)
        {
            // echo command
            if (code.Keyword == KeywordType.Echo)
            {
                foreach (string expression in code.KeywordArgument.Split(','))
                {
                    if (ContainsLinuxExpressions(expression, code))
                    {
                        return true;
                    }
                }
                return false;
            }

            // Conditional code
            if (code.Keyword != KeywordType.None)
            {
                return ContainsLinuxExpressions(code.KeywordArgument, code);
            }

            // Regular G/M/T-code
            foreach (CodeParameter parameter in code.Parameters)
            {
                if (parameter.IsExpression && ContainsLinuxExpressions(parameter, code))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if the given expression string contains any Linux object model fields
        /// </summary>
        /// <param name="expression">Expression to check</param>
        /// <param name="code">Code for providing potential exception details</param>
        /// <returns>Whether the expressions contains any Linux object model fields</returns>
        /// <exception cref="CodeParserException">Failed to parse expression</exception>
        public static bool ContainsLinuxExpressions(string expression, Code code)
        {
            StringBuilder subExpression = new StringBuilder(), parsedExpression = new StringBuilder();
            int numCurlyBraces = 0, numRoundBraces = 0;
            bool inQuotes = false;

            char lastC = '\0';
            foreach (char c in expression)
            {
                if (inQuotes)
                {
                    inQuotes = (lastC != '"' && c == '"');
                    if (lastC != '"' && c == '"')
                    {
                        inQuotes = false;
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == '{')
                {
                    numCurlyBraces++;
                }
                else if (numCurlyBraces > 0)
                {
                    if (c == '}')
                    {
                        numCurlyBraces--;
                        if (numCurlyBraces == 0)
                        {
                            if (ContainsLinuxExpressions(subExpression.ToString(), code))
                            {
                                return true;
                            }
                            subExpression.Clear();
                        }
                    }
                    else
                    {
                        subExpression.Append(c);
                    }
                }
                else if (c == '(')
                {
                    numRoundBraces++;
                }
                else if (numRoundBraces > 0)
                {
                    if (c == ')')
                    {
                        numRoundBraces--;
                        if (numRoundBraces == 0)
                        {
                            if (ContainsLinuxExpressions(subExpression.ToString(), code))
                            {
                                return true;
                            }
                            subExpression.Clear();
                        }
                    }
                    else
                    {
                        subExpression.Append(c);
                    }
                }
                else if (c == '.' || char.IsLetterOrDigit(c))
                {
                    parsedExpression.Append(c);
                }
                else if (c != '[' && c != ']' && !char.IsDigit(c) && parsedExpression.Length > 0)
                {
                    if (IsLinuxExpression(parsedExpression.ToString()))
                    {
                        return true;
                    }
                    parsedExpression.Clear();
                }
                lastC = c;
            }

            if (inQuotes)
            {
                throw new CodeParserException("Unterminated quotes", code);
            }
            if (numCurlyBraces != 0)
            {
                throw new CodeParserException("Invalid number of curly braces", code);
            }
            if (numRoundBraces != 0)
            {
                throw new CodeParserException("Invalid number of round braces", code);
            }
            return (parsedExpression.Length > 0 && IsLinuxExpression(parsedExpression.ToString()));
        }

        /// <summary>
        /// Checks if the given expression is a Linux object model field
        /// </summary>
        /// <param name="expression">Expression without indices to check</param>
        /// <returns>Whether the given expression is a Linux object model field</returns>
        public static bool IsLinuxExpression(string expression)
        {
            // We neither read from nor write to the OM so don't care about the lock
            ModelObject model = Provider.Get;
            foreach (string pathItem in expression.Split('.'))
            {
                if (model.JsonProperties.TryGetValue(pathItem, out PropertyInfo property))
                {
                    if (Attribute.IsDefined(property, typeof(LinuxPropertyAttribute)))
                    {
                        return true;
                    }

                    if (property.PropertyType.IsSubclassOf(typeof(ModelObject)))
                    {
                        model = (ModelObject)Activator.CreateInstance(property.PropertyType);
                    }
                    if (property.PropertyType.IsGenericType)
                    {
                        Type itemType = property.PropertyType.GetGenericArguments()[0];
                        if (itemType.IsSubclassOf(typeof(ModelObject)))
                        {
                            model = (ModelObject)Activator.CreateInstance(itemType);
                        }
                    }
                }
            }
            return false;
        }

#if false
        /// <summary>
        /// Replace Linux attributes with values from the machine model
        /// </summary>
        /// <param name="code">Code holding expressions</param>
        /// <param name="replaceOnlyLinuxFields">Whether to replace only Linux attributes</param>
        public static async Task EvaluateExpressions(Code code, bool replaceOnlyLinuxFields)
        {
            if (code.Keyword == KeywordType.Echo)
            {
                StringBuilder builder = new StringBuilder();
                foreach (string expression in code.KeywordArgument.Split(','))
                {
                    string trimmedExpression = expression.Trim();
                    try
                    {
                        bool expressionFound;
                        object expressionResult;
                        using (await Provider.AccessReadOnlyAsync())
                        {
                            expressionFound = Filter.GetSpecific(trimmedExpression, true, out expressionResult);
                        }
                        if (!expressionFound)
                        {
                            expressionResult = await SPI.Interface.EvaluateExpression(code.Channel, trimmedExpression);
                        }

                        if (builder.Length != 0)
                        {
                            builder.Append(' ');
                        }
                        builder.Append(expressionResult);
                    }
                    catch (CodeParserException e)
                    {
                        Result = new CodeResult(MessageType.Error, $"Failed to evaluate \"{trimmedExpression}\": {e.Message}");
                    }
                }
                code.KeywordArgument = builder.ToString();
            }
            else if (Keyword != KeywordType.None)
            {

            }
            else
            {
                // Regular G/M/T-code, possibly with expressions

            }
        }

        /// <summary>
        /// Replace expression(s) internally
        /// </summary>
        /// <param name="expression">Expression(s) to replace</param>
        /// <param name="onlyLinuxFields">Whether to replace only Linux fields</param>
        /// <returns>Replaced expression(s)</returns>
        /// <exception cref="CodeParserException">Failed to parse expression(s)</exception>
        private static async Task<string> ReplaceExpression(string expression, bool onlyLinuxFields)
        {
            StringBuilder result = new StringBuilder(), subExpression = new StringBuilder(), parsedExpression = new StringBuilder();
            StringBuilder currentBuilder = result;
            int numCurlyBraces = 0, numRoundBraces = 0;
            bool inQuotes = false;

            char lastC = '\0';
            foreach (char c in expression)
            {
                if (inQuotes)
                {
                    inQuotes = (lastC != '"' && c == '"');
                    if (lastC != '"' && c == '"')
                    {
                        inQuotes = false;
                    }
                    currentBuilder.Append(c);
                }
                else if (c == '"')
                {
                    inQuotes = true;
                    currentBuilder.Append(c);
                }
                else if (c == '{')
                {
                    numCurlyBraces++;
                    currentBuilder = subExpression;
                }
                else if (numCurlyBraces > 0)
                {
                    if (c == '}')
                    {
                        numCurlyBraces--;
                        if (numCurlyBraces == 0)
                        {
                            result.Append(ReplaceExpression(subExpression.ToString(), onlyLinuxFields));
                            subExpression.Clear();
                            currentBuilder = result;
                        }
                    }
                    else
                    {
                        subExpression.Append(c);
                    }
                }
                else if (c == '(')
                {
                    numRoundBraces++;
                    currentBuilder = subExpression;
                }
                else if (numRoundBraces > 0)
                {
                    if (c == ')')
                    {
                        numRoundBraces--;
                        if (numRoundBraces == 0)
                        {
                            result.Append(ReplaceExpression(subExpression.ToString(), onlyLinuxFields));
                            subExpression.Clear();
                            currentBuilder = result;
                        }
                    }
                    else
                    {
                        subExpression.Append(c);
                    }
                }
                else if (c == '#' || c == '[' || c == ']' || c == '.' || char.IsLetterOrDigit(c))
                {
                    parsedExpression.Append(c);
                }
                else
                {
                    if (parsedExpression.Length > 0)
                    {
                        // TODO
                        parsedExpression.Clear();
                    }
                    result.Append(c);
                }
                lastC = c;
            }

            if (parsedExpression.Length > 0)
            {
                // TODO
            }

            if (inQuotes)
            {
                throw new CodeParserException("Unterminated quotes", this);
            }
            if (numCurlyBraces != 0)
            {
                throw new CodeParserException("Invalid number of curly braces", this);
            }
            if (numRoundBraces != 0)
            {
                throw new CodeParserException("Invalid number of round braces", this);
            }

            return result.ToString();
        }
#endif
    }
}
