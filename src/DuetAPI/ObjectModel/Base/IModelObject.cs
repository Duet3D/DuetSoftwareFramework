using System;
using System.ComponentModel;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Base interface for object model classes
    /// </summary>
    public interface IModelObject : ICloneable, INotifyPropertyChanged { }
}
