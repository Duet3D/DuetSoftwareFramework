using DuetAPI.Utility;
using System;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the configured probe grid (see M557)
    /// </summary>
    /// <seealso cref="Heightmap"/>
    public sealed class ProbeGrid : ModelObject
    {
        /// <summary>
        /// Axis letters of this heightmap
        /// </summary>
        public ModelCollection<char> Axes { get; } = new ModelCollection<char>() { 'X', 'Y' };

        /// <summary>
        /// End coordinates of the heightmap
        /// </summary>
        public ModelCollection<float> Maxs { get; } = new ModelCollection<float>() { -1F, -1F };

        /// <summary>
        /// Start coordinates of the heightmap
        /// </summary>
        public ModelCollection<float> Mins { get; } = new ModelCollection<float>() { 0F, 0F };

        /// <summary>
        /// Probing radius for delta kinematics
        /// </summary>
        public float Radius
        {
            get => _radius;
            set => SetPropertyValue(ref _radius, value);
        }
        private float _radius;

        /// <summary>
        /// Spacings between the coordinates
        /// </summary>
        public ModelCollection<float> Spacings { get; } = new ModelCollection<float>() { 0F, 0F };
    }
}
