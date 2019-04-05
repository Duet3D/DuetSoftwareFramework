using System;

namespace DuetControlServer
{
    /// <summary>
    /// Static class used for message logging via the object model
    /// </summary>
    public static class Log
    {
        /// <summary>
        /// Log an information message
        /// </summary>
        /// <param name="message"></param>
        public static async void LogInfo(string message)
        {
            using (await Model.Provider.AccessReadWrite())
            {
                Model.Provider.Get.Messages.Add(new DuetAPI.Message(DuetAPI.MessageType.Success, message));
            }
            Console.WriteLine("[info] " + message);
        }

        /// <summary>
        /// Log a warning message
        /// </summary>
        /// <param name="message"></param>
        public static async void LogWarning(string message)
        {
            using (await Model.Provider.AccessReadWrite())
            {
                Model.Provider.Get.Messages.Add(new DuetAPI.Message(DuetAPI.MessageType.Warning, message));
            }
            Console.WriteLine("[warn] " + message);
        }

        /// <summary>
        /// Log an error message
        /// </summary>
        /// <param name="message"></param>
        public static async void LogError(string message)
        {
            using (await Model.Provider.AccessReadWrite())
            {
                Model.Provider.Get.Messages.Add(new DuetAPI.Message(DuetAPI.MessageType.Error, message));
            }
            Console.WriteLine("[err] " + message);
        }
    }
}
