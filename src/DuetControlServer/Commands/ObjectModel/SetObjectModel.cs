using DuetAPI.Utility;
using System;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.SetObjectModel"/> command
    /// </summary>
    public sealed class SetObjectModel : DuetAPI.Commands.SetObjectModel
    {
        /// <summary>
        /// Set an atomic property in the object model
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task<bool> Execute()
        {
            if (!IPC.LockManager.IsLocked)
            {
                throw new InvalidOperationException("Machine model has not been locked");
            }

            // Split the path
            string[] pathItems = PropertyPath.Split('/');
            if (pathItems.Length < 2)
            {
                return Task.FromResult(false);
            }

            // Try to find the object that the path references
            string lastPathItem = "<root>";
            object? obj = Model.Provider.Get;
            for (int i = 0; i < pathItems.Length - 1; i++)
            {
                string pathItem = pathItems[i];
                if (string.IsNullOrWhiteSpace(pathItem))
                {
                    continue;
                }

                if (int.TryParse(pathItem, out int index))
                {
                    if (obj is IList list)
                    {
                        obj = list[index];
                    }
                    else
                    {
                        throw new ArgumentException($"Cannot index into {lastPathItem} because the type is incompatible (segment {i})");
                    }
                }
                else
                {
                    PropertyInfo? property = obj?.GetType().GetProperty(pathItem, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (property is not null)
                    {
                        obj = property.GetValue(obj);
                    }
                }
                lastPathItem = pathItem;
            }

            // Try to update the property
            if (obj != Model.Provider.Get)
            {
                PropertyInfo? property = obj?.GetType().GetProperty(pathItems[^1], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property is not null)
                {
                    object? newValue = JsonSerializer.Deserialize(Value, property.PropertyType, JsonHelper.DefaultJsonOptions);
                    property.SetValue(obj, newValue);
                    return Task.FromResult(true);
                }
            }
            return Task.FromResult(false);
        }
    }
}
