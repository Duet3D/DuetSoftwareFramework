namespace LinuxApi
{
    /// <summary>
    /// Exit codes derived from sysexits.h
    /// </summary>
    public static class ExitCode
    {
        /// <summary>
        /// Successful termination
        /// </summary>
        public const int Success = 0;

        /// <summary>
        /// Command line usage error
        /// </summary>
        public const int Usage = 64;

        /// <summary>
        /// Data format error
        /// </summary>
        public const int DataError = 65;

        /// <summary>
        /// Cannot open input
        /// </summary>
        public const int NoInput = 66;

        /// <summary>
        /// Addressee unknown
        /// </summary>
        public const int NoUser = 67;

        /// <summary>
        /// Host name unknown
        /// </summary>
        public const int NoHost = 68;

        /// <summary>
        /// Service unavailable
        /// </summary>
        public const int ServiceUnavailable = 69;

        /// <summary>
        /// Internal software error
        /// </summary>
        public const int Software = 70;

        /// <summary>
        /// System error (e.g., can't fork)
        /// </summary>
        public const int OsError = 71;

        /// <summary>
        /// Critical OS file missing
        /// </summary>
        public const int OsFile = 72;

        /// <summary>
        /// Can't create (user) output file 
        /// </summary>
        public const int CantCreate = 73;

        /// <summary>
        /// Input/output error 
        /// </summary>
        public const int IoError = 74;

        /// <summary>
        /// Temp failure; user is invited to retry
        /// </summary>
        public const int TempFailure = 75;

        /// <summary>
        /// Remote error in protocol
        /// </summary>
        public const int Protocol = 76;

        /// <summary>
        /// Permission denied
        /// </summary>
        public const int NoPerm = 77;

        /// <summary>
        /// Configuration error
        /// </summary>
        public const int Configuration = 78;
    }
}
