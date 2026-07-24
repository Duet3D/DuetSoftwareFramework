using DuetAPI.ObjectModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DuetControlServer.Model;

/// <summary>
/// Partial class implementation of the observer for model objects
/// </summary>
public partial class Observer
{
    /// <summary>
    /// Handler to unregister events from variable model object instances
    /// </summary>
    /// <param name="sender">Parent object</param>
    /// <param name="e">Event arguments</param>
    private void VariableModelObjectChanging(object? sender, PropertyChangingEventArgs e)
    {
        ModelPropertyDescriptor? property = FindProperty(sender, e.PropertyName!, out object? currentValue);
        if (currentValue is ModelObject modelMember)
        {
            // Prevent memory leaks in case variable model objects are replaced
            UnsubscribeFromModelObject(modelMember);
        }
        else if (property?.Kind == ModelPropertyKind.ObservableCollection && currentValue is not null)
        {
            // Same for observable collections
            UnsubscribeFromObservableCollection((INotifyCollectionChanged)currentValue);
        }
    }

    /// <summary>
    /// Look up a property of a model object instance by its name
    /// </summary>
    /// <param name="instance">Model object instance</param>
    /// <param name="propertyName">Name of the property</param>
    /// <param name="value">Value of the property</param>
    /// <returns>Property descriptor or null if the instance does not have such a property</returns>
    private static ModelPropertyDescriptor? FindProperty(object? instance, string propertyName, out object? value)
    {
        if (instance is IModelObjectAccessor accessor)
        {
            ModelPropertyDescriptor? property = accessor.Descriptor.FindProperty(propertyName, false);
            if (property is not null)
            {
                value = accessor.GetPropertyValue(property.Index);
                return property;
            }
        }
        value = null;
        return null;
    }

    /// <summary>
    /// Dictionary of model objects vs property change handlers
    /// </summary>
    private readonly Dictionary<ModelObject, PropertyChangedEventHandler> _propertyChangedHandlers = [];

    /// <summary>
    /// Function to generate a property change handler
    /// </summary>
    /// <param name="hasVariableModelObjects">Whether this instance has any variable model objects</param>
    /// <param name="hasVariableObservableCollections">Whether this instance has any variable observable collections</param>
    /// <param name="path">Property path</param>
    /// <returns>Property change handler</returns>
    private PropertyChangedEventHandler PropertyChanged(bool hasVariableModelObjects, bool hasVariableObservableCollections, object[] path)
    {
        return (sender, e) =>
        {
            ModelPropertyDescriptor? property = FindProperty(sender, e.PropertyName!, out object? value);
            if (property is null)
            {
                // Properties outside the DuetAPI object model are never reported to clients
                return;
            }
            OnPropertyPathChanged?.Invoke(AddToPath(path, property.JsonName), PropertyChangeType.Property, value);

            if (hasVariableModelObjects && value is ModelObject modelMember)
            {
                // Subscribe to variable ModelObject events
                SubscribeToModelObject(modelMember, AddToPath(path, property.JsonName));
            }
            else if (hasVariableObservableCollections && property.Kind == ModelPropertyKind.ObservableCollection && value is not null)
            {
                // Subscribe to variable ObservableCollection events
                SubscribeToObservableCollection((INotifyCollectionChanged)value, property.JsonName, path);
            }
        };
    }

    /// <summary>
    /// Subscribe to changes of the given model object
    /// </summary>
    /// <param name="modelObject">Object to subscribe to</param>
    /// <param name="path">Collection path</param>
    private void SubscribeToModelObject(ModelObject modelObject, object[] path)
    {
        bool hasVariableModelObjects = false, hasVariableObservableCollections = false;
        IModelObjectAccessor accessor = (IModelObjectAccessor)modelObject;
        foreach (ModelPropertyDescriptor property in accessor.Descriptor.Properties)
        {
            object? value = accessor.GetPropertyValue(property.Index);

            if (value is ModelObject objectValue)
            {
                SubscribeToModelObject(objectValue, AddToPath(path, property.JsonName));
            }
            else if (value is IModelCollection collectionValue)
            {
                SubscribeToModelCollection(collectionValue, property.JsonName, path);
            }
            else if (value is IModelDictionary dictionaryValue)
            {
                SubscribeToModelDictionary(dictionaryValue, AddToPath(path, property.JsonName));
            }
            else if (property.Kind == ModelPropertyKind.ObservableCollection && value is not null)
            {
                SubscribeToObservableCollection((INotifyCollectionChanged)value, property.JsonName, path);
            }

            if ((property.Flags & ModelPropertyFlags.HasSetter) != 0)
            {
                hasVariableModelObjects |= property.Kind == ModelPropertyKind.ModelObject;
                hasVariableObservableCollections |= property.Kind == ModelPropertyKind.ObservableCollection;
            }
        }

        if (modelObject is INotifyPropertyChanged propChangeModel)
        {
            PropertyChangedEventHandler changeHandler = PropertyChanged(hasVariableModelObjects, hasVariableObservableCollections, path);
            propChangeModel.PropertyChanged += changeHandler;
            _propertyChangedHandlers[modelObject] = changeHandler;
        }

        if (hasVariableModelObjects || hasVariableObservableCollections)
        {
            // This is barely needed so only register it where it is actually required.
            // It makes sure that events are removed again when a ModelObject instance is replaced
            modelObject.PropertyChanging += VariableModelObjectChanging;
        }
    }

    /// <summary>
    /// Unsubscribe from model object changes
    /// </summary>
    /// <param name="modelObject">Model object to unsubscribe from</param>
    private void UnsubscribeFromModelObject(ModelObject modelObject)
    {
        if (_propertyChangedHandlers.TryGetValue(modelObject, out PropertyChangedEventHandler? changeHandler))
        {
            modelObject.PropertyChanged -= changeHandler;
            _propertyChangedHandlers.Remove(modelObject);
        }

        bool hasVariableModelObjects = false;
        IModelObjectAccessor accessor = (IModelObjectAccessor)modelObject;
        foreach (ModelPropertyDescriptor property in accessor.Descriptor.Properties)
        {
            object? value = accessor.GetPropertyValue(property.Index);
            if (value is ModelObject objectValue)
            {
                UnsubscribeFromModelObject(objectValue);
            }
            else if (value is IModelCollection collectionValue)
            {
                UnsubscribeFromModelCollection(collectionValue);
            }
            else if (value is IModelDictionary dictionaryValue)
            {
                UnsubscribeFromModelDictionary(dictionaryValue);
            }
            else if (property.Kind == ModelPropertyKind.ObservableCollection && value is not null)
            {
                UnsubscribeFromObservableCollection((INotifyCollectionChanged)value);
            }

            hasVariableModelObjects |= property.Kind == ModelPropertyKind.ModelObject && (property.Flags & ModelPropertyFlags.HasSetter) != 0;
        }

        if (hasVariableModelObjects)
        {
            // Same here - unregister the event handler only where required
            modelObject.PropertyChanging -= VariableModelObjectChanging;
        }
    }
}
