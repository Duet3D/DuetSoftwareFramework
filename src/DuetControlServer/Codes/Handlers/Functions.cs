using DuetAPI;
using System;
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
            Model.Expressions.CustomFunctions.Add("fileexists", FileExists);    // Register custom fileexists() function, evaluating it via RRF would cause a deadlock
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
                return System.IO.File.Exists(resolvedPath);
            }
            throw new ArgumentException("fileexists requires a string argument");
        }
    }
}
