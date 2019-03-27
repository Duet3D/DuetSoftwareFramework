namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Enum representing the allowed binary data types of parameters
    /// </summary>
    public enum DataType : byte
    {
        /// <summary>
        /// Parameter is a signed integer
        /// </summary>
        Int = 0,
        
        /// <summary>
        /// Parameter is an unsigned integer
        /// </summary>
        UInt = 1,
        
        /// <summary>
        /// Parameter is a float
        /// </summary>
        Float = 2,
        
        /// <summary>
        /// Parameter is a signed integer array
        /// </summary>
        IntArray = 3,
        
        /// <summary>
        /// Parameter is an unsigned integer array
        /// </summary>
        UIntArray = 4,
        
        /// <summary>
        /// Parameter is a float array
        /// </summary>
        FloatArray = 5,
        
        /// <summary>
        /// Parameter is a UTF-8 string
        /// </summary>
        String = 6,
        
        /// <summary>
        /// Parameter is an expression
        /// </summary>
        Expression = 7
    }
}