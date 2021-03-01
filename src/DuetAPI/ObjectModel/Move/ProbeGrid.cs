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

        /// <summary>
        /// X start coordinate of the heightmap
        /// </summary>
        [JsonIgnore]
        [Obsolete("Use Mins instead")]
        public float XMin
        {
            get => Mins[0];
            set => Mins[0] = value;
        }

        /// <summary>
        /// X end coordinate of the heightmap
        /// </summary>
        [JsonIgnore]
        [Obsolete("Use Maxs instead")]
        public float XMax
        {
            get => Maxs[0];
            set => Maxs[0] = value;
        }

        /// <summary>
        /// Spacing between the probe points in X direction
        /// </summary>
        [JsonIgnore]
        [Obsolete("Use Spacings instead")]
        public float XSpacing
        {
            get => Spacings[0];
            set => Spacings[0] = value;
        }

        /// <summary>
        /// Y start coordinate of the heightmap
        /// </summary>
        [JsonIgnore]
        [Obsolete("Use Mins instead")]
        public float YMin
        {
            get => Mins[1];
            set => Mins[1] = value;
        }

        /// <summary>
        /// Y end coordinate of the heightmap
        /// </summary>
        [JsonIgnore]
        [Obsolete("Use Maxs instead")]
        public float YMax
        {
            get => Maxs[1];
            set => Maxs[1] = value;
         }

        /// <summary>
        /// Spacing between the probe points in Y direction
        /// </summary>
        [JsonIgnore]
        [Obsolete("Use Spacings instead")]
        public float YSpacing
        {
            get => Spacings[1];
            set => Spacings[1] = value;
        }
    }
}
