using System;
using System.Collections;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Node of an object model path pointing to a list item
    /// </summary>
    /// <remarks>
    /// This is necessary for the case of model items changing in a collection
    /// </remarks>
    public class ItemPathNode
    {
        /// <summary>
        /// Name of the list
        /// </summary>
        public readonly string Name;

        /// <summary>
        /// Index of the item
        /// </summary>
        public readonly int Index;

        /// <summary>
        /// Internal list reference
        /// </summary>
        private readonly IList _list;

        /// <summary>
        /// Count of the items in the list or 0 if unknown
        /// </summary>
        public int Count { get => (_list != null) ? _list.Count : 0; }

        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="name">List name</param>
        /// <param name="index">Index of the changed item</param>
        /// <param name="list">Reference to the list</param>
        public ItemPathNode(string name, int index, IList list)
        {
            Name = name;
            Index = index;
            _list = list;
        }

        /// <summary>
        /// Check if this instance equals another
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public override bool Equals(object obj)
        {
            return (obj != null &&
                    obj is ItemPathNode other &&
                    other.Name == Name &&
                    other.Index == Index &&
                    other.Count == Count);
        }

        /// <summary>
        /// Compute a hash code for this instance
        /// </summary>
        /// <returns></returns>
        public override int GetHashCode() => HashCode.Combine(Name.GetHashCode(), Index.GetHashCode(), Count.GetHashCode());

        /// <summary>
        /// Convert an item node to a string (for debugging)
        /// </summary>
        /// <returns>String representation of this node</returns>
        public override string ToString() => $"{Name}[{Index} of {Count}]";
    }
}
