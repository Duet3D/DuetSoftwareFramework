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
            // Register custom fileexists() function, evaluating it via RRF would cause a timeout
            Model.Expressions.CustomFunctions.Add("fileexists", FileExists);
        }

        /// <summary>
        /// Implementation for fileexists() meta G-code call
        /// </summary>
        /// <param name="functionName">Function name</param>
        /// <param name="argument">Function argument</param>
        /// <returns>Whether the file exists</returns>
        public static async Task<object> FileExists(string functionName, object[] arguments)
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
