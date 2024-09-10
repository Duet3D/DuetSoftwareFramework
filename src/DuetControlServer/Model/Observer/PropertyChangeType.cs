namespace DuetControlServer.Model
{
    /// <summary>
    /// Type of path modification
    /// </summary>
    public enum PropertyChangeType
    {
        /// <summary>
        /// Property has changed
        /// </summary>
        /// <remarks>Value is the property value</remarks>
        Property,

        /// <summary>
        /// Collection has changed
        /// </summary>
        /// <remarks>Value is the new item value</remarks>
        Collection,

        /// <summary>
        /// Message collection has changed
        /// </summary>
        /// <remarks>If value is null, the list has been cleared, else only the added items are passed</remarks>
        MessageCollection
    }
}
