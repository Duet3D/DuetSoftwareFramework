using System;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the configured geometry
    /// </summary>
    public partial class Kinematics : ModelObject, IDynamicModelObject
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
        public MoveSegmentation? Segmentation
        {
            get => _segmentation;
            set => SetPropertyValue(ref _segmentation, value);
        }
        private MoveSegmentation? _segmentation;

        /// <summary>
        /// Figure out the required type for the given kinematics name
        /// </summary>
        /// <param name="name">Kinematics name</param>
        /// <returns>Required type</returns>
        private static Type GetKinematicsType(KinematicsName? name)
        {
            return name switch
            {
                KinematicsName.Cartesian or KinematicsName.CoreXY or KinematicsName.CoreXYU or KinematicsName.CoreXYUV or KinematicsName.CoreXZ or KinematicsName.MarkForged => typeof(CoreKinematics),
                KinematicsName.Delta => typeof(DeltaKinematics),
                KinematicsName.Hangprinter => typeof(HangprinterKinematics),
                KinematicsName.FiveBarScara or KinematicsName.Scara => typeof(ScaraKinematics),
                KinematicsName.Polar => typeof(PolarKinematics),
                _ => typeof(Kinematics),
            };
        }

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        public IDynamicModelObject? UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (jsonElement.TryGetProperty("name", out JsonElement nameProperty))
            {
                KinematicsName? kinematicsName = (KinematicsName?)JsonSerializer.Deserialize(nameProperty.GetRawText(), typeof(KinematicsName));
                Type requiredType = GetKinematicsType(kinematicsName);
                if (GetType() != requiredType)
                {
                    Kinematics newInstance = (Kinematics)Activator.CreateInstance(requiredType)!;
                    return newInstance.UpdateFromJson(jsonElement, ignoreSbcProperties);
                }
            }
            return GeneratedUpdateFromJson(jsonElement, ignoreSbcProperties);
        }

        public IDynamicModelObject? UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties)
        {
            throw new NotImplementedException();
        }
    }
}
