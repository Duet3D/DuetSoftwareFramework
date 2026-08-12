using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Files;
using DuetControlServer.Link;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// Meta G-code keyword handler
/// </summary>
/// <param name="codeProcessor">Code processor</param>
/// <param name="expressions">Meta G-code expression parser</param>
/// <param name="filePathResolver">File path resolver</param>
/// <param name="variableStore">Variables in scope</param>
/// <param name="logger">Logger</param>
/// <param name="settings">Settings</param>
public sealed class KeywordHandler(CodeProcessor codeProcessor, Expressions expressions, FilePathResolver filePathResolver, VariableStore variableStore, ILogger<KeywordHandler> logger, IOptions<Settings> settings) : ICodeHandler
{
    // Private fields
    private readonly ILogger<KeywordHandler> _logger = logger;
    private readonly Settings _settings = settings.Value;

    /// <summary>
    /// Process a non-branching meta G-code statement
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the code if the code completed</returns>
    /// <exception cref="OperationCanceledException">The code was cancelled</exception>
    public async ValueTask<Message> ProcessAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.KeywordArgument is null)
        {
            throw new ArgumentException("KeywordArgument must not be empty");
        }

        bool inSingleQuotes = false, startedSingleQuote = false, inDoubleQuotes = false;
        switch (code.Keyword)
        {
            case KeywordType.Echo:
            case KeywordType.Abort:
                if (!await codeProcessor.FlushAsync(code, false, cancellationToken: cancellationToken))
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        // echo and abort may be executed only if the channel is active
                        return new Message();
                    }
                    throw new OperationCanceledException();
                }

                string? result;
                if (code.Keyword == KeywordType.Echo)
                {
                    string keywordArgument = code.KeywordArgument.TrimStart();
                    if (keywordArgument.StartsWith('>'))
                    {
                        // File redirection requested
                        bool append = keywordArgument.StartsWith(">>"), appendNoNL = keywordArgument.StartsWith(">>>");
                        keywordArgument = keywordArgument[(appendNoNL ? 3 : (append ? 2 : 1))..].TrimStart();

                        // Get the file string or expression to write to
                        bool isComplete = false;
                        int numCurlyBraces = 0;
                        string filenameExpression = string.Empty;
                        for (int i = 0; i < keywordArgument.Length; i++)
                        {
                            char c = keywordArgument[i];
                            if (inSingleQuotes)
                            {
                                if (c == '\'' && !startedSingleQuote)
                                {
                                    inSingleQuotes = false;
                                    isComplete = numCurlyBraces == 0;
                                }
                                startedSingleQuote = false;
                            }
                            else if (inDoubleQuotes)
                            {
                                if (c == '"')
                                {
                                    inDoubleQuotes = false;
                                    isComplete = numCurlyBraces == 0;
                                }
                            }
                            else if (c == '\'')
                            {
                                inSingleQuotes = startedSingleQuote = true;
                            }
                            else if (c == '"')
                            {
                                inDoubleQuotes = true;
                            }
                            else if (c == '{')
                            {
                                numCurlyBraces++;
                            }
                            else if (c == '}')
                            {
                                numCurlyBraces--;
                                isComplete = numCurlyBraces == 0;
                            }
                            else if (char.IsWhiteSpace(c))
                            {
                                // Whitespaces after the initial > or >> are not permitted
                                isComplete = numCurlyBraces == 0;
                            }

                            if (isComplete)
                            {
                                if (i == 0)
                                {
                                    return new Message(MessageType.Error, "Missing filename for file redirection");
                                }

                                filenameExpression = keywordArgument[..(i + 1)];
                                code.KeywordArgument = keywordArgument[(i + 1)..];
                                break;
                            }
                        }

                        // Evaluate the filename and result to write
                        string filename = await expressions.EvaluateExpressionToStringAsync(code, filenameExpression, false, false, cancellationToken);
                        string physicalFilename = await filePathResolver.ToPhysicalAsync(filename, FileDirectory.System, cancellationToken), parentDirectory = Path.GetDirectoryName(physicalFilename)!;
                        result = await expressions.EvaluateAsync(code, true, cancellationToken);

                        // Write it to the designated file
                        _logger.LogDebug("{Operation} '{Expression}' to {File}", append ? "Appending" : "Writing", result, filename);

                        if (!Directory.Exists(parentDirectory))
                        {
                            Directory.CreateDirectory(parentDirectory);
                        }

                        await using (FileStream fs = new(physicalFilename, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, _settings.FileBufferSize))
                        {
                            await using StreamWriter writer = new(fs, Encoding.UTF8, _settings.FileBufferSize);
                            if (appendNoNL)
                            {
                                await writer.WriteAsync(result);
                            }
                            else
                            {
                                await writer.WriteLineAsync(result);
                            }
                        }

                        // Done
                        return new Message();
                    }
                }
                result = await expressions.EvaluateAsync(code, true, cancellationToken);

                if (code.Keyword == KeywordType.Abort)
                {
                    await codeProcessor.AbortAllFilesAsync(code.Channel, cancellationToken);
                }
                return new Message(MessageType.Success, result ?? string.Empty);

            case KeywordType.Global:
            case KeywordType.Var:
            case KeywordType.Set:
                // Do not attempt to process cancelled codes
                cancellationToken.ThrowIfCancellationRequested();

                // Validate the keyword and expression first
                string varName = string.Empty, expression = string.Empty;
                bool inExpression = false, wantExpression = false;
                int numSquareBrackets = 0;
                foreach (char c in code.KeywordArgument)
                {
                    if (inExpression)
                    {
                        expression += c;
                    }
                    else if (c == '=')
                    {
                        inExpression = true;
                    }
                    else if (numSquareBrackets > 0 || c == '[')
                    {
                        // Contents of square brackets are not trimmed
                        if (inSingleQuotes)
                        {
                            inSingleQuotes = c != '\'' || startedSingleQuote;
                            startedSingleQuote = false;
                        }
                        else if (inDoubleQuotes)
                        {
                            inDoubleQuotes = c != '"';
                        }
                        else if (c == '\'')
                        {
                            inSingleQuotes = startedSingleQuote = true;
                        }
                        else if (c == '"')
                        {
                            inDoubleQuotes = true;
                        }
                        else if (c == '[')
                        {
                            numSquareBrackets++;
                        }
                        else if (c == ']')
                        {
                            numSquareBrackets--;
                        }
                        varName += c;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        // Permit only a certain subset of chars for variable names
                        if (!char.IsLetterOrDigit(c) && c != '_' && (c != '.' || code.Keyword != KeywordType.Set) || wantExpression)
                        {
                            if (!await codeProcessor.FlushAsync(code, false, ifExecuting: code.Keyword == KeywordType.Global, cancellationToken: cancellationToken))
                            {
                                throw new OperationCanceledException();
                            }
                            throw new CodeParserException("expected '='", code);
                        }
                        varName += c;
                    }
                    else if (!string.IsNullOrEmpty(varName))
                    {
                        // Don't allow illegal variable names like "global. fo o", although a name like "global.foo [4]" is valid
                        wantExpression = true;
                    }
                }

                if (!await codeProcessor.FlushAsync(code, false, ifExecuting: code.Keyword == KeywordType.Global || (code.Keyword == KeywordType.Set && varName.StartsWith("global.")), cancellationToken: cancellationToken))
                {
                    // global and set global.* may be only executed if the corresponding channel is active
                    throw new OperationCanceledException();
                }

                // Check the variable and expression
                if (string.IsNullOrWhiteSpace(varName))
                {
                    throw new CodeParserException("expected a new variable name", code);
                }
                if (!inExpression)
                {
                    throw new CodeParserException("expected '='", code);
                }

                // Work out what is being assigned to. "set" names the scope itself, the other two imply it
                bool isGlobal = code.Keyword == KeywordType.Global;
                if (code.Keyword == KeywordType.Set)
                {
                    varName = await expressions.EvaluateExpressionToStringAsync(code, varName, true, false, cancellationToken);
                    if (varName.StartsWith("global.", StringComparison.Ordinal))
                    {
                        isGlobal = true;
                        varName = varName["global.".Length..];
                    }
                    else if (varName.StartsWith("var.", StringComparison.Ordinal))
                    {
                        varName = varName["var.".Length..];
                    }
                    else
                    {
                        // Parameters are read-only, so "param." lands here too, as it does in RepRapFirmware
                        throw new CodeParserException("expected a global or local variable", code);
                    }
                }
                string fullVarName = (isGlobal ? "global." : "var.") + varName;

                // "set" may name an element of an array; "var" and "global" name the variable they create
                if (!VariableStore.TrySplitIndexedName(varName, out varName, out IReadOnlyList<string> indexExpressions))
                {
                    throw new CodeParserException($"expected a variable name, got '{fullVarName}'", code);
                }
                if (indexExpressions.Count > 0 && code.Keyword != KeywordType.Set)
                {
                    throw new CodeParserException($"expected a new variable name, got '{fullVarName}'", code);
                }

                // An index is an expression of its own, which is what makes "set var.a[var.i] = ..." work
                int[] indices = new int[indexExpressions.Count];
                for (int index = 0; index < indexExpressions.Count; index++)
                {
                    object? indexValue = await expressions.EvaluateExpressionToValueAsync(code, indexExpressions[index], false, cancellationToken);
                    indices[index] = indexValue switch
                    {
                        int intIndex => intIndex,
                        long longIndex when longIndex is >= 0 and <= int.MaxValue => (int)longIndex,
                        uint uintIndex when uintIndex <= int.MaxValue => (int)uintIndex,
                        _ => throw new CodeParserException(Meta.Parsing.ExpressionErrors.ExpectedNonNegativeInt, code)
                    };
                    if (indices[index] < 0)
                    {
                        throw new CodeParserException(Meta.Parsing.ExpressionErrors.ExpectedNonNegativeInt, code);
                    }
                }

                // Evaluate what it is being assigned to
                object? value = await expressions.EvaluateExpressionToValueAsync(code, expression, false, cancellationToken);

                // Assign it. A "var" or "global" statement creates, "set" assigns to what already exists;
                // neither does the other's job, so that a name cannot quietly change meaning halfway through a file
                if (code.Keyword == KeywordType.Set)
                {
                    VariableAssignment assignment;
                    if (indices.Length > 0)
                    {
                        assignment = isGlobal
                            ? await variableStore.TryAssignGlobalElementAsync(varName, indices, value, cancellationToken)
                            : variableStore.For(code).TryAssignVariableElement(varName, indices, value);
                    }
                    else
                    {
                        bool assigned = isGlobal
                            ? await variableStore.TryAssignGlobalAsync(varName, value, cancellationToken)
                            : variableStore.For(code).TryAssignVariable(varName, value);
                        assignment = assigned ? VariableAssignment.Assigned : VariableAssignment.UnknownVariable;
                    }

                    switch (assignment)
                    {
                        case VariableAssignment.Assigned:
                            break;
                        case VariableAssignment.NotAnArray:
                            throw new CodeParserException("Expected an array expression", code);
                        case VariableAssignment.IndexOutOfRange:
                            throw new CodeParserException(Meta.Parsing.ExpressionErrors.ArrayIndexOutOfRange, code);
                        default:
                            throw new CodeParserException($"unknown variable '{varName}'", code);
                    }
                }
                else if (isGlobal)
                {
                    if (!await variableStore.TryCreateGlobalAsync(varName, value, cancellationToken))
                    {
                        throw new CodeParserException($"variable '{varName}' already exists", code);
                    }
                }
                else
                {
                    if (!variableStore.For(code).TryCreateVariable(varName, value))
                    {
                        throw new CodeParserException($"variable '{varName}' already exists", code);
                    }

                    // The block that created it is the block that deletes it again
                    if (code.File is not null)
                    {
                        using (await code.File.LockAsync(cancellationToken))
                        {
                            code.File.AddLocalVariable(varName);
                        }
                    }
                }
                _logger.LogDebug("Set variable {Variable} to {Value}", fullVarName, value);
                return new Message();
        }

        throw new NotSupportedException($"Unsupported keyword '{code.Keyword}'");
    }

    /// <summary>
    /// React to an executed T-code before its result is returned
    /// </summary>
    /// <param name="code">Code processed by RepRapFirmware</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result to output</returns>
    public ValueTask CodeExecutedAsync(Commands.Code code, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
