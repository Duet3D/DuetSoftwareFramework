using System;

namespace DuetControlServer.Files.ImageProcessing
{
    /// <summary>
    /// Exception that is thrown when a thumbnail could not be processed
    /// </summary>
    public class ImageProcessingException : Exception
    {
        /// <summary>
        /// Default exception constructor
        /// </summary>
        public ImageProcessingException() { }

        /// <summary>
        /// Special exception constructor
        /// </summary>
        /// <param name="message">Exception message</param>
        /// <param name="ex">Inner exception</param>
        public ImageProcessingException(string message, Exception ex) : base(message, ex) { }
    }
}
