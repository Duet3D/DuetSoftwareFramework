using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Delta kinematics
    /// </summary>
    public sealed class DeltaKinematics : Kinematics
    {
        /// <summary>
        /// Delta radius (in mm)
        /// </summary>
        public float DeltaRadius
        {
            get => _deltaRadius;
			set => SetPropertyValue(ref _deltaRadius, value);
        }
        private float _deltaRadius;

        /// <summary>
        /// Homed height of a delta printer in mm
        /// </summary>
        public float HomedHeight
        {
            get => _homedHeight;
			set => SetPropertyValue(ref _homedHeight, value);
        }
        private float _homedHeight;

        /// <summary>
        /// Print radius for Hangprinter and Delta geometries (in mm)
        /// </summary>
        public float PrintRadius
        {
            get => _printRadius;
			set => SetPropertyValue(ref _printRadius, value);
        }
        private float _printRadius;

        /// <summary>
        /// Delta tower properties
        /// </summary>
        public ModelCollection<DeltaTower> Towers { get; } = new ModelCollection<DeltaTower>();

        /// <summary>
        /// How much Z needs to be raised for each unit of movement in the +X direction
        /// </summary>
        public float XTilt
        {
            get => _xTilt;
			set => SetPropertyValue(ref _xTilt, value);
        }
        private float _xTilt;

        /// <summary>
        /// How much Z needs to be raised for each unit of movement in the +Y direction
        /// </summary>
        public float YTilt
        {
            get => _yTilt;
			set => SetPropertyValue(ref _yTilt, value);
        }
        private float _yTilt;

        /// <summary>
        /// Write the calibration parameters to config-override.g
        /// </summary>
        /// <param name="writer">Stream writer for config-override.g</param>
        /// <returns>Asynchronous task</returns>
        public override async Task WriteCalibrationParameters(StreamWriter writer)
        {
            await writer.WriteLineAsync("; Delta parameters");
            await writer.WriteLineAsync("M665 " +
                $"L{string.Join(':', Towers.Select(tower => tower.Diagonal.ToString("F3")))} " +
                $"R{DeltaRadius:F3} H{HomedHeight:F3} B{PrintRadius:F1} " +
                $"X{Towers[0].AngleCorrection:F3} Y{Towers[1].AngleCorrection:F3} Z{Towers[2].AngleCorrection:F3}");
            await writer.WriteLineAsync("M666 " +
                $"X{Towers[0].EndstopAdjustment:F3} Y{Towers[1].EndstopAdjustment:F3} Z{Towers[2].EndstopAdjustment:F3} " +
                $"A{XTilt * 100F:F2} B{YTilt * 100F:F2}");
        }
    }
}
