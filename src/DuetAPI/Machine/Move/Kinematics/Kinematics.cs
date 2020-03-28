using System;
using System.Text.Json;

namespace DuetAPI.Machine
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
        /// Figure out the required type for the given kinematics name
        /// </summary>
        /// <param name="name">Kinematics name</param>
        /// <returns>Required type</returns>
        private Type GetKinematicsType(KinematicsName name)
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
                case KinematicsName.RotaryDelta:
                    return typeof(DeltaKinematics);
                case KinematicsName.Hangprinter:
                    return typeof(HangprinterKinematics);
                default:
                    return typeof(Kinematics);
            }
        }

        /// <summary>
        /// Update this instance from a given JSON object
        /// </summary>
        /// <param name="jsonElement">JSON object</param>
        /// <returns>Updated instance</returns>
        public override ModelObject UpdateFromJson(JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {
                throw new ArgumentNullException(nameof(jsonElement));
            }

            if (jsonElement.TryGetProperty("name", out JsonElement nameProperty))
            {
                KinematicsName kinematicsName = (KinematicsName)JsonSerializer.Deserialize(nameProperty.GetRawText(), typeof(KinematicsName));
                Type requiredType = GetKinematicsType(kinematicsName);
                if (GetType() != requiredType)
                {
                    Kinematics newInstance = (Kinematics)Activator.CreateInstance(requiredType);
                    newInstance.UpdateFromJson(jsonElement);
                    return newInstance;
                }
            }
            base.UpdateFromJson(jsonElement);
            return this;
        }
    }
}
