using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Class providing functions for expression testing and evaluation
    /// </summary>
    public static class Expressions
    {
        /// <summary>
        /// Split an echo expression separated by commas
        /// </summary>
        /// <param name="expression">Expression to spli</param>
        /// <returns>Expression items</returns>
        private static IEnumerable<string> SplitExpression(string expression)
        {
            StringBuilder parsedExpression = new StringBuilder();
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
                    parsedExpression.Append(c);
                }
                else if (c == '"')
                {
                    inQuotes = true;
                    parsedExpression.Append(c);
                }
                else if (c == ',')
                {
                    yield return parsedExpression.ToString().Trim();
                    parsedExpression.Clear();
                }
                else
                {
                    parsedExpression.Append(c);
                }
            }

            if (parsedExpression.Length > 0)
            {
                yield return parsedExpression.ToString().Trim();
            }
        }

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
                foreach (string expression in SplitExpression(code.KeywordArgument))
                {
                    if (ContainsLinuxFields(expression, code))
                    {
                        return true;
                    }
                }
                return false;
            }

            // Conditional code
            if (code.Keyword != KeywordType.None && code.KeywordArgument != null)
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
            Stack<char> lastBracketTypes = new Stack<char>();
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
                else if (c == '{' || c == '(' || c == '[')
                {
                    lastBracketTypes.Push(c);
                    parsedExpressions.Push(new StringBuilder());
                }
                else if (c == '}' || c == ')' || c == ']')
                {
                    if (lastBracketTypes.TryPop(out char lastBracketType))
                    {
                        if ((lastBracketType == '{' && c != '}') ||
                            (lastBracketType == '(' && c != ')') ||
                            (lastBracketType == '[' && c != ']'))
                        {
                            if (c == '}')
                            {
                                throw new CodeParserException($"Unexpected curly bracket", code);
                            }
                            if (c == ')')
                            {
                                throw new CodeParserException($"Unexpected round bracket", code);
                            }
                            throw new CodeParserException($"Unexpected square bracket", code);
                        }

                        string subExpression = parsedExpressions.Pop().ToString();
                        if (IsLinuxExpression(subExpression))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        if (c == '}')
                        {
                            throw new CodeParserException($"Unexpected curly bracket", code);
                        }
                        if (c == ')')
                        {
                            throw new CodeParserException($"Unexpected round bracket", code);
                        }
                        throw new CodeParserException($"Unexpected square bracket", code);
                    }
                }
                else if (c == '.' || char.IsLetter(c))
                {
                    parsedExpressions.Peek().Append(c);
                }
                else if (!char.IsDigit(c) && parsedExpressions.Peek().Length > 0)
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

            if (lastBracketTypes.TryPeek(out char lastBracket))
            {
                if (lastBracket == '{')
                {
                    throw new CodeParserException($"Unterminated curly bracket", code);
                }
                if (lastBracket == '(')
                {
                    throw new CodeParserException($"Unterminated round bracket", code);
                }
                throw new CodeParserException($"Unterminated square bracket", code);
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
            // Check for special variables
            if (expression == "iterations" || expression == "line" || expression == "result")
            {
                return true;
            }

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
            if (code.KeywordArgument != null)
            {
                if (code.Keyword == KeywordType.Echo)
                {
                    StringBuilder builder = new StringBuilder();
                    foreach (string expression in SplitExpression(code.KeywordArgument))
                    {
                        try
                        {
                            string result = await EvaluateExpression(code, expression, replaceOnlyLinuxFields, false);
                            if (builder.Length != 0)
                            {
                                builder.Append(' ');
                            }
                            builder.Append(result);
                        }
                        catch (CodeParserException cpe)
                        {
                            throw new CodeParserException($"Failed to evaluate \"{expression}\": {cpe.Message}", cpe);
                        }
                    }
                    return builder.ToString();
                }

                string keywordArgument = code.KeywordArgument.Trim();
                try
                {
                    string result = await EvaluateExpression(code, keywordArgument, replaceOnlyLinuxFields, false);
                    return result;
                }
                catch (CodeParserException cpe)
                {
                    throw new CodeParserException($"Failed to evaluate \"{keywordArgument}\": {cpe.Message}", cpe);
                }
            }

            for (int i = 0; i < code.Parameters.Count; i++)
            {
                if (code.Parameters[i].IsExpression)
                {
                    string trimmedExpression = ((string)code.Parameters[i]).Trim();
                    try
                    {
                        string parameterValue = await EvaluateExpression(code, trimmedExpression, replaceOnlyLinuxFields, true);
                        if (!parameterValue.StartsWith('{') && !parameterValue.EndsWith('}'))
                        {
                            // Encapsulate even fully expanded parameters so that plugins and RRF know it was an expression
                            parameterValue = '{' + parameterValue + '}';
                        }
                        code.Parameters[i] = new CodeParameter(code.Parameters[i].Letter, parameterValue);
                    }
                    catch (CodeParserException cpe)
                    {
                        throw new CodeParserException($"Failed to evaluate \"{trimmedExpression}\": {cpe.Message}", cpe);
                    }
                }
            }
            code.ConvertDriverIds();
            return null;
        }

        /// <summary>
        /// Evaluate expression(s)
        /// </summary>
        /// <param name="code">Code holding the expression(s)</param>
        /// <param name="expression">Expression(s) to replace</param>
        /// <param name="onlyLinuxFields">Whether to replace only Linux fields</param>
        /// <param name="encodeResult">Whether the final result shall be encoded</param>
        /// <returns>Replaced expression(s)</returns>
        /// <exception cref="CodeParserException">Failed to parse expression(s)</exception>
        private static async Task<string> EvaluateExpression(Code code, string expression, bool onlyLinuxFields, bool encodeResult)
        {
            Stack<char> lastBracketTypes = new Stack<char>();
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
                else if (c == '{' || c == '(' || c == '[')
                {
                    lastBracketTypes.Push(c);
                    parsedExpressions.Push(new StringBuilder());
                }
                else if (c == '}' || c == ')' || c == ']')
                {
                    if (lastBracketTypes.TryPop(out char lastBracketType))
                    {
                        char expectedBracketType = lastBracketType switch
                        {
                            '{' => '}',
                            '(' => ')',
                            _   => ']'
                        };

                        if (c != expectedBracketType)
                        {
                            if (c == '}')
                            {
                                throw new CodeParserException($"Unexpected curly bracket", code);
                            }
                            if (c == ')')
                            {
                                throw new CodeParserException($"Unexpected round bracket", code);
                            }
                            throw new CodeParserException($"Unexpected square bracket", code);
                        }

                        string subExpression = parsedExpressions.Pop().ToString();
                        string evaluationResult = await EvaluateSubExpression(code, subExpression.Trim(), onlyLinuxFields, true);
                        if (lastBracketType == '[' || subExpression == evaluationResult || onlyLinuxFields)
                        {
                            parsedExpressions.Peek().Append(lastBracketType);
                            parsedExpressions.Peek().Append(evaluationResult);
                            parsedExpressions.Peek().Append(expectedBracketType);
                        }
                        else
                        {
                            parsedExpressions.Peek().Append(evaluationResult);
                        }
                    }
                    else
                    {
                        throw new CodeParserException($"Unexpected '{c}'", code);
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
                throw new CodeParserException("Unterminated quotes", code);
            }

            if (lastBracketTypes.TryPeek(out char lastBracket))
            {
                if (lastBracket == '{')
                {
                    throw new CodeParserException($"Unterminated curly bracket", code);
                }
                if (lastBracket == '(')
                {
                    throw new CodeParserException($"Unterminated round bracket", code);
                }
                throw new CodeParserException($"Unterminated square bracket", code);
            }

            return await EvaluateSubExpression(code, parsedExpressions.Pop().ToString().Trim(), onlyLinuxFields, encodeResult);
        }

        /// <summary>
        /// Evaluate a sub-expression
        /// </summary>
        /// <param name="code">Code holdng the sub-expression</param>
        /// <param name="expression">Expression to evaluate</param>
        /// <param name="onlyLinuxFields">Whether to replace only Linux fields</param>
        /// <param name="encodeResult">Whether the final result shall be encoded</param>
        /// <returns>String result or the expresion</returns>
        /// <exception cref="CodeParserException">Failed to parse expression(s)</exception>
        private static async Task<string> EvaluateSubExpression(Code code, string expression, bool onlyLinuxFields, bool encodeResult)
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
                    string subExpression = partialExpression.ToString().Trim();
                    if (subExpression == "iterations")
                    {
                        if (code.File == null)
                        {
                            throw new CodeParserException("not executing a file", code);
                        }
                        using (await code.File.LockAsync())
                        {
                            result.Append(code.File.GetIterations(code));
                        }
                    }
                    else if (subExpression == "line")
                    {
                        result.Append(code.LineNumber);
                    }
                    else if (subExpression == "result")
                    {
                        if (code.File == null)
                        {
                            throw new CodeParserException("not executing a file", code);
                        }
                        using (await code.File.LockAsync())
                        {
                            result.Append(code.File.LastResult);
                        }
                    }
                    else
                    {
                        string subFilter = subExpression;
                        bool wantsCount = subExpression.StartsWith('#');
                        if (wantsCount)
                        {
                            subFilter = subExpression[1..];
                        }
                        if (Filter.GetSpecific(subFilter, true, out object linuxField))
                        {
                            if (subExpression == expression)
                            {
                                return ObjectToString(linuxField, wantsCount, encodeResult, code);
                            }
                            string subResult = ObjectToString(linuxField, wantsCount, true, code);
                            result.Append(subResult);
                        }
                        else
                        {
                            result.Append(partialExpression);
                        }
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
            string finalExpression = result.ToString();
            if (finalExpression == "iterations")
            {
                if (code.File == null)
                {
                    throw new CodeParserException("not executing a file", code);
                }
                using (await code.File.LockAsync())
                {
                    return code.File.GetIterations(code).ToString();
                }
            }
            if (finalExpression == "line")
            {
                return code.LineNumber.ToString();
            }
            if (finalExpression == "result")
            {
                if (code.File == null)
                {
                    throw new CodeParserException("not executing a file", code);
                }
                using (await code.File.LockAsync())
                {
                    return code.File.LastResult.ToString();
                }
            }
            string finalFilter = finalExpression;
            bool wantsFinalCount = finalExpression.StartsWith('#');
            if (wantsFinalCount)
            {
                finalFilter = finalExpression[1..];
            }
            if (Filter.GetSpecific(finalFilter, true, out object finalLinuxField))
            {
                return ObjectToString(finalLinuxField, wantsFinalCount, encodeResult, code);
            }

            // If that failed, try to evaluate the expression in RRF as the final step
            if (!onlyLinuxFields)
            {
                object firmwareField = await SPI.Interface.EvaluateExpression(code.Channel, finalExpression);
                return ObjectToString(firmwareField, false, encodeResult, code);
            }

            // In case we're not done yet, return only the partial expression value
            return finalExpression;
        }

        /// <summary>
        /// Convert an object to a string internally
        /// </summary>
        /// <param name="obj">Object to convert</param>
        /// <param name="wantsCount">Whether the count is wanted</param>
        /// <param name="encodeStrings">Whether the value is supposed to be encoded if it is a string</param>
        /// <param name="code">Code requesting the conversion</param>
        /// <returns>String representation of obj</returns>
        private static string ObjectToString(object obj, bool wantsCount, bool encodeStrings, Code code)
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
                return encodeStrings ? ('"' + stringValue.Replace("\"", "\"\"") + '"') : stringValue;
            }
            if (obj is IList list)
            {
                if (wantsCount)
                {
                    return list.Count.ToString();
                }
                throw new CodeParserException("missing array index", code);
            }
            if (obj.GetType().IsClass)
            {
                return "{object}";
            }
            return obj.ToString();
        }
    }
}
