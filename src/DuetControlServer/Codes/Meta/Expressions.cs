using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Link;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Codes.Meta;

/// <summary>
/// Functionality for meta G-code expressions
/// </summary>
/// <param name="filter">Object model filter</param>
/// <param name="model">Object model</param>
/// <param name="variableStore">Variables in scope, by the code being evaluated</param>
public sealed class Expressions(Model.Filter filter, Model.ObjectModel model, VariableStore variableStore)
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
            lastC = c;
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
    public bool ContainsSbcFields(string expression)
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
    /// Extracts all object model field paths referenced in the given expression string.
    /// The returned paths use dot-notation (e.g. "heat.heaters", "state.status").
    /// Both SBC and non-SBC OM fields are included so that any change to a referenced
    /// field triggers re-evaluation of the expression.
    /// </summary>
    /// <param name="expression">Expression to analyse</param>
    /// <returns>Set of field paths referenced in the expression</returns>
    public IReadOnlySet<string> ExtractFieldPaths(string expression)
    {
        // Strip bracketed index expressions first so that e.g. tools[0].current becomes tools.current
        StringBuilder stripped = new(expression.Length);
        int bracketDepth = 0;
        bool inQuotes = false;
        foreach (char c in expression)
        {
            if (inQuotes)
            {
                inQuotes = c != '"';
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == '[')
            {
                bracketDepth++;
            }
            else if (c == ']')
            {
                if (bracketDepth > 0) bracketDepth--;
            }
            else if (bracketDepth == 0)
            {
                stripped.Append(c);
            }
        }

        // Collect dot-separated identifier tokens — any token that starts with a letter and
        // contains at least one dot is treated as an OM field path to watch
        HashSet<string> result = [];
        StringBuilder token = new();
        bool clearToken = false;
        foreach (char c in stripped.ToString())
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '_')
            {
                if (clearToken)
                {
                    token.Clear();
                    clearToken = false;
                }
                token.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                clearToken = true;
            }
            else
            {
                if (token.Length > 0)
                {
                    string t = token.ToString();
                    if (char.IsLetter(t[0]) && t.Contains('.'))
                    {
                        result.Add(t);
                    }
                    token.Clear();
                }
            }
        }

        if (token.Length > 0)
        {
            string t = token.ToString();
            if (char.IsLetter(t[0]) && t.Contains('.'))
            {
                result.Add(t);
            }
        }

        return result;
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
        if (expression == "iterations" || expression == "line")
        {
            return true;
        }

        // Strip bracketed index expressions (e.g. tools[0].current -> tools.current) before splitting
        StringBuilder strippedExpression = new(expression.Length);
        int bracketDepth = 0;
        foreach (char c in expression)
        {
            if (c == '[') bracketDepth++;
            else if (c == ']') { if (bracketDepth > 0) bracketDepth--; }
            else if (bracketDepth == 0) strippedExpression.Append(c);
        }

        // This walks the generated type descriptors, so it neither reads from nor instantiates the OM
        IModelObjectDescriptor descriptor = model.Descriptor;
        foreach (string pathItem in strippedExpression.ToString().Split('.'))
        {
            if (string.IsNullOrEmpty(pathItem))
            {
                return false;
            }

            ModelPropertyDescriptor? property = descriptor.FindProperty(pathItem, true);
            if (property is null)
            {
                return false;
            }

            if ((property.Flags & ModelPropertyFlags.SbcProperty) != 0)
            {
                return true;
            }

            if (property.ElementDescriptor is null)
            {
                // Reached a scalar or non-model item type; no SBC property found along this path
                break;
            }
            descriptor = property.ElementDescriptor;
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
        if (!string.IsNullOrEmpty(code.KeywordArgument))
        {
            if (code.Keyword == KeywordType.Echo)
            {
                StringBuilder builder = new();
                foreach (string expression in SplitExpression(code.KeywordArgument))
                {
                    string result = await EvaluateExpressionToStringAsync(code, expression, !evaluateAll, false, cancellationToken);
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
                return await EvaluateExpressionToStringAsync(code, keywordArgument, !evaluateAll, false, cancellationToken);
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
            return await EvaluateExpressionToStringAsync(code, keywordExpression.Trim(), !evaluateAll, false, cancellationToken);
        }

        if (code.Parameters.Any(parameter => parameter.IsExpression))
        {
            List<CodeParameter> newParameters = [];
            foreach (CodeParameter parameter in code.Parameters)
            {
                if (parameter.IsExpression)
                {
                    string trimmedExpression = ((string)parameter).Trim();
                    string parameterValue = await EvaluateExpressionToStringAsync(code, trimmedExpression, !evaluateAll, !evaluateAll, cancellationToken);
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
        if (obj is Enum)
        {
            // Enums are represented by their JSON name, which does not have to match the CLR name
            string jsonName = JsonSerializer.Serialize(obj, JsonHelper.DefaultJsonOptions.GetTypeInfo(obj.GetType())).Trim('"');
            return encodeValues ? encodeString(jsonName) : jsonName;
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
            return '[' + string.Join(',', boolArray.Select(boolValue => boolValue ? "true" : "false")) + ']';
        }
        if (obj is char[] charArray)
        {
            return '[' + string.Join(',', charArray.Select(charValue => $"'{charValue}'")) + ']';
        }
        if (obj is string[] stringArray)
        {
            return '[' + string.Join(',', stringArray.Select(stringValue => encodeString(stringValue))) + ']';
        }
        if (obj is DriverId[] driverIdArray)
        {
            return '[' + string.Join(',', driverIdArray.Select(driverIdValue => encodeString(driverIdValue.ToString()))) + ']';
        }
        if (obj is int[] intArray)
        {
            return '[' + string.Join(',', intArray.Select(intValue => intValue.ToString("G", CultureInfo.InvariantCulture))) + ']';
        }
        if (obj is uint[] uintArray)
        {
            return '[' + string.Join(',', uintArray.Select(uintValue => uintValue.ToString("G", CultureInfo.InvariantCulture))) + ']';
        }
        if (obj is float[] floatArray)
        {
            return '[' + string.Join(',', floatArray.Select(floatValue => floatValue.ToString("G", CultureInfo.InvariantCulture))) + ']';
        }
        if (obj is long[] longArray)
        {
            return '[' + string.Join(',', longArray.Select(longValue => longValue.ToString("G", CultureInfo.InvariantCulture))) + ']';
        }
        if (obj is object[] objectArray)
        {
            return '[' + string.Join(',', objectArray.Select(objectValue => ObjectToString(objectValue, false, encodeValues, code))) + ']';
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
    /// Evaluate expression(s), returning the resulting value (or the partially-substituted string when only SBC fields are replaced)
    /// </summary>
    /// <param name="code">Code holding the expression(s)</param>
    /// <param name="expression">Expression(s) to replace</param>
    /// <param name="onlySbcFields">Whether to replace only SBC fields</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Resulting value, or the partially-substituted expression</returns>
    /// <exception cref="CodeParserException">Failed to parse expression(s)</exception>
    /// <exception cref="OperationCanceledException">Code was cancelled</exception>
    public async Task<object?> EvaluateExpressionToValueAsync(Code code, string expression, bool onlySbcFields, CancellationToken cancellationToken = default)
    {
        int i = 0;

        // What the running code can see: the object model, its own variables, and where it is in its file
        Parsing.IExpressionEvaluationContext context = new ExpressionContext(() => code.File?.GetIterations(code),
                                                                            (int)(code.LineNumber ?? 0), filter,
                                                                            variableStore.For(code), model);

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
                        lastC = c;
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
            cancellationToken.ThrowIfCancellationRequested();

            // Not a literal, so it is an expression of its own - a function argument or an array index
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                if (Parsing.MetaExpressionParser.TryEvaluate(subExpression, context, out object? parsedResult))
                {
                    return parsedResult;
                }
            }
            throw new CodeParserException(string.Format(Parsing.ExpressionErrors.CannotEvaluate, subExpression.Trim()), code);
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

        // First pass: evaluate the whole expression in one go, which is what nearly everything takes
        if (!onlySbcFields)
        {
            bool resolvedLocally;
            object? localResult;
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                resolvedLocally = Parsing.MetaExpressionParser.TryEvaluate(expression, context, out localResult);
            }
            if (resolvedLocally)
            {
                return localResult;
            }
        }

        // Second pass: substitute what only an asynchronous lookup can produce - the custom functions
        // fileexists(), fileread() and exists() - which the synchronous evaluator above cannot call
        string expressionContent = await eatExpression('\0');
        if (onlySbcFields)
        {
            return expressionContent;
        }

        // Don't return exceptions from cancelled codes
        cancellationToken.ThrowIfCancellationRequested();

        // Those substitutions are encoded as literals, so what came back is an expression the
        // evaluator can finish. Anything that still will not resolve is an error: there is no
        // firmware behind this to forward it to, and a silent null reads as a valid answer
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (Parsing.MetaExpressionParser.TryEvaluate(expressionContent, context, out object? substitutedResult))
            {
                return substitutedResult;
            }
        }
        throw new CodeParserException(string.Format(Parsing.ExpressionErrors.CannotEvaluate, expression.Trim()), code);
    }

    /// <summary>
    /// Evaluate expression(s) and return the result as a string
    /// </summary>
    /// <param name="code">Code holding the expression(s)</param>
    /// <param name="expression">Expression(s) to replace</param>
    /// <param name="onlySbcFields">Whether to replace only SBC fields</param>
    /// <param name="encodeResult">Whether the final result shall be encoded</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Result as a string</returns>
    /// <exception cref="CodeParserException">Failed to parse expression(s)</exception>
    /// <exception cref="OperationCanceledException">Code was cancelled</exception>
    public async Task<string> EvaluateExpressionToStringAsync(Code code, string expression, bool onlySbcFields, bool encodeResult, CancellationToken cancellationToken = default)
    {
        object? result = await EvaluateExpressionToValueAsync(code, expression, onlySbcFields, cancellationToken);
        return (onlySbcFields && result is string resultString) ? resultString : ObjectToString(result, false, encodeResult, code);
    }

    /// <summary>
    /// Evaluation context backing the SBC-side expression evaluator with the running code and the object model mirror
    /// </summary>
    /// <param name="iterationsProvider">Provides the current loop iteration count lazily (it errors outside a loop)</param>
    /// <param name="lineNumber">Current G-code line number</param>
    /// <param name="filter">Object model filter</param>
    /// <param name="variables">Variables the running code can see</param>
    /// <param name="objectModel">Object model, which is where the global variables live</param>
    internal sealed class ExpressionContext(Func<int?> iterationsProvider, int lineNumber, Model.Filter filter, VariableSet variables, Model.ObjectModel objectModel) : Parsing.IExpressionEvaluationContext
    {
        /// <inheritdoc/>
        public int? Iterations => iterationsProvider();

        /// <inheritdoc/>
        public int LineNumber => lineNumber;

        /// <inheritdoc/>
        public bool TryResolveIdentifier(string path, bool wantExists, bool wantArrayLength, out object? value)
        {
            value = null;

            // Variables are not object model fields: which ones a code can see depends on the file it
            // came from, so they are resolved from the set it was given rather than through the filter
            if (TryResolveVariable(path, wantExists, wantArrayLength, out value))
            {
                return true;
            }

            if (wantExists)
            {
                if (wantArrayLength)
                {
                    return false;       // exists(#...) is not answered here yet
                }

                value = filter.GetSpecific(path, false, out _);
                return true;
            }

            // The whole object model is resolved here. It used to be only the SBC-owned branches, because
            // everything else was the firmware's to answer; DuetControlServer owns all of it now
            if (!filter.GetSpecific(path, false, out object? field))
            {
                return false;
            }

            if (wantArrayLength)
            {
                // The count is read while the model lock is held and returned as an int, so it is safe afterwards
                switch (field)
                {
                    case string s:
                        value = s.Length;
                        return true;
                    case ICollection collection:
                        value = collection.Count;
                        return true;
                    default:
                        return false;   // the length operator only applies to an array or a string
                }
            }

            return TryConvertField(field, out value);
        }

        /// <summary>
        /// Convert an object model field into a value an expression can hold
        /// </summary>
        /// <param name="field">Field as the object model stores it</param>
        /// <param name="value">The same thing as an expression value</param>
        /// <returns>True if it could be converted</returns>
        /// <remarks>
        /// <para>
        /// Everything handed back has to be immutable, because it is read under the object model lock and
        /// used after that lock has been released, while the update task goes on mutating what it was read
        /// from. So a collection is copied rather than passed on, and an object becomes a stand-in that
        /// holds nothing - which is all RepRapFirmware does with one either.
        /// </para>
        /// <para>
        /// One function decides this for a field and for the elements inside a collection, so that an array
        /// cannot end up holding something a scalar of the same type would have been refused
        /// </para>
        /// </remarks>
        private static bool TryConvertField(object? field, out object? value)
        {
            switch (field)
            {
                // Values the language has, handed on as they are
                case null or bool or char or string or int or uint or long or ulong or float or double or DateTime or DriverId:
                    value = field;
                    return true;

                // An enum is a string in the object model - "processing", "inactive" - and that is what a
                // macro compares it against, so it is one here too rather than a CLR enumerator name
                case Enum enumValue:
                    value = JsonSerializer.Serialize(enumValue, JsonHelper.DefaultJsonOptions.GetTypeInfo(enumValue.GetType())).Trim('"');
                    return true;

                // A dictionary is an object, not an array: its elements are keys and values, not values
                case IModelDictionary:
                    value = Parsing.ObjectModelValue.Instance;
                    return true;

                case ICollection collection:
                    {
                        value = null;
                        object?[] snapshot = new object?[collection.Count];
                        int index = 0;
                        foreach (object? element in collection)
                        {
                            if (!TryConvertField(element, out snapshot[index++]))
                            {
                                return false;
                            }
                        }
                        value = snapshot;
                        return true;
                    }

                case IModelObject:
                    value = Parsing.ObjectModelValue.Instance;
                    return true;

                default:
                    value = null;
                    return false;
            }
        }

        /// <summary>
        /// Resolve a path that names a variable rather than an object model field
        /// </summary>
        /// <param name="path">Fully-qualified identifier path</param>
        /// <param name="wantExists">Caller only wants to know whether the variable exists</param>
        /// <param name="wantArrayLength">The length operator '#' was applied</param>
        /// <param name="value">Value the variable holds, or whether it exists</param>
        /// <returns>True if the path named a variable and could be resolved</returns>
        /// <exception cref="CodeParserException">The variable does not exist</exception>
        /// <remarks>
        /// <c>global</c> is read from the object model, where it lives, but through this path rather
        /// than the filter: it is not an SBC property, so the filter refuses it in the default mode
        /// </remarks>
        private bool TryResolveVariable(string path, bool wantExists, bool wantArrayLength, out object? value)
        {
            value = null;

            string name;
            bool isParameter = false, isGlobal = false;
            if (path.StartsWith("var.", StringComparison.Ordinal))
            {
                name = path["var.".Length..];
            }
            else if (path.StartsWith("param.", StringComparison.Ordinal))
            {
                name = path["param.".Length..];
                isParameter = true;
            }
            else if (path.StartsWith("global.", StringComparison.Ordinal))
            {
                name = path["global.".Length..];
                isGlobal = true;
            }
            else
            {
                return false;
            }

            // The parser folds evaluated indices into the path it asks about, so var.x[2] arrives here
            // as one string. A field of a variable is not a thing: a variable holds a value, not an object
            if (!VariableStore.TrySplitIndexedName(name, out name, out IReadOnlyList<string> indexExpressions) ||
                !VariableStore.TryParseIndices(indexExpressions, out IReadOnlyList<int> indices))
            {
                return false;
            }

            bool found;
            if (isGlobal)
            {
                found = objectModel.Global.TryGetValue(name, out JsonElement? globalValue) &&
                        VariableStore.TryFromJson(globalValue, out value);
            }
            else
            {
                found = isParameter ? variables.TryGetParameter(name, out value) : variables.TryGetVariable(name, out value);
            }

            if (!found)
            {
                value = null;
                if (wantExists)
                {
                    value = false;
                    return true;
                }
                throw new CodeParserException(string.Format(isParameter ? Parsing.ExpressionErrors.UnknownParameter
                                                                       : Parsing.ExpressionErrors.UnknownVariable, name));
            }

            // Apply the indices, if any. An index past the end is an error when the value is being read
            // and merely a "no" when its existence is the question
            foreach (int index in indices)
            {
                int length = value switch
                {
                    object?[] array => array.Length,
                    string text => text.Length,
                    _ => -1
                };
                if (length < 0)
                {
                    value = null;
                    if (wantExists)
                    {
                        value = false;
                        return true;
                    }
                    return false;       // an index applied to something that is not indexable
                }
                if (index < 0 || index >= length)
                {
                    value = null;
                    if (wantExists)
                    {
                        value = false;
                        return true;
                    }
                    throw new CodeParserException(Parsing.ExpressionErrors.ArrayIndexOutOfRange);
                }
                value = (value is object?[] indexedArray) ? indexedArray[index] : ((string)value!)[index];
            }

            if (wantExists)
            {
                value = true;
                return true;
            }

            if (wantArrayLength)
            {
                switch (value)
                {
                    case object?[] array:
                        value = array.Length;
                        return true;
                    case string text:
                        value = text.Length;
                        return true;
                    default:
                        value = null;
                        return false;   // the length operator only applies to an array or a string
                }
            }
            return true;
        }

        /// <inheritdoc/>
        public bool TryCallFunction(string name, object?[] arguments, bool wantArrayLength, out object? value)
        {
            // Meta G-code functions are not implemented on the SBC yet, so forward them to the firmware
            value = null;
            return false;
        }
    }
}
