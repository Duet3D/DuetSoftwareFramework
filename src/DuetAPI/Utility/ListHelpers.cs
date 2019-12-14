using System;
using System.Collections.Generic;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Class holding a few functions for lists that implement <c>IClonable</c> and <c>IAssignFrom</c>
    /// </summary>
    public static class ListHelpers
    {
        /// <summary>
        /// Helper function to assign generic lists. Missing items are cloned, existing items are assigned
        /// </summary>
        /// <typeparam name="T">Item type</typeparam>
        /// <param name="to">List to assign to</param>
        /// <param name="from">List to assign from</param>
        public static void AssignList<T>(IList<T> to, IList<T> from) where T : IAssignable, ICloneable
        {
            for (int i = 0; i < from.Count; i++)
            {
                if (i >= to.Count)
                {
                    to.Add((T)from[i]?.Clone());
                }
                else
                {
                    to[i].Assign(from[i]);
                }
            }

            for (int i = to.Count; i > from.Count; i--)
            {
                to.RemoveAt(i - 1);
            }
        }

        /// <summary>
        /// Helper function to set generic lists
        /// </summary>
        /// <typeparam name="T">Item type</typeparam>
        /// <param name="destination">List to set</param>
        /// <param name="source">List to copy from</param>
        public static void SetList<T>(IList<T> destination, IList<T> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                if (i >= destination.Count)
                {
                    destination.Add(source[i]);
                }
                else if (!destination[i].Equals(source[i]))
                {
                    destination[i] = source[i];
                }
            }

            for (int i = source.Count; i > destination.Count; i--)
            {
                destination.RemoveAt(i - 1);
            }
        }

        /// <summary>
        /// Helper function to add items from one list to another
        /// </summary>
        /// <typeparam name="T">Item type</typeparam>
        /// <param name="to">List to add items from</param>
        /// <param name="from">List to add items to</param>
        public static void AddItems<T>(IList<T> to, IList<T> from)
        {
            foreach (T item in from)
            {
                to.Add(item);
            }
        }

        /// <summary>
        /// Helper function to clone items and to add them to the specified list
        /// </summary>
        /// <remarks>This function does not clear the destination list</remarks>
        /// <typeparam name="T">Item type</typeparam>
        /// <param name="destination">List to add the clones to</param>
        /// <param name="source">List to clone items from</param>
        public static void CloneItems<T>(IList<T> destination, IList<T> source) where T : ICloneable
        {
            foreach (T item in source)
            {
                destination.Add((T)item?.Clone());
            }
        }

        /// <summary>
        /// Helper functionto remove a specific item from a list of assignable items
        /// </summary>
        /// <remarks>
        /// This must be used to ensure Patch mode keeps working when DCS removes certain items like user sessions or HTTP endpoints
        /// </remarks>
        /// <typeparam name="T">Item type</typeparam>
        /// <param name="list">List to remove an item from</param>
        /// <param name="index">Index of the item to remove</param>
        public static void RemoveItem<T>(IList<T> list, int index) where T : IAssignable
        {
            for (int i = list.Count - 1; i > index; i--)
            {
                list[i - 1].Assign(list[i]);
            }
            list.RemoveAt(list.Count - 1);
        }
    }
}
