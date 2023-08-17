namespace DuetControlServer.SPI.Communication.Shared
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
        Expression = 7,

        /// <summary>
        /// Parameter is a driver identifier (format: [board.]driver)
        /// </summary>
        /// <remarks>The top 16 bits contain the board ID and the bottom 16 bits contain the driver number</remarks>
        DriverId = 8,

        /// <summary>
        /// Parameter is a driver identifier array (format: [board1.]driver1:[board2.]driver2)
        /// </summary>
        /// <remarks>The top 16 bits contain the board ID and the bottom 16 bits contain the driver number</remarks>
        DriverIdArray = 9,

        /// <summary>
        /// Parameter is a boolean (byte)
        /// </summary>
        Bool = 10,

        /// <summary>
        /// Parameter is a boolean array (byte[])
        /// </summary>
        BoolArray = 11,

        /// <summary>
        /// Parameter is an unsigned long
        /// </summary>
        ULong = 12,

        /// <summary>
        /// Parameter is a datetime string
        /// </summary>
        DateTime = 13,

        /// <summary>
        /// Parameter is a null value
        /// </summary>
        Null = 14,

        /// <summary>
        /// Parameter is a char value
        /// </summary>
        Char = 15
    }
}