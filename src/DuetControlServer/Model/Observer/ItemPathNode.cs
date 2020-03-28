using System;

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
        /// Constructor of this class
        /// </summary>
        /// <param name="name">List name</param>
        /// <param name="index">Index of the changed item</param>
        /// <param name="count">Number of items in the collection</param>
        public ItemPathNode(string name, int index, int count)
        {
            Name = name;
            Index = index;
            Count = count;
        }

        /// <summary>
        /// Name of the list
        /// </summary>
        public string Name;

        /// <summary>
        /// Index of the item
        /// </summary>
        public int Index;

        /// <summary>
        /// Count of the list owning this item
        /// </summary>
        public int Count;

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
