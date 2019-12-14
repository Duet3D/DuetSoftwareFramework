namespace DuetAPI.Commands
{
    /// <summary>
    /// Remove an existing user session
    /// </summary>
    public class RemoveUserSession : Command<bool>
    {
        /// <summary>
        /// Identifier of the user session to remove
        /// </summary>
        public int Id { get; set; }
    }
}
