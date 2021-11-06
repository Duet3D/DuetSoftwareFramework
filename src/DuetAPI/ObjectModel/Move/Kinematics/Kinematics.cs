using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the configured geometry
    /// </summary>
    public class Kinematics : ModelObject
    {
        /// <summary>
        /// Currently configured geometry type
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
            return name switch
            {
                KinematicsName.Cartesian or KinematicsName.CoreXY or KinematicsName.CoreXYU or KinematicsName.CoreXYUV or KinematicsName.CoreXZ or KinematicsName.MarkForged => typeof(CoreKinematics),
                KinematicsName.Delta or KinematicsName.RotaryDelta => typeof(DeltaKinematics),
                KinematicsName.Hangprinter => typeof(HangprinterKinematics),
                KinematicsName.FiveBarScara or KinematicsName.Scara => typeof(ScaraKinematics),
                _ => typeof(Kinematics)
            };
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

        /// <summary>
        /// Optional method to override in case a kinematics class supports writing calibration parameters to config-override.g
        /// </summary>
        /// <param name="writer">Stream writer for config-override.g</param>
        /// <returns>Asynchronous task</returns>
        public virtual Task WriteCalibrationParameters(StreamWriter writer) => Task.CompletedTask;
    }
}
