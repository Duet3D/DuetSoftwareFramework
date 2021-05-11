using System;

namespace DuetControlServer.Files.ImageProcessing
{
    public class ImageProcessingException : Exception
    {
        public ImageProcessingException() { }
        public ImageProcessingException(string message, Exception ex) : base(message, ex)
        {
        }
    }
}
