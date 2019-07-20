namespace DuetAPI.Utility
{
    /// <summary>
    /// Interface that provides a method to assign all values from another instance
    /// </summary>
    /// <remarks>Non-value types are supposed to be cloned when implementing this</remarks>
    public interface IAssignable
    {
        /// <summary>
        /// Assign every property from another instance
        /// </summary>
        /// <param name="from">Source object</param>
        void Assign(object from);
    }
}
