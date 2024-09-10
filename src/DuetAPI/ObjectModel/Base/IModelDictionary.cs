using System;
using System.Collections;
using System.ComponentModel;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Interface for model dictionaries
    /// </summary>
    public interface IModelDictionary : IStaticModelObject, IDictionary, INotifyPropertyChanged, INotifyPropertyChanging
    {
        /// <summary>
        /// Event that is called when the entire directory is cleared
        /// </summary>
        event EventHandler DictionaryCleared;
    }
}
