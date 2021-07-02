using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using System;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Codes
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
        public static async Task<CodeResult> Process(Code code)
        {
            if (!await SPI.Interface.Flush(code, false))
            {
                throw new OperationCanceledException();
            }

            if (code.Keyword == KeywordType.Echo || code.Keyword == KeywordType.Abort ||
#pragma warning disable CS0618 // Type or member is obsolete
                code.Keyword == KeywordType.Return)
            {
                if (code.Keyword == KeywordType.Return)
                {
                    await Utility.Logger.LogOutput(MessageType.Warning, "'return' keyword is deprecated and will be removed soon");
                }
#pragma warning restore CS0618 // Type or member is obsolete

                string result = string.IsNullOrEmpty(code.KeywordArgument) ? string.Empty : await Expressions.Evaluate(code, true);

                if (code.Keyword == KeywordType.Abort)
                {
                    await SPI.Interface.AbortAll(code.Channel);
                }
                return new CodeResult(MessageType.Success, result);
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
                        if ((!char.IsLetterOrDigit(c) && c != '_' && (c != '.' || code.Keyword != KeywordType.Set)) || wantExpression)
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

                // Replace Linux fields and assign the variable
                if (code.Keyword != KeywordType.Set)
                {
                    varName = (code.Keyword == KeywordType.Global ? "global." : "var.") + varName;
                }
                expression = await Expressions.Evaluate(code, false);
                object varContent = await SPI.Interface.SetVariable(code.Channel, code.Keyword != KeywordType.Set, varName, expression);
                _logger.Debug("Assigned variable {0} to {1}", varName, varContent);
                return new CodeResult(MessageType.Success, string.Empty);
            }

            throw new NotSupportedException($"Unsupported keyword '{code.Keyword}'");
        }
    }
}
