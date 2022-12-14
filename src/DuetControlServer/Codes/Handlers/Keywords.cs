using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Model;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Codes.Handlers
{
    /// <summary>
    /// Functions for interpreting meta G-code keywords
    /// </summary>
    public static class Keywords
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Process a T-code that should be interpreted by the control server
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Result of the code if the code completed, else null</returns>
        public static async Task<Message> Process(Code code)
        {
            if (!await Processor.FlushAsync(code, false))
            {
                throw new OperationCanceledException();
            }

            if (code.Keyword == KeywordType.Echo || code.Keyword == KeywordType.Abort)
            {
                string result;
                if (code.Keyword == KeywordType.Echo && !string.IsNullOrEmpty(code.KeywordArgument))
                {
                    string keywordArgument = code.KeywordArgument.TrimStart();
                    if (keywordArgument.StartsWith('>'))
                    {
                        // File redirection requested
                        bool append = keywordArgument.StartsWith(">>");
                        keywordArgument = keywordArgument[(append ? 2 : 1)..].TrimStart();

                        // Get the file string or expression to write to
                        bool inQuotes = false, isComplete = false;
                        int numCurlyBraces = 0;
                        string filenameExpression = string.Empty;
                        for (int i = 0; i < keywordArgument.Length; i++)
                        {
                            char c = keywordArgument[i];
                            if (inQuotes)
                            {
                                if (c == '"')
                                {
                                    inQuotes = false;
                                    isComplete = numCurlyBraces == 0;
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
                        string filename = await Expressions.EvaluateExpression(code, filenameExpression, false, false);
                        string physicalFilename = await FilePath.ToPhysicalAsync(filename, FileDirectory.System);
                        result = await Expressions.Evaluate(code, true);

                        // Write it to the SD card
                        _logger.Debug("{0} '{1}' to {2}", append ? "Appending" : "Writing", result, filename);
                        await using (FileStream fs = new(physicalFilename, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, Settings.FileBufferSize))
                        {
                            await using StreamWriter writer = new(fs, Encoding.UTF8, Settings.FileBufferSize);
                            await writer.WriteLineAsync(result);
                        }

                        // Done
                        return new Message();
                    }
                }
                result = await Expressions.Evaluate(code, true);

                if (code.Keyword == KeywordType.Abort)
                {
                    await SPI.Interface.AbortAllAsync(code.Channel);
                }
                return new Message(MessageType.Success, result);
            }

            if (code.Keyword == KeywordType.Global ||
                code.Keyword == KeywordType.Var ||
                code.Keyword == KeywordType.Set)
            {
                // Validate the keyword and expression first
                string varName = string.Empty, expression = string.Empty;
                bool inExpression = false, wantExpression = false;
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
                    else if (!char.IsWhiteSpace(c))
                    {
                        if (!char.IsLetterOrDigit(c) && c != '_' && (c != '.' || code.Keyword != KeywordType.Set) || wantExpression)
                        {
                            throw new CodeParserException("expected '='", code);
                        }
                        varName += c;
                    }
                    else if (!string.IsNullOrEmpty(varName))
                    {
                        wantExpression = true;
                    }
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

                // Replace SBC fields and assign the variable
                expression = await Expressions.Evaluate(code, false);

                // Assign the variable
                string fullVarName = varName;
                if (code.Keyword != KeywordType.Set)
                {
                    fullVarName = (code.Keyword == KeywordType.Global ? "global." : "var.") + varName;
                }
                object varContent = await SPI.Interface.SetVariable(code.Channel, code.Keyword != KeywordType.Set, fullVarName, expression);
                _logger.Debug("Set variable {0} to {1}", fullVarName, varContent);

                // Keep track of it
                if (code.Keyword == KeywordType.Var && code.File != null)
                {
                    using (await code.File.LockAsync())
                    {
                        code.File.AddLocalVariable(varName);
                    }
                }
                return new Message();
            }

            throw new NotSupportedException($"Unsupported keyword '{code.Keyword}'");
        }
    }
}
