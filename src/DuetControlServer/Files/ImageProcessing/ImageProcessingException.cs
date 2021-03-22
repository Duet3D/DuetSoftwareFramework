using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
