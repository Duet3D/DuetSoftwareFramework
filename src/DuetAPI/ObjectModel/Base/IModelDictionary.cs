using System.Collections;
using System.ComponentModel;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Interface for model dictionaries
    /// </summary>
    public interface IModelDictionary : IModelObject, IDictionary, INotifyPropertyChanged, INotifyPropertyChanging { }
}
