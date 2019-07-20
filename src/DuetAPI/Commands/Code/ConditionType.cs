namespace DuetAPI.Commands
{
    /// <summary>
    /// Types of conditional G-code
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
        Set
    }
}
