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
    }
}
