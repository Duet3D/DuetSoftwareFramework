namespace DuetAPI.Commands
{
    /// <summary>
    /// Enumeration of conditional G-code keywords
    /// </summary>
    public enum KeywordType : byte
    {
        /// <summary>
        /// No conditional code
        /// </summary>
        None,

        /// <summary>
        /// If condition
        /// </summary>
        If,

        /// <summary>
        /// Else-if condition
        /// </summary>
        ElseIf,

        /// <summary>
        /// Else condition
        /// </summary>
        Else,

        /// <summary>
        /// While condition
        /// </summary>
        While,

        /// <summary>
        /// Break instruction
        /// </summary>
        /// <seealso cref="While"/>
        Break,

        /// <summary>
        /// Return instruction
        /// </summary>
        Return,

        /// <summary>
        /// Abort instruction
        /// </summary>
        Abort,

        /// <summary>
        /// Var operation
        /// </summary>
        Var,

        /// <summary>
        /// Set operation
        /// </summary>
        Set,

        /// <summary>
        /// Echo operation
        /// </summary>
        Echo,

        /// <summary>
        /// Continue instruction
        /// </summary>
        /// <seealso cref="While"/>
        Continue
    }
}
