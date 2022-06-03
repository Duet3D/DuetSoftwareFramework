using System;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the configured geometry
    /// </summary>
    public class Kinematics : ModelObject
    {
        /// <summary>
        /// Name of the configured kinematics
        /// </summary>
        public KinematicsName Name
        {
            get => _name;
			protected set => SetPropertyValue(ref _name, value);
        }
        private KinematicsName _name = KinematicsName.Unknown;

        /// <summary>
        /// Segmentation parameters or null if not configured
        /// </summary>
        public MoveSegmentation Segmentation
        {
            get => _segmentation;
            set => SetPropertyValue(ref _segmentation, value);
        }
        private MoveSegmentation _segmentation;

        /// <summary>
        /// Figure out the required type for the given kinematics name
        /// </summary>
        /// <param name="name">Kinematics name</param>
        /// <returns>Required type</returns>
        private static Type GetKinematicsType(KinematicsName name)
        {
            switch (name)
            {
                case KinematicsName.Cartesian:
                case KinematicsName.CoreXY:
                case KinematicsName.CoreXYU:
                case KinematicsName.CoreXYUV:
                case KinematicsName.CoreXZ:
                case KinematicsName.MarkForged:
                    return typeof(CoreKinematics);
                case KinematicsName.Delta:
                    return typeof(DeltaKinematics);
                case KinematicsName.Hangprinter:
                    return typeof(HangprinterKinematics);
                case KinematicsName.FiveBarScara:
                case KinematicsName.Scara:
                    return typeof(ScaraKinematics);
                case KinematicsName.Polar:
                    return typeof(PolarKinematics);
                case KinematicsName.RotaryDelta:
                default:
                    return typeof(Kinematics);
            }
        }

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        public override IModelObject UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {
                throw new ArgumentNullException(nameof(jsonElement));
            }

            if (jsonElement.TryGetProperty("name", out JsonElement nameProperty))
            {
                KinematicsName kinematicsName = (KinematicsName)Enum.Parse(typeof(KinematicsName), nameProperty.GetString(), true);
                Type requiredType = GetKinematicsType(kinematicsName);
                if (GetType() != requiredType)
                {
                    Kinematics newInstance = (Kinematics)Activator.CreateInstance(requiredType);
                    return newInstance.UpdateFromJson(jsonElement, ignoreSbcProperties);
                }
            }
            return base.UpdateFromJson(jsonElement, ignoreSbcProperties);
        }
    }
}
