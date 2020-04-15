using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Machine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Class providing functions for expression testing and evaluation
    /// </summary>
    public static class Expressions
    {
        /// <summary>
        /// Checks if the given code contains any Linux object model fields
        /// </summary>
        /// <param name="code">Code to check</param>
        /// <returns>Whether the code contains any Linux object model fields</returns>
        /// <exception cref="CodeParserException">Failed to parse expression</exception>
        public static bool ContainsLinuxFields(Code code)
        {
            // echo command
            if (code.Keyword == KeywordType.Echo)
            {
                foreach (string expression in code.KeywordArgument.Split(','))
                {
                    if (ContainsLinuxFields(expression, code))
                    {
                        return true;
                    }
                }
                return false;
            }

            // Conditional code
            if (code.Keyword != KeywordType.None)
            {
                return ContainsLinuxFields(code.KeywordArgument, code);
            }

            // Regular G/M/T-code
            foreach (CodeParameter parameter in code.Parameters)
            {
                if (parameter.IsExpression && ContainsLinuxFields(parameter, code))
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
        private static bool ContainsLinuxFields(string expression, Code code)
        {
            Stack<bool> lastBraceCurly = new Stack<bool>();
            Stack<StringBuilder> parsedExpressions = new Stack<StringBuilder>();
            parsedExpressions.Push(new StringBuilder());

            bool inQuotes = false;
            char lastC = '\0';
            foreach (char c in expression)
            {
                if (inQuotes)
                {
                    if (lastC != '"' && c == '"')
                    {
                        inQuotes = false;
                    }
                    parsedExpressions.Peek().Append(c);
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == '{' || c == '(')
                {
                    lastBraceCurly.Push(c == '{');
                    parsedExpressions.Push(new StringBuilder());
                }
                else if (c == '}' || c == ')')
                {
                    if (lastBraceCurly.TryPop(out bool lastWasCurly))
                    {
                        if (c != (lastWasCurly ? '}' : ')'))
                        {
                            throw new CodeParserException($"Unexpected {(lastWasCurly ? "curly" : "round")} brace", code);
                        }

                        string subExpression = parsedExpressions.Pop().ToString();
                        if (IsLinuxExpression(subExpression))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        throw new CodeParserException($"Unexpected {(c == '}' ? "curly" : "round")} brace", code);
                    }
                }
                else if (c == '.' || char.IsLetter(c))
                {
                    parsedExpressions.Peek().Append(c);
                }
                else if (c != '[' && c != ']' && !char.IsDigit(c) && parsedExpressions.Peek().Length > 0)
                {
                    string subExpression = parsedExpressions.Peek().ToString();
                    if (IsLinuxExpression(subExpression))
                    {
                        return true;
                    }
                    parsedExpressions.Peek().Clear();
                }
                lastC = c;
            }

            if (inQuotes)
            {
                throw new CodeParserException("Unterminated quotes", code);
            }
            if (lastBraceCurly.TryPeek(out bool wasCurly))
            {
                throw new CodeParserException($"Unterminated {(wasCurly ? "curly" : "round")} brace", code);
            }

            return IsLinuxExpression(parsedExpressions.Peek().ToString());
        }

        /// <summary>
        /// Checks if the given expression without indices is a Linux object model field
        /// </summary>
        /// <param name="expression">Expression without indices to check</param>
        /// <returns>Whether the given expression is a Linux object model field</returns>
        public static bool IsLinuxExpression(string expression)
        {
            // We neither read from nor write data to the OM so don't care about locking it
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
                    else if (property.PropertyType.IsGenericType)
                    {
                        Type itemType = property.PropertyType.GetGenericArguments()[0];
                        if (itemType.IsSubclassOf(typeof(ModelObject)))
                        {
                            model = (ModelObject)Activator.CreateInstance(itemType);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Evaluate a conditional code
        /// </summary>
        /// <param name="code">Code holding expressions</param>
        /// <param name="replaceOnlyLinuxFields">Whether to evaluate only Linux attributes (not applicable for echo)</param>
        /// <returns>Evaluation result or null</returns>
        public static async Task<string> Evaluate(Code code, bool replaceOnlyLinuxFields)
        {
            if (code.Keyword == KeywordType.Echo)
            {
                StringBuilder builder = new StringBuilder();
                foreach (string expression in code.KeywordArgument.Split(','))
                {
                    string trimmedExpression = expression.Trim();
                    try
                    {
                        string result = await Evaluate(code.Channel, trimmedExpression, replaceOnlyLinuxFields);
                        if (builder.Length != 0)
                        {
                            builder.Append(' ');
                        }
                        builder.Append(result);
                    }
                    catch (CodeParserException cpe)
                    {
                        throw new CodeParserException($"Failed to evaluate \"{trimmedExpression}\": {cpe.Message}", code, cpe);
                    }
                }
                return builder.ToString();
            }

            if (code.Keyword != KeywordType.None)
            {
                string keywordArgument = code.KeywordArgument.Trim();
                try
                {
                    string result = await Evaluate(code.Channel, keywordArgument, replaceOnlyLinuxFields);
                    return result;
                }
                catch (CodeParserException cpe)
                {
                    throw new CodeParserException($"Failed to evaluate \"{keywordArgument}\": {cpe.Message}", code, cpe);
                }
            }

            for (int i = 0; i < code.Parameters.Count; i++)
            {
                if (code.Parameters[i].IsExpression)
                {
                    string trimmedExpression = code.Parameters[i].ToString().Trim();
                    try
                    {
                        string parameterValue = await Evaluate(code.Channel, trimmedExpression, replaceOnlyLinuxFields);
                        code.Parameters[i] = new CodeParameter(code.Parameters[i].Letter, parameterValue);
                    }
                    catch (CodeParserException cpe)
                    {
                        throw new CodeParserException($"Failed to evaluate \"{trimmedExpression}\": {cpe.Message}", code, cpe);
                    }
                }
            }
            code.ConvertDriverIds();
            return null;
        }

        /// <summary>
        /// Evaluate sub-expression(s)
        /// </summary>
        /// <param name="channel">Channel to evaluate this on</param>
        /// <param name="expression">Expression(s) to replace</param>
        /// <param name="onlyLinuxFields">Whether to replace only Linux fields</param>
        /// <returns>Replaced expression(s)</returns>
        /// <exception cref="CodeParserException">Failed to parse expression(s)</exception>
        private static async Task<string> Evaluate(CodeChannel channel, string expression, bool onlyLinuxFields)
        {
            Stack<bool> lastBraceCurly = new Stack<bool>();
            Stack<StringBuilder> parsedExpressions = new Stack<StringBuilder>();
            parsedExpressions.Push(new StringBuilder());

            bool inQuotes = false;
            char lastC = '\0';
            foreach (char c in expression)
            {
                if (inQuotes)
                {
                    if (lastC != '"' && c == '"')
                    {
                        inQuotes = false;
                    }
                    parsedExpressions.Peek().Append(c);
                }
                else if (c == '"')
                {
                    inQuotes = true;
                    parsedExpressions.Peek().Append(c);
                }
                else if (c == '{' || c == '(')
                {
                    lastBraceCurly.Push(c == '{');
                    parsedExpressions.Push(new StringBuilder());
                }
                else if (c == '}' || c == ')')
                {
                    if (lastBraceCurly.TryPop(out bool lastWasCurly))
                    {
                        if (c != (lastWasCurly ? '}' : ')'))
                        {
                            throw new CodeParserException($"Unexpected {(lastWasCurly ? "curly" : "round")} brace");
                        }

                        string subExpression = parsedExpressions.Pop().ToString();
                        string evaluationResult = await EvaluateToString(channel, subExpression.Trim(), onlyLinuxFields);
                        if (subExpression == evaluationResult || onlyLinuxFields)
                        {
                            parsedExpressions.Peek().Append(lastWasCurly ? '{' : '(');
                            parsedExpressions.Peek().Append(evaluationResult);
                            parsedExpressions.Peek().Append(lastWasCurly ? '}' : ')');
                        }
                        else
                        {
                            parsedExpressions.Peek().Append(evaluationResult);
                        }
                    }
                    else
                    {
                        throw new CodeParserException($"Unexpected '{c}'");
                    }
                }
                else
                {
                    parsedExpressions.Peek().Append(c);
                }
                lastC = c;
            }

            if (inQuotes && lastC != '"')
            {
                throw new CodeParserException("Unterminated quotes");
            }
            if (lastBraceCurly.TryPeek(out bool wasCurly))
            {
                throw new CodeParserException($"Unterminated {(wasCurly ? "curly" : "round")} brace");
            }

            return await EvaluateToString(channel, parsedExpressions.Pop().ToString().Trim(), onlyLinuxFields);
        }

        /// <summary>
        /// Evaluate a sub-expression to string
        /// </summary>
        /// <param name="channel">Code channel</param>
        /// <param name="expression">Expression to evaluate</param>
        /// <param name="onlyLinuxFields">Whether to replace only Linux fields</param>
        /// <returns>String result or the expresion</returns>
        private static async Task<string> EvaluateToString(CodeChannel channel, string expression, bool onlyLinuxFields)
        {
            StringBuilder result = new StringBuilder(), partialExpression = new StringBuilder();
            bool inQuotes = false;

            char lastC = '\0';
            for (int i = 0; i <= expression.Length; i++)
            {
                char c = (i < expression.Length) ? expression[i] : '\0';
                if (inQuotes)
                {
                    if (lastC != '"' && c == '"')
                    {
                        inQuotes = false;
                    }
                    if (c != '\0')
                    {
                        result.Append(c);
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                    result.Append(c);
                }
                else if (c == '#' || c == '.' || c == '[' || c == ']' || char.IsLetterOrDigit(c))
                {
                    partialExpression.Append(c);
                }
                else if (partialExpression.Length > 0)
                {
                    string subExpression = partialExpression.ToString().Trim(), subFilter = subExpression;
                    bool wantsCount = subExpression.StartsWith('#');
                    if (wantsCount)
                    {
                        subFilter = subExpression.Substring(1);
                    }
                    if (Filter.GetSpecific(subFilter, onlyLinuxFields, out object linuxField))
                    {
                        string subResult = ObjectToString(linuxField, wantsCount);
                        if (subExpression == expression)
                        {
                            return subResult;
                        }
                        result.Append(subResult);
                    }
                    else
                    {
                        result.Append(partialExpression);
                    }

                    partialExpression.Clear();
                    if (c != '\0')
                    {
                        result.Append(c);
                    }
                }
                else if (c != '\0')
                {
                    result.Append(c);
                }
                lastC = c;
            }

            // Try to evaluate the resulting expression from DSF one last time
            string finalExpression = result.ToString(), finalFilter = finalExpression;
            bool wantsFinalCount = finalExpression.StartsWith('#');
            if (wantsFinalCount)
            {
                finalFilter = finalExpression.Substring(1);
            }
            if (Filter.GetSpecific(finalFilter, onlyLinuxFields, out object finalLinuxField))
            {
                return ObjectToString(finalLinuxField, wantsFinalCount);
            }

            // If that failed, try to evaluate the expression in RRF as the final step
            if (!onlyLinuxFields)
            {
                object firmwareField = await SPI.Interface.EvaluateExpression(channel, finalExpression);
                return ObjectToString(firmwareField, false);
            }

            // In case we're not done yet, return only the partial expression value
            return finalExpression;
        }

        /// <summary>
        /// Convert an object to a string internally
        /// </summary>
        /// <param name="obj">Object to convert</param>
        /// <param name="wantsCount">Whether the count is wanted</param>
        /// <returns>String representation of obj</returns>
        private static string ObjectToString(object obj, bool wantsCount)
        {
            if (obj == null)
            {
                return "null";
            }
            if (obj is bool boolValue)
            {
                return boolValue ? "true" : "false";
            }
            if (obj is string stringValue)
            {
                return '"' + stringValue.Replace("\"", "\"\"") + '"';
            }
            if (obj is IList list)
            {
                if (wantsCount)
                {
                    return list.Count.ToString();
                }
                throw new CodeParserException("missing array index");
            }
            if (obj.GetType().IsClass)
            {
                return "{object}";
            }
            return obj.ToString();
        }
    }
}
