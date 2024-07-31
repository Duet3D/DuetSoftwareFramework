using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Base class for object model classes
    /// </summary>
    public class ModelObject : INotifyPropertyChanging
    {
        /// <summary>
        /// Event that is triggered when a property is being changed
        /// </summary>
        public event PropertyChangingEventHandler? PropertyChanging;

        /// <summary>
        /// Event that is triggered when a property has been changed
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Method to update a property value internally
        /// </summary>
        /// <param name="propertyStorage">Reference to the variable that holds the current value</param>
        /// <param name="value">New property value</param>
        /// <param name="propertyName">Name of the property</param>
        protected void SetPropertyValue<T>(ref T propertyStorage, T value, [CallerMemberName] string propertyName = "")
        {
            if (!Equals(propertyStorage, value))
            {
                PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(propertyName));
                propertyStorage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

#if false
        /// <summary>
        /// Assign the properties from another instance
        /// </summary>
        /// <param name="from">Other instance</param>
        public void Assign(object from)
        {
            // Validate the types
            Type myType = GetType();
            if (from?.GetType() != myType)
            {
                throw new ArgumentException("Types do not match", nameof(from));
            }

#warning TODO
        }

        /// <summary>
        /// Create a clone of this instance
        /// </summary>
        /// <returns>Cloned object</returns>
        public object Clone()
        {
            // Make a new clone
            Type myType = GetType();
            ModelObject clone = (ModelObject)Activator.CreateInstance(myType)!;

#warning TODO
            return clone;
        }

        /// <summary>
        /// Create a dictionary or list of all the differences between this instance and another.
        /// This method outputs own property values that differ from the other instance
        /// </summary>
        /// <param name="other">Other instance</param>
        /// <returns>Object differences or null if both instances are equal</returns>
        public object? FindDifferences(IStaticModelObject other)
        {
            // Check the types
            Type myType = GetType(), otherType = other.GetType();
            if (myType != otherType)
            {
                // Types differ, return the entire instance
                return this;
            }

            // Look for differences
            Dictionary<string, object?>? diffs = null;
#warning TODO
            return diffs;
        }

        /// <summary>
        /// Create a UTF8-encoded JSON patch to bring an old instance to this state
        /// </summary>
        /// <param name="old">Old object state</param>
        /// <returns>JSON patch</returns>
        public byte[] MakeUtf8Patch(ModelObject old)
        {
            object? diffs = FindDifferences(old);
            return JsonSerializer.SerializeToUtf8Bytes(diffs, Utility.JsonHelper.DefaultJsonOptions);
        }

        /// <summary>
        /// Create a string-encoded JSON patch to bring an old instance to this state
        /// </summary>
        /// <param name="old">Old object state</param>
        /// <returns>JSON patch</returns>
        public string MakeStringPatch(ModelObject old)
        {
            object? diffs = FindDifferences(old);
            return JsonSerializer.Serialize(diffs, Utility.JsonHelper.DefaultJsonOptions);
        }
#endif

    }
}
