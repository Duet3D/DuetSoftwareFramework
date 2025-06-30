using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Codes.Meta;

/// <summary>
/// Functionality for meta G-code expressions
/// </summary>
/// <param name="filter">Object model filter</param>
/// <param name="model">Object model</param>
/// <param name="linkInterface">Link interface</param>
public sealed class Expressions(Model.Filter filter, Model.ObjectModel model, Link.Interface linkInterface)
{
    /// <summary>
    /// Delegate for asynchronously resolving custom meta G-code fuctions
    /// </summary>
    /// <param name="channel">Code channel where this function is requested</param>
    /// <param name="functionName">Name of the function</param>
    /// <param name="arguments">Function arguments</param>
    /// <returns>Result value</returns>
    public delegate Task<object?> CustomAsyncFunctionResolver(CodeChannel channel, string functionName, object?[] arguments);

    /// <summary>
    /// Dictionary of custom meta G-code functions vs. async resolvers
    /// </summary>
    public Dictionary<string, CustomAsyncFunctionResolver> CustomFunctions { get; } = [];

    /// <summary>
    /// Try to get the last function from a string builder and if applicable a custom function handler
    /// </summary>
    /// <param name="lastExpression">Last full expression before the next round brace</param>
    /// <param name="lastFunction">Last function name</param>
    /// <param name="wantsCount">If the function name is prefixed with a #</param>
    /// <param name="fn">Asynchronous function handler if applicable</param>
    /// <returns>If any handler could be found</returns>
    private bool TryGetCustomFunction(string lastExpression, out string lastFunction, out bool wantsCount, [NotNullWhen(true)] out CustomAsyncFunctionResolver? fn)
    {
        // Read the last valid function
        lastFunction = string.Empty;
        wantsCount = false;
        bool fnComplete = false;
        for (int i = lastExpression.Length - 1; i >= 0; i--)
        {
            char c = lastExpression[i];
            if (char.IsLetterOrDigit(c) || c == '.' || c == '_')
            {
                if (fnComplete)
                {
                    break;
                }
                lastFunction = c + lastFunction;
            }
            else if (c == '#')
            {
                wantsCount = true;
                break;
            }
            else if (char.IsWhiteSpace(c))
            {
                fnComplete = true;
            }
            else
            {
                break;
            }
        }

        // Try to get the corresponding function
        return CustomFunctions.TryGetValue(lastFunction, out fn);
    }

    /// <summary>
    /// Split a comma-separated expression
    /// </summary>
    /// <param name="expression">Expression to split</param>
    /// <returns>Expression items</returns>
    private static IEnumerable<string> SplitExpression(string expression)
    {
        int numCurlyBraces = 0, numSquareBraces = 0, numRoundBraces = 0;
        StringBuilder parsedExpression = new();
        bool inSingleQuotes = false, inDoubleQuotes = false;
        char lastC = '\0';
        foreach (char c in expression)
        {
            if (inSingleQuotes)
            {
                if (lastC != '\'' && c == '\'')
                {
                    inSingleQuotes = false;
                }
                parsedExpression.Append(c);
            }
            else if (inDoubleQuotes)
            {
                if (lastC != '"' && c == '"')
                {
                    inDoubleQuotes = false;
                }
                parsedExpression.Append(c);
            }
            else if (c == '\'')
            {
                inSingleQuotes = true;
                parsedExpression.Append(c);
            }
            else if (c == '"')
            {
                inDoubleQuotes = true;
                parsedExpression.Append(c);
            }
            else if (c == ',' && numCurlyBraces + numSquareBraces + numRoundBraces == 0)
            {
                yield return parsedExpression.ToString().Trim();
                parsedExpression.Clear();
            }
            else
            {
                switch (c)
                {
                    case '{':
                        numCurlyBraces++;
                        break;
                    case '}':
                        numCurlyBraces--;
                        break;
                    case '[':
                        numSquareBraces++;
                        break;
                    case ']':
                        numSquareBraces--;
                        break;
                    case '(':
                        numRoundBraces++;
                        break;
                    case ')':
                        numRoundBraces--;
                        break;
                }
                parsedExpression.Append(c);
            }
        }

        if (parsedExpression.Length > 0)
        {
            yield return parsedExpression.ToString().Trim();
        }
    }

    /// <summary>
    /// Checks if the given code contains any SBC object model fields
    /// </summary>
    /// <param name="code">Code to check</param>
    /// <returns>Whether the code contains any SBC object model fields</returns>
    /// <exception cref="CodeParserException">Failed to parse expression</exception>
    public bool ContainsSbcFields(Code code)
    {
        if (code.KeywordArgument is not null)
        {
            // echo command
            if (code.Keyword == KeywordType.Echo)
            {
                foreach (string expression in SplitExpression(code.KeywordArgument))
                {
                    if (ContainsSbcFields(expression))
                    {
                        return true;
                    }
                }
                return false;
            }

            // Conditional code
            if (code.Keyword != KeywordType.None)
            {
                return ContainsSbcFields(code.KeywordArgument);
            }
        }

        // Regular G/M/T-code
        foreach (CodeParameter parameter in code.Parameters)
        {
            if (parameter.IsExpression && ContainsSbcFields((string)parameter))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if the given expression string contains any SBC object model fields
    /// </summary>
    /// <param name="expression">Expression to check</param>
    /// <returns>Whether the expressions contains any SBC object model fields</returns>
    /// <exception cref="CodeParserException">Failed to parse expression</exception>
    private bool ContainsSbcFields(string expression)
    {
        bool inQuotes = false, clearToken = false;
        StringBuilder lastExpression = new();
        foreach (char c in expression)
        {
            if (inQuotes)
            {
                inQuotes = (c != '"');
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (char.IsLetterOrDigit(c) || c == '.' || c == '_')
            {
                if (clearToken)
                {
                    lastExpression.Clear();
                    clearToken = false;
                }
                lastExpression.Append(c);
            }
            else if (!char.IsWhiteSpace(c))
            {
                if (lastExpression.Length > 0 && IsSbcExpression(lastExpression.ToString(), c == '('))
                {
                    return true;
                }
                lastExpression.Clear();
            }
            else
            {
                // Expressions may be "sin (3)" but in case we encounter "foo sin (3)"
                // we must make sure our parser does not read "foosin(3)" but only "sin(3)"
                clearToken = true;
            }
        }

        return lastExpression.Length > 0 && IsSbcExpression(lastExpression.ToString(), false);
    }

    /// <summary>
    /// Checks if the given expression without indices is a SBC object model field
    /// </summary>
    /// <param name="expression">Expression without indices to check</param>
    /// <param name="isFunction">Expression is followed by an opening brace, check only if it is a custom function</param>
    /// <returns>Whether the given expression is a SBC object model field</returns>
    public bool IsSbcExpression(string expression, bool isFunction)
    {
        // Check for functions
        if (isFunction)
        {
            return CustomFunctions.ContainsKey(expression);
        }

        // Check for special variables
        if (expression == "iterations" || expression == "line" || expression == "result")
        {
            return true;
        }

        // We neither read from nor write data to the OM so don't care about locking it
        ModelObject modelItem = model;
        foreach (string pathItem in expression.Split('.', '['))
        {
            PropertyInfo? property = modelItem.GetType().GetProperty(pathItem, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (property is not null)
            {
                if (Attribute.IsDefined(property, typeof(SbcPropertyAttribute)))
                {
                    return true;
                }

                if (property.PropertyType.IsSubclassOf(typeof(ModelObject)))
                {
                    modelItem = (ModelObject)Activator.CreateInstance(property.PropertyType)!;
                }
                else if (property.PropertyType.IsGenericType)
                {
                    Type itemType = property.PropertyType.GetGenericArguments()[0];
                    if (itemType.IsSubclassOf(typeof(ModelObject)))
                    {
                        modelItem = (ModelObject)Activator.CreateInstance(itemType)!;
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
    /// <param name="evaluateAll">Whether all or only SBC fields are supposed to be evaluated</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Evaluation result or null</returns>
    public async Task<string?> EvaluateAsync(Code code, bool evaluateAll, CancellationToken cancellationToken = default)
    {
        if (code.KeywordArgument is not null)
        {
            if (code.Keyword == KeywordType.Echo)
            {
                StringBuilder builder = new();
                foreach (string expression in SplitExpression(code.KeywordArgument))
                {
                    string result = await EvaluateExpression(code, expression, !evaluateAll, false, cancellationToken);
                    if (builder.Length != 0)
                    {
                        builder.Append(' ');
                    }
                    builder.Append(result);
                }
                return builder.ToString();
            }

            if (code.Keyword == KeywordType.Abort)
            {
                string keywordArgument = code.KeywordArgument.Trim();
                return await EvaluateExpression(code, keywordArgument, !evaluateAll, false, cancellationToken);
            }

            string keywordExpression;
            if (code.Keyword == KeywordType.Global || code.Keyword == KeywordType.Var || code.Keyword == KeywordType.Set)
            {
                // Get the actual expression
                keywordExpression = string.Empty;
                bool inExpression = false;
                foreach (char c in code.KeywordArgument)
                {
                    if (inExpression)
                    {
                        keywordExpression += c;
                    }
                    else if (c == '=')
                    {
                        inExpression = true;
                    }
                }
            }
            else
            {
                // Condition equals the keyword argument
                keywordExpression = code.KeywordArgument;
            }

            // Evaluate SBC properties
            return await EvaluateExpression(code, keywordExpression.Trim(), !evaluateAll, false, cancellationToken);
        }

        if (code.Parameters.Any(parameter => parameter.IsExpression))
        {
            List<CodeParameter> newParameters = [];
            foreach (CodeParameter parameter in code.Parameters)
            {
                if (parameter.IsExpression)
                {
                    string trimmedExpression = ((string)parameter).Trim();
                    string parameterValue = await EvaluateExpression(code, trimmedExpression, !evaluateAll, !evaluateAll, cancellationToken);
                    if (!evaluateAll && !parameterValue.StartsWith('{') && !parameterValue.EndsWith('}'))
                    {
                        // Encapsulate fully expanded parameters so that plugins and RRF know it was an expression
                        parameterValue = '{' + parameterValue + '}';
                    }
                    newParameters.Add(new CodeParameter(parameter.Letter, parameterValue, false, false));
                }
                else
                {
                    newParameters.Add(parameter);
                }
            }

            lock (code)
            {
                code.Parameters = newParameters;
                code.ConvertDriverIds();
            }
        }
        return null;
    }

    /// <summary>
    /// Used only internally by the following function
    /// </summary>
    private static readonly object _nullResult = new();

    /// <summary>
    /// Convert an object to a string value
    /// </summary>
    /// <param name="obj">Object to convert</param>
    /// <param name="wantsCount">Whether the count or length is requested</param>
    /// <param name="encodeValues">Whether values are supposed to be encoded for further evaluation</param>
    /// <param name="code">Code requesting the conversion</param>
    /// <returns>Converted object</returns>
    /// <exception cref="CodeParserException">Thrown on invalid request</exception>
    private string ObjectToString(object? obj, bool wantsCount, bool encodeValues, Code code)
    {
        static string encodeString(string value)
        {
            return '"' + value.Replace("\"", "\"\"").Replace("'", "''") + '"';
        }

        if (obj is null)
        {
            return "null";
        }
        if (obj is bool boolValue)
        {
            return boolValue ? "true" : "false";
        }
        if (obj is char charValue)
        {
            return encodeValues ? $"'{charValue}'" : charValue.ToString();
        }
        if (obj is string stringValue)
        {
            if (wantsCount)
            {
                return stringValue.Length.ToString();
            }
            return encodeValues ? encodeString(stringValue) : stringValue;
        }
        if (obj is DriverId driverId)
        {
            return driverId.ToString();
        }
        if (obj is int intValue)
        {
            return intValue.ToString("G", CultureInfo.InvariantCulture);
        }
        if (obj is uint uintValue)
        {
            return uintValue.ToString("G", CultureInfo.InvariantCulture);
        }
        if (obj is float floatValue)
        {
            return floatValue.ToString("G", CultureInfo.InvariantCulture);
        }
        if (obj is long longValue)
        {
            return longValue.ToString("G", CultureInfo.InvariantCulture);
        }
        if (obj is DateTime dateTimeValue)
        {
            return encodeValues ? $"\"{dateTimeValue:s}\"" : dateTimeValue.ToString("s");
        }
        if (obj is IList list)
        {
            if (wantsCount)
            {
                return list.Count.ToString();
            }
            if (list.Count == 0)
            {
                return "vector(0,0)";
            }
        }
        if (obj is bool[] boolArray)
        {
            return '{' + string.Join(',', boolArray.Select(boolValue => boolValue ? "true" : "false")) + (encodeValues && boolArray.Length == 1 ? ",}" : "}");
        }
        if (obj is char[] charArray)
        {
            return '{' + string.Join(',', charArray.Select(charValue => $"'{charValue}'")) + (encodeValues && charArray.Length == 1 ? ",}" : "}");
        }
        if (obj is string[] stringArray)
        {
            return '{' + string.Join(',', stringArray.Select(stringValue => encodeString(stringValue))) + (encodeValues && stringArray.Length == 1 ? ",}" : "}");
        }
        if (obj is DriverId[] driverIdArray)
        {
            return '{' + string.Join(',', driverIdArray.Select(driverIdValue => encodeString(driverIdValue.ToString()))) + (encodeValues && driverIdArray.Length == 1 ? ",}" : "}");
        }
        if (obj is int[] intArray)
        {
            return '{' + string.Join(',', intArray.Select(intValue => intValue.ToString("G", CultureInfo.InvariantCulture))) + (encodeValues && intArray.Length == 1 ? ",}" : "}");
        }
        if (obj is uint[] uintArray)
        {
            return '{' + string.Join(',', uintArray.Select(uintValue => uintValue.ToString("G", CultureInfo.InvariantCulture))) + (encodeValues && uintArray.Length == 1 ? ",}" : "}");
        }
        if (obj is float[] floatArray)
        {
            return '{' + string.Join(',', floatArray.Select(floatValue => floatValue.ToString("G", CultureInfo.InvariantCulture))) + (encodeValues && floatArray.Length == 1 ? ",}" : "}");
        }
        if (obj is long[] longArray)
        {
            return '{' + string.Join(',', longArray.Select(longValue => longValue.ToString("G", CultureInfo.InvariantCulture))) + (encodeValues && longArray.Length == 1 ? ",}" : "}");
        }
        if (obj is object[] objectArray)
        {
            return '{' + string.Join(',', objectArray.Select(objectValue => ObjectToString(objectValue, false, encodeValues, code))) + (encodeValues && objectArray.Length == 1 ? ",}" : "}");
        }
        if (!wantsCount && obj is IList)
        {
            throw new CodeParserException("missing array index", code);
        }
        if (obj.GetType().IsClass)
        {
            return "{object}";
        }
        return obj.ToString() ?? "null";
    }

    /// <summary>
    /// Evaluate expression(s) and return the raw evaluation result (if applicable)
    /// </summary>
    /// <param name="code">Code holding the expression(s)</param>
    /// <param name="expression">Expression(s) to replace</param>
    /// <param name="onlySbcFields">Whether to replace only SBC fields</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Replaced expression(s)</returns>
    /// <exception cref="CodeParserException">Failed to parse expression(s)</exception>
    /// <exception cref="OperationCanceledException">Code was cancelled</exception>
    public async Task<object?> EvaluateExpressionRaw(Code code, string expression, bool onlySbcFields, CancellationToken cancellationToken = default)
    {
        int i = 0;

        // Eat a single-quoted char and append its content to the given builder instance
        void eatChar(StringBuilder builder)
        {
            builder.Append('\'');

            // Read char
            if (i < expression.Length)
            {
                builder.Append(expression[i++]);
            }
            else
            {
                throw new CodeParserException("Unterminated quotes", code);
            }

            // Check for terminating quote
            if (i < expression.Length && expression[i] == '\'')
            {
                builder.Append(expression[i++]);
            }
            else
            {
                throw new CodeParserException("Unterminated quotes", code);
            }
        }

        // Eat a double-quoted string and append its content to the given builder instance
        void eatString(StringBuilder builder)
        {
            builder.Append('"');
            while (i < expression.Length)
            {
                char c = expression[i++];
                builder.Append(c);

                if (c == '"')
                {
                    if (i >= expression.Length || expression[i] != '"')
                    {
                        // end of string
                        return;
                    }

                    // dealing with a double-quote
                    builder.Append('"');
                    i++;
                    continue;
                }
            }
            throw new CodeParserException("Unterminated quotes", code);
        }

        // Finish a token before appending it to the resulting expression
        async Task appendToken(StringBuilder result, StringBuilder lastToken)
        {
            string lastTokenValue = lastToken.ToString();
            lastToken.Clear();

            switch (lastTokenValue.Trim())
            {
                case "iterations":
                    if (code.File is null)
                    {
                        throw new CodeParserException("not executing a file", code);
                    }
                    result.Append(code.File.GetIterations(code));
                    break;

                case "line":
                    if (code.File is null)
                    {
                        throw new CodeParserException("not executing a file", code);
                    }
                    result.Append(code.LineNumber ?? 0);
                    break;

                default:
                    bool wantsCount = lastTokenValue.TrimStart().StartsWith('#');
                    string filterString = wantsCount ? lastTokenValue[1..].Trim() : lastTokenValue.Trim();
                    if (IsSbcExpression(filterString, false))
                    {
                        using (await model.AccessReadOnlyAsync(cancellationToken))
                        {
                            if (filter.GetSpecific(filterString, true, out object? sbcField))
                            {
                                string subResult = ObjectToString(sbcField, wantsCount, true, code);
                                result.Append(subResult);
                            }
                            else
                            {
                                result.Append(lastTokenValue);
                            }
                        }
                    }
                    else
                    {
                        result.Append(lastTokenValue);
                    }
                    break;
            }
        }

        // Evaluate a given expression to its final value. This function attempts to look up well-known values before asking RRF
        async Task<object?> getExpressionValue(string subExpression)
        {
            // Attempt to evaluate an atomic value and return the parsed result, returns null if that failed
            // Note that it returns _nullResult instead of null in case value is "null"
            object? attemptToEvaluate(string value)
            {
                string trimmedValue = value.Trim();

                // Check for well-known constants
                switch (trimmedValue)
                {
                    case "null":
                        return _nullResult;

                    case "true":
                        return true;
                    case "false":
                        return false;

                    case "iterations":
                        if (code.File is null)
                        {
                            throw new CodeParserException("not executing a file", code);
                        }
                        return code.File.GetIterations(code);
                    case "line":
                        if (code.LineNumber is null)
                        {
                            throw new CodeParserException("not executing a file", code);
                        }
                        return code.LineNumber;
                }

                // Check for character
                if (trimmedValue.StartsWith('\''))
                {
                    if (trimmedValue.Length != 3)
                    {
                        throw new CodeParserException("invalid character literal", code);
                    }
                    return trimmedValue[1];
                }

                // Check for valid string
                if (trimmedValue.StartsWith('"'))
                {
                    StringBuilder stringContent = new();
                    bool inQuotes = false;

                    char lastC = '\0';
                    foreach (char c in trimmedValue)
                    {
                        if (inQuotes)
                        {
                            if (c == '"')
                            {
                                inQuotes = false;
                            }
                            else if (lastC == '\'')
                            {
                                stringContent.Append(char.ToLower(c));
                            }
                            else if (c != '\'')
                            {
                                stringContent.Append(c);
                            }
                        }
                        else if (c == '"')
                        {
                            if (lastC == '"')
                            {
                                stringContent.Append('"');
                            }
                            inQuotes = true;
                        }
                        else
                        {
                            // Not an atomic string...
                            return null;
                        }
                    }

                    if (inQuotes)
                    {
                        // Unterminated string...
                        return null;
                    }
                    return stringContent.ToString();
                }

                // Check for integer
                if (int.TryParse(trimmedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out int intValue))
                {
                    return intValue;
                }

                // Check for float
                if (float.TryParse(trimmedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out float floatValue))
                {
                    return floatValue;
                }

                // Not an atomic value...
                return null;
            }

            // Perform final expression evalution here
            object? evaluatedSubExpression = attemptToEvaluate(subExpression);
            if (evaluatedSubExpression is not null)
            {
                return (evaluatedSubExpression != _nullResult) ? evaluatedSubExpression : null;
            }

            // Don't return exceptions from cancelled codes
            code.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await linkInterface.EvaluateExpressionAsync(code.Channel, subExpression, cancellationToken: cancellationToken);
            }
            catch (CodeParserException) when (code.CancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }
        }

        // Eat a sub-expression and evaluate SBC-only properties + custom functions where applicable
        async Task<string> eatExpression(char brace, bool raw = false)
        {
            StringBuilder lastToken = new(), result = new();
            while (i < expression.Length)
            {
                char c = expression[i++];
                if (c == '\'')
                {
                    result.Append(lastToken);
                    eatChar(result);
                    lastToken.Clear();
                }
                else if (c == '"')
                {
                    result.Append(lastToken);
                    eatString(result);
                    lastToken.Clear();
                }
                else if (c == '(')
                {
                    bool isCustomFunction = TryGetCustomFunction(lastToken.ToString(), out string functionName, out bool wantsCount, out CustomAsyncFunctionResolver? fn);
                    string subExpression = await eatExpression(c, functionName == "exists");
                    if (isCustomFunction)
                    {
                        object? fnResult;
                        if (functionName == "exists")
                        {
                            // There may be valid properties that are null, so we need a special check for exists()
                            fnResult = await fn!(code.Channel, functionName, [subExpression]);
                        }
                        else
                        {
                            List<object?> arguments = [];
                            foreach (string arg in SplitExpression(subExpression))
                            {
                                object? argValue = await getExpressionValue(arg);
                                arguments.Add(argValue);
                            }
                            fnResult = await fn!(code.Channel, functionName, [.. arguments]);
                        }
                        result.Append(ObjectToString(fnResult, wantsCount, true, code));
                    }
                    else
                    {
                        result.Append(lastToken);
                        result.Append('(');
                        result.Append(subExpression);
                        result.Append(')');
                    }
                    lastToken.Clear();
                }
                else if (c == '[')
                {
                    lastToken.Append('[');

                    string subExpression = await eatExpression(c);
                    if (IsSbcExpression(lastToken.ToString().Trim(), false))
                    {
                        object? evaluatedSubExpression = await getExpressionValue(subExpression);
                        if (evaluatedSubExpression is int intValue)
                        {
                            lastToken.Append(intValue);
                        }
                        else
                        {
                            throw new CodeParserException("Index value in square brackets must be of type integer", code);
                        }
                    }
                    else
                    {
                        lastToken.Append(subExpression);
                    }

                    lastToken.Append(']');
                }
                else if (c == '{')
                {
                    result.Append(lastToken);
                    result.Append('{');
                    result.Append(await eatExpression(c));
                    result.Append('}');
                    lastToken.Clear();
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (brace != '(' && c == ')')
                    {
                        throw new CodeParserException("Unexpected round bracket", code);
                    }
                    if (brace != '[' && c == ']')
                    {
                        throw new CodeParserException("Unexpected square bracket", code);
                    }
                    if (brace != '{' && c == '}')
                    {
                        throw new CodeParserException("Unexpected curly bracket", code);
                    }

                    if (raw)
                    {
                        result.Append(lastToken);
                    }
                    else
                    {
                        await appendToken(result, lastToken);
                    }
                    return result.ToString();
                }
                else if (char.IsLetterOrDigit(c) || c == '#' || c == '.' || c == '_' || char.IsWhiteSpace(c))
                {
                    lastToken.Append(c);
                }
                else
                {
                    await appendToken(result, lastToken);
                    result.Append(c);
                }
            }

            if (brace == '(')
            {
                throw new CodeParserException("Unterminated round bracket", code);
            }
            if (brace == '[')
            {
                throw new CodeParserException("Unterminated square bracket", code);
            }
            if (brace == '{')
            {
                throw new CodeParserException("Unterminated curly bracket", code);
            }

            if (raw)
            {
                result.Append(lastToken);
            }
            else
            {
                await appendToken(result, lastToken);
            }
            return result.ToString();
        }

        string expressionContent = await eatExpression('\0');
        if (onlySbcFields)
        {
            return expressionContent;
        }

        // Don't return exceptions from cancelled codes
        code.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await linkInterface.EvaluateExpressionAsync(code.Channel, expressionContent, cancellationToken);
        }
        catch (CodeParserException) when (code.CancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }
    }

    /// <summary>
    /// Evaluate expression(s)
    /// </summary>
    /// <param name="code">Code holding the expression(s)</param>
    /// <param name="expression">Expression(s) to replace</param>
    /// <param name="onlySbcFields">Whether to replace only SBC fields</param>
    /// <param name="encodeResult">Whether the final result shall be encoded</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Replaced expression(s)</returns>
    /// <exception cref="CodeParserException">Failed to parse expression(s)</exception>
    /// <exception cref="OperationCanceledException">Code was cancelled</exception>
    public async Task<string> EvaluateExpression(Code code, string expression, bool onlySbcFields, bool encodeResult, CancellationToken cancellationToken = default)
    {
        object? result = await EvaluateExpressionRaw(code, expression, onlySbcFields, cancellationToken);
        return (onlySbcFields && result is string resultString) ? resultString : ObjectToString(result, false, encodeResult, code);
    }
}
